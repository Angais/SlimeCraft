using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    /// <summary>What a Slime Rancher surface yields when "mined" like a Minecraft block.</summary>
    internal sealed class SurfaceKind
    {
        public static readonly SurfaceKind None = new SurfaceKind { Name = "none" };

        public string Name;
        /// <summary>The Minecraft block whose hardness/tool/sound/particles are used.</summary>
        public BlockDef ActsLike;
        public string Drop;
        public int Min = 1, Max = 1;
        public string Extra;
        public float ExtraChance;
        public bool OreTable;

        public bool Harvestable => ActsLike != null;

        public static SurfaceKind Of(string name, string actsLike, string drop = null, int min = 1, int max = 1)
        {
            var def = Content.Block(actsLike);
            if (def == null) return None;
            return new SurfaceKind
            {
                Name = name,
                ActsLike = def,
                Drop = drop ?? (def.Drop == null ? def.Id : def.Drop),
                Min = min,
                Max = max
            };
        }
    }

    /// <summary>
    /// Classifies Slime Rancher world colliders (rock/cliff → stone, ground → dirt, sand, tree bark → logs,
    /// leaves, quarry ore, crystals...) from material / mesh / object names, never touching SR geometry.
    /// SR actors, gadgets, plots, ranch buildings and other interactive objects are never harvestable.
    /// Results are cached per collider (+ sub-mesh).
    /// </summary>
    internal static class TerrainHarvest
    {
        private static readonly Dictionary<long, SurfaceKind> cache = new Dictionary<long, SurfaceKind>();
        /// <summary>Short "collider 'x' mesh 'y' material 'z'" description per cache key (for the action log).</summary>
        private static readonly Dictionary<long, string> descs = new Dictionary<long, string>();
        private static readonly Dictionary<string, SurfaceKind> kinds = new Dictionary<string, SurfaceKind>();
        private static Type[] blacklist;

        // Tokens that mark ranch buildings / interactive props / liquids → never harvest.
        private static readonly string[] excluded =
        {
            "house", "roof", "kit0", "canvaskit", "plot", "corral", "coop", "silo", "incinerator", "garden", "pond",
            "gadget", "teleport", "warp", "drone", "market", "door", "gate", "barrier", "fence", "lamp", "sign",
            "water", "sandsea", "slimesea", "ocean", "chest", "statue", "totem", "ranchtech", "dock",
            "shop", "vending", "machine", "pipe", "panel", "screen", "monitor", "cage", "fountain", "spotlight",
            "slime", "plort", "gordo", "treasure", "pod", "vac", "tarr", "hologram", "ornament", "fashion",
            "ui_", "trigger", "invisible", "kill", "boundary", "bounds", "wall_col"
        };

        /// <summary>Drops every per-collider entry (instance ids of destroyed scene objects must not linger).</summary>
        public static void ClearCache() { cache.Clear(); descs.Clear(); }

        private static long Key(Collider col, int sub) => ((long)col.GetInstanceID() << 8) | (long)(sub & 0xFF);

        /// <summary>"collider 'name' (mesh 'm', material 'mat')" of a classified hit, for logs.</summary>
        public static string Describe(RaycastHit hit)
        {
            var col = hit.collider;
            if (col == null) return "collider (destroyed)";
            int sub = SubMeshOf(col, hit.triangleIndex, out _);
            return descs.TryGetValue(Key(col, sub), out var d) ? d : "collider '" + col.name + "'";
        }

        private static Type[] Blacklist()
        {
            if (blacklist != null) return blacklist;
            // Slime Rancher components (all MonoBehaviours) whose objects must never count as harvestable terrain.
            blacklist = new[]
            {
                typeof(Identifiable), typeof(LandPlot), typeof(LandPlotLocation), typeof(Gadget), typeof(GadgetSite),
                typeof(TeleportSource), typeof(TeleportDestination), typeof(ScorePlort), typeof(UIActivator),
                typeof(PuzzleSlot), typeof(TreasurePod), typeof(GordoEat), typeof(GordoIdentifiable), typeof(DroneStation),
                typeof(SiloStorage), typeof(AccessDoor), typeof(SlimeGateActivator), typeof(Vacuumable),
                typeof(ResourceCycle), typeof(PlortCollector), typeof(KookadobaPatchNode), typeof(LiquidSource)
            };
            return blacklist;
        }

        /// <summary>True if the collider belongs to an SR actor (slime, food, animal...): those are attacked, not mined.</summary>
        public static GameObject SRActorOf(Collider c)
        {
            if (c == null) return null;
            try
            {
                var id = c.GetComponentInParent<Identifiable>();
                if (id != null) return id.gameObject;
                var gid = c.GetComponentInParent<GordoIdentifiable>();
                if (gid != null) return gid.gameObject;
            }
            catch { }
            return null;
        }

        public static SurfaceKind Classify(RaycastHit hit, BlockWorld world)
        {
            var col = hit.collider;
            if (col == null || col.isTrigger || world.IsBlockCollider(col)) return SurfaceKind.None;
            int sub = SubMeshOf(col, hit.triangleIndex, out Renderer subRenderer);
            long key = Key(col, sub);
            if (cache.TryGetValue(key, out var k)) return k;
            string desc = null;
            try { k = ClassifyUncached(col, sub, subRenderer, out desc); }
            catch (Exception e) { RateLog.Error("classify SR surface", e, 30f); k = SurfaceKind.None; }
            cache[key] = k;
            if (desc == null) desc = "collider '" + col.name + "'";
            descs[key] = desc;
            // once per collider (+ sub-mesh): what the SR surface was classified as and from which names
            if (RateLog.Actions)
                RateLog.Action("classify", "SR surface classified as '" + k.Name + "'"
                    + (k.Harvestable ? " (acts like " + k.ActsLike.Id + ", drops " + (k.OreTable ? "ore table" : k.Drop) + ")" : " (not harvestable)")
                    + ": " + desc + ", zone '" + SafeZone() + "'", 0f);
            return k;
        }

        private static string SafeZone()
        {
            try { return SC.SR?.ZoneName ?? ""; } catch { return ""; }
        }

        private static string Clip(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "...";

        private static int SubMeshOf(Collider col, int triangleIndex, out Renderer renderer)
        {
            renderer = null;
            if (!(col is MeshCollider mc) || mc.sharedMesh == null || triangleIndex < 0) return -1;
            try
            {
                var mf = col.GetComponent<MeshFilter>();
                var r = col.GetComponent<Renderer>();
                if (mf == null || r == null || mf.sharedMesh != mc.sharedMesh || mc.sharedMesh.subMeshCount <= 1) return -1;
                var mesh = mc.sharedMesh;
                long index = (long)triangleIndex * 3;
                for (int s = 0; s < mesh.subMeshCount; s++)
                {
                    long start = mesh.GetIndexStart(s), cnt = mesh.GetIndexCount(s);
                    if (index >= start && index < start + cnt) { renderer = r; return s; }
                }
            }
            catch { }
            return -1;
        }

        private static SurfaceKind ClassifyUncached(Collider col, int sub, Renderer subRenderer, out string desc)
        {
            var sr = SC.SR;
            desc = "collider '" + col.name + "'";
            if (col.attachedRigidbody != null && !col.attachedRigidbody.isKinematic) { desc += " [dynamic rigidbody]"; return SurfaceKind.None; }
            foreach (var t in Blacklist())
                if (col.GetComponentInParent(t) != null) { desc += " [SR " + t.Name + "]"; return SurfaceKind.None; }

            var mats = new StringBuilder();
            var objs = new StringBuilder();
            var parents = new StringBuilder();

            objs.Append(col.gameObject.name).Append('|');
            if (col is MeshCollider mc && mc.sharedMesh != null) objs.Append(mc.sharedMesh.name).Append('|');
            if (col is TerrainCollider) objs.Append("terrain|");

            Renderer r = subRenderer ?? col.GetComponent<Renderer>();
            var tr = col.transform;
            if (r == null && tr.parent != null)
            {
                r = tr.parent.GetComponent<Renderer>();
                if (r == null)
                {
                    int n = Mathf.Min(tr.parent.childCount, 8);
                    for (int i = 0; i < n && r == null; i++) r = tr.parent.GetChild(i).GetComponent<Renderer>();
                }
            }
            if (r != null)
            {
                objs.Append(r.gameObject.name).Append('|');
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) objs.Append(mf.sharedMesh.name).Append('|');
                var ms = r.sharedMaterials;
                for (int i = 0; i < ms.Length; i++)
                {
                    if (sub >= 0 && i != sub) continue;
                    AppendMaterial(mats, ms[i]);
                }
            }
            for (var p = tr.parent; p != null && parents.Length < 400; p = p.parent)
            {
                parents.Append(p.name).Append('|');
                if (p.parent == null) break;
            }

            string m = mats.ToString().ToLowerInvariant();
            string o = objs.ToString().ToLowerInvariant();
            string pa = parents.ToString().ToLowerInvariant();
            string zone = "";
            try { zone = (sr?.ZoneName ?? "").ToLowerInvariant(); } catch { }

            SurfaceKind result = SurfaceKind.None;
            bool excluded = Excluded(m) || Excluded(o) || ExcludedParent(pa);
            if (!excluded)
            {
                string ctx = m + "|" + o + "|" + pa + "|" + zone;
                result = Match(m, ctx) ?? Match(o, ctx) ?? Match(pa, ctx) ?? SurfaceKind.None;
            }
            desc = "collider '" + col.name + "' names '" + Clip(o.TrimEnd('|'), 140) + "' materials '" + Clip(m.TrimEnd('|'), 160)
                + "' parents '" + Clip(pa.TrimEnd('|'), 100) + "'" + (excluded ? " [excluded token]" : "");
            if (BlocksConfig.DebugHarvest != null && BlocksConfig.DebugHarvest.Value)
                SC.Log?.LogInfo("[Blocks] harvest classify mats='" + m + "' objs='" + o + "' parents='" + pa + "' zone='" + zone + "' → " + result.Name);
            return result;
        }

        private static void AppendMaterial(StringBuilder sb, Material mat)
        {
            if (mat == null) return;
            sb.Append(mat.name).Append('|');
            if (mat.shader != null) sb.Append(mat.shader.name).Append('|');
            try
            {
                if (mat.HasProperty("_MainTex")) { var t = mat.GetTexture("_MainTex"); if (t != null) sb.Append(t.name).Append('|'); }
                if (mat.HasProperty("_PrimaryTex")) { var t = mat.GetTexture("_PrimaryTex"); if (t != null) sb.Append(t.name).Append('|'); }
            }
            catch { }
        }

        private static bool Has(string s, string t) => s.IndexOf(t, StringComparison.Ordinal) >= 0;
        private static bool HasAny(string s, params string[] ts)
        {
            for (int i = 0; i < ts.Length; i++) if (s.IndexOf(ts[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static bool Excluded(string s)
        {
            for (int i = 0; i < excluded.Length; i++) if (s.IndexOf(excluded[i], StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Parents are often generic groups ("Zone_Ranch", "cellReef"...): only strong building/plot tokens count.
        private static bool ExcludedParent(string s) =>
            HasAny(s, "house", "plot", "corral", "coop", "silo", "incinerator", "garden", "gadget", "teleport", "drone", "market", "lab_", "dock");

        private static SurfaceKind K(string name, string actsLike, string drop = null, int min = 1, int max = 1)
        {
            if (!kinds.TryGetValue(name, out var k))
            {
                k = SurfaceKind.Of(name, actsLike, drop, min, max);
                kinds[name] = k;
            }
            return k;
        }

        /// <summary>Ordered keyword rules (most specific first). ctx adds zone hints (reef, quarry, moss, ash, desert, ruins).</summary>
        private static SurfaceKind Match(string s, string ctx)
        {
            if (string.IsNullOrEmpty(s)) return null;
            bool reef = Has(ctx, "reef"), quarry = Has(ctx, "quar"), moss = Has(ctx, "moss"), ash = Has(ctx, "ash"),
                 desert = Has(ctx, "desert"), ruin = Has(ctx, "ruin");

            // Ores (Indigo Quarry): "envQuarOre01", "ore_01", "orePebbles_01"
            if (HasAny(s, "quarore", "_ore", "ore_", "orepebble") || s.StartsWith("ore"))
            {
                var k = K("ore", "minecraft:coal_ore");
                if (k.Harvestable) k.OreTable = true;
                return k;
            }
            if (Has(s, "quartz")) return K("quartz", "minecraft:quartz_block");
            if (HasAny(s, "crystal", "crys")) return K("crystal", "minecraft:amethyst_block");
            if (Has(s, "glow")) return K("glow", "minecraft:glowstone", "minecraft:glowstone_dust", 2, 4);
            if (Has(s, "mushroom")) return K("mushroom", "minecraft:shroomlight");
            if (Has(s, "coral")) return K("coral", "minecraft:prismarine");

            // Leaves before bark: "objTreeLeaves01", "envLeavesMoss05", "bushBlob01"
            if (HasAny(s, "leaves", "leaf", "bush", "foliage", "canopy", "hedge"))
            {
                bool pink = HasAny(ctx, "cherry", "pink", "sakura");
                var k = pink ? K("cherry_leaves", "minecraft:cherry_leaves", "minecraft:cherry_leaves")
                             : K("leaves", "minecraft:oak_leaves", "minecraft:oak_leaves");
                if (!pink && k.Harvestable && k.Extra == null) { k.Extra = "minecraft:apple"; k.ExtraChance = -1f; } // chance from config
                return k;
            }
            // Wood: "objTreeBark01", "treeTrunk01", "objTreeCore01", "treeMossStump02"
            if (HasAny(s, "bark", "trunk", "treecore", "stump", "branch", "tree", "log_", "_log"))
            {
                if (moss || ash) return K("spruce_log", "minecraft:spruce_log");
                if (HasAny(ctx, "pear", "cherry", "pink")) return K("cherry_log", "minecraft:cherry_log");
                if (desert || ruin) return K("birch_log", "minecraft:birch_log");
                return K("oak_log", "minecraft:oak_log");
            }
            if (HasAny(s, "plank", "woodplat", "platwood", "woodkit", "wood")) return K("planks", "minecraft:oak_planks");
            if (Has(s, "hay")) return K("hay", "minecraft:hay_block");

            // Rock-like: "envRocky01", "envDesertCliff01", "rockBoulder01", "ruinBlock01", "envAshCobbled01"
            if (HasAny(s, "rock", "cliff", "boulder", "crag", "stone", "mtn", "mnt_", "mineral", "mountain", "pebble",
                       "pillar", "ruinblock", "ruinsblock", "rubble", "cobble", "brick", "mesa", "spire", "ledge"))
            {
                if (Has(s, "sandstone")) return K("sandstone", "minecraft:sand");
                if (Has(s, "cobble")) return K("cobblestone", "minecraft:cobblestone");
                if (ruin && HasAny(s, "block", "pillar", "brick", "ruin"))
                    return moss ? K("mossy_stone_bricks", "minecraft:mossy_stone_bricks") : K("stone_bricks", "minecraft:stone_bricks");
                if (moss) return K("mossy_cobblestone", "minecraft:mossy_cobblestone");
                if (reef) return K("terracotta", "minecraft:terracotta");
                if (ash) return K("netherrack", "minecraft:netherrack");
                if (quarry) return K("deepslate", "minecraft:deepslate");
                if (desert) return K("calcite", "minecraft:calcite");
                return K("stone", "minecraft:stone"); // drops cobblestone like MC stone
            }
            if (HasAny(s, "sand", "beach", "dune", "shore"))
            {
                if (ash) return K("gravel", "minecraft:gravel");
                if (reef) return K("red_sand", "minecraft:red_sand");
                return K("sand", "minecraft:sand");
            }
            if (HasAny(s, "grass", "turf", "lawn", "meadow")) return K("grass", "minecraft:grass_block"); // drops dirt
            if (HasAny(s, "dirt", "soil", "ground", "mud", "nav", "earth", "terrain"))
            {
                if (ash) return K("coarse_dirt", "minecraft:coarse_dirt");
                if (moss) return K("podzol", "minecraft:podzol");
                return K("dirt", "minecraft:dirt");
            }
            if (Has(s, "moss")) return K("moss", "minecraft:moss_block");
            if (Has(s, "snow")) return K("snow", "minecraft:snow_block");
            return null;
        }

        /// <summary>
        /// Rolls the drops of a completed harvest (ore table honors the pickaxe tier like MC) and spawns them via
        /// SC.Entities. Returns what was dropped (for the log), e.g. "minecraft:cobblestone x1".
        /// </summary>
        public static string RollDrops(SurfaceKind k, ItemDef tool, Vector3 point, Vector3 normal)
        {
            if (k == null || !k.Harvestable) return "none";
            if (SC.Entities == null) return "none (Entities module missing)";
            if (k.OreTable)
            {
                int lvl = tool != null && tool.Tool == ToolType.Pickaxe ? Mining.Level(tool.Tier) : -1;
                float r = Rng.F();
                string id; int n = 1;
                if (r < 0.03f && lvl >= 2) id = "minecraft:emerald";
                else if (r < 0.07f && lvl >= 2) id = "minecraft:diamond";
                else if (r < 0.15f && lvl >= 2) { id = "minecraft:redstone"; n = Rng.Int(4, 5); }
                else if (r < 0.23f && lvl >= 1) { id = "minecraft:lapis_lazuli"; n = Rng.Int(4, 6); }
                else if (r < 0.31f && lvl >= 2) id = "minecraft:gold_ore";
                else if (r < 0.51f && lvl >= 1) id = "minecraft:iron_ore";
                else id = "minecraft:coal";
                BlockLoot.PopAt(id, n, point, normal);
                return id + " x" + n;
            }
            string result = "none";
            if (!string.IsNullOrEmpty(k.Drop))
            {
                int n = Rng.Int(k.Min, k.Max);
                BlockLoot.PopAt(k.Drop, n, point, normal);
                result = k.Drop + " x" + n;
            }
            if (k.Extra != null)
            {
                float chance = k.ExtraChance >= 0f ? k.ExtraChance : (BlocksConfig.LeafAppleChance?.Value ?? 0.05f);
                if (Rng.F() < chance)
                {
                    BlockLoot.PopAt(k.Extra, 1, point, normal);
                    result += " + " + k.Extra + " x1";
                }
            }
            return result;
        }
    }
}
