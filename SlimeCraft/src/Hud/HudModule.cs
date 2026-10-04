using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Hud module (Order 100): owns SC.Inventory (<see cref="PlayerInventory"/>) and SC.Hud (this).
    /// Builds a pixel-perfect Minecraft GUI with UGUI in code: a ScreenSpaceOverlay canvas (sortingOrder 32000)
    /// scaled by an integer GUI scale, drawn every frame through the immediate-mode <see cref="Gui"/>.
    /// Handles hotbar selection, HUD/vacpack mode switching, screens (inventory, crafting, creative, chat),
    /// the F3 overlay and the title-screen extras.
    /// </summary>
    public sealed class HudModule : MonoBehaviour, IModule, IHud
    {
        public string ModuleName => "Hud";
        public int Order => 100;

        internal static HudModule Instance { get; private set; }

        internal PlayerInventory PInv { get; private set; }
        internal readonly GuiAtlas Atlas = new GuiAtlas();
        internal readonly McFont Font = new McFont();
        internal Gui G { get; private set; }
        internal readonly ChatHistory Chat = new ChatHistory();
        internal HudLayer Layer { get; private set; }
        private readonly DebugOverlay debug = new DebugOverlay();
        private readonly TitleExtras titleExtras = new TitleExtras();

        private Canvas canvas;
        private CanvasScaler scaler;
        private RectTransform guiRoot;

        internal int GuiScale { get; private set; } = 1;
        internal int TickCount { get; private set; }
        internal bool DebugVisible { get; private set; }
        private float tickAccumulator;
        private float PartialTick => Mathf.Clamp01(tickAccumulator / 0.05f);

        private HudScreen screen;
        private ItemStack carried = ItemStack.Empty;
        internal ItemStack Carried { get => carried; set => carried = value ?? ItemStack.Empty; }
        private const string ScreenId = "slimecraft.hud";
        private bool screenPushed;
        private int pendingPopFrames = -1;
        private int screenOpenedFrame = -1;
        private bool weOwnCursor;

        private bool appliedVacpack;
        private int appliedSlot = -1;
        private bool forceApplyVacpack = true;
        private float reapplySrHudAt = -1f, reapplySrHudAt2 = -1f;
        private bool persistenceRegistered, commandsRegistered, srSubscribed;
        private bool fontWasLoaded;
        private bool ensureVacpackPending;

        // mouse state for screens
        private double lastMouseX = double.NaN, lastMouseY = double.NaN;
        private int heldButton = -1;
        private float lastClickTime = -10f;
        private int lastClickButton = -1;
        private readonly Dictionary<KeyCode, float> repeatAt = new Dictionary<KeyCode, float>();

        private static readonly KeyCode[] ScreenKeys =
        {
            KeyCode.Escape, KeyCode.Return, KeyCode.KeypadEnter, KeyCode.Tab, KeyCode.UpArrow, KeyCode.DownArrow,
            KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.Home, KeyCode.End, KeyCode.Delete, KeyCode.PageUp,
            KeyCode.PageDown, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.Alpha3, KeyCode.Alpha4, KeyCode.Alpha5,
            KeyCode.Alpha6, KeyCode.Alpha7, KeyCode.Alpha8, KeyCode.Alpha9, KeyCode.A, KeyCode.C, KeyCode.V, KeyCode.X
        };
        private static readonly KeyCode[] RepeatKeys = { KeyCode.LeftArrow, KeyCode.RightArrow, KeyCode.Delete, KeyCode.UpArrow, KeyCode.DownArrow };

        // ================================================================== lifecycle
        public void Init()
        {
            Instance = this;
            HudConfig.Bind();
            TitleExtras.TakeOverCoreSplash(); // Core (Order 0) has bound its config by now
            PInv = new PlayerInventory();
            PInv.SlotPopped += i => Layer?.StartPop(i);
            PInv.GameModeChanged += OnGameModeChanged;
            PInv.GuardSuspended = () => screen is PanelScreen || !Carried.IsEmpty;
            PInv.PendingStacks = PendingStacks;
            PInv.Changed += () => ensureVacpackPending = true;
            SC.Inventory = PInv;
            SC.Hud = this;
            Layer = new HudLayer(this);
            CreateCanvas();
            TryRegisterServices();
            SC.AllModulesInitialized += TryRegisterServices;
        }

        private void CreateCanvas()
        {
            var go = new GameObject("SlimeCraftHudCanvas", typeof(RectTransform));
            go.layer = 5; // UI
            go.transform.SetParent(SC.Root != null ? SC.Root.transform : transform, false);
            canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;
            canvas.pixelPerfect = false;
            canvas.additionalShaderChannels = AdditionalCanvasShaderChannels.None;
            scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            scaler.referencePixelsPerUnit = 100f;
            var rootGo = new GameObject("Gui", typeof(RectTransform));
            rootGo.layer = 5;
            guiRoot = (RectTransform)rootGo.transform;
            guiRoot.SetParent(go.transform, false);
            guiRoot.anchorMin = Vector2.zero; guiRoot.anchorMax = Vector2.one;
            guiRoot.pivot = new Vector2(0f, 1f);
            guiRoot.offsetMin = Vector2.zero; guiRoot.offsetMax = Vector2.zero;
            G = new Gui(guiRoot, Atlas, Font);
            G.VacpackIcon = () => HudConfig.SRVacpackIcon && SC.SR != null && SC.SR.InGame ? Layer.Ammo.VacpackIcon() : null;
            G.VacpackPixelIcon = () => HudConfig.PixelVacpackIcon && !HudConfig.SRVacpackIcon ? SpecialRegion("slimecraft:vacpack_icon") : null;
            canvas.enabled = false;
        }

        private void TryRegisterServices()
        {
            try
            {
                if (!persistenceRegistered && SC.Persistence != null) { PInv.RegisterPersistence(); persistenceRegistered = true; }
                if (!commandsRegistered && SC.Commands != null) { HudCommands.Register(this); commandsRegistered = true; }
                if (!srSubscribed && SC.SR != null)
                {
                    SC.SR.WorldLoaded += OnWorldLoaded;
                    SC.SR.WorldUnloading += OnWorldUnloading;
                    SC.SR.PlayerDamaged += OnSRPlayerDamaged;
                    srSubscribed = true;
                }
            }
            catch (Exception e) { SC.Log?.LogError("[Hud] service registration failed: " + e); }
        }

        private void OnWorldLoaded()
        {
            try
            {
                PInv.OnWorldLoaded();
                Layer.Reset();
                forceApplyVacpack = true;
                ApplySRHud();
                reapplySrHudAt = Time.unscaledTime + 1f;
                reapplySrHudAt2 = Time.unscaledTime + 3f;
                Chat.ScrollToNewest();
            }
            catch (Exception e) { SC.Log?.LogError("[Hud] WorldLoaded failed: " + e); }
        }

        private void OnWorldUnloading()
        {
            try
            {
                if (screen != null) CloseScreen();
                FlushPendingPop(true);
                Chat.Wipe(false);
                PInv.OnWorldUnloading();
                Layer.Reset();
            }
            catch (Exception e) { SC.Log?.LogError("[Hud] WorldUnloading failed: " + e); }
        }

        private void OnGameModeChanged()
        {
            // switching game mode with an inventory open swaps it for the matching survival / creative screen
            if (screen is SurvivalInventoryScreen && PInv.Creative) OpenScreen(new CreativePaletteScreen());
            else if (screen is CreativePaletteScreen && !PInv.Creative) OpenScreen(new SurvivalInventoryScreen());
        }

        // ================================================================== frame
        private void Update()
        {
            try
            {
                var sr = SC.SR;
                bool inGame = sr != null && sr.InGame;
                bool paused = sr != null && sr.IsPaused;
                if (!paused || screen != null)
                {
                    tickAccumulator += Time.unscaledDeltaTime;
                    int n = 0;
                    while (tickAccumulator >= 0.05f && n++ < 10)
                    {
                        tickAccumulator -= 0.05f;
                        TickCount++;
                        if (inGame) Layer.Tick();
                        screen?.Step();
                    }
                    if (tickAccumulator > 0.5f) tickAccumulator = 0f;
                }
                debug.CountFrame();
                titleExtras.Update();
                if (pendingPopFrames >= 0 && --pendingPopFrames < 0) FlushPendingPop(false);
                if (inGame) GameUpdate(sr);
                else if (screen != null) CloseScreen();
                if (inGame && ensureVacpackPending && screen == null && Carried.IsEmpty)
                {
                    ensureVacpackPending = false;
                    PInv.EnsureVacpack();
                }
                Recipes.Pump(inGame && screen == null ? 3.0 : 8.0);
            }
            catch (Exception e) { LogLimited("update", "Hud.Update failed: " + e); }
        }

        private void GameUpdate(ISRBridge sr)
        {
            // re-assert SR HUD visibility a little after load (SR builds its HUD late)
            if (reapplySrHudAt > 0 && Time.unscaledTime >= reapplySrHudAt) { reapplySrHudAt = -1f; ApplySRHud(); }
            if (reapplySrHudAt2 > 0 && Time.unscaledTime >= reapplySrHudAt2) { reapplySrHudAt2 = -1f; ApplySRHud(); }

            var inp = SC.Input;
            bool textFocused = screen != null && screen.WantsText;
            KeyCode debugKey = inp != null ? inp.DebugKey : KeyCode.F3;
            KeyCode hudKey = inp != null ? inp.HudToggleKey : KeyCode.F8;
            if (!textFocused && Input.GetKeyDown(debugKey)) DebugVisible = !DebugVisible;
            if (!textFocused && Input.GetKeyDown(hudKey)) McHudEnabled = !McHudEnabled;

            if (screen != null && PauseMenuOpen()) CloseScreen();

            if (screen != null)
            {
                ScreenInput();
            }
            else
            {
                bool allowed = inp == null || inp.GameplayInputAllowed;
                if (allowed) GameplayInput(inp);
            }

            // vacpack / MC item mode (SuppressSRWeapon + SR vacpack visibility)
            bool vac = PInv.VacpackSelected;
            if (vac != appliedVacpack || forceApplyVacpack || PInv.SelectedSlot != appliedSlot)
            {
                appliedVacpack = vac;
                appliedSlot = PInv.SelectedSlot;
                forceApplyVacpack = false;
                if (inp != null) inp.SuppressSRWeapon = !vac;
                try { sr.SetVacpackActive(vac); } catch (Exception e) { LogLimited("vacpack", "SetVacpackActive failed: " + e.Message); }
            }
        }

        private void GameplayInput(IInputGate inp)
        {
            // hotbar 1-9
            for (int i = 0; i < 9; i++)
                if (Input.GetKeyDown(KeyCode.Alpha1 + i)) PInv.SelectedSlot = i;

            // mouse wheel: SR owns it while the vacpack is selected (vac slot cycling), Ctrl+wheel always moves the hotbar
            float scroll = inp != null ? inp.Scroll : Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.001f)
            {
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (!PInv.VacpackSelected || ctrl)
                {
                    int d = scroll > 0 ? -1 : 1; // wheel up selects the previous slot, as in Minecraft
                    PInv.SelectedSlot = ((PInv.SelectedSlot + d) % 9 + 9) % 9;
                }
            }

            KeyCode invKey = inp != null ? inp.InventoryKey : KeyCode.I;
            KeyCode chatKey = inp != null ? inp.ChatKey : KeyCode.Y;
            KeyCode cmdKey = inp != null ? inp.CommandKey : KeyCode.Slash;
            KeyCode dropKey = inp != null ? inp.DropKey : KeyCode.Z;
            if (KeyDown(inp, invKey)) { OpenInventory(); return; }
            if (KeyDown(inp, chatKey)) { OpenChat(""); return; }
            if (cmdKey != KeyCode.None && KeyDown(inp, cmdKey)) { OpenChat("/"); return; }
            if (HudConfig.DropKey && KeyDown(inp, dropKey)) DropSelected(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl));
        }

        private static bool KeyDown(IInputGate inp, KeyCode k) => k != KeyCode.None && (inp != null ? inp.GameKeyDown(k) : Input.GetKeyDown(k));

        private void DropSelected(bool wholeStack)
        {
            var sel = PInv.Selected;
            if (sel.IsEmpty || sel.Id == Content.Vacpack) return;
            int n = wholeStack ? sel.Count : 1;
            var st = sel.WithCount(n);
            if (!DropStack(st)) return;
            sel.Count -= n;
            if (sel.Count <= 0) PInv.Inv.Set(PInv.SelectedSlot, ItemStack.Empty);
            else PInv.Inv.NotifyChanged();
            try { SC.FirstPerson?.Swing(); } catch { }
        }

        private void LateUpdate()
        {
            try { RenderFrame(); }
            catch (Exception e) { LogLimited("render", "Hud render failed: " + e); }
        }

        private void RenderFrame()
        {
            var sr = SC.SR;
            bool inGame = sr != null && sr.InGame;
            bool assets = SC.Assets != null && SC.Assets.Ready;
            if (assets && !Atlas.Created) { Atlas.Create(); BuildSpecialRegions(); }
            if (assets && !Font.Loaded) Font.TryLoad(Atlas);
            if (Font.Loaded && !fontWasLoaded) { fontWasLoaded = true; Chat.Reflow(Font); }

            // nothing Minecraft over Slime Rancher's loading screen (AutoSaveDirector.IsLoadingGame covers the whole
            // load, from the main menu button until the game is fully loaded); title extras only on the menu page
            bool loading = SRLoading();
            bool title = !inGame && !loading && titleExtras.Active;
            bool show = assets && Atlas.Created && ((inGame && !loading) || title);
            if (canvas.enabled != show) canvas.enabled = show;
            if (!show) return;

            int s = ComputeScale();
            if (GuiScale != s || !Mathf.Approximately(scaler.scaleFactor, s)) { GuiScale = s; scaler.scaleFactor = s; }
            int w = Mathf.CeilToInt(UnityEngine.Screen.width / (float)s), h = Mathf.CeilToInt(UnityEngine.Screen.height / (float)s);
            int mx = (int)Math.Floor(Input.mousePosition.x / s), my = (int)Math.Floor((UnityEngine.Screen.height - Input.mousePosition.y) / s);

            G.Begin(w, h);
            if (title)
            {
                titleExtras.Render(G, s);
            }
            else
            {
                bool pauseMenu = PauseMenuOpen();
                bool visible = !pauseMenu && (screen != null || (!sr.IsPaused && !sr.SRUIOpen));
                if (visible)
                {
                    float pt = PartialTick;
                    Layer.TrackPickups(screen != null);
                    Layer.Render(G, pt, McHudEnabled);
                    if (!(screen is ChatInputScreen)) Chat.Draw(G, TickCount, false);
                    if (DebugVisible) debug.Render(G, this);
                    if (screen != null)
                    {
                        screen.Fit(w, h);
                        G.LayerBreak();
                        screen.Draw(G, mx, my, pt);
                    }
                }
            }
            G.End();
            Atlas.ApplyIfDirty();
        }

        /// <summary>
        /// Automatic GUI scale: the biggest whole factor that still leaves at least 320 x 240 GUI pixels on screen
        /// (never below 1). A positive [Hud] GuiScale caps it.
        /// </summary>
        private static int ComputeScale()
        {
            int auto = Math.Max(1, Math.Min(UnityEngine.Screen.width / 320, UnityEngine.Screen.height / 240));
            int cfg = HudConfig.GuiScaleValue;
            return cfg > 0 ? Math.Min(cfg, auto) : auto;
        }

        /// <summary>Slime Rancher is loading a game (its loading screen is up): AutoSaveDirector.IsLoadingGame.</summary>
        internal static bool SRLoading()
        {
            try
            {
                var gc = SRSingleton<GameContext>.Instance;
                return gc != null && gc.AutoSaveDirector != null && gc.AutoSaveDirector.IsLoadingGame();
            }
            catch { return false; }
        }

        private static bool PauseMenuOpen()
        {
            try
            {
                var pm = SRSingleton<PauseMenu>.Instance;
                return pm != null && pm.pauseUI != null && pm.pauseUI.activeSelf;
            }
            catch { return false; }
        }

        // ================================================================== screens
        internal bool ScreenIsOpen => screen != null;
        internal string CurrentScreenName => screen?.Name;

        internal void OpenScreen(HudScreen s)
        {
            if (s == null) { CloseScreen(); return; }
            if (SC.SR == null || !SC.SR.InGame) return;
            if (screen != null)
            {
                var old = screen;
                screen = null;
                try { old.Closed(); } catch (Exception e) { LogLimited("screen.removed", e.ToString()); }
            }
            pendingPopFrames = -1; // reuse the existing push
            if (!screenPushed)
            {
                screenPushed = true;
                try { SC.Input?.PushScreen(ScreenId); } catch (Exception e) { LogLimited("push", e.ToString()); }
                if (SC.Input == null) { weOwnCursor = true; Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            }
            screen = s;
            int sc = Math.Max(1, GuiScale);
            s.Open(this, Mathf.CeilToInt(UnityEngine.Screen.width / (float)sc), Mathf.CeilToInt(UnityEngine.Screen.height / (float)sc));
            screenOpenedFrame = Time.frameCount;
            heldButton = -1;
            lastMouseX = double.NaN;
        }

        internal void CloseScreen()
        {
            var s = screen;
            if (s == null) return;
            screen = null;
            try { s.Closed(); } catch (Exception e) { LogLimited("screen.removed", e.ToString()); }
            if (!Carried.IsEmpty)
            {
                var c = Carried; Carried = ItemStack.Empty;
                int left = PInv.Give(c);
                if (left > 0) DropStack(c.WithCount(left), true);
            }
            // pop a couple of frames later so the Escape that closed us does not also open SR's pause menu
            pendingPopFrames = 2;
            ensureVacpackPending = true;
        }

        private void FlushPendingPop(bool immediate)
        {
            pendingPopFrames = -1;
            if (!screenPushed || (screen != null && !immediate)) return;
            screenPushed = false;
            try { SC.Input?.PopScreen(ScreenId); } catch (Exception e) { LogLimited("pop", e.ToString()); }
            if (weOwnCursor) { weOwnCursor = false; Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
        }

        internal void OpenInventory()
        {
            if (PInv.Creative) OpenScreen(new CreativePaletteScreen());
            else OpenScreen(new SurvivalInventoryScreen());
        }

        internal void OpenChat(string initial) => OpenScreen(new ChatInputScreen(initial));
        internal void RequestVacpackCheck() => ensureVacpackPending = true;
        internal void OpenCrafting() => OpenScreen(new WorkbenchScreen());

        private ItemStack[] PendingStacks()
        {
            var list = new List<ItemStack>();
            if (!Carried.IsEmpty) list.Add(Carried);
            if (screen is PanelScreen ps) list.AddRange(ps.ScreenOwnedStacks());
            return list.ToArray();
        }

        private void ScreenInput()
        {
            var s = screen;
            int sc = Math.Max(1, GuiScale);
            s.Fit(Mathf.CeilToInt(UnityEngine.Screen.width / (float)sc), Mathf.CeilToInt(UnityEngine.Screen.height / (float)sc));
            double mx = Input.mousePosition.x / sc, my = (UnityEngine.Screen.height - Input.mousePosition.y) / sc;
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            for (int b = 0; b < 3 && screen == s; b++)
            {
                if (!Input.GetMouseButtonDown(b)) continue;
                bool dbl = Time.unscaledTime - lastClickTime < 0.25f && lastClickButton == b;
                lastClickTime = Time.unscaledTime; lastClickButton = b;
                heldButton = b;
                s.PointerDown(mx, my, b, dbl);
            }
            if (screen != s) return;
            if (heldButton >= 0 && Input.GetMouseButton(heldButton) && !double.IsNaN(lastMouseX) && (mx != lastMouseX || my != lastMouseY))
                s.PointerMove(mx, my, heldButton);
            lastMouseX = mx; lastMouseY = my;
            for (int b = 0; b < 3 && screen == s; b++)
            {
                if (!Input.GetMouseButtonUp(b)) continue;
                s.PointerUp(mx, my, b);
                if (heldButton == b) heldButton = -1;
            }
            if (screen != s) return;
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) > 0.001f) s.Wheel(mx, my, wheel > 0 ? 1 : -1);
            if (screen != s) return;

            KeyCode invKey = SC.Input != null ? SC.Input.InventoryKey : KeyCode.I;
            KeyCode dropKey = SC.Input != null ? SC.Input.DropKey : KeyCode.Z;
            KeyCode chatKey = SC.Input != null ? SC.Input.ChatKey : KeyCode.Y;
            foreach (var k in ScreenKeys)
            {
                if (screen != s) return;
                if (Input.GetKeyDown(k)) { repeatAt[k] = Time.unscaledTime + 0.5f; s.Key(k, ctrl, shift); }
            }
            foreach (var k in RepeatKeys)
            {
                if (screen != s) return;
                if (Input.GetKey(k) && repeatAt.TryGetValue(k, out float t) && Time.unscaledTime >= t)
                {
                    repeatAt[k] = Time.unscaledTime + 0.033f;
                    s.Key(k, ctrl, shift);
                }
            }
            if (screen != s) return;
            // configurable keys that are not in the fixed list (letters typed into text fields are not hotkeys)
            if (!s.WantsText)
            {
                if (Input.GetKeyDown(invKey) && Array.IndexOf(ScreenKeys, invKey) < 0) s.Key(invKey, ctrl, shift);
                if (screen == s && Input.GetKeyDown(dropKey) && Array.IndexOf(ScreenKeys, dropKey) < 0) s.Key(dropKey, ctrl, shift);
                if (screen == s && Input.GetKeyDown(chatKey) && Array.IndexOf(ScreenKeys, chatKey) < 0) s.Key(chatKey, ctrl, shift);
            }
            if (screen != s) return;
            if (Time.frameCount == screenOpenedFrame) return; // the key that opened the screen is not typed into it
            string typed = Input.inputString;
            if (!string.IsNullOrEmpty(typed) && !(ctrl && typed.Length == 1 && typed[0] < ' ' && typed[0] != '\b'))
                foreach (char c in typed)
                {
                    if (screen != s) return;
                    s.Typed(c);
                }
        }

        // ================================================================== world interaction
        /// <summary>Throws a stack in front of the player, like dropping an item in Minecraft. False if it could not be spawned (kept by caller).</summary>
        internal bool DropStack(ItemStack st, bool mandatory = false)
        {
            if (st == null || st.IsEmpty) return true;
            if (st.Id == Content.Vacpack) return false;
            var sr = SC.SR;
            var ent = SC.Entities;
            if (sr == null || !sr.InGame || ent == null)
            {
                if (mandatory) LogLimited("drop", "No Entities service: " + st + " could not be dropped and was lost");
                return false;
            }
            try
            {
                Vector3 look = sr.LookDirection.normalized;
                Vector3 pos = sr.EyePosition - new Vector3(0f, 0.3f, 0f) + look * 0.6f;
                // toss speed in blocks per tick: 0.3 along the view, a small lift of 0.1 (+-0.1 random) and a
                // sideways nudge of at most 0.02; converted to m/s (20 ticks per second)
                Vector3 toss = look * 0.3f;
                toss.y += 0.1f + 0.1f * (UnityEngine.Random.value - UnityEngine.Random.value);
                Vector2 nudge = UnityEngine.Random.insideUnitCircle * 0.02f;
                toss.x += nudge.x;
                toss.z += nudge.y;
                Vector3 vel = toss * 20f;
                var go = ent.SpawnItem(st.Copy(), pos, vel, 2f);
                return go != null;
            }
            catch (Exception e)
            {
                LogLimited("drop", "SpawnItem failed: " + e.Message);
                return false;
            }
        }

        /// <summary>Runs a chat command and prints its feedback (Minecraft shows command output in chat).</summary>
        internal void RunCommand(string line)
        {
            string fb;
            if (SC.Commands == null) fb = "§cCommands are not available (the Core module is not running)";
            else
            {
                try { fb = SC.Commands.Execute(line); }
                catch (Exception e)
                {
                    fb = "§c" + McLang.Format("command.failed", "Something went wrong while running that command") + " (" + e.Message + ")";
                }
            }
            if (!string.IsNullOrEmpty(fb)) AddChat(fb);
        }

        // ================================================================== player figure / special sprites
        internal GuiRegion SpecialRegion(string key)
        {
            if (!Atlas.Created) return null;
            if (Atlas.TryGetCached(key, out var r)) return r;
            BuildSpecialRegions();
            Atlas.TryGetCached(key, out r);
            return r;
        }

        private bool specialsBuilt;

        /// <summary>Composites Steve's front view (head 8x8, body 16x24 with overlays) and a chest front icon from the jar.</summary>
        private void BuildSpecialRegions()
        {
            if (specialsBuilt || SC.Assets == null || !SC.Assets.Ready) return;
            specialsBuilt = true;
            try
            {
                var skin = SC.Assets.GetTexture("entity/player/wide/steve");
                var px = skin.GetPixels32();
                int sw = skin.width, sh = skin.height;
                float k = sw / 64f;
                Color32 P(int x, int y)
                {
                    int xx = Mathf.Clamp((int)(x * k), 0, sw - 1), yy = Mathf.Clamp((int)(y * k), 0, sh - 1);
                    return px[(sh - 1 - yy) * sw + xx];
                }
                var head = new Color32[8 * 8];
                Blit(head, 8, 8, 0, 0, 8, 8, P, 8, 8, false);
                Blit(head, 8, 8, 0, 0, 8, 8, P, 40, 8, true);
                Atlas.AddPixels("slimecraft:steve_head", 8, 8, head);
                var body = new Color32[16 * 24];
                Blit(body, 16, 24, 4, 0, 8, 12, P, 20, 20, false);   // body
                Blit(body, 16, 24, 4, 0, 8, 12, P, 20, 36, true);    // jacket
                Blit(body, 16, 24, 0, 0, 4, 12, P, 44, 20, false);   // right arm (viewer's left)
                Blit(body, 16, 24, 0, 0, 4, 12, P, 44, 36, true);
                Blit(body, 16, 24, 12, 0, 4, 12, P, 36, 52, false);  // left arm
                Blit(body, 16, 24, 12, 0, 4, 12, P, 52, 52, true);
                Blit(body, 16, 24, 4, 12, 4, 12, P, 4, 20, false);   // right leg
                Blit(body, 16, 24, 4, 12, 4, 12, P, 4, 36, true);
                Blit(body, 16, 24, 8, 12, 4, 12, P, 20, 52, false);  // left leg
                Blit(body, 16, 24, 8, 12, 4, 12, P, 4, 52, true);
                Atlas.AddPixels("slimecraft:steve_body", 16, 24, body);
            }
            catch (Exception e) { SC.Log?.LogWarning("[Hud] steve figure: " + e.Message); }
            try
            {
                var chest = SC.Assets.GetTexture("entity/chest/normal");
                var px = chest.GetPixels32();
                int cw = chest.width, ch = chest.height;
                float k = cw / 64f;
                Color32 P(int x, int y)
                {
                    int xx = Mathf.Clamp((int)(x * k), 0, cw - 1), yy = Mathf.Clamp((int)(y * k), 0, ch - 1);
                    return px[(ch - 1 - yy) * cw + xx];
                }
                var icon = new Color32[14 * 15];
                Blit(icon, 14, 15, 0, 0, 14, 5, P, 14, 14, false);  // lid front
                Blit(icon, 14, 15, 0, 5, 14, 10, P, 14, 33, false); // base front
                Blit(icon, 14, 15, 6, 3, 2, 4, P, 1, 1, false);     // latch
                Atlas.AddPixels("slimecraft:chest", 14, 15, icon);
            }
            catch (Exception e) { SC.Log?.LogWarning("[Hud] chest icon: " + e.Message); }
            try { Atlas.AddPixels("slimecraft:vacpack_icon", 16, 16, PixelArt(VacpackArt, VacpackPalette)); }
            catch (Exception e) { SC.Log?.LogWarning("[Hud] vacpack icon: " + e.Message); }
        }

        /// <summary>
        /// 16x16 Minecraft-style item icon of Slime Rancher's vacpack, drawn diagonally like Minecraft's tools:
        /// orange tank with the round pressure gauge bottom-left, glass barrel with cyan rings, dark nozzle top-right.
        /// </summary>
        private static readonly string[] VacpackArt =
        {
            "............KK..",
            "...........KGGK.",
            "..........KGGgBK",
            ".........KCwgBK.",
            "........KWwCwK..",
            ".......KWCwwK...",
            "...KKKKCwwcK....",
            "..KYYOOwCwK.....",
            ".KYYOOOOwK......",
            "KYYCCCOOoK......",
            "KYCBBWCooK......",
            "KOCBWBCooK......",
            "KOCBBBCooK......",
            ".KOCCCooK.......",
            "..KooooK........",
            "...KKKK.........",
        };

        private static readonly Dictionary<char, uint> VacpackPalette = new Dictionary<char, uint>
        {
            { 'K', 0xFF2A221E }, { 'O', 0xFFF28C28 }, { 'o', 0xFFB35A14 }, { 'Y', 0xFFFFC05A },
            { 'C', 0xFF3AE8F2 }, { 'c', 0xFF148FA8 }, { 'W', 0xFFE6FAFC }, { 'w', 0xFF9CCFDA },
            { 'G', 0xFFA8AEB6 }, { 'g', 0xFF5C626A }, { 'B', 0xFF404048 },
        };

        /// <summary>Character grid (top row first) → Unity pixel array (bottom row first); unknown chars are transparent.</summary>
        private static Color32[] PixelArt(string[] rows, Dictionary<char, uint> palette)
        {
            int h = rows.Length, w = rows[0].Length;
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    char ch = x < rows[y].Length ? rows[y][x] : '.';
                    px[(h - 1 - y) * w + x] = palette.TryGetValue(ch, out uint c) ? Argb.ToColor32(c) : new Color32(0, 0, 0, 0);
                }
            return px;
        }

        /// <summary>Copies a w x h block from skin coords (sx,sy, top-down) to dst (dx,dy, top-down); overlay alpha-blends.</summary>
        private static void Blit(Color32[] dst, int dw, int dh, int dx, int dy, int w, int h, Func<int, int, Color32> src, int sx, int sy, bool overlay)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var c = src(sx + x, sy + y);
                    int idx = (dh - 1 - (dy + y)) * dw + dx + x;
                    if (idx < 0 || idx >= dst.Length) continue;
                    if (!overlay) { dst[idx] = c; continue; }
                    if (c.a == 0) continue;
                    if (c.a == 255) { dst[idx] = c; continue; }
                    var b = dst[idx];
                    float t = c.a / 255f;
                    dst[idx] = new Color32((byte)(b.r + (c.r - b.r) * t), (byte)(b.g + (c.g - b.g) * t), (byte)(b.b + (c.b - b.b) * t), (byte)Math.Max(b.a, c.a));
                }
        }

        /// <summary>
        /// Draws Steve facing the viewer inside the entity box of the inventory screens: a flat 2D stand-in for
        /// Minecraft's 3D player preview whose head turns toward the mouse.
        /// </summary>
        internal void DrawPlayerFigure(Gui g, int x0, int y0, int x1, int y1, int pixelScale, int mouseX, int mouseY)
        {
            var head = SpecialRegion("slimecraft:steve_head");
            var body = SpecialRegion("slimecraft:steve_body");
            if (head == null || body == null) return;
            int ps = Math.Max(1, pixelScale);
            int figW = 16 * ps, figH = 32 * ps;
            int fx = (x0 + x1) / 2 - figW / 2;
            int fy = y1 - figH - (y1 - y0 - figH) / 2;
            float centerX = (x0 + x1) / 2f, centerY = (y0 + y1) / 2f;
            float xAngle = Mathf.Atan((centerX - mouseX) / 40f);
            float yAngle = Mathf.Atan((centerY - mouseY) / 40f);
            int hx = Mathf.RoundToInt(-xAngle * 1.2f * ps), hy = Mathf.RoundToInt(-yAngle * 0.8f * ps);
            g.DrawRegion(body, fx, fy + 8 * ps, 16 * ps, 24 * ps, 0, 0, 16, 24, 16, 24);
            g.DrawRegion(head, fx + 4 * ps + hx, fy + hy, 8 * ps, 8 * ps, 0, 0, 8, 8, 8, 8);
        }

        // ================================================================== IHud
        public bool McHudEnabled
        {
            get => HudConfig.McHud;
            set
            {
                if (HudConfig.McHud == value && HudConfig.McHudEnabled != null) { ApplySRHud(); return; }
                HudConfig.McHud = value;
                ApplySRHud();
            }
        }

        private void ApplySRHud()
        {
            try
            {
                if (SC.SR != null && SC.SR.InGame) SC.SR.SetSRHudVisible(!McHudEnabled);
            }
            catch (Exception e) { LogLimited("srhud", "SetSRHudVisible failed: " + e.Message); }
        }

        public void ShowActionBar(string text, float seconds = 2.5f) => Layer?.ShowActionBar(text, seconds);

        public void ShowTitle(string title, string subtitle = null, float seconds = 3f) => Layer?.ShowTitle(title, subtitle, seconds);

        public void AddChat(string message)
        {
            if (message == null) return;
            Chat.Post(message, TickCount, Font, screen is ChatInputScreen);
        }

        public void OnPlayerDamaged() => Layer?.PlayerHurt();

        private void OnSRPlayerDamaged(int lost, GameObject source)
        {
            try { OnPlayerDamaged(); } catch (Exception e) { LogLimited("damaged", "OnPlayerDamaged failed: " + e.Message); }
        }

        public bool ScreenOpen => screen != null;

        public bool DebugScreenVisible { get => DebugVisible; set => DebugVisible = value; }

        // ================================================================== logging
        private static readonly Dictionary<string, float> lastLog = new Dictionary<string, float>();

        /// <summary>Error logging limited to once per 10 s per key (never spam the log every frame).</summary>
        internal static void LogLimited(string key, string message)
        {
            float now = Time.unscaledTime;
            if (lastLog.TryGetValue(key, out float t) && now - t < 10f) return;
            lastLog[key] = now;
            SC.Log?.LogError("[Hud] " + message);
        }
    }
}
