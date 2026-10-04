using System;
using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>
    /// FirstPerson module (Order 400): Steve's first-person right arm and the held Minecraft item, rendered on top of
    /// Slime Rancher's world by an overlay camera, with the timing and poses of Minecraft's first-person hand
    /// animations at 20 ticks/s and partial-tick interpolation. Implements <see cref="IFirstPerson"/> (SC.FirstPerson).
    /// </summary>
    public sealed class FirstPersonModule : MonoBehaviour, IModule, IFirstPerson
    {
        private const float TickLen = 0.05f; // 20 ticks per second

        public string ModuleName => "FirstPerson";
        public int Order => 400;

        private FpConfig cfg;
        private readonly ItemInHand hand = new ItemInHand();
        private readonly HandRig rig = new HandRig();
        private HeldItemVisuals visuals;
        private EatParticles particles;
        private readonly System.Random rng = new System.Random();

        // arm mesh / material
        private Mesh armMesh;
        private Material armMaterial;
        private bool armDirty = true;
        private float armRetryAt;

        // world/tick state
        private bool worldActive;
        private int chosenLayer = -1;
        private float tickAcc;
        private int lastHealth;
        private bool pendingHurt;      // ISRBridge.PlayerDamaged since the last tick
        private float pendingHurtDir;
        private Vector3 lastFeet;
        private float lastYawRaw;
        private bool haveYaw;

        // renderer toggles (avoid redundant enabled writes)
        private bool armOn, itemOn;

        // one-time (per world) screen-bounds log of each held visual at rest, for in-game verification
        private readonly System.Collections.Generic.HashSet<string> boundsLogged = new System.Collections.Generic.HashSet<string>();
        private readonly Vector3[] boundsCorners = new Vector3[8];

        // bow FOV zoom
        private bool fovActive;
        private float fovBase;
        private Camera fovCam;

        // ------------------------------------------------------------------ IModule
        public void Init()
        {
            cfg = new FpConfig();
            visuals = new HeldItemVisuals(cfg);
            particles = new EatParticles(visuals);
            cfg.ArmSettingsChanged += () => { armDirty = true; armRetryAt = 0f; };
            SC.FirstPerson = this;
            SC.AllModulesInitialized += OnAllModulesInitialized;
        }

        private void OnAllModulesInitialized()
        {
            var sr = SC.SR;
            if (sr == null)
            {
                FpLog.Warn("SC.SR is null: first-person hand disabled until the SR bridge is available");
                return;
            }
            sr.WorldLoaded += OnWorldLoaded;
            sr.WorldUnloading += OnWorldUnloading;
            sr.PlayerDamaged += OnPlayerDamaged;
        }

        private void OnWorldLoaded()
        {
            worldActive = false; // re-initialised on the next frame
            chosenLayer = -1;
            boundsLogged.Clear();
        }

        private void OnWorldUnloading()
        {
            try
            {
                RestoreFov();
                rig.Destroy();
                particles?.Destroy();
                visuals?.ClearLookups();
                armOn = itemOn = false;
                worldActive = false;
                chosenLayer = -1;
            }
            catch (Exception e) { FpLog.Error("WorldUnloading", e); }
        }

        private void OnDestroy()
        {
            try { RestoreFov(); rig.Destroy(); } catch { }
        }

        // ------------------------------------------------------------------ IFirstPerson
        public float MiningProgress
        {
            get { return hand.MiningProgress; }
            set { hand.MiningProgress = value; }
        }

        public void Swing()
        {
            try
            {
                // an attack swing (left mouse down, not a right-click use) resets the attack-strength dip
                var input = SC.Input;
                bool attack = input != null && (input.AttackPressed || input.AttackHeld);
                bool use = input != null && (input.UsePressed || input.UseHeld);
                hand.Swing(attack && !(use && input.UsePressed));
            }
            catch (Exception e) { FpLog.Error("Swing", e); }
        }

        public void ItemUsed()
        {
            try { hand.ItemUsed(); }
            catch (Exception e) { FpLog.Error("ItemUsed", e); }
        }

        /// <summary>
        /// ISRBridge.PlayerDamaged: works out the hurt tilt direction as the angle of the damage source around the
        /// player relative to the view yaw (0 = source on the left, 90 = in front, 180 = right, -90 = behind).
        /// </summary>
        private void OnPlayerDamaged(int lost, GameObject source)
        {
            try
            {
                float dir = 0f;
                var sr = SC.SR;
                var cam = sr != null ? sr.MainCamera : null;
                var player = sr != null ? sr.Player : null;
                if (source != null && cam != null && (player == null || (source != player && !source.transform.IsChildOf(player.transform))))
                {
                    Vector3 d = source.transform.position - cam.transform.position;
                    Vector3 f = cam.transform.forward, r = cam.transform.right;
                    d.y = 0f; f.y = 0f; r.y = 0f;
                    if (d.sqrMagnitude > 1e-4f && f.sqrMagnitude > 1e-4f && r.sqrMagnitude > 1e-4f)
                        dir = Mathf.Atan2(Vector3.Dot(d, f.normalized), -Vector3.Dot(d, r.normalized)) * Mathf.Rad2Deg;
                }
                pendingHurt = true;
                pendingHurtDir = dir;
            }
            catch (Exception e) { FpLog.Error("PlayerDamaged", e); }
        }

        public void StartUsing(UseAnimation anim)
        {
            try { hand.StartUsing(anim, SelectedId(SC.Inventory), NowTicks()); }
            catch (Exception e) { FpLog.Error("StartUsing", e); }
        }

        public void StopUsing()
        {
            try { hand.StopUsing(); }
            catch (Exception e) { FpLog.Error("StopUsing", e); }
        }

        private float NowTicks() { return hand.TickCount + tickAcc / TickLen; }

        // ------------------------------------------------------------------ frame
        private void LateUpdate()
        {
            if (cfg == null) return;
            try { Frame(); }
            catch (Exception e)
            {
                FpLog.Error("Frame", e);
                try { HideAll(); } catch { }
            }
        }

        private void Frame()
        {
            var sr = SC.SR;
            if (!cfg.Enabled.Value || sr == null || !sr.InGame)
            {
                HideAll();
                RestoreFov();
                if (worldActive) { worldActive = false; particles.Clear(); }
                return;
            }
            Camera main = sr.MainCamera;
            if (main == null || !main.isActiveAndEnabled)
            {
                HideAll();
                return;
            }

            if (!rig.IsBuiltFor(main))
            {
                if (chosenLayer < 0) chosenLayer = HandRig.ChooseLayer(cfg.HandLayer.Value);
                rig.Build(main, chosenLayer, cfg.HandFov.Value, cfg.CameraDepthOverride.Value);
                armOn = itemOn = false;
            }

            UpdateLook(main);
            if (!worldActive) InitWorldState(sr);

            // ---- 20 Hz game ticks (Time.deltaTime is 0 while SR is paused)
            float dt = Time.deltaTime;
            if (dt < 0f) dt = 0f;
            if (dt > 0.25f) dt = 0.25f;
            tickAcc += dt;
            int guard = 0;
            while (tickAcc >= TickLen && guard++ < 10)
            {
                tickAcc -= TickLen;
                GameTick(sr, main);
            }
            if (tickAcc >= TickLen) tickAcc = 0f;
            float partial = Mathf.Clamp01(tickAcc / TickLen);
            float now = hand.TickCount + partial;

            rig.Maintain(main, cfg.HandFov.Value, cfg.CameraDepthOverride.Value);

            // ---- visibility
            string vis = hand.VisibleId;
            bool show = !sr.IsPaused && !sr.SRUIOpen && SC.Inventory != null && vis != Content.Vacpack;
            if (!show)
            {
                SetRenderers(false, false);
                rig.SetCameraEnabled(false);
            }
            else
            {
                bool viewBob = cfg.ViewBobbing.Value && !(cfg.RespectSRCameraBobOption.Value && SRCameraBobDisabled());
                McPose basePose = hand.BasePose(partial, viewBob, cfg.HurtTilt.Value, cfg.HandSway.Value, cfg.MiningShake.Value);
                if (string.IsNullOrEmpty(vis))
                {
                    if (EnsureArm())
                    {
                        McPose arm = hand.ArmPose(basePose, partial, hand.InverseArmHeight(partial, 1f));
                        arm.ApplyTo(rig.Arm);
                        SetRenderers(true, false);
                        LogScreenBounds("empty hand (arm)", rig.ArmFilter, rig.ArmRenderer, partial, 1f);
                    }
                    else SetRenderers(false, false);
                }
                else
                {
                    RenderItem(vis, basePose, partial, now);
                }
                rig.SetCameraEnabled(armOn || itemOn);
            }

            particles.Frame(partial, main.transform);
            UpdateFov(main, partial);
        }

        private void RenderItem(string id, McPose basePose, float partial, float now)
        {
            ItemDef def = Content.Item(id);
            int bowStage = (hand.Using && hand.UseAnim == UseAnimation.Bow) ? hand.BowPullStage : -1;
            Mesh mesh; Material mat; Quaternion corr;
            if (!visuals.TryGet(id, def, bowStage, out mesh, out mat, out corr))
            {
                SetRenderers(false, false);
                return;
            }
            ItemModelInfo info = ItemDisplayResolver.Get(id);
            McPose a, b; Vector3 pull;
            hand.ItemPoses(basePose, partial, now, hand.InverseArmHeight(partial, info.SwapAnimationScale), info.FirstPersonRight, out a, out pull, out b);
            a.ApplyTo(rig.ItemA);
            rig.ItemPull.localScale = pull;
            b.ApplyTo(rig.ItemB);
            rig.ItemDisplay.localScale = info.FirstPersonRight.Scale;
            rig.ItemMesh.localRotation = corr;
            if (rig.ItemFilter.sharedMesh != mesh) rig.ItemFilter.sharedMesh = mesh;
            if (rig.ItemRenderer.sharedMaterial != mat) rig.ItemRenderer.sharedMaterial = mat;
            SetRenderers(false, true);
            LogScreenBounds(id, rig.ItemFilter, rig.ItemRenderer, partial, info.SwapAnimationScale);
        }

        /// <summary>
        /// Once per visual and world, when the hand is fully raised and idle, logs where the visual sits on screen
        /// (viewport % of the hand camera, y from the top like a screenshot), so the pose can be checked against Minecraft
        /// from the log alone. The 8 corners of the mesh's local bounding box are projected (independent of where the
        /// player looks; values beyond 0..100 are off screen). Reference values measured in Minecraft at FOV 70 / 16:9:
        /// flat/handheld items x 72..183 y 31..225, block items x 65..101 y 73..168, empty arm x 70..112 y 68..192.
        /// </summary>
        private void LogScreenBounds(string what, MeshFilter mf, Renderer r, float partial, float swapScale)
        {
            if (r == null || mf == null || mf.sharedMesh == null || rig.Cam == null || boundsLogged.Contains(what)) return;
            if (hand.Using || hand.AttackAnim(partial) > 0f || hand.InverseArmHeight(partial, swapScale) > 0.001f || hand.MiningProgress >= 0f) return;
            boundsLogged.Add(what);
            try
            {
                Bounds b = mf.sharedMesh.bounds;
                Matrix4x4 m = mf.transform.localToWorldMatrix;
                Vector3 c = b.center, e = b.extents;
                int k = 0;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                            boundsCorners[k++] = m.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3(sx, sy, sz)));
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 v = rig.Cam.WorldToViewportPoint(boundsCorners[i]);
                    if (v.z <= 0f) continue;
                    minX = Mathf.Min(minX, v.x); maxX = Mathf.Max(maxX, v.x);
                    minY = Mathf.Min(minY, 1f - v.y); maxY = Mathf.Max(maxY, 1f - v.y);
                }
                FpLog.Info("At rest, " + what + " mesh box covers screen x " + Pct(minX) + ".." + Pct(maxX) + " %, y " + Pct(minY) + ".." + Pct(maxY)
                    + " % from top-left (hand fov " + rig.Cam.fieldOfView.ToString("0.#") + ", aspect " + rig.Cam.aspect.ToString("0.###")
                    + ", layer " + r.gameObject.layer + ", material '" + (r.sharedMaterial != null ? r.sharedMaterial.name : "null") + "')");
            }
            catch (Exception ex) { FpLog.Error("LogScreenBounds", ex, 60f); }
        }

        private static string Pct(float v) { return Mathf.RoundToInt(Mathf.Clamp(v, -9.99f, 9.99f) * 100f).ToString(); }

        private void GameTick(ISRBridge sr, Camera main)
        {
            var input = new TickInput();
            input.SelectedId = SelectedId(SC.Inventory);
            input.SelectedDef = input.SelectedId != null ? Content.Item(input.SelectedId) : null;
            input.Grounded = sr.PlayerGrounded;
            Vector3 feet = sr.PlayerFeet;
            Vector3 d = feet - lastFeet;
            d.y = 0f;
            float moved = d.magnitude;
            input.HorizontalMoved = moved > 3f ? 0f : moved; // teleports don't bob
            lastFeet = feet;
            int hp = sr.Health;
            input.Hurt = hp < lastHealth || pendingHurt;
            input.HurtDir = pendingHurt ? pendingHurtDir : 0f;
            pendingHurt = false;
            pendingHurtDir = 0f;
            lastHealth = hp;

            hand.Tick(ref input);
            particles.Tick();

            if (hand.EmitEatEffects) EmitEatEffects(sr, main, input.SelectedDef);
        }

        /// <summary>One eating "bite": 5 item crumbs (food only) plus the eat/drink sound (optional).</summary>
        private void EmitEatEffects(ISRBridge sr, Camera main, ItemDef def)
        {
            if (def == null) return;
            Vector3 eye = main.transform.position;
            if (cfg.EatParticles.Value && !hand.IsDrinking)
                particles.Spawn(def, eye, hand.XRot, main.transform.eulerAngles.y, 5);
            if (cfg.EatSounds.Value && SC.Audio != null)
            {
                float vol = 0.5f + 0.5f * rng.Next(2);
                float pitch = ((float)rng.NextDouble() - (float)rng.NextDouble()) * 0.2f + 1f;
                SC.Audio.Play(hand.IsDrinking ? "item.honey_bottle.drink" : "entity.generic.eat", eye, vol, pitch);
            }
        }

        private void InitWorldState(ISRBridge sr)
        {
            hand.Reset(SelectedId(SC.Inventory));
            lastHealth = sr.Health;
            pendingHurt = false;
            lastFeet = sr.PlayerFeet;
            tickAcc = 0f;
            particles.Clear();
            worldActive = true;
        }

        /// <summary>Camera pitch (down positive) and unwrapped yaw (right positive), Minecraft xRot/yRot semantics.</summary>
        private void UpdateLook(Camera main)
        {
            Vector3 e = main.transform.eulerAngles;
            float pitch = e.x > 180f ? e.x - 360f : e.x;
            float yawRaw = e.y;
            if (haveYaw) hand.YRot += Mathf.DeltaAngle(lastYawRaw, yawRaw);
            else hand.YRot = yawRaw;
            lastYawRaw = yawRaw;
            haveYaw = true;
            hand.XRot = pitch;
            if (Mathf.Abs(hand.YRot) > 7200f) hand.RebaseYaw(Mathf.Round(hand.YRot / 360f) * 360f);
        }

        private static string SelectedId(IPlayerInventory inv)
        {
            if (inv == null) return null;
            if (inv.VacpackSelected) return Content.Vacpack;
            var st = inv.Selected;
            return (st == null || st.IsEmpty) ? null : st.Id;
        }

        // ------------------------------------------------------------------ arm
        private bool EnsureArm()
        {
            if (armDirty || armMesh == null || armMaterial == null)
            {
                if (Time.realtimeSinceStartup < armRetryAt) return false;
                armRetryAt = Time.realtimeSinceStartup + 1f;
                var assets = SC.Assets;
                if (assets == null || !assets.Ready) return false;
                string path = cfg.SkinTexture.Value;
                if (string.IsNullOrEmpty(path)) path = "entity/player/wide/steve";
                bool slim = path.Contains("/slim/");
                var tex = assets.GetTexture(path);
                if (tex == null) return false;
                var mat = HeldItemVisuals.CreateLit(tex);
                if (mat == null) return false;
                var mesh = PlayerArmMesh.Build(slim, cfg.ShowSleeve.Value);
                if (armMesh != null) Destroy(armMesh);
                // the old material is not destroyed: IItemVisuals may share/cache the materials it creates
                armMesh = mesh;
                armMaterial = mat;
                armDirty = false;
                FpLog.Info("Arm built from " + path + (slim ? " (slim)" : " (wide)") + ", material shader " + (mat.shader != null ? mat.shader.name : "?"));
            }
            if (rig.ArmFilter.sharedMesh != armMesh) rig.ArmFilter.sharedMesh = armMesh;
            if (rig.ArmRenderer.sharedMaterial != armMaterial) rig.ArmRenderer.sharedMaterial = armMaterial;
            return true;
        }

        // ------------------------------------------------------------------ helpers
        private void SetRenderers(bool arm, bool item)
        {
            if (rig.ArmRenderer != null && armOn != arm) { rig.ArmRenderer.enabled = arm; }
            if (rig.ItemRenderer != null && itemOn != item) { rig.ItemRenderer.enabled = item; }
            armOn = arm; itemOn = item;
        }

        private void HideAll()
        {
            if (rig.Cam == null) { armOn = itemOn = false; return; }
            SetRenderers(false, false);
            rig.SetCameraEnabled(false);
        }

        private static bool SRCameraBobDisabled()
        {
            try
            {
                var gc = SRSingleton<GameContext>.Instance;
                return gc != null && gc.OptionsDirector != null && gc.OptionsDirector.disableCameraBob;
            }
            catch { return false; }
        }

        /// <summary>Field of view: SR's FOV option × the (interpolated) bow zoom factor while drawing.</summary>
        private void UpdateFov(Camera main, float partial)
        {
            float mod = cfg.BowFovZoom.Value ? Mathf.Lerp(hand.OFovMod, hand.FovMod, partial) : 1f;
            bool want = Mathf.Abs(mod - 1f) > 1e-4f;
            if (want)
            {
                if (!fovActive || fovCam != main)
                {
                    RestoreFov();
                    fovCam = main;
                    fovBase = BaseFov(main);
                    fovActive = true;
                }
                main.fieldOfView = fovBase * mod;
            }
            else if (fovActive) RestoreFov();
        }

        /// <summary>SR's FOV option (OptionsDirector.GetFOV) if this camera is driven by a CameraFOVAdjuster, else -1.</summary>
        private static float OptionsFov(Camera cam)
        {
            try
            {
                if (cam != null && cam.GetComponent<CameraFOVAdjuster>() != null)
                {
                    var gc = SRSingleton<GameContext>.Instance;
                    if (gc != null && gc.OptionsDirector != null) return gc.OptionsDirector.GetFOV();
                }
            }
            catch { }
            return -1f;
        }

        private static float BaseFov(Camera main)
        {
            float f = OptionsFov(main);
            return f > 0f ? f : main.fieldOfView;
        }

        private void RestoreFov()
        {
            if (!fovActive) return;
            fovActive = false;
            try
            {
                if (fovCam != null)
                {
                    float f = OptionsFov(fovCam);
                    fovCam.fieldOfView = f > 0f ? f : fovBase;
                }
            }
            catch { }
            fovCam = null;
        }
    }
}
