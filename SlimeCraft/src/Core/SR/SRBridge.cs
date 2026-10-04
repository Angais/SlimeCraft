using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SlimeCraft.Core
{
    /// <summary>
    /// The Core implementation of <see cref="ISRBridge"/> (<c>SC.SR</c>): the one place where SlimeCraft talks to
    /// Slime Rancher about the loaded world, the player, time and zones, physics layers, actors and the vacpack/HUD.
    ///
    /// World lifecycle: <see cref="Tick"/> notices when a save has finished loading (world scene active, player
    /// present, SR no longer loading) and raises <see cref="WorldLoaded"/>; unloading is raised exactly once per world
    /// from whichever comes first of SR's session-end patch, Unity's scene-unloaded callback or the SceneContext
    /// disappearing. Everything stays readable while the unload listeners run so they can still save.
    ///
    /// Every public member is safe outside a world: it returns a neutral default and never throws.
    /// </summary>
    internal sealed class SRBridge : ISRBridge
    {
        // ------------------------------------------------------------------ helpers and hooks used inside Core

        /// <summary>SR vacpack visibility / blocking helper.</summary>
        public readonly SRVacpack Vacpack = new SRVacpack();

        /// <summary>Runs after <see cref="WorldLoaded"/> (persistence load, late Core setup).</summary>
        internal Action AfterWorldLoaded;
        /// <summary>Runs before <see cref="WorldUnloading"/> while the world is still readable (persistence save).</summary>
        internal Action BeforeWorldUnloading;
        /// <summary>Runs after <see cref="WorldUnloading"/> (Core cleanup).</summary>
        internal Action AfterWorldUnloading;
        /// <summary>Runs after <see cref="Saving"/> (persistence save).</summary>
        internal Action AfterSaving;

        public event Action WorldLoaded;
        public event Action WorldUnloading;
        public event Action Saving;
        public event Action<int, GameObject> PlayerDamaged;

        // ------------------------------------------------------------------ tuning facts

        private const float ZonePollInterval = 0.5f;
        private const float RebindInterval = 2f;
        private const float CameraRetryInterval = 1f;
        private const float EyeHeight = 1.6f;
        private const float DefaultDayFraction = 0.375f;   // 09:00
        private const float MaxLookPitch = 89f;
        private const string ExplosionSourceName = "SlimeCraft_ExplosionSource";

        // UFPS controller facts (per physics step at the controller's 60 Hz reference rate)
        private const float StepsPerSecond = 60f;
        private const float MaxRisePerStep = 0.09f;        // SR's cap on upward fall speed
        private const float MinThrottle = 0.002f;          // just above the controller's 0.001 anti-bump threshold
        private const float MaxThrottle = 3f;

        private static readonly string[] WorldLayerHints =
            { "terrain", "ground", "mountain", "static", "platform", "environment", "rock", "building" };


        // ------------------------------------------------------------------ state

        private readonly SRHudHider hud = new SRHudHider();

        private bool worldLoaded;
        private bool unloadRaised;
        private int liveContextId;
        private int deadContextId;      // the context we last unloaded; it may linger for one more frame
        private string worldSceneName;

        private SceneContext context;
        private GameObject player;
        private PlayerState playerState;
        private vp_FPController motor;
        private CharacterController capsule;
        private vp_FPPlayerEventHandler playerEvents;
        private Damageable playerHealth;
        private Camera viewCamera;
        private string saveId;

        private string zoneName = "";
        private readonly Dictionary<ZoneDirector.Zone, string> zoneNameCache = new Dictionary<ZoneDirector.Zone, string>();

        private float nextZonePoll;
        private float nextRebind;
        private float nextCameraRetry;

        private int blockLayer = 0;
        private int worldMask = 1;
        private readonly RaycastHit[] rayHits = new RaycastHit[32];

        private GameObject explosionSource;
        private Collider[] blastBuffer = new Collider[256];
        private readonly HashSet<Rigidbody> blastPushed = new HashSet<Rigidbody>();

        private Dictionary<string, Identifiable.Id> actorIdsByName;

        // lazily resolved private UFPS fields used for vertical impulses
        private bool motorFieldsResolved;
        private AccessTools.FieldRef<vp_Controller, float> fallSpeedField;
        private AccessTools.FieldRef<vp_FPController, Vector3> throttleField;

        public SRBridge()
        {
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        // ================================================================== lifecycle

        /// <summary>Once per rendered frame from <see cref="CoreModule"/>.</summary>
        public void Tick()
        {
            try
            {
                var ctx = SRSingleton<SceneContext>.Instance;

                if (worldLoaded && (ctx == null || ctx.GetInstanceID() != liveContextId || ctx.Player == null))
                    RaiseWorldUnloading("world gone");

                if (!worldLoaded)
                {
                    if (WorldIsReady(ctx) && ctx.GetInstanceID() != deadContextId) EnterWorld(ctx);
                    if (!worldLoaded) return;
                }

                float now = Time.unscaledTime;
                if (now >= nextZonePoll)
                {
                    nextZonePoll = now + ZonePollInterval;
                    RefreshZone();
                }
                if (now >= nextRebind)
                {
                    nextRebind = now + RebindInterval;
                    if (viewCamera == null) viewCamera = LocateCamera();
                    if (Vacpack.Vac == null && player != null)
                    {
                        var vac = player.GetComponentInChildren<WeaponVacuum>(true);
                        if (vac != null) Vacpack.Bind(vac);
                    }
                }

                hud.Tick(liveContextId);
                hud.SetAmmoVisible(Vacpack.Active);
                Vacpack.Enforce();
            }
            catch (Exception e)
            {
                CoreLog.Rate("bridge tick", e);
            }
        }

        private static bool WorldIsReady(SceneContext ctx)
        {
            if (ctx == null || ctx.Player == null || ctx.PlayerState == null) return false;
            if (SceneManager.GetActiveScene().name != Levels.WORLD) return false;
            var game = SRSingleton<GameContext>.Instance;
            if (game == null) return false;
            var saves = game.AutoSaveDirector;
            return saves != null && !saves.IsLoadingGame();
        }

        private void EnterWorld(SceneContext ctx)
        {
            // 1. cache the world and the player rig
            context = ctx;
            liveContextId = ctx.GetInstanceID();
            player = ctx.Player;
            playerState = ctx.PlayerState;
            motor = player.GetComponent<vp_FPController>();
            if (motor == null) motor = player.GetComponentInChildren<vp_FPController>();
            capsule = player.GetComponent<CharacterController>();
            if (capsule == null) capsule = player.GetComponentInChildren<CharacterController>();
            playerEvents = player.GetComponentInChildren<vp_FPPlayerEventHandler>();
            playerHealth = player.GetInterfaceComponent<Damageable>();

            // 2. camera
            viewCamera = LocateCamera();

            // 3. vacpack
            var vac = player.GetComponentInChildren<WeaponVacuum>(true);
            if (vac != null) Vacpack.Bind(vac);
            else CoreLog.Warn("WeaponVacuum not found on the player");

            // 4. save id
            saveId = ReadSaveName();

            // 5. layers
            try { ChooseLayers(); }
            catch (Exception e) { CoreLog.Rate("layers", e); }

            // 6. SR HUD binding
            hud.Unbind();
            hud.Refresh(liveContextId);

            // 7. materials made before SR's template was available
            try { MaterialFactory.TryUpgrade(); }
            catch (Exception e) { CoreLog.Rate("material upgrade", e); }

            // 8. zone (the player may have switched SR's language in the menu, so names are re-resolved)
            zoneName = "";
            zoneNameCache.Clear();
            RefreshZone();

            // 9-10. remember the scene, mark loaded
            worldSceneName = SceneManager.GetActiveScene().name;
            worldLoaded = true;
            unloadRaised = false;

            // 11. log
            CoreLog.Info("World loaded: save '" + (saveId ?? "?") + "', zone " + zoneName + ", camera " +
                         (viewCamera != null ? viewCamera.name : "none"));

            // 12-13. notify
            NotifyEach(WorldLoaded, "WorldLoaded");
            RunHook(AfterWorldLoaded, "Core persistence load");
        }

        private static string ReadSaveName()
        {
            try
            {
                string name = SRSingleton<GameContext>.Instance.AutoSaveDirector.SavedGame.GetName();
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch (Exception e)
            {
                CoreLog.Warn("Could not read save name: " + e.Message);
                return null;
            }
        }

        /// <summary>Raises unloading for the current world (at most once per world).</summary>
        internal void RaiseWorldUnloading(string why)
        {
            if (!worldLoaded || unloadRaised) return;
            unloadRaised = true;
            CoreLog.Info("World unloading (" + why + ")");
            try
            {
                RunHook(BeforeWorldUnloading, "Core persistence save");
                NotifyEach(WorldUnloading, "WorldUnloading");
                RunHook(AfterWorldUnloading, "Core unload cleanup");
            }
            finally
            {
                LeaveWorld();
            }
        }

        /// <summary>Forgets everything about the world that just ended. Runs only after every unload listener.</summary>
        private void LeaveWorld()
        {
            worldLoaded = false;
            deadContextId = liveContextId;
            liveContextId = 0;

            context = null;
            player = null;
            motor = null;
            capsule = null;
            playerEvents = null;
            viewCamera = null;
            playerState = null;
            playerHealth = null;
            saveId = null;

            try { Vacpack.Unbind(); } catch (Exception e) { CoreLog.Rate("vacpack unbind", e); }
            try { hud.Unbind(); } catch (Exception e) { CoreLog.Rate("hud unbind", e); }
            CoreRuntime.OpenUIs.Clear();

            if (explosionSource != null) Object.Destroy(explosionSource);
            explosionSource = null;
        }

        private void OnSceneUnloaded(Scene scene)
        {
            try
            {
                if (worldLoaded && worldSceneName != null && scene.name == worldSceneName)
                    RaiseWorldUnloading("scene unloaded");
            }
            catch (Exception e)
            {
                CoreLog.Rate("scene unload", e);
            }
        }

        /// <summary>SR is about to write its save file.</summary>
        internal void RaiseSaving()
        {
            if (!worldLoaded || unloadRaised) return;
            NotifyEach(Saving, "Saving");
            RunHook(AfterSaving, "Core persistence save");
        }

        /// <summary>The player really lost <paramref name="lost"/> health through SR's damage path.</summary>
        internal void RaisePlayerDamaged(int lost, GameObject source)
        {
            if (!InGame || lost <= 0) return;
            var handlers = PlayerDamaged;
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<int, GameObject>)d)(lost, source);
                }
                catch (Exception e)
                {
                    string owner = d.Method.DeclaringType != null ? d.Method.DeclaringType.Name : "?";
                    CoreLog.Rate("PlayerDamaged handler " + owner, e);
                }
            }
        }

        /// <summary>Invokes each subscriber on its own so one failing module never silences the others.</summary>
        private static void NotifyEach(Action handlers, string eventName)
        {
            if (handlers == null) return;
            foreach (var d in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)d)();
                }
                catch (Exception e)
                {
                    var m = d.Method;
                    string who = (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "." + m.Name;
                    SC.Log?.LogError(eventName + " handler " + who + " failed: " + e);
                }
            }
        }

        private static void RunHook(Action hook, string what)
        {
            if (hook != null) SC.Safe(hook, what);
        }

        // ================================================================== state queries

        public bool InGame => worldLoaded && player != null && context != null;

        public bool IsPaused
        {
            get
            {
                try
                {
                    if (Time.timeScale <= 0f) return true;
                    var pause = SRSingleton<PauseMenu>.Instance;
                    return pause != null && pause.pauseUI != null && pause.pauseUI.activeSelf;
                }
                catch { return false; }
            }
        }

        public bool SRUIOpen
        {
            get
            {
                if (!InGame) return false;
                try
                {
                    if (CoreRuntime.AnySRWindowOpen()) return true;
                    var deathLock = SRSingleton<LockOnDeath>.Instance;
                    if (deathLock != null && deathLock.Locked()) return true;

                    // our own screens put SR into a non-default input mode themselves; that must not count here
                    var gate = CoreRuntime.Gate;
                    if (gate != null && gate.HoldsSRInputMode) return false;
                    var input = SRInput.Instance;
                    return input != null && input.GetInputMode() != SRInput.InputMode.DEFAULT;
                }
                catch { return false; }
            }
        }

        public GameObject Player => InGame ? player : null;

        public Camera MainCamera
        {
            get
            {
                if (!InGame) return null;
                if (viewCamera == null && Time.unscaledTime >= nextCameraRetry)
                {
                    nextCameraRetry = Time.unscaledTime + CameraRetryInterval;
                    viewCamera = LocateCamera();
                }
                return viewCamera != null ? viewCamera : null;
            }
        }

        private Camera LocateCamera()
        {
            if (player != null)
            {
                var fpCam = player.GetComponentInChildren<vp_FPCamera>(true);
                if (fpCam != null)
                {
                    var cam = fpCam.GetComponent<Camera>();
                    if (cam != null) return cam;
                }
            }
            return Camera.main;
        }

        public Vector3 EyePosition
        {
            get
            {
                var cam = MainCamera;
                if (cam != null) return cam.transform.position;
                var p = Player;
                return p != null ? p.transform.position + Vector3.up * EyeHeight : Vector3.zero;
            }
        }

        public Vector3 LookDirection
        {
            get
            {
                var cam = MainCamera;
                return cam != null ? cam.transform.forward : Vector3.forward;
            }
        }

        private bool CapsuleUsable => capsule != null && capsule.enabled;

        public Vector3 PlayerFeet
        {
            get
            {
                if (!InGame) return Vector3.zero;
                var pos = player.transform.position;
                if (CapsuleUsable) pos.y = capsule.bounds.min.y;
                return pos;
            }
        }

        public Vector3 PlayerVelocity => InGame && motor != null ? motor.Velocity : Vector3.zero;

        public bool PlayerGrounded => InGame && motor != null && motor.Grounded;

        public Bounds PlayerBounds
        {
            get
            {
                if (!InGame) return new Bounds();
                if (CapsuleUsable) return capsule.bounds;
                return new Bounds(player.transform.position + Vector3.up, new Vector3(0.6f, 2f, 0.6f));
            }
        }

        public int Health => ReadStat(ps => ps.GetCurrHealth());
        public int MaxHealth => ReadStat(ps => ps.GetMaxHealth());
        public int Energy => ReadStat(ps => ps.GetCurrEnergy());
        public int MaxEnergy => ReadStat(ps => ps.GetMaxEnergy());
        public int Newbucks => ReadStat(ps => ps.GetCurrency());

        private int ReadStat(Func<PlayerState, int> read)
        {
            if (!InGame || playerState == null) return 0;
            try { return read(playerState); }
            catch { return 0; }
        }

        // ================================================================== player control

        public void TeleportPlayer(Vector3 feetPosition)
        {
            if (!InGame) return;
            try
            {
                float lift = CapsuleUsable ? player.transform.position.y - capsule.bounds.min.y : 0f;
                var target = feetPosition + Vector3.up * lift;

                var setter = playerEvents != null && playerEvents.Position != null ? playerEvents.Position.Set : null;
                if (setter != null) setter(target);
                player.transform.position = target;
                if (motor != null)
                {
                    motor.Stop();
                    motor.SetPosition(target);
                }
            }
            catch (Exception e)
            {
                CoreLog.Rate("TeleportPlayer", e);
            }
        }

        public void SetLook(float pitch, float yaw)
        {
            if (!InGame) return;
            try
            {
                var angles = new Vector2(Mathf.Clamp(pitch, -MaxLookPitch, MaxLookPitch), yaw);
                if (playerEvents == null) playerEvents = player.GetComponentInChildren<vp_FPPlayerEventHandler>();

                var setter = playerEvents != null && playerEvents.Rotation != null ? playerEvents.Rotation.Set : null;
                if (setter != null)
                {
                    setter(angles);
                    return;
                }
                var fpCam = player.GetComponentInChildren<vp_FPCamera>(true);
                if (fpCam != null) fpCam.Angle = angles;
            }
            catch (Exception e)
            {
                CoreLog.Rate("SetLook", e);
            }
        }

        public void DamagePlayer(int amount, GameObject source)
        {
            if (!InGame || amount <= 0) return;
            try
            {
                if (!IsAlive(playerHealth)) playerHealth = player.GetInterfaceComponent<Damageable>();
                if (IsAlive(playerHealth))
                {
                    // SR's own path: hurt sound, red overlay, camera shake, death screen
                    if (playerHealth.Damage(amount, source))
                        DeathHandler.Kill(player, DeathHandler.Source.UNDEFINED, source, "SlimeCraft.DamagePlayer");
                    return;
                }

                if (playerState == null) return;
                int before = playerState.GetCurrHealth();
                int after = Math.Max(0, before - amount);
                playerState.SetHealth(after);
                RaisePlayerDamaged(before - after, source);
            }
            catch (Exception e)
            {
                CoreLog.Rate("DamagePlayer", e);
            }
        }

        public void HealPlayer(int amount)
        {
            if (!InGame || amount == 0) return;
            if (amount < 0)
            {
                DamagePlayer(-amount, null);
                return;
            }
            try { if (playerState != null) playerState.Heal(amount); }
            catch (Exception e) { CoreLog.Rate("HealPlayer", e); }
        }

        public void AddEnergy(int amount)
        {
            if (!InGame || amount == 0 || playerState == null) return;
            try
            {
                if (amount > 0) playerState.SetEnergy(Math.Min(playerState.GetMaxEnergy(), playerState.GetCurrEnergy() + amount));
                else playerState.SpendEnergy(-amount);
            }
            catch (Exception e)
            {
                CoreLog.Rate("AddEnergy", e);
            }
        }

        public void AddNewbucks(int amount)
        {
            if (!InGame || amount == 0 || playerState == null) return;
            try
            {
                if (amount > 0)
                {
                    playerState.AddCurrency(amount);
                    return;
                }
                int spend = Math.Min(-amount, playerState.GetCurrency());
                if (spend > 0) playerState.SpendCurrency(spend);
            }
            catch (Exception e)
            {
                CoreLog.Rate("AddNewbucks", e);
            }
        }

        /// <summary>
        /// Adds a velocity (m/s) to the player. The UFPS controller moves by (external force + motor throttle +
        /// fall speed) per step at a 60 Hz reference, so horizontal parts become external force and vertical parts
        /// are added to the controller's own fall speed (a real ballistic arc). Upward speed beyond SR's per-step cap
        /// is handed to the motor throttle, sized so the launch apex still matches v²/2g.
        /// </summary>
        public void AddPlayerVelocity(Vector3 velocity)
        {
            if (!InGame || motor == null) return;
            try
            {
                if (velocity.x != 0f || velocity.z != 0f)
                    motor.AddForce(new Vector3(velocity.x, 0f, velocity.z) / StepsPerSecond);

                float vy = velocity.y;
                if (vy == 0f) return;

                ResolveMotorFields();
                if (fallSpeedField == null)
                {
                    motor.AddForce(new Vector3(0f, vy / StepsPerSecond, 0f));
                    return;
                }

                ref float fallSpeed = ref fallSpeedField(motor);
                float wanted = fallSpeed + vy / StepsPerSecond;
                if (vy < 0f)
                {
                    fallSpeed = wanted;
                    return;
                }

                fallSpeed = Mathf.Min(wanted, MaxRisePerStep);
                if (throttleField == null) return;

                float lift = MinThrottle;
                if (wanted > MaxRisePerStep) lift = ThrottleForRise(wanted);
                ref Vector3 throttle = ref throttleField(motor);
                if (throttle.y < lift) throttle.y = lift;
            }
            catch (Exception e)
            {
                CoreLog.Rate("AddPlayerVelocity", e);
            }
        }

        /// <summary>
        /// Throttle needed so a launch at <paramref name="risePerStep"/> (fall-speed units) climbs as high as the
        /// ballistic formula says, given that fall speed itself is capped and the throttle decays every step.
        /// </summary>
        private float ThrottleForRise(float risePerStep)
        {
            float dt = Mathf.Max(0.005f, Time.fixedDeltaTime);
            float gravityPerStep = Mathf.Abs(Physics.gravity.y * motor.PhysicsGravityModifier * 0.002f);
            float gravity = Mathf.Max(0.1f, gravityPerStep * StepsPerSecond / dt);

            float launchSpeed = risePerStep * StepsPerSecond;
            float cappedSpeed = MaxRisePerStep * StepsPerSecond;
            float missingHeight = (launchSpeed * launchSpeed - cappedSpeed * cappedSpeed) / (2f * gravity);

            float decayTime = dt / Mathf.Log(1f + Mathf.Max(0.01f, motor.MotorJumpForceDamping));
            return Mathf.Clamp(missingHeight / (StepsPerSecond * decayTime), MinThrottle, MaxThrottle);
        }

        private void ResolveMotorFields()
        {
            if (motorFieldsResolved) return;
            motorFieldsResolved = true;
            try
            {
                fallSpeedField = AccessTools.FieldRefAccess<vp_Controller, float>("m_FallSpeed");
                throttleField = AccessTools.FieldRefAccess<vp_FPController, Vector3>("m_MotorThrottle");
            }
            catch (Exception e)
            {
                fallSpeedField = null;
                throttleField = null;
                CoreLog.Warn("vp_FPController internals not accessible (" + e.Message + "); vertical impulses use AddForce only");
            }
        }

        // ================================================================== time

        internal TimeDirector TimeDirector => InGame ? context.TimeDirector : null;

        public float DayFraction
        {
            get
            {
                try
                {
                    var td = TimeDirector;
                    return td != null ? td.CurrDayFraction() : DefaultDayFraction;
                }
                catch { return DefaultDayFraction; }
            }
        }

        public bool IsNight
        {
            get
            {
                float f = DayFraction;
                return f < 0.25f || f > 0.75f;
            }
        }

        public int DayNumber
        {
            get
            {
                try
                {
                    var td = TimeDirector;
                    return td != null ? td.CurrDay() : 1;
                }
                catch { return 1; }
            }
        }

        /// <summary>Skips SR time forward to the next occurrence of <paramref name="hour"/> (SR time never runs backwards).</summary>
        internal bool FastForwardToHour(float hour, out double secondsSkipped)
        {
            secondsSkipped = 0;
            var td = TimeDirector;
            if (td == null) return false;
            try
            {
                double target = td.GetHourAfter(0, hour);
                secondsSkipped = target - td.WorldTime();
                td.FastForwardTo(target);
                return true;
            }
            catch (Exception e)
            {
                CoreLog.Rate("FastForwardToHour", e);
                secondsSkipped = 0;
                return false;
            }
        }

        // ================================================================== zone / save

        public string ZoneName => InGame ? zoneName : "";

        public string SaveGameId => InGame ? saveId : null;

        private void RefreshZone()
        {
            try
            {
                var tracker = context != null ? context.PlayerZoneTracker : null;
                var zone = tracker != null ? tracker.GetCurrentZone() : ZoneDirector.Zone.NONE;
                if (!zoneNameCache.TryGetValue(zone, out var name))
                {
                    name = DescribeZone(zone);
                    zoneNameCache[zone] = name;
                }
                zoneName = name;
            }
            catch (Exception e)
            {
                CoreLog.Rate("zone", e, 60f);
            }
        }

        /// <summary>SR's localized Slimepedia title for the zone, else a readable form of the enum name.</summary>
        private static string DescribeZone(ZoneDirector.Zone zone)
        {
            string localized = LocalizedZoneTitle(zone);
            if (!string.IsNullOrEmpty(localized)) return localized;
            return ReadableEnumName(zone.ToString());
        }

        /// <summary>"MOCHI_RANCH" becomes "Mochi Ranch".</summary>
        private static string ReadableEnumName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            var sb = new StringBuilder(raw.Length);
            bool startOfWord = true;
            foreach (char ch in raw)
            {
                if (ch == '_') { sb.Append(' '); startOfWord = true; continue; }
                sb.Append(startOfWord ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch));
                startOfWord = false;
            }
            return sb.ToString();
        }

        private static string LocalizedZoneTitle(ZoneDirector.Zone zone)
        {
            try
            {
                var lookup = ZoneDirector.zonePediaIdLookup;
                if (lookup == null || !lookup.TryGetValue(zone, out var pediaId)) return null;
                var game = SRSingleton<GameContext>.Instance;
                if (game == null || game.MessageDirector == null) return null;
                var bundle = game.MessageDirector.GetBundle("pedia");
                if (bundle == null) return null;
                string key = "t." + pediaId.ToString().ToLowerInvariant();
                return bundle.Exists(key) ? bundle.Get(key) : null;
            }
            catch (Exception e)
            {
                CoreLog.Rate("zone", e, 60f);
                return null;
            }
        }

        // ================================================================== layers and raycasts

        public int WorldMask => worldMask;
        public int BlockLayer => blockLayer;

        /// <summary>
        /// Picks the layer for our block colliders (the first of Default and the unnamed user layers that collides
        /// with both the player and SR actors) and the mask of solid world geometry used by our raycasts.
        /// </summary>
        private void ChooseLayers()
        {
            int chosen = 0;
            foreach (int candidate in BlockLayerCandidates())
            {
                if (CollidesWith(candidate, vp_Layer.Player) && CollidesWith(candidate, vp_Layer.Actor))
                {
                    chosen = candidate;
                    break;
                }
            }
            blockLayer = chosen;

            int mask = 1 | (1 << blockLayer);
            for (int layer = 0; layer < 32; layer++)
            {
                string name = LayerMask.LayerToName(layer);
                if (string.IsNullOrEmpty(name)) continue;
                string lower = name.ToLowerInvariant();
                foreach (string hint in WorldLayerHints)
                {
                    if (lower.Contains(hint))
                    {
                        mask |= 1 << layer;
                        break;
                    }
                }
            }
            if (!string.IsNullOrEmpty(LayerMask.LayerToName(vp_Layer.Mountains))) mask |= 1 << vp_Layer.Mountains;
            worldMask = mask;

            var info = new StringBuilder();
            info.Append("BlockLayer = ").Append(blockLayer).Append(" '").Append(LayerMask.LayerToName(blockLayer)).Append("'")
                .Append(" (collides with Player:").Append(CollidesWith(blockLayer, vp_Layer.Player))
                .Append(" Actor:").Append(CollidesWith(blockLayer, vp_Layer.Actor)).Append("); WorldMask =");
            for (int layer = 0; layer < 32; layer++)
                if ((worldMask & (1 << layer)) != 0)
                    info.Append(' ').Append(LayerMask.LayerToName(layer)).Append('(').Append(layer).Append(')');
            CoreLog.Info(info.ToString());

            if (CoreConfig.VerboseLog)
            {
                var table = new StringBuilder("Layer table (x = ignores BlockLayer):");
                for (int layer = 0; layer < 32; layer++)
                {
                    string name = LayerMask.LayerToName(layer);
                    if (string.IsNullOrEmpty(name)) continue;
                    table.Append(' ').Append(layer).Append('=').Append(name);
                    if (!CollidesWith(layer, blockLayer)) table.Append("(x)");
                }
                CoreLog.Debug(table.ToString());
            }
        }

        private static IEnumerable<int> BlockLayerCandidates()
        {
            yield return 0;
            for (int layer = 8; layer < 32; layer++)
                if (string.IsNullOrEmpty(LayerMask.LayerToName(layer))) yield return layer;
        }

        private static bool CollidesWith(int a, int b) => !Physics.GetIgnoreLayerCollision(a, b);

        public bool RaycastWorld(Ray ray, float maxDistance, out RaycastHit hit)
        {
            hit = default(RaycastHit);
            try
            {
                int count = Physics.RaycastNonAlloc(ray, rayHits, maxDistance, worldMask, QueryTriggerInteraction.Ignore);
                Transform self = player != null ? player.transform : null;
                int nearest = -1;
                float nearestDistance = float.PositiveInfinity;
                for (int i = 0; i < count; i++)
                {
                    var col = rayHits[i].collider;
                    if (col == null) continue;
                    if (self != null && col.transform.IsChildOf(self)) continue;
                    if (rayHits[i].distance < nearestDistance)
                    {
                        nearestDistance = rayHits[i].distance;
                        nearest = i;
                    }
                }
                if (nearest < 0) return false;
                hit = rayHits[nearest];
                return true;
            }
            catch (Exception e)
            {
                CoreLog.Rate("RaycastWorld", e);
                hit = default(RaycastHit);
                return false;
            }
        }

        // ================================================================== SR actors

        public GameObject SpawnSRActor(string identifiableId, Vector3 position, Quaternion rotation)
        {
            if (!InGame || string.IsNullOrEmpty(identifiableId)) return null;
            try
            {
                string name = identifiableId.Trim();
                int colon = name.IndexOf(':');
                if (colon >= 0) name = name.Substring(colon + 1);
                if (!ActorIds().TryGetValue(name, out var id)) return null;

                var game = SRSingleton<GameContext>.Instance;
                if (game == null || game.LookupDirector == null) return null;
                var prefab = game.LookupDirector.GetPrefab(id);
                if (prefab == null) return null;

                var regionSet = context.RegionRegistry.GetCurrentRegionSetId();
                return SRBehaviour.InstantiateActor(prefab, regionSet, position, rotation, false);
            }
            catch (Exception e)
            {
                CoreLog.Rate("SpawnSRActor " + identifiableId, e);
                return null;
            }
        }

        /// <summary>Lower-case names of every spawnable Identifiable.Id (for command completion).</summary>
        internal IEnumerable<string> SpawnableIds()
        {
            foreach (var pair in ActorIds())
                yield return pair.Key.ToLowerInvariant();
        }

        /// <summary>Case-insensitive name → id table of all Identifiable.Id values except NONE and PLAYER.</summary>
        private Dictionary<string, Identifiable.Id> ActorIds()
        {
            if (actorIdsByName != null) return actorIdsByName;
            var table = new Dictionary<string, Identifiable.Id>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in Enum.GetNames(typeof(Identifiable.Id)))
            {
                var id = (Identifiable.Id)Enum.Parse(typeof(Identifiable.Id), name);
                if (IsNonActor(id)) continue;
                table[name] = id;
            }
            actorIdsByName = table;
            return table;
        }

        private static bool IsNonActor(Identifiable.Id id) => id == Identifiable.Id.NONE || id == Identifiable.Id.PLAYER;

        public string GetSRActorId(GameObject go)
        {
            if (go == null) return null;
            try
            {
                var ident = go.GetComponentInParent<Identifiable>();
                if (ident == null || IsNonActor(ident.id)) return null;
                return ident.id.ToString();
            }
            catch { return null; }
        }

        public bool HitSRActor(GameObject target, float damage, Vector3 knockback)
        {
            if (!InGame || target == null) return false;
            try
            {
                var ident = target.GetComponentInParent<Identifiable>();
                if (ident == null || ident.id == Identifiable.Id.PLAYER) return false;
                var root = ident.gameObject;
                if (root == player) return false;

                var body = root.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic) body.AddForce(knockback, ForceMode.VelocityChange);

                var emotions = root.GetComponent<SlimeEmotions>();
                if (emotions != null)
                {
                    emotions.Adjust(SlimeEmotions.Emotion.AGITATION, 0.1f + damage * 0.03f);
                    emotions.Adjust(SlimeEmotions.Emotion.FEAR, 0.05f + damage * 0.02f);
                }

                var hurtsActors = CoreConfig.WeaponsDamageSRActors;
                if (damage > 0f && hurtsActors != null && hurtsActors.Value)
                {
                    var health = root.GetInterfaceComponent<Damageable>();
                    if (IsAlive(health))
                    {
                        float multiplier = CoreConfig.SRDamageMultiplier != null ? CoreConfig.SRDamageMultiplier.Value : 4f;
                        int amount = Math.Max(1, Mathf.RoundToInt(damage * multiplier));
                        if (health.Damage(amount, player))
                            DeathHandler.Kill(root, DeathHandler.Source.UNDEFINED, player, "SlimeCraft.HitSRActor");
                    }
                }
                return true;
            }
            catch (Exception e)
            {
                CoreLog.Rate("HitSRActor", e);
                return false;
            }
        }

        // ================================================================== explosions

        public void SRExplode(Vector3 position, float radius, float power, float minPlayerDamage, float maxPlayerDamage, bool ignites)
        {
            if (!InGame) return;
            try
            {
                var source = ExplosionSourceAt(position);
                if (minPlayerDamage > 0f || maxPlayerDamage > 0f || ignites)
                {
                    // full SR explosion: actors fly, the player is pushed, hurt and maybe set on fire
                    PhysicsUtil.Explode(source, radius, power, minPlayerDamage, maxPlayerDamage, ignites);
                    return;
                }
                PushPhysicsBodies(position, radius, power, source);
            }
            catch (Exception e)
            {
                CoreLog.Rate("SRExplode", e);
            }
        }

        /// <summary>The hidden, reused object SR's explosion API needs as a source, moved to the blast centre.</summary>
        private GameObject ExplosionSourceAt(Vector3 position)
        {
            if (explosionSource == null)
                explosionSource = new GameObject(ExplosionSourceName) { hideFlags = HideFlags.HideInHierarchy };
            explosionSource.transform.position = position;
            return explosionSource;
        }

        /// <summary>
        /// Blast that only shoves SR physics objects and leaves the player alone (no hurt cue, no red flash, no
        /// controller push). Each rigidbody whose own GameObject carries an overlapping solid collider is pushed once
        /// through SR's soft explosion force. Bodies reached only through child colliders are skipped, because the
        /// Entities module pushes its own mobs (which keep their colliders on children) during the same blast.
        /// </summary>
        private void PushPhysicsBodies(Vector3 centre, float radius, float power, GameObject source)
        {
            int layers = ~(1 << vp_Layer.ActorEchoes);
            int found = Physics.OverlapSphereNonAlloc(centre, radius, blastBuffer, layers, QueryTriggerInteraction.Ignore);
            while (found >= blastBuffer.Length)
            {
                // buffer was full: grow it and ask again so large blasts never silently miss anything
                blastBuffer = new Collider[blastBuffer.Length * 2];
                found = Physics.OverlapSphereNonAlloc(centre, radius, blastBuffer, layers, QueryTriggerInteraction.Ignore);
            }

            blastPushed.Clear();
            try
            {
                for (int i = 0; i < found; i++)
                {
                    var col = blastBuffer[i];
                    if (col == null || col.isTrigger) continue;
                    var body = col.attachedRigidbody;
                    if (body == null) continue;
                    var owner = body.gameObject;
                    if (owner != col.gameObject || owner == source) continue;
                    if (owner.GetComponent<vp_FPController>() != null) continue;
                    if (!blastPushed.Add(body)) continue;
                    PhysicsUtil.SoftExplosionForce(power, centre, radius, body);
                }
            }
            finally
            {
                blastPushed.Clear();
                Array.Clear(blastBuffer, 0, found);
            }
        }

        // ================================================================== vacpack and HUD

        public void SetVacpackActive(bool active) => Vacpack.SetActive(active);

        public bool VacpackActive => Vacpack.Active;

        public bool VacActive => InGame && Vacpack.InVacMode;

        public Ray VacRay => InGame ? Vacpack.VacRay : new Ray(Vector3.zero, Vector3.forward);

        public void SetSRHudVisible(bool visible)
        {
            hud.SetHudVisible(visible);
            if (InGame) hud.Refresh(liveContextId);
        }

        internal bool SRHudVisible => hud.HudVisible;

        // ================================================================== misc

        /// <summary>Null check that also treats destroyed Unity objects behind an interface reference as gone.</summary>
        private static bool IsAlive(object o)
        {
            if (o is Object unityObject) return unityObject != null;
            return o != null;
        }
    }
}
