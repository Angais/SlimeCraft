using System;
using System.Collections.Generic;
using System.Linq;

namespace SlimeCraft
{
    public enum RenderLayer { Opaque, Cutout, Translucent }
    public enum ToolType { None, Pickaxe, Axe, Shovel, Sword, Hoe }
    /// <summary>Minecraft tool tiers (mining speed 2/4/6/8/12/9 for wood/stone/iron/diamond/gold/netherite).</summary>
    public enum ToolTier { None = 0, Wood = 1, Stone = 2, Iron = 3, Diamond = 4, Netherite = 5, Gold = 6 }
    public enum BlockRotation { None, Horizontal /* front faces the player */, Axis /* logs: along clicked face axis */ }
    public enum ItemKind { Block, Tool, Weapon, Food, Material, SpawnEgg, Special }

    /// <summary>
    /// Static definition of a Minecraft block. Faces use atlas texture specs (see IBlockAtlas):
    /// order Up, Down, North(-Z... front), South, East, West. For Horizontal rotation, "North" is the FRONT face.
    /// For Axis rotation, Up/Down are the log "end" faces when the log stands vertically.
    /// </summary>
    public sealed class BlockDef
    {
        public string Id;
        public string[] Faces = new string[6];
        public RenderLayer Layer = RenderLayer.Opaque;
        public BlockRotation Rotation = BlockRotation.None;
        /// <summary>Minecraft hardness (seconds-ish base; -1 = unbreakable).</summary>
        public float Hardness = 1.5f;
        public float BlastResistance = 6f;
        public ToolType Tool = ToolType.None;
        /// <summary>Needs the right tool (with MinTier) to drop anything (stone/ores).</summary>
        public bool RequiresTool;
        public ToolTier MinTier = ToolTier.None;
        /// <summary>Sound group: events are "block.{Sound}.break/place/step/hit/fall".</summary>
        public string Sound = "stone";
        /// <summary>Light emission 0..15 (spawns a Unity point light for &gt;0).</summary>
        public int Light;
        public bool Gravity;          // sand, gravel: falls when unsupported
        public bool Bouncy;           // slime block: bounces entities
        public bool Sticky;           // honey: slows movement
        public float Slipperiness = 0.6f; // ice 0.98
        public bool IsTnt;
        /// <summary>Item dropped when broken (null = own item; "" = nothing).</summary>
        public string Drop;
        public int DropCount = 1;

        public string Up => Faces[0];
        public string Down => Faces[1];
        public string DisplayName => Content.DisplayName(Id);
    }

    /// <summary>Static definition of an item. Every block has a matching block item with the same id.</summary>
    public sealed class ItemDef
    {
        public string Id;
        public ItemKind Kind;
        /// <summary>Texture path for the GUI/held sprite (e.g. "item/apple"). Null for block items (icon rendered from the block).</summary>
        public string Texture;
        public int MaxStack = 64;
        public string BlockId;           // Kind == Block
        public ToolType Tool;            // Tool/Weapon
        public ToolTier Tier;
        public int MaxDamage;            // durability (0 = unbreakable)
        public float AttackDamage = 1f;  // Minecraft attack damage (fist = 1)
        public float AttackSpeed = 4f;   // attacks per second (sword 1.6, axe ~1, others 4) → cooldown
        public int FoodNutrition;        // Minecraft hunger points restored
        public float FoodSaturation;
        /// <summary>Slime Rancher health restored when eaten (SR max 100+).</summary>
        public int SRHeal;
        /// <summary>Slime Rancher energy restored when eaten.</summary>
        public int SREnergy;
        /// <summary>
        /// Slime Rancher Identifiable.Id this item turns into when a dropped stack touches a slime
        /// (so SR slimes can eat Minecraft food), e.g. "CARROT_VEGGIE". Null = not edible by slimes.
        /// </summary>
        public string SRFoodEquivalent;
        public string SpawnsMob;         // SpawnEgg
        public string Special;           // "vacpack", "flint_and_steel", "bow", "arrow", "bucket"

        public bool IsBlock => Kind == ItemKind.Block;
        public bool IsFood => FoodNutrition > 0;
        public string DisplayName => Content.DisplayName(Id);
    }

