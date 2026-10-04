using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    /// <summary>Rate-limited logging so per-frame failures never spam the BepInEx log.</summary>
    internal static class RateLog
    {
        private static readonly Dictionary<string, float> last = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> suppressed = new Dictionary<string, int>();

        private static bool Allow(string key, float interval)
        {
            float now = Time.realtimeSinceStartup;
            if (last.TryGetValue(key, out var t) && now - t < interval) return false;
            last[key] = now;
            return true;
        }

        /// <summary>True if player-action logging is enabled ([Blocks] LogActions). Check before building the message.</summary>
        public static bool Actions => BlocksConfig.LogActions == null || BlocksConfig.LogActions.Value;

        /// <summary>
        /// Info line for a player action (block broken/placed, harvest, food...), at most one per key and interval;
        /// lines dropped meanwhile are counted and reported with the next one ("+N more").
        /// </summary>
        public static void Action(string key, string msg, float interval = 0.1f)
        {
            if (!Actions) return;
            if (!Allow("act:" + key, interval))
            {
                suppressed.TryGetValue(key, out var n);
                suppressed[key] = n + 1;
                return;
            }
            if (suppressed.TryGetValue(key, out var s) && s > 0)
            {
                suppressed[key] = 0;
                msg += " (+" + s + " more '" + key + "' since the last line)";
            }
            SC.Log?.LogInfo("[Blocks] " + msg);
        }

        /// <summary>Compact block position "(x, y, z)".</summary>
        public static string P(Vector3Int p) => "(" + p.x + ", " + p.y + ", " + p.z + ")";
        public static string P(Vector3 p) => "(" + F(p.x, "0.0") + ", " + F(p.y, "0.0") + ", " + F(p.z, "0.0") + ")";
        /// <summary>Culture-invariant number (the game may run with a ',' decimal separator).</summary>
        public static string F(float v, string fmt = "0.00") => v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);

        public static void Error(string key, Exception e, float interval = 10f)
        {
            if (Allow(key, interval)) SC.Log?.LogError("[Blocks] " + key + " failed: " + e);
        }

        public static void Warn(string key, string msg, float interval = 30f)
        {
            if (Allow(key, interval)) SC.Log?.LogWarning("[Blocks] " + msg);
        }
    }

    /// <summary>
    /// The six face directions, indexed in <see cref="BlockDef.Faces"/> order:
    /// 0 Up(+Y) 1 Down(-Y) 2 North(-Z) 3 South(+Z) 4 East(+X) 5 West(-X).
    /// Also holds the per-face corner tables used by the mesher (texture orientation like Minecraft).
    /// </summary>
    internal static class Dirs
    {
        public const int Up = 0, Down = 1, North = 2, South = 3, East = 4, West = 5;

        public static readonly Vector3Int[] Vec =
        {
            new Vector3Int(0, 1, 0), new Vector3Int(0, -1, 0), new Vector3Int(0, 0, -1),
            new Vector3Int(0, 0, 1), new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0)
        };
        public static readonly Vector3[] VecF =
        {
            Vector3.up, Vector3.down, Vector3.back, Vector3.forward, Vector3.right, Vector3.left
        };
        public static readonly int[] Opposite = { 1, 0, 3, 2, 5, 4 };
        /// <summary>Minecraft's directional face brightness (up 1, down 0.5, N/S 0.8, E/W 0.6).</summary>
        public static readonly float[] Shade = { 1f, 0.5f, 0.8f, 0.8f, 0.6f, 0.6f };

        // Horizontal ring (a rotation around Y): North -> East -> South -> West.
        private static readonly int[] ring = { North, East, South, West };

        /// <summary>Corner offsets [dir*6+upDir] → TL, BL, BR, TR as seen from outside the face (unit cube, origin at min corner).</summary>
        private static readonly Vector3[][] corners = new Vector3[36][];

        static Dirs()
        {
            for (int d = 0; d < 6; d++)
            {
                Vector3 n = VecF[d];
                for (int u = 0; u < 6; u++)
                {
                    Vector3 up = VecF[u];
                    if (Mathf.Abs(Vector3.Dot(n, up)) > 0.5f) continue; // not perpendicular
                    // Viewer looks along -n with "up" = up: screen right = Cross(up, -n) (Unity convention).
                    Vector3 r = Vector3.Cross(up, -n);
                    Vector3 c = new Vector3(0.5f, 0.5f, 0.5f) + n * 0.5f;
                    corners[d * 6 + u] = new[]
                    {
                        c + (-r + up) * 0.5f, // TL
                        c + (-r - up) * 0.5f, // BL
                        c + (r - up) * 0.5f,  // BR
                        c + (r + up) * 0.5f   // TR
                    };
                }
            }
        }

        /// <summary>Corners TL,BL,BR,TR. Triangles (0,3,2) (0,2,1) are front-facing (clockwise) from outside.</summary>
        public static Vector3[] Corners(int dir, int upDir) => corners[dir * 6 + upDir] ?? corners[dir * 6 + DefaultUp(dir)];

        /// <summary>Texture "up" of a face when the block is not rotated (sides: +Y, top: north, bottom: south like MC).</summary>
        public static int DefaultUp(int d) => d == Up ? North : d == Down ? South : Up;

        public static int RingIndex(int d) => d == North ? 0 : d == East ? 1 : d == South ? 2 : d == West ? 3 : -1;

        public static int FromFacing(BlockFacing f)
        {
            switch (f)
            {
                case BlockFacing.North: return North;
                case BlockFacing.South: return South;
                case BlockFacing.East: return East;
                case BlockFacing.West: return West;
                case BlockFacing.Up: return Up;
                default: return Down;
            }
        }

        public static BlockFacing ToFacing(int d)
        {
            switch (d)
            {
                case North: return BlockFacing.North;
                case South: return BlockFacing.South;
                case East: return BlockFacing.East;
                case West: return BlockFacing.West;
                case Up: return BlockFacing.Up;
                default: return BlockFacing.Down;
            }
        }

        /// <summary>Direction index of the axis with the largest component of v (sign respected).</summary>
        public static int Dominant(Vector3 v)
        {
            float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
            if (ay >= ax && ay >= az) return v.y >= 0 ? Up : Down;
            if (ax >= az) return v.x >= 0 ? East : West;
            return v.z >= 0 ? South : North;
        }

        public static int DominantHorizontal(Vector3 v)
        {
            if (Mathf.Abs(v.x) >= Mathf.Abs(v.z)) return v.x >= 0 ? East : West;
            return v.z >= 0 ? South : North;
        }

        /// <summary>
        /// Which texture slot of <paramref name="def"/>.Faces is shown on world face <paramref name="d"/> and which
        /// world direction is the texture's "up", honoring the block's rotation:
        /// Horizontal: Faces[2] (front) points to the stored facing, top/bottom textures rotate with it.
        /// Axis: Faces[0]/[1] (log ends) lie on the axis faces, the bark grain runs along the axis.
        /// </summary>
        public static void ResolveFace(BlockDef def, byte facing, int d, out int slot, out int up)
        {
            slot = d; up = DefaultUp(d);
            if (def == null) return;
            if (def.Rotation == BlockRotation.Horizontal)
            {
                int fd = FromFacing((BlockFacing)facing);
                if (fd == Up || fd == Down) fd = North;
                if (d == Up) { slot = 0; up = fd; return; }
                if (d == Down) { slot = 1; up = Opposite[fd]; return; }
                int li = (RingIndex(d) - RingIndex(fd) + 4) & 3;
                slot = ring[li];
                up = Up;
                return;
            }
            if (def.Rotation == BlockRotation.Axis)
            {
                int fd = FromFacing((BlockFacing)facing);
                if (fd == North || fd == South)
                {
                    // Axis Z: ends on north/south faces, grain along Z elsewhere.
                    if (d == North) { slot = 0; up = Up; return; }
                    if (d == South) { slot = 1; up = Up; return; }
                    slot = d == Up ? 2 : d == Down ? 3 : d; // side slots
                    up = North;
                    return;
                }
                if (fd == East || fd == West)
                {
                    // Axis X: ends on east/west faces, grain along X elsewhere.
                    if (d == East) { slot = 0; up = Up; return; }
                    if (d == West) { slot = 1; up = Up; return; }
                    slot = d == Up ? 2 : d == Down ? 3 : d == North ? 4 : 5;
                    up = East;
                    return;
                }
            }
        }
    }

    /// <summary>Minecraft block sound playback with the exact volume/pitch rules of the MC client.</summary>
    internal static class BlockSounds
    {
        private static readonly Dictionary<string, string[]> cache = new Dictionary<string, string[]>();
        private static readonly string[] kinds = { "break", "place", "step", "hit", "fall" };

        private static string Ev(BlockDef def, int kind)
        {
            string group = def?.Sound ?? "stone";
            if (!cache.TryGetValue(group, out var arr))
            {
                arr = new string[kinds.Length];
                for (int i = 0; i < kinds.Length; i++) arr[i] = "block." + group + "." + kinds[i];
                cache[group] = arr;
            }
            return arr[kind];
        }

        /// <summary>Base pitch of a block's sounds (metal 1.5, every other group we use 1.0; all base volumes 1.0).</summary>
        public static float Pitch(BlockDef def) => def != null && def.Sound == "metal" ? 1.5f : 1f;
        private const float Volume = 1f;

        // breaking and placing: volume half-way between the base and full, pitch lowered to 80 %
        public static void Break(BlockDef def, Vector3 pos) => SC.Audio?.Play(Ev(def, 0), pos, (Volume + 1f) / 2f, Pitch(def) * 0.8f);
        public static void Place(BlockDef def, Vector3 pos) => SC.Audio?.Play(Ev(def, 1), pos, (Volume + 1f) / 2f, Pitch(def) * 0.8f);
        // footsteps: 15 % volume, base pitch
        public static void Step(BlockDef def, Vector3 pos) => SC.Audio?.Play(Ev(def, 2), pos, Volume * 0.15f, Pitch(def));
        // repeated hits while mining: a quarter of the break volume, half pitch
        public static void Hit(BlockDef def, Vector3 pos) => SC.Audio?.Play(Ev(def, 3), pos, (Volume + 1f) / 8f, Pitch(def) * 0.5f);
        // landing on the block after a fall: half volume, 75 % pitch
        public static void Fall(BlockDef def, Vector3 pos) => SC.Audio?.Play(Ev(def, 4), pos, Volume * 0.5f, Pitch(def) * 0.75f);
    }

    /// <summary>Minecraft mining rules (tool speeds, harvest levels, break progress per tick).</summary>
    internal static class Mining
    {
        /// <summary>Mining speed per tool tier: wood 2, stone 4, iron 6, diamond 8, netherite 9, gold 12.</summary>
        public static float TierSpeed(ToolTier t)
        {
            switch (t)
            {
                case ToolTier.Wood: return 2f;
                case ToolTier.Stone: return 4f;
                case ToolTier.Iron: return 6f;
                case ToolTier.Diamond: return 8f;
                case ToolTier.Netherite: return 9f;
                case ToolTier.Gold: return 12f;
                default: return 1f;
            }
        }

        /// <summary>Harvest level (gold mines like wood).</summary>
        public static int Level(ToolTier t)
        {
            switch (t)
            {
                case ToolTier.Stone: return 1;
                case ToolTier.Iron: return 2;
                case ToolTier.Diamond: return 3;
                case ToolTier.Netherite: return 4;
                default: return 0;
            }
        }

        private static bool SwordEfficient(BlockDef b)
        {
            string id = b.Id;
            return id.EndsWith("_leaves") || id == "minecraft:pumpkin" || id == "minecraft:carved_pumpkin"
                || id == "minecraft:jack_o_lantern" || id == "minecraft:melon";
        }

        /// <summary>Speed of the held item against a block: tier speed when the tool type matches, sword 1.5 on leaves/pumpkins, else 1.</summary>
        public static float ToolSpeed(ItemDef item, BlockDef b)
        {
            if (item == null || b == null) return 1f;
            if (item.Tool == ToolType.Sword) return SwordEfficient(b) ? 1.5f : 1f;
            if (item.Tool != ToolType.None && item.Tool == b.Tool) return TierSpeed(item.Tier);
            return 1f;
        }

        /// <summary>True when breaking the block with this item yields drops (no tool needed, or right tool type and tier).</summary>
        public static bool CanHarvest(ItemDef item, BlockDef b)
        {
            if (b == null) return false;
            if (!b.RequiresTool) return true;
            if (item == null || item.Tool != b.Tool || item.Tool == ToolType.None) return false;
            return Level(item.Tier) >= Level(b.MinTier);
        }

        /// <summary>Break progress per tick: speed / hardness / 30 with a tool that gets drops (else / 100); speed is divided by 5 while airborne.</summary>
        public static float ProgressPerTick(ItemDef item, BlockDef b, bool onGround)
        {
            if (b == null || b.Hardness < 0f) return 0f;
            float speed = ToolSpeed(item, b);
            if (!onGround) speed /= 5f;
            if (b.Hardness <= 0f) return float.PositiveInfinity;
            return speed / b.Hardness / (CanHarvest(item, b) ? 30f : 100f);
        }

        /// <summary>Durability lost per mined block: swords 2, other tools 1.</summary>
        public static int MineDamage(ItemDef item)
        {
            if (item == null || item.MaxDamage <= 0 || item.Tool == ToolType.None) return 0;
            return item.Tool == ToolType.Sword ? 2 : 1;
        }

        /// <summary>Durability lost per hit on an entity: swords 1, other tools 2.</summary>
        public static int AttackDamageCost(ItemDef item)
        {
            if (item == null || item.MaxDamage <= 0 || item.Tool == ToolType.None) return 0;
            return item.Tool == ToolType.Sword ? 1 : 2;
        }
    }

    internal static class Rng
    {
        public static readonly System.Random R = new System.Random();
        public static float F() => (float)R.NextDouble();
        public static float Range(float a, float b) => a + (b - a) * F();
        public static int Int(int minInclusive, int maxInclusive) => R.Next(minInclusive, maxInclusive + 1);
        /// <summary>Random value around <paramref name="mode"/> with a triangular spread of ±<paramref name="dev"/>.</summary>
        public static float Triangle(float mode, float dev) => mode + dev * (F() - F());
        public static float Gaussian()
        {
            double u1 = 1.0 - R.NextDouble(), u2 = R.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2));
        }
    }

    internal static class CamUtil
    {
        public static Camera Main
        {
            get
            {
                Camera c = null;
                try { c = SC.SR?.MainCamera; } catch { }
                return c != null ? c : Camera.main;
            }
        }
    }
}
