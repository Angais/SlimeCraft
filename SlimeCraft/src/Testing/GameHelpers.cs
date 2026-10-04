using System;
using System.Collections;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>
    /// Slime Rancher / SlimeCraft helpers for the scenario. Everything prefers the SC.* services (that is what the
    /// test is supposed to exercise) and falls back to Slime Rancher's own API (Assembly-CSharp) only where the
    /// harness itself needs it to keep going (player position/look, waiting for the world).
    /// </summary>
    internal static partial class G
    {
        // ------------------------------------------------------------------ game state
        public static bool AtMainMenu
        {
            get
            {
                if (!Levels.isMainMenu()) return false;
                var gc = SRSingleton<GameContext>.Instance;
                if (gc == null || gc.AutoSaveDirector == null || gc.AutoSaveDirector.IsLoadingGame()) return false;
                return UnityEngine.Object.FindObjectOfType<MainMenuUI>() != null;
            }
        }

        public static bool SRLoading
        {
            get
            {
                var gc = SRSingleton<GameContext>.Instance;
                return gc != null && gc.AutoSaveDirector != null && gc.AutoSaveDirector.IsLoadingGame();
            }
        }

        /// <summary>World scene loaded, player exists and SR finished loading (uses SC.SR when available).</summary>
        public static bool InGame
        {
            get
            {
                if (SRLoading) return false;
                if (SC.SR != null) return SC.SR.InGame;
                return InGameRaw;
            }
        }

        /// <summary>The harness' own in-game detection straight from SR (independent of the Core bridge).</summary>
        public static bool InGameRaw
        {
            get
            {
                if (SRLoading || !Levels.IsLevel(Levels.WORLD)) return false;
                var sc = SRSingleton<SceneContext>.Instance;
                return sc != null && sc.Player != null;
            }
        }

        public static GameObject Player
        {
            get
            {
                GameObject p = SC.SR != null ? SC.SR.Player : null;
                if (p != null) return p;
                var sc = SRSingleton<SceneContext>.Instance;
                return sc != null ? sc.Player : null;
            }
        }

        public static Vector3 Feet
        {
            get
            {
                if (SC.SR != null && SC.SR.InGame) return SC.SR.PlayerFeet;
                var p = Player;
                return p != null ? p.transform.position - Vector3.up * FeetOffset(p) : Vector3.zero;
            }
        }

        /// <summary>
        /// Height of the player's transform above its feet: the vp_FPController transform sits at the
        /// CharacterController's pivot, not at the bottom of the capsule.
        /// </summary>
        public static float FeetOffset(GameObject p)
        {
            if (p == null) return 0f;
            var cc = p.GetComponent<CharacterController>();
            if (cc == null) cc = p.GetComponentInChildren<CharacterController>();
            return cc != null && cc.enabled ? p.transform.position.y - cc.bounds.min.y : 0f;
        }

        public static Vector3 Eye
        {
            get
            {
                if (SC.SR != null && SC.SR.InGame) return SC.SR.EyePosition;
                var cam = Camera.main;
                return cam != null ? cam.transform.position : Feet + Vector3.up * 1.6f;
            }
        }

        public static Vector3 Look
        {
            get
            {
                if (SC.SR != null && SC.SR.InGame) return SC.SR.LookDirection;
                var cam = Camera.main;
                return cam != null ? cam.transform.forward : Vector3.forward;
            }
        }

        /// <summary>Horizontal look direction (normalized).</summary>
        public static Vector3 FlatLook
        {
            get
            {
                var f = Look; f.y = 0;
                return f.sqrMagnitude < 1e-4f ? Vector3.forward : f.normalized;
            }
        }

        public static int Mask
        {
            get
            {
                int m = SC.SR != null ? SC.SR.WorldMask : 0;
                return m != 0 ? m : Physics.DefaultRaycastLayers;
            }
        }

        // ------------------------------------------------------------------ physics queries
        /// <summary>
        /// First solid surface below <paramref name="from"/> that is not the player, a dynamic actor (slime, mob)
        /// or – optionally – one of our Minecraft blocks.
        /// </summary>
        public static bool FindGround(Vector3 from, float maxDown, out Vector3 point, bool ignoreBlocks = false)
        {
            point = from;
            var hits = Physics.RaycastAll(from, Vector3.down, maxDown, Mask, QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0) return false;
            var player = Player;
            float best = float.MaxValue;
            bool found = false;
            foreach (var h in hits)
            {
                var c = h.collider;
                if (c == null) continue;
                if (player != null && c.transform.IsChildOf(player.transform)) continue;
                if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
                if (ignoreBlocks && SC.Blocks != null && SC.Blocks.IsBlockCollider(c)) continue;
                if (h.distance < best) { best = h.distance; point = h.point; found = true; }
            }
            return found;
        }

        /// <summary>Free distance along a ray (ignoring the player and dynamic actors), capped at max.</summary>
        public static float FreeDistance(Vector3 from, Vector3 dir, float max)
        {
            var hits = Physics.RaycastAll(from, dir, max, Mask, QueryTriggerInteraction.Ignore);
            float best = max;
            var player = Player;
            foreach (var h in hits)
            {
                var c = h.collider;
                if (c == null) continue;
                if (player != null && c.transform.IsChildOf(player.transform)) continue;
                if (c.attachedRigidbody != null && !c.attachedRigidbody.isKinematic) continue;
                if (h.distance < best) best = h.distance;
            }
            return best;
        }

        // ------------------------------------------------------------------ player control (SR API: vp_FP*)
        private static T FindOnPlayer<T>(GameObject p) where T : Component
        {
            if (p == null) return null;
            var c = p.GetComponent<T>();
            if (c == null) c = p.GetComponentInChildren<T>(true);
            if (c == null) c = p.GetComponentInParent<T>();
            return c;
        }

        /// <summary>
        /// Puts the player's feet at a position and stops its motion through SR's player event handler and character
        /// controller, without any teleport overlay or sound – used every frame to hold the player while far regions
        /// stream in.
        /// </summary>
        public static void HoldPlayerAt(Vector3 feet)
        {
            var p = Player;
            if (p == null) return;
            Vector3 target = feet + Vector3.up * FeetOffset(p);
            var handler = FindOnPlayer<vp_FPPlayerEventHandler>(p);
            if (handler != null && handler.Position != null && handler.Position.Set != null) handler.Position.Set(target);
            else p.transform.position = target;
            var ctrl = FindOnPlayer<vp_FPController>(p);
            if (ctrl != null)
            {
                ctrl.m_Velocity = Vector3.zero;
                ctrl.Stop();
            }
        }

        /// <summary>Teleports through SC.SR (the bridge under test) or falls back to <see cref="HoldPlayerAt"/>.</summary>
        public static void Teleport(Vector3 feet)
        {
            if (SC.SR != null) SC.SR.TeleportPlayer(feet);
            else HoldPlayerAt(feet);
        }

        /// <summary>
        /// Sets camera pitch (positive = down) and yaw in degrees: through the bridge under test (ISRBridge.SetLook),
        /// or directly via vp_FPPlayerEventHandler.Rotation when the bridge is missing.
        /// </summary>
        public static bool SetLook(float pitch, float yaw)
        {
            if (SC.SR != null && SC.SR.InGame)
            {
                try { SC.SR.SetLook(pitch, yaw); return true; }
                catch (Exception e) { SC.Log?.LogWarning("[Test] SC.SR.SetLook threw " + e.Message + "; using vp_FPPlayerEventHandler"); }
            }
            var p = Player;
            if (p == null) return false;
            var rot = new Vector2(Mathf.Clamp(pitch, -85f, 85f), yaw);
            var handler = FindOnPlayer<vp_FPPlayerEventHandler>(p);
            if (handler != null && handler.Rotation != null && handler.Rotation.Set != null)
            {
                handler.Rotation.Set(rot);
                return true;
            }
            var cam = FindOnPlayer<vp_FPCamera>(p);
            if (cam != null) { cam.SetRotation(rot); return true; }
            return false;
        }

        public static bool LookAt(Vector3 target)
        {
            Vector3 d = target - Eye;
            if (d.sqrMagnitude < 1e-4f) return false;
            d.Normalize();
            float yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(d.y, -1f, 1f)) * Mathf.Rad2Deg;
            return SetLook(pitch, yaw);
        }

        public static void Heal()
        {
            try { if (SC.SR != null && SC.SR.InGame) SC.SR.HealPlayer(SC.SR.MaxHealth > 0 ? SC.SR.MaxHealth : 1000); }
            catch { }
        }

        /// <summary>Closes SR's pause menu if it is open (a paused game stops TNT fuses, mobs, time...).</summary>
        public static bool EnsureUnpaused()
        {
            try
            {
                var pm = SRSingleton<PauseMenu>.Instance;
                if (pm != null && pm.pauseUI != null && pm.pauseUI.activeInHierarchy)
                {
                    pm.Resume();
                    return true;
                }
            }
            catch { }
            return false;
        }

        // ------------------------------------------------------------------ SlimeCraft helpers
        /// <summary>Runs a SlimeCraft command, records its feedback; returns false when the command is unavailable.</summary>
        public static bool Exec(StepResult r, string command, bool mustBeKnown = true)
        {
            if (SC.Commands == null)
            {
                r.Check("SC.Commands available for '" + command + "'", false);
                return false;
            }
            string fb;
            try { fb = SC.Commands.Execute(command); }
            catch (Exception e)
            {
                r.Check("command '" + command + "' threw", false, e.GetType().Name + ": " + e.Message);
                return false;
            }
            r.Note(command + "  ->  " + (fb == null ? "(no feedback)" : fb.Replace("\n", " | ")));
            bool unknown = fb != null && fb.IndexOf("unknown command", StringComparison.OrdinalIgnoreCase) >= 0;
            if (mustBeKnown && unknown) r.Check("command '" + command + "' is registered", false, fb);
            return !unknown;
        }

        public static int FindSlot(string itemId, bool hotbarOnly)
        {
            var inv = SC.Inventory != null ? SC.Inventory.Inv : null;
            if (inv == null) return -1;
            string id = Content.Norm(itemId);
            int n = hotbarOnly ? Math.Min(9, inv.Size) : inv.Size;
            for (int i = 0; i < n; i++)
            {
                var s = inv.Get(i);
                if (!s.IsEmpty && s.Id == id) return i;
            }
            return -1;
        }

        /// <summary>Makes sure the item is in the hotbar (gives it if missing, swaps it in from the main inventory). Returns the slot or -1.</summary>
        public static int EnsureInHotbar(string itemId, int preferredSlot)
        {
            if (SC.Inventory == null || SC.Inventory.Inv == null) return -1;
            string id = Content.Norm(itemId);
            var inv = SC.Inventory.Inv;
            int slot = FindSlot(id, true);
            if (slot >= 0) return slot;
            int any = FindSlot(id, false);
            if (any < 0)
            {
                SC.Inventory.Give(new ItemStack(id, 1));
                slot = FindSlot(id, true);
                if (slot >= 0) return slot;
                any = FindSlot(id, false);
                if (any < 0) return -1;
            }
            // pick an empty hotbar slot, else the preferred one (never the vacpack's slot)
            int target = -1;
            for (int i = 0; i < 9 && i < inv.Size; i++) if (inv.Get(i).IsEmpty) { target = i; break; }
            if (target < 0)
            {
                target = Mathf.Clamp(preferredSlot, 0, 8);
                if (inv.Get(target).Id == Content.Vacpack) target = (target + 1) % 9;
            }
            var moving = inv.Get(any);
            var displaced = inv.Get(target);
            inv.Set(target, moving);
            inv.Set(any, displaced);
            return target;
        }

        public static string HotbarText()
        {
            var inv = SC.Inventory != null ? SC.Inventory.Inv : null;
            if (inv == null) return "(no inventory)";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 9 && i < inv.Size; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(i).Append('=').Append(inv.Get(i).ToString());
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ waiting (driven by the step runner)
        /// <summary>Real-time wait (unaffected by SR pausing / time scale).</summary>
        public static IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        /// <summary>Game-time wait (TNT fuses, mob AI): stops while the game is paused.</summary>
        public static IEnumerator WaitGame(float seconds)
        {
            float end = Time.time + seconds;
            while (Time.time < end) yield return null;
        }

        /// <summary>Real-time wait until the condition holds (or the timeout elapses).</summary>
        public static IEnumerator WaitUntil(Func<bool> cond, float timeout)
        {
            float end = Time.realtimeSinceStartup + timeout;
            while (Time.realtimeSinceStartup < end)
            {
                bool ok = false;
                try { ok = cond(); } catch { }
                if (ok) yield break;
                yield return null;
            }
        }

        public static IEnumerator WaitFrames(int frames)
        {
            for (int i = 0; i < frames; i++) yield return null;
        }

        public static Vector3 BlockCenter(Vector3Int b) => new Vector3(b.x + 0.5f, b.y + 0.5f, b.z + 0.5f);
        public static Vector3Int ToBlock(Vector3 p) => new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
    }
}