    public static class MobIds
    {
        public const string Creeper = "minecraft:creeper";
        public const string Zombie = "minecraft:zombie";
        public const string Skeleton = "minecraft:skeleton";
        public const string Pig = "minecraft:pig";
        public const string Cow = "minecraft:cow";
        public const string Sheep = "minecraft:sheep";
        public const string Chicken = "minecraft:chicken";
        public const string Slime = "minecraft:slime";
        public const string Enderman = "minecraft:enderman";
        public const string IronGolem = "minecraft:iron_golem";
        public static readonly string[] All = { Creeper, Zombie, Skeleton, Pig, Cow, Sheep, Chicken, Slime, Enderman, IronGolem };
        public static readonly string[] Hostile = { Creeper, Zombie, Skeleton, Slime, Enderman };
        public static readonly string[] Passive = { Pig, Cow, Sheep, Chicken };
    }

    /// <summary>Registry of all blocks and items. Pure data; safe to use from any module at any time.</summary>
    public static class Content
    {
        public const string Vacpack = "slimecraft:vacpack";
        public const string GrassTint = "91BD59";
        public const string FoliageTint = "77AB2F";

        private static readonly Dictionary<string, BlockDef> blocks = new Dictionary<string, BlockDef>();
        private static readonly Dictionary<string, ItemDef> items = new Dictionary<string, ItemDef>();
        private static readonly List<string> creativeOrder = new List<string>();
        private static readonly Dictionary<string, string> nameOverrides = new Dictionary<string, string>();

        public static IEnumerable<BlockDef> Blocks => blocks.Values;
        public static IEnumerable<ItemDef> Items => items.Values;
        /// <summary>Item ids in creative-inventory order.</summary>
        public static IReadOnlyList<string> CreativeOrder => creativeOrder;

        public static BlockDef Block(string id) => id != null && blocks.TryGetValue(Norm(id), out var b) ? b : null;
        public static ItemDef Item(string id) => id != null && items.TryGetValue(Norm(id), out var i) ? i : null;

        /// <summary>"stone" → "minecraft:stone"; keeps namespaced ids.</summary>
        public static string Norm(string id) => id == null ? null : (id.IndexOf(':') >= 0 ? id : "minecraft:" + id);

        /// <summary>English name: override, else the jar's lang (via SC.Assets), else prettified id.</summary>
        public static string DisplayName(string id)
        {
            if (id == null) return "";
            if (nameOverrides.TryGetValue(id, out var n)) return n;
            string path = id.Substring(id.IndexOf(':') + 1);
            string ns = id.Substring(0, Math.Max(0, id.IndexOf(':')));
            if (SC.Assets != null && SC.Assets.Ready)
            {
                var key = (blocks.ContainsKey(id) ? "block." : "item.") + ns + "." + path;
                var t = SC.Assets.Translate(key);
                if (t != key) return t;
                var t2 = SC.Assets.Translate("item." + ns + "." + path);
                if (t2 != "item." + ns + "." + path) return t2;
            }
            return string.Join(" ", path.Split('_').Select(w => w.Length == 0 ? w : char.ToUpper(w[0]) + w.Substring(1)));
        }

        // ------------------------------------------------------------------ builders
        private static BlockDef B(string id, string all, float hardness, string sound, ToolType tool = ToolType.None, bool requiresTool = false)
        {
            var b = new BlockDef { Id = Norm(id), Hardness = hardness, Sound = sound, Tool = tool, RequiresTool = requiresTool };
            for (int i = 0; i < 6; i++) b.Faces[i] = all;
            b.BlastResistance = hardness < 0 ? 3600000f : Math.Max(hardness, 0.5f) * 4f;
            blocks[b.Id] = b;
            items[b.Id] = new ItemDef { Id = b.Id, Kind = ItemKind.Block, BlockId = b.Id };
            creativeOrder.Add(b.Id);
            return b;
        }
        private static BlockDef Column(BlockDef b, string side, string end) { b.Faces = new[] { end, end, side, side, side, side }; return b; }
        private static BlockDef BottomTop(BlockDef b, string side, string bottom, string top) { b.Faces = new[] { top, bottom, side, side, side, side }; return b; }
        private static BlockDef Front(BlockDef b, string front, string side, string top, string bottom = null)
        { b.Faces = new[] { top, bottom ?? top, front, side, side, side }; b.Rotation = BlockRotation.Horizontal; return b; }
        private static BlockDef Log(string id, string wood, string sound = "wood")
        { var b = Column(B(id, null, 2f, sound, ToolType.Axe), "block/" + wood + "_log", "block/" + wood + "_log_top"); b.Rotation = BlockRotation.Axis; return b; }

