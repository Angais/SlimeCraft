using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>Random helpers: uniform floats and ints, coin flips, triangular and normal distributions, voice pitch.</summary>
    internal static class Rng
    {
        private static readonly System.Random r = new System.Random(Environment.TickCount);
        private static bool haveSpare; private static double spare;

        public static float F() => (float)r.NextDouble();
        public static double D() => r.NextDouble();
        /// <summary>[0, n)</summary>
        public static int I(int n) => n <= 0 ? 0 : r.Next(n);
        /// <summary>[min, max] inclusive.</summary>
        public static int Range(int min, int max) => max <= min ? min : min + r.Next(max - min + 1);
        public static float Range(float a, float b) => a + (b - a) * F();
        public static bool Chance(float p) => r.NextDouble() < p;
        /// <summary>Difference of two uniform samples: triangular distribution over [-1, 1] peaking at 0.</summary>
        public static float Diff() => F() - F();
        /// <summary>Triangular distribution centred on <paramref name="mode"/>, reaching +-<paramref name="deviation"/>.</summary>
        public static float Triangle(float mode, float deviation) => mode + deviation * (F() - F());
        /// <summary>A vector whose three components are each <see cref="Triangle"/>(0, <paramref name="deviation"/>).</summary>
        public static Vector3 Scatter(float deviation) => new Vector3(Triangle(0f, deviation), Triangle(0f, deviation), Triangle(0f, deviation));
        public static float Gaussian()
        {
            if (haveSpare) { haveSpare = false; return (float)spare; }
            double u, v, s;
            do { u = r.NextDouble() * 2 - 1; v = r.NextDouble() * 2 - 1; s = u * u + v * v; } while (s >= 1 || s == 0);
            double m = Math.Sqrt(-2.0 * Math.Log(s) / s);
            spare = v * m; haveSpare = true;
            return (float)(u * m);
        }
        /// <summary>Voice pitch of mob sounds: 1 plus 0.2 times <see cref="Diff"/> (0.8 .. 1.2, peaking at 1).</summary>
        public static float Pitch() => Diff() * 0.2f + 1f;
        public static Vector3 InsideUnitSphere() => UnityEngine.Random.insideUnitSphere;
        public static Vector2 OnUnitCircle() { float a = F() * Mathf.PI * 2f; return new Vector2(Mathf.Cos(a), Mathf.Sin(a)); }
    }

    /// <summary>Rate limited logging so per-frame failures never spam the BepInEx log.</summary>
    internal static class ELog
    {
        private static readonly Dictionary<string, float> last = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> suppressed = new Dictionary<string, int>();

        public static void Info(string msg) => SC.Log?.LogInfo("[Entities] " + msg);
        public static void Warn(string msg) => SC.Log?.LogWarning("[Entities] " + msg);

        /// <summary>Logs an error at most once every 10 seconds per key.</summary>
        public static void Error(string key, Exception e)
        {
            float now = Time.realtimeSinceStartup;
            if (last.TryGetValue(key, out float t) && now - t < 10f)
            {
                suppressed.TryGetValue(key, out int n); suppressed[key] = n + 1;
                return;
            }
            last[key] = now;
            suppressed.TryGetValue(key, out int s); suppressed[key] = 0;
            SC.Log?.LogError("[Entities] " + key + " failed" + (s > 0 ? " (" + s + " similar errors suppressed)" : "") + ": " + e);
        }

        public static void WarnOnce(string key, string msg)
        {
            if (last.ContainsKey("once:" + key)) return;
            last["once:" + key] = Time.realtimeSinceStartup;
            Warn(msg);
        }

        private sealed class EvState { public float WindowStart = -1e9f; public int InWindow, Suppressed; }
        private static readonly Dictionary<string, EvState> events = new Dictionary<string, EvState>();

        /// <summary>
        /// Rate limited gameplay event (Info level, useful for checking behaviour from the log): at most <paramref name="burst"/> lines per
        /// <paramref name="window"/> seconds per key (so several things happening in the same frame are all logged up to the
        /// burst); the next line that gets through reports how many were skipped in between.
        /// </summary>
        public static void Event(string key, string msg, float window = 1f, int burst = 4)
        {
            float now = Time.realtimeSinceStartup;
            if (!events.TryGetValue(key, out var st)) { st = new EvState(); events[key] = st; }
            if (now - st.WindowStart >= window) { st.WindowStart = now; st.InWindow = 0; }
            if (st.InWindow >= burst) { st.Suppressed++; return; }
            st.InWindow++;
            int s = st.Suppressed; st.Suppressed = 0;
            Info(s > 0 ? msg + " (+" + s + " similar events not logged before this one)" : msg);
        }

        /// <summary>Culture independent "(x.x, y.y, z.z)" (SR may run with a comma decimal separator).</summary>
        public static string V(Vector3 v) =>
            "(" + v.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ", " + v.y.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            + ", " + v.z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ")";

        public static string F(float f, string fmt = "F1") => f.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>"2x minecraft:bone, 1x minecraft:arrow" or "nothing".</summary>
        public static string Stacks(List<ItemStack> stacks)
        {
            if (stacks == null || stacks.Count == 0) return "nothing";
            var sb = new System.Text.StringBuilder();
            foreach (var s in stacks)
            {
                if (s == null || s.IsEmpty) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(s.Count).Append("x ").Append(s.Id);
            }
            return sb.Length == 0 ? "nothing" : sb.ToString();
        }
    }

    /// <summary>Unit conversions between Minecraft ticks/blocks and Unity seconds/meters (1 block = 1 m).</summary>
    internal static class Mc
    {
        public const float TPS = 20f;
        /// <summary>Blocks per tick -> meters per second.</summary>
        public const float BptToMs = 20f;
        /// <summary>Blocks/tick^2 -> m/s^2.</summary>
        public const float Bpt2ToMs2 = 400f;
        /// <summary>Per-tick multiplicative factor applied over dt seconds.</summary>
        public static float PerTick(float factor, float dt) => Mathf.Pow(factor, dt * TPS);
        /// <summary>
        /// Real ground speed (m/s) of a Minecraft mob walking with movement_speed attribute * modifier:
        /// on the ground the mob gains speed^2 blocks/tick every tick while friction keeps 0.546 of its motion, so the
        /// steady movement per tick is speed^2 / (1 - 0.546).
        /// </summary>
        public static float WalkSpeed(float attr, float modifier = 1f)
        {
            float s = attr * modifier;
            return s * s / (1f - 0.546f) * BptToMs;
        }
        public static float WrapDeg(float a) { a %= 360f; if (a >= 180f) a -= 360f; if (a < -180f) a += 360f; return a; }
        public static float RotLerp(float from, float to, float maxStep)
        {
            float d = WrapDeg(to - from);
            d = Mathf.Clamp(d, -maxStep, maxStep);
            return from + d;
        }
        /// <summary>Triangle wave with the given period: 1 at t = 0, -1 half a period later, linear in between.</summary>
        public static float TriangleWave(float t, float period)
        {
            float phase = Mathf.Repeat(t / period, 1f);
            return 4f * Mathf.Abs(phase - 0.5f) - 1f;
        }
        /// <summary>Unity yaw (deg) of a horizontal direction: Euler(0, yaw, 0) * forward == dir.</summary>
        public static float YawOf(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        public static Vector3 Horizontal(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }

    /// <summary>Layers our entities live on (decided at WorldLoaded from the actual collision matrix) and raycast masks.</summary>
    internal static class ELayers
    {
        public const int SRDefault = 0, SRIgnoreRaycast = 2, SRWater = 4, SRPlayer = 8, SRLaunched = 9, SRActorIgnorePlayer = 11, SRHeld = 13, SRActor = 15, SRWeapon = 31;

        /// <summary>Layer of mob / TNT / falling block colliders.</summary>
        public static int Mob = SRActor;
        /// <summary>Layer of dropped item colliders (preferably one that ignores the player).</summary>
        public static int Item = SRActorIgnorePlayer;
        /// <summary>When the item layer still collides with the player, item colliders ignore the player's colliders one by one.</summary>
        public static bool ItemNeedsPlayerIgnore;
        public static bool Decided;

        /// <summary>Solid world for ground checks / line of sight (SR terrain + our blocks), never the player.</summary>
        public static int World
        {
            get
            {
                int m = SC.SR != null ? SC.SR.WorldMask : 0;
                if (m == 0) m = Physics.DefaultRaycastLayers;
                m &= ~(1 << SRPlayer) & ~(1 << SRIgnoreRaycast) & ~(1 << SRWater) & ~(1 << SRWeapon);
                return m;
            }
        }
        /// <summary>World + things a mob can stand on (other mobs, SR actors).</summary>
        public static int Ground => World | (1 << Mob) | (1 << SRActor) | (1 << SRLaunched);
        /// <summary>Everything a projectile / attack ray can hit (excluding the player).</summary>
        public static int Hittable => (World | (1 << Mob) | (1 << SRActor) | (1 << SRLaunched) | (1 << SRActorIgnorePlayer) | (1 << SRHeld)) & ~(1 << SRPlayer);

        public static void Decide()
        {
            try
            {
                int block = SC.SR != null ? SC.SR.BlockLayer : 0;
                int[] mobCandidates = { SRActor, SRDefault };
                Mob = SRDefault;
                foreach (int l in mobCandidates)
                {
                    if (!Physics.GetIgnoreLayerCollision(l, SRDefault) && !Physics.GetIgnoreLayerCollision(l, block)
                        && !Physics.GetIgnoreLayerCollision(l, SRPlayer) && !Physics.GetIgnoreLayerCollision(l, l))
                    { Mob = l; break; }
                }
                Item = Mob; ItemNeedsPlayerIgnore = true;
                int[] itemCandidates = { SRActorIgnorePlayer };
                foreach (int l in itemCandidates)
                {
                    if (!Physics.GetIgnoreLayerCollision(l, SRDefault) && !Physics.GetIgnoreLayerCollision(l, block)
                        && Physics.GetIgnoreLayerCollision(l, SRPlayer))
                    { Item = l; ItemNeedsPlayerIgnore = false; break; }
                }
                Decided = true;
                ELog.Info("Layer decision: mobs/TNT on layer " + Mob + " (" + LayerMask.LayerToName(Mob) + "), items on layer " + Item + " (" + LayerMask.LayerToName(Item) + ")"
                    + (ItemNeedsPlayerIgnore ? " with per-collider player ignore" : "") + "; block layer " + block
                    + "; mob<->player " + !Physics.GetIgnoreLayerCollision(Mob, SRPlayer) + ", mob<->blocks " + !Physics.GetIgnoreLayerCollision(Mob, block)
                    + ", mob<->SRActor " + !Physics.GetIgnoreLayerCollision(Mob, SRActor));
            }
            catch (Exception e) { ELog.Error("ELayers.Decide", e); }
        }
    }

    /// <summary>Small allocation-free physics query helpers.</summary>
    internal static class EPhys
    {
        private static readonly RaycastHit[] hits = new RaycastHit[32];
        private static readonly Collider[] cols = new Collider[48];

        /// <summary>True if solid world geometry (not entities, not the player) lies between a and b.</summary>
        public static bool WorldBlocked(Vector3 a, Vector3 b)
        {
            Vector3 d = b - a; float len = d.magnitude;
            if (len < 1e-4f) return false;
            int n = Physics.RaycastNonAlloc(a, d / len, hits, len, ELayers.World, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var c = hits[i].collider;
                if (c == null) continue;
                if (c.GetComponentInParent<McEntity>() != null) continue;
                if (SC.SR != null && SC.SR.Player != null && c.transform.IsChildOf(SC.SR.Player.transform)) continue;
                return true;
            }
            return false;
        }

        /// <summary>Nearest solid world hit (ignores our entities and the player).</summary>
        public static bool RaycastWorld(Vector3 origin, Vector3 dir, float maxDist, out RaycastHit best)
        {
            best = default(RaycastHit);
            int n = Physics.RaycastNonAlloc(origin, dir, hits, maxDist, ELayers.World, QueryTriggerInteraction.Ignore);
            float bd = float.MaxValue; bool found = false;
            for (int i = 0; i < n; i++)
            {
                var c = hits[i].collider;
                if (c == null || hits[i].distance >= bd) continue;
                if (c.GetComponentInParent<McEntity>() != null) continue;
                if (SC.SR != null && SC.SR.Player != null && c.transform.IsChildOf(SC.SR.Player.transform)) continue;
                bd = hits[i].distance; best = hits[i]; found = true;
            }
            return found;
        }

        /// <summary>Overlap query with a shared buffer (valid until the next call).</summary>
        public static int Overlap(Vector3 pos, float radius, int mask, QueryTriggerInteraction q, out Collider[] buffer)
        {
            buffer = cols;
            return Physics.OverlapSphereNonAlloc(pos, radius, cols, mask, q);
        }

        public static RaycastHit[] HitBuffer => hits;

        /// <summary>Distance from p to an AABB (0 inside).</summary>
        public static float DistToBounds(Bounds b, Vector3 p) => Mathf.Sqrt(b.SqrDistance(p));
    }
}
