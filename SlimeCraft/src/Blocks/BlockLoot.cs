using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    /// <summary>Block drops as item entities, including a few Minecraft drop specials that Content does not express.</summary>
    internal static class BlockLoot
    {
        /// <summary>Spawns the drops of a broken block around the centre of its cell.</summary>
        public static void Drop(BlockDef def, Vector3Int pos)
        {
            if (def == null) return;
            var center = BlockWorld.Center(pos);
            switch (def.Id)
            {
                case "minecraft:glowstone": Pop("minecraft:glowstone_dust", Rng.Int(2, 4), center); return;   // 2-4 dust
                case "minecraft:melon": Pop("minecraft:melon_slice", Rng.Int(3, 7), center); return;          // 3-7 slices
                case "minecraft:gravel":                                                                        // 10% flint
                    Pop(Rng.F() < 0.1f ? "minecraft:flint" : "minecraft:gravel", 1, center); return;
                case "minecraft:oak_leaves":                                                                    // apple 0.5%, sticks 2%
                    if (Rng.F() < 0.005f) Pop("minecraft:apple", 1, center);
                    if (Rng.F() < 0.02f) Pop("minecraft:stick", Rng.Int(1, 2), center);
                    return;
                case "minecraft:cherry_leaves":
                    if (Rng.F() < 0.02f) Pop("minecraft:stick", Rng.Int(1, 2), center);
                    return;
            }
            string id = def.Drop == null ? def.Id : def.Drop;
            if (string.IsNullOrEmpty(id)) return;
            Pop(id, Mathf.Max(1, def.DropCount), center);
        }

        /// <summary>Spawns item entities near <paramref name="center"/>: ±0.25 jitter, a small upward toss, 0.5 s pickup delay.</summary>
        public static void Pop(string itemId, int count, Vector3 center)
        {
            var ent = SC.Entities;
            if (ent == null || count <= 0 || Content.Item(itemId) == null) return;
            var item = Content.Item(itemId);
            int max = Mathf.Max(1, item.MaxStack);
            while (count > 0)
            {
                int n = Mathf.Min(count, max);
                count -= n;
                var p = center + new Vector3(Rng.Range(-0.25f, 0.25f), Rng.Range(-0.25f, 0.25f) - 0.125f, Rng.Range(-0.25f, 0.25f));
                // a small upward toss with a random sideways drift, tuned for Unity gravity
                var v = new Vector3(Rng.Range(-1f, 1f), 2.5f, Rng.Range(-1f, 1f));
                ent.SpawnItem(new ItemStack(item.Id, n), p, v, 0.5f);
            }
        }

        /// <summary>Spawns drops at a surface point (harvested SR terrain): thrown out along the normal.</summary>
        public static void PopAt(string itemId, int count, Vector3 point, Vector3 normal)
        {
            var ent = SC.Entities;
            var item = Content.Item(itemId);
            if (ent == null || count <= 0 || item == null) return;
            int max = Mathf.Max(1, item.MaxStack);
            while (count > 0)
            {
                int n = Mathf.Min(count, max);
                count -= n;
                var p = point + normal * 0.35f + new Vector3(Rng.Range(-0.1f, 0.1f), 0.1f, Rng.Range(-0.1f, 0.1f));
                var v = normal * 1.5f + new Vector3(Rng.Range(-0.6f, 0.6f), 2f, Rng.Range(-0.6f, 0.6f));
                ent.SpawnItem(new ItemStack(item.Id, n), p, v, 0.5f);
            }
        }
    }
}