        private static ItemDef I(string id, ItemKind kind, string texture = null, int maxStack = 64)
        {
            var it = new ItemDef { Id = Norm(id), Kind = kind, Texture = texture ?? ("item/" + Norm(id).Substring(Norm(id).IndexOf(':') + 1)), MaxStack = maxStack };
            items[it.Id] = it;
            creativeOrder.Add(it.Id);
            return it;
        }
        private static ItemDef Tool(string id, ToolType type, ToolTier tier, int durability, float damage, float speed)
        { var t = I(id, type == ToolType.Sword ? ItemKind.Weapon : ItemKind.Tool, null, 1); t.Tool = type; t.Tier = tier; t.MaxDamage = durability; t.AttackDamage = damage; t.AttackSpeed = speed; return t; }
        private static ItemDef Food(string id, int nutrition, float saturation, string srFood = null)
        { var f = I(id, ItemKind.Food); f.FoodNutrition = nutrition; f.FoodSaturation = saturation; f.SRHeal = nutrition * 5; f.SREnergy = (int)(saturation * 4); f.SRFoodEquivalent = srFood; return f; }
        private static ItemDef Egg(string mob) { var e = I(mob + "_spawn_egg", ItemKind.SpawnEgg); e.SpawnsMob = Norm(mob); return e; }

        static Content()
        {
            // ---------------- Special
            var vac = new ItemDef { Id = Vacpack, Kind = ItemKind.Special, Special = "vacpack", MaxStack = 1, Texture = null };
            items[vac.Id] = vac; creativeOrder.Add(vac.Id);
            nameOverrides[Vacpack] = "Vacpack";

            // ---------------- Natural / building blocks
            BottomTop(B("grass_block", null, 0.6f, "grass", ToolType.Shovel), "block/grass_block_side|block/grass_block_side_overlay@" + GrassTint, "block/dirt", "block/grass_block_top@" + GrassTint).Drop = "minecraft:dirt";
            B("dirt", "block/dirt", 0.5f, "gravel", ToolType.Shovel);
            B("coarse_dirt", "block/coarse_dirt", 0.5f, "gravel", ToolType.Shovel);
            BottomTop(B("podzol", null, 0.5f, "gravel", ToolType.Shovel), "block/podzol_side", "block/dirt", "block/podzol_top").Drop = "minecraft:dirt";
            B("moss_block", "block/moss_block", 0.1f, "moss", ToolType.Hoe);
            B("stone", "block/stone", 1.5f, "stone", ToolType.Pickaxe, true).Drop = "minecraft:cobblestone";
            B("cobblestone", "block/cobblestone", 2f, "stone", ToolType.Pickaxe, true);
            B("mossy_cobblestone", "block/mossy_cobblestone", 2f, "stone", ToolType.Pickaxe, true);
            B("smooth_stone", "block/smooth_stone", 2f, "stone", ToolType.Pickaxe, true);
            B("stone_bricks", "block/stone_bricks", 1.5f, "stone", ToolType.Pickaxe, true);
            B("mossy_stone_bricks", "block/mossy_stone_bricks", 1.5f, "stone", ToolType.Pickaxe, true);
            B("bricks", "block/bricks", 2f, "stone", ToolType.Pickaxe, true);
            B("mud_bricks", "block/mud_bricks", 1.5f, "mud_bricks", ToolType.Pickaxe, true);
            B("deepslate", "block/deepslate", 3f, "deepslate", ToolType.Pickaxe, true);
            B("calcite", "block/calcite", 0.75f, "calcite", ToolType.Pickaxe, true);
            B("tuff", "block/tuff", 1.5f, "tuff", ToolType.Pickaxe, true);
            B("sand", "block/sand", 0.5f, "sand", ToolType.Shovel).Gravity = true;
            B("red_sand", "block/red_sand", 0.5f, "sand", ToolType.Shovel).Gravity = true;
            B("gravel", "block/gravel", 0.6f, "gravel", ToolType.Shovel).Gravity = true;
            B("clay", "block/clay", 0.6f, "gravel", ToolType.Shovel);
            B("terracotta", "block/terracotta", 1.25f, "stone", ToolType.Pickaxe, true);
            Front(B("crafting_table", null, 2.5f, "wood", ToolType.Axe), "block/crafting_table_front", "block/crafting_table_side", "block/crafting_table_top", "block/oak_planks");
            Front(B("furnace", null, 3.5f, "stone", ToolType.Pickaxe, true), "block/furnace_front", "block/furnace_side", "block/furnace_top");
            Column(B("bookshelf", null, 1.5f, "wood", ToolType.Axe), "block/bookshelf", "block/oak_planks");

            // ---------------- Wood
            Log("oak_log", "oak"); Log("birch_log", "birch"); Log("spruce_log", "spruce"); Log("cherry_log", "cherry", "cherry_wood");
            B("oak_planks", "block/oak_planks", 2f, "wood", ToolType.Axe);
            B("birch_planks", "block/birch_planks", 2f, "wood", ToolType.Axe);
            B("spruce_planks", "block/spruce_planks", 2f, "wood", ToolType.Axe);
            B("cherry_planks", "block/cherry_planks", 2f, "cherry_wood", ToolType.Axe);
            B("bamboo_planks", "block/bamboo_planks", 2f, "bamboo_wood", ToolType.Axe);
            var leaves = B("oak_leaves", "block/oak_leaves@" + FoliageTint, 0.2f, "grass", ToolType.Hoe); leaves.Layer = RenderLayer.Cutout; leaves.Drop = "";
            var cherryLeaves = B("cherry_leaves", "block/cherry_leaves", 0.2f, "cherry_leaves", ToolType.Hoe); cherryLeaves.Layer = RenderLayer.Cutout; cherryLeaves.Drop = "";

            // ---------------- Glass / ice / transparent
            var glass = B("glass", "block/glass", 0.3f, "glass"); glass.Layer = RenderLayer.Cutout; glass.Drop = "";
            var sglass = B("white_stained_glass", "block/white_stained_glass", 0.3f, "glass"); sglass.Layer = RenderLayer.Translucent; sglass.Drop = "";
            var lbglass = B("light_blue_stained_glass", "block/light_blue_stained_glass", 0.3f, "glass"); lbglass.Layer = RenderLayer.Translucent; lbglass.Drop = "";
            var ice = B("ice", "block/ice", 0.5f, "glass", ToolType.Pickaxe); ice.Layer = RenderLayer.Translucent; ice.Slipperiness = 0.98f; ice.Drop = "";
            B("packed_ice", "block/packed_ice", 0.5f, "glass", ToolType.Pickaxe).Slipperiness = 0.98f;
            B("blue_ice", "block/blue_ice", 2.8f, "glass", ToolType.Pickaxe).Slipperiness = 0.989f;
            B("snow_block", "block/snow", 0.2f, "snow", ToolType.Shovel);
            var slime = B("slime_block", "block/slime_block", 0f, "slime_block"); slime.Layer = RenderLayer.Translucent; slime.Bouncy = true;
            var honey = BottomTop(B("honey_block", null, 0f, "honey_block"), "block/honey_block_side", "block/honey_block_bottom", "block/honey_block_top"); honey.Layer = RenderLayer.Translucent; honey.Sticky = true;

            // ---------------- Light sources
            B("glowstone", "block/glowstone", 0.3f, "glass").Light = 15;
            B("sea_lantern", "block/sea_lantern", 0.3f, "glass").Light = 15;
            B("shroomlight", "block/shroomlight", 1f, "shroomlight", ToolType.Hoe).Light = 15;
            var lamp = B("redstone_lamp", "block/redstone_lamp_on", 0.3f, "glass"); lamp.Light = 15;
            var jack = Front(B("jack_o_lantern", null, 1f, "wood", ToolType.Axe), "block/jack_o_lantern", "block/pumpkin_side", "block/pumpkin_top"); jack.Light = 15;
            Column(B("pumpkin", null, 1f, "wood", ToolType.Axe), "block/pumpkin_side", "block/pumpkin_top");
            Front(B("carved_pumpkin", null, 1f, "wood", ToolType.Axe), "block/carved_pumpkin", "block/pumpkin_side", "block/pumpkin_top");
            Column(B("melon", null, 1f, "wood", ToolType.Axe), "block/melon_side", "block/melon_top");
            var hay = Column(B("hay_block", null, 0.5f, "grass", ToolType.Hoe), "block/hay_block_side", "block/hay_block_top"); hay.Rotation = BlockRotation.Axis;

            // ---------------- Ores & precious
            B("coal_ore", "block/coal_ore", 3f, "stone", ToolType.Pickaxe, true).Drop = "minecraft:coal";
            var iron = B("iron_ore", "block/iron_ore", 3f, "stone", ToolType.Pickaxe, true); iron.MinTier = ToolTier.Stone;
            var gold = B("gold_ore", "block/gold_ore", 3f, "stone", ToolType.Pickaxe, true); gold.MinTier = ToolTier.Iron;
            var dia = B("diamond_ore", "block/diamond_ore", 3f, "stone", ToolType.Pickaxe, true); dia.MinTier = ToolTier.Iron; dia.Drop = "minecraft:diamond";
            var em = B("emerald_ore", "block/emerald_ore", 3f, "stone", ToolType.Pickaxe, true); em.MinTier = ToolTier.Iron; em.Drop = "minecraft:emerald";
            B("coal_block", "block/coal_block", 5f, "stone", ToolType.Pickaxe, true);
            B("iron_block", "block/iron_block", 5f, "metal", ToolType.Pickaxe, true).MinTier = ToolTier.Stone;
            B("gold_block", "block/gold_block", 3f, "metal", ToolType.Pickaxe, true).MinTier = ToolTier.Iron;
            B("diamond_block", "block/diamond_block", 5f, "metal", ToolType.Pickaxe, true).MinTier = ToolTier.Iron;
            B("emerald_block", "block/emerald_block", 5f, "metal", ToolType.Pickaxe, true).MinTier = ToolTier.Iron;
            B("lapis_block", "block/lapis_block", 3f, "stone", ToolType.Pickaxe, true).MinTier = ToolTier.Stone;
            B("redstone_block", "block/redstone_block", 5f, "metal", ToolType.Pickaxe, true);
            B("copper_block", "block/copper_block", 3f, "copper", ToolType.Pickaxe, true).MinTier = ToolTier.Stone;
            B("amethyst_block", "block/amethyst_block", 1.5f, "amethyst_block", ToolType.Pickaxe, true);
            BottomTop(B("quartz_block", null, 0.8f, "stone", ToolType.Pickaxe, true), "block/quartz_block_side", "block/quartz_block_bottom", "block/quartz_block_top");
            B("prismarine", "block/prismarine", 1.5f, "stone", ToolType.Pickaxe, true);
            B("dark_prismarine", "block/dark_prismarine", 1.5f, "stone", ToolType.Pickaxe, true);
            B("purpur_block", "block/purpur_block", 1.5f, "stone", ToolType.Pickaxe, true);
            B("end_stone", "block/end_stone", 3f, "stone", ToolType.Pickaxe, true);
            B("netherrack", "block/netherrack", 0.4f, "netherrack", ToolType.Pickaxe, true);
            B("obsidian", "block/obsidian", 50f, "stone", ToolType.Pickaxe, true).MinTier = ToolTier.Diamond;
            B("crying_obsidian", "block/crying_obsidian", 50f, "stone", ToolType.Pickaxe, true).MinTier = ToolTier.Diamond;
            blocks["minecraft:obsidian"].BlastResistance = 1200f; blocks["minecraft:crying_obsidian"].BlastResistance = 1200f;
            var bedrock = B("bedrock", "block/bedrock", -1f, "stone"); bedrock.Drop = "";

            // ---------------- Wool (16 colors)
            foreach (var c in new[] { "white", "light_gray", "gray", "black", "brown", "red", "orange", "yellow", "lime", "green", "cyan", "light_blue", "blue", "purple", "magenta", "pink" })
                B(c + "_wool", "block/" + c + "_wool", 0.8f, "wool", ToolType.None);

            // ---------------- TNT
            var tnt = BottomTop(B("tnt", null, 0f, "grass"), "block/tnt_side", "block/tnt_bottom", "block/tnt_top"); tnt.IsTnt = true; tnt.BlastResistance = 0f;

            // ---------------- Tools & weapons (MC stats)
            Tool("wooden_sword", ToolType.Sword, ToolTier.Wood, 59, 4, 1.6f);
            Tool("stone_sword", ToolType.Sword, ToolTier.Stone, 131, 5, 1.6f);
            Tool("iron_sword", ToolType.Sword, ToolTier.Iron, 250, 6, 1.6f);
            Tool("golden_sword", ToolType.Sword, ToolTier.Gold, 32, 4, 1.6f);
            Tool("diamond_sword", ToolType.Sword, ToolTier.Diamond, 1561, 7, 1.6f);
            Tool("netherite_sword", ToolType.Sword, ToolTier.Netherite, 2031, 8, 1.6f);
            // Attack damage = 1 (bare hand) + the tool's base value + a tier bonus (wood/gold 0, stone 1, iron 2,
            // diamond 3, netherite 4); attack speed = 4 + the tool's speed offset. Pickaxe 1.0/-2.8, shovel 1.5/-3.0,
            // axes vary per tier.
            foreach (var t in new[]
            {
                ("wooden", ToolTier.Wood, 59, 0f, 7f, 0.8f), ("stone", ToolTier.Stone, 131, 1f, 9f, 0.8f),
                ("iron", ToolTier.Iron, 250, 2f, 9f, 0.9f), ("golden", ToolTier.Gold, 32, 0f, 7f, 1f),
                ("diamond", ToolTier.Diamond, 1561, 3f, 9f, 1f), ("netherite", ToolTier.Netherite, 2031, 4f, 10f, 1f)
            })
            {
                Tool(t.Item1 + "_pickaxe", ToolType.Pickaxe, t.Item2, t.Item3, 2f + t.Item4, 1.2f);
                Tool(t.Item1 + "_axe", ToolType.Axe, t.Item2, t.Item3, t.Item5, t.Item6);
                Tool(t.Item1 + "_shovel", ToolType.Shovel, t.Item2, t.Item3, 2.5f + t.Item4, 1f);
            }
            var fns = I("flint_and_steel", ItemKind.Tool, null, 1); fns.Special = "flint_and_steel"; fns.MaxDamage = 64;
            var bow = I("bow", ItemKind.Weapon, null, 1); bow.Special = "bow"; bow.MaxDamage = 384;
            I("arrow", ItemKind.Material).Special = "arrow";

            // ---------------- Food (nutrition, saturation, SR slime equivalent)
            Food("apple", 4, 2.4f, "POGO_FRUIT");
            Food("golden_apple", 4, 9.6f, "GOLD_PLORT").SRHeal = 100;
            Food("carrot", 3, 3.6f, "CARROT_VEGGIE");
            Food("potato", 1, 0.6f, "OCAOCA_VEGGIE");
            Food("baked_potato", 5, 6f);
            Food("bread", 5, 6f);
            Food("melon_slice", 2, 1.2f, "CUBERRY_FRUIT");
            Food("cookie", 2, 0.4f);
            Food("pumpkin_pie", 8, 4.8f);
            Food("porkchop", 3, 1.8f, "ROOSTER");
            Food("cooked_porkchop", 8, 12.8f);
            Food("beef", 3, 1.8f, "ROOSTER");
            Food("cooked_beef", 8, 12.8f);
            Food("chicken", 2, 1.2f, "HEN");
            Food("cooked_chicken", 6, 7.2f);
            Food("mutton", 2, 1.2f, "HEN");
            Food("cooked_mutton", 6, 9.6f);
            Food("honey_bottle", 6, 1.2f).MaxStack = 16;
            Food("rotten_flesh", 4, 0.8f);

            // ---------------- Materials
            foreach (var m in new[] { "stick", "coal", "iron_ingot", "gold_ingot", "copper_ingot", "netherite_ingot", "diamond", "emerald", "lapis_lazuli", "redstone", "glowstone_dust", "gunpowder", "string", "feather", "bone", "leather", "slime_ball", "wheat", "wheat_seeds", "flint", "ender_pearl" })
                I(m, ItemKind.Material).MaxStack = m == "ender_pearl" ? 16 : 64;
            I("egg", ItemKind.Material).MaxStack = 16;

            // ---------------- Spawn eggs
            foreach (var mob in new[] { "creeper", "zombie", "skeleton", "pig", "cow", "sheep", "chicken", "slime", "enderman", "iron_golem" })
                Egg(mob);
        }
    }
}
