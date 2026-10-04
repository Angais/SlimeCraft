using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Crafting recipes loaded from the user's Minecraft jar (data/minecraft/recipe/*.json, types
    /// minecraft:crafting_shaped and minecraft:crafting_shapeless) with item tags from
    /// data/minecraft/tags/item/*.json (nested "#tag" references resolved recursively). Only recipes whose
    /// result and every ingredient exist in <see cref="Content"/> are kept. Matching behaves like Minecraft's
    /// crafting grid: the input grid is trimmed to its bounding box, shaped patterns may be mirrored,
    /// shapeless ingredients are matched as a bipartite assignment. Includes the tool repair recipe
    /// (crafting_special_repairitem). Loading is incremental (time budgeted per frame).
    /// </summary>
    internal static class Recipes
    {
        internal sealed class Recipe
        {
            public string Id;
            public bool Shaped;
            public int W, H;
            public HashSet<string>[] Cells;        // shaped: W*H, null = must be empty
            public List<HashSet<string>> Ingredients; // shapeless
            public string Result;
            public int Count;
        }

        private static readonly List<Recipe> recipes = new List<Recipe>();
        private static readonly Dictionary<string, HashSet<string>> tagCache = new Dictionary<string, HashSet<string>>();
        private static List<string> pending;
        private static int pendingIndex;
        private static bool done;
        private static int skipped;

        public static bool Loaded => done;
        public static int Count => recipes.Count;

        /// <summary>Loads a slice of recipe files (call every frame; cheap once done).</summary>
        public static void Pump(double budgetMs)
        {
            if (done) return;
            var assets = SC.Assets;
            if (assets == null || !assets.Ready) return;
            try
            {
                if (pending == null)
                {
                    pending = new List<string>();
                    foreach (var e in assets.ListJar("data/minecraft/recipe/"))
                        if (e.EndsWith(".json", StringComparison.Ordinal)) pending.Add(e);
                    pendingIndex = 0;
                }
                var sw = Stopwatch.StartNew();
                while (pendingIndex < pending.Count)
                {
                    LoadFile(pending[pendingIndex++]);
                    if (sw.Elapsed.TotalMilliseconds > budgetMs) return;
                }
                done = true;
                SC.Log?.LogInfo("[Hud] crafting: " + recipes.Count + " recipes usable with SlimeCraft items (" + skipped + " skipped of " + pending.Count + ")");
                pending = null;
            }
            catch (Exception e)
            {
                SC.Log?.LogError("[Hud] recipe loading failed: " + e);
                done = true;
            }
        }

        public static void EnsureLoaded()
        {
            int guard = 0;
            while (!done && SC.Assets != null && SC.Assets.Ready && guard++ < 100000) Pump(1e9);
        }

        private static void LoadFile(string entry)
        {
            string text;
            try { text = SC.Assets.ReadJarText(entry); } catch { text = null; }
            if (text == null) { skipped++; return; }
            bool shaped = text.Contains("\"minecraft:crafting_shaped\"");
            bool shapeless = !shaped && text.Contains("\"minecraft:crafting_shapeless\"");
            if (!shaped && !shapeless) { skipped++; return; }
            var json = JsonNode.Parse(text);
            if (json == null || !json.IsObject) { skipped++; return; }
            var r = new Recipe { Id = entry, Shaped = shaped };
            var res = json["result"];
            string rid = res.IsString ? res.AsString() : (res["id"].AsString(null) ?? res["item"].AsString(null));
            if (rid == null || Content.Item(Content.Norm(rid)) == null) { skipped++; return; }
            r.Result = Content.Norm(rid);
            r.Count = Math.Max(1, res["count"].AsInt(1));

            if (shaped)
            {
                var rows = new List<string>();
                foreach (var row in json["pattern"].Items) rows.Add(row.AsString(""));
                if (rows.Count == 0) { skipped++; return; }
                // trim the pattern: drop empty leading/trailing rows and columns
                int width = 0; foreach (var row in rows) width = Math.Max(width, row.Length);
                int firstRow = rows.Count, lastRow = -1, firstCol = width, lastCol = -1;
                for (int y = 0; y < rows.Count; y++)
                    for (int x = 0; x < rows[y].Length; x++)
                        if (rows[y][x] != ' ')
                        {
                            firstRow = Math.Min(firstRow, y); lastRow = Math.Max(lastRow, y);
                            firstCol = Math.Min(firstCol, x); lastCol = Math.Max(lastCol, x);
                        }
                if (lastRow < 0) { skipped++; return; }
                r.W = lastCol - firstCol + 1;
                r.H = lastRow - firstRow + 1;
                r.Cells = new HashSet<string>[r.W * r.H];
                var keyCache = new Dictionary<char, HashSet<string>>();
                for (int y = 0; y < r.H; y++)
                {
                    string row = rows[firstRow + y];
                    for (int x = 0; x < r.W; x++)
                    {
                        int sx = firstCol + x;
                        char c = sx < row.Length ? row[sx] : ' ';
                        if (c == ' ') continue;
                        if (!keyCache.TryGetValue(c, out var ing))
                        {
                            ing = Ingredient(json["key"][c.ToString()]);
                            keyCache[c] = ing;
                        }
                        if (ing == null || ing.Count == 0) { skipped++; return; }
                        r.Cells[y * r.W + x] = ing;
                    }
                }
            }
            else
            {
                r.Ingredients = new List<HashSet<string>>();
                foreach (var i in json["ingredients"].Items)
                {
                    var ing = Ingredient(i);
                    if (ing == null || ing.Count == 0) { skipped++; return; }
                    r.Ingredients.Add(ing);
                }
                if (r.Ingredients.Count == 0 || r.Ingredients.Count > 9) { skipped++; return; }
            }
            recipes.Add(r);
        }

        /// <summary>Ingredient JSON: "minecraft:x", "#minecraft:tag", an array of those, or legacy {"item"}/{"tag"} objects.</summary>
        private static HashSet<string> Ingredient(JsonNode n)
        {
            var set = new HashSet<string>();
            AddIngredient(n, set);
            return set;
        }

        private static void AddIngredient(JsonNode n, HashSet<string> set)
        {
            if (n == null || n.IsNull) return;
            if (n.IsString)
            {
                string s = n.AsString();
                if (s.StartsWith("#", StringComparison.Ordinal)) set.UnionWith(Tag(s.Substring(1), 0));
                else { var id = Content.Norm(s); if (Content.Item(id) != null) set.Add(id); }
            }
            else if (n.IsArray)
            {
                foreach (var e in n.Items) AddIngredient(e, set);
            }
            else if (n.IsObject)
            {
                if (n.Has("item")) AddIngredient(n["item"], set);
                if (n.Has("tag")) set.UnionWith(Tag(n["tag"].AsString(""), 0));
            }
        }

        /// <summary>Item tag members that exist in Content (recursive, cached).</summary>
        private static HashSet<string> Tag(string tagId, int depth)
        {
            tagId = Content.Norm(tagId);
            if (tagCache.TryGetValue(tagId, out var cached)) return cached;
            var set = new HashSet<string>();
            tagCache[tagId] = set; // recursion guard
            if (depth > 16) return set;
            int colon = tagId.IndexOf(':');
            string ns = tagId.Substring(0, colon), path = tagId.Substring(colon + 1);
            var json = SC.Assets.ReadJarJson("data/" + ns + "/tags/item/" + path + ".json");
            if (json == null) return set;
            foreach (var v in json["values"].Items)
            {
                string id = v.IsString ? v.AsString() : v["id"].AsString(null);
                if (id == null) continue;
                if (id.StartsWith("#", StringComparison.Ordinal)) set.UnionWith(Tag(id.Substring(1), depth + 1));
                else { var nid = Content.Norm(id); if (Content.Item(nid) != null) set.Add(nid); }
            }
            return set;
        }

        // ------------------------------------------------------------------ matching
        /// <summary>Result of crafting the grid (gw x gh, row-major), or Empty.</summary>
        public static ItemStack Match(ItemStack[] grid, int gw, int gh)
        {
            EnsureLoaded();
            int minX = gw, minY = gh, maxX = -1, maxY = -1, count = 0;
            for (int y = 0; y < gh; y++)
                for (int x = 0; x < gw; x++)
                {
                    var s = grid[y * gw + x];
                    if (s == null || s.IsEmpty) continue;
                    count++;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            if (count == 0) return ItemStack.Empty;
            int w = maxX - minX + 1, h = maxY - minY + 1;

            foreach (var r in recipes)
            {
                if (r.Shaped)
                {
                    if (r.W != w || r.H != h) continue;
                    if (MatchShaped(r, grid, gw, minX, minY, false) || MatchShaped(r, grid, gw, minX, minY, true))
                        return new ItemStack(r.Result, r.Count);
                }
                else
                {
                    if (r.Ingredients.Count != count) continue;
                    if (MatchShapeless(r, grid)) return new ItemStack(r.Result, r.Count);
                }
            }
            return Repair(grid, count);
        }

        private static bool MatchShaped(Recipe r, ItemStack[] grid, int gw, int ox, int oy, bool mirror)
        {
            for (int y = 0; y < r.H; y++)
                for (int x = 0; x < r.W; x++)
                {
                    var cell = r.Cells[y * r.W + (mirror ? r.W - 1 - x : x)];
                    var s = grid[(oy + y) * gw + ox + x];
                    bool empty = s == null || s.IsEmpty;
                    if (cell == null) { if (!empty) return false; }
                    else if (empty || !cell.Contains(s.Id)) return false;
                }
            return true;
        }

        private static bool MatchShapeless(Recipe r, ItemStack[] grid)
        {
            var items = new List<string>();
            foreach (var s in grid) if (s != null && !s.IsEmpty) items.Add(s.Id);
            var used = new bool[items.Count];
            return Assign(r.Ingredients, 0, items, used);
        }

        private static bool Assign(List<HashSet<string>> ings, int i, List<string> items, bool[] used)
        {
            if (i == ings.Count) return true;
            for (int k = 0; k < items.Count; k++)
            {
                if (used[k] || !ings[i].Contains(items[k])) continue;
                used[k] = true;
                if (Assign(ings, i + 1, items, used)) return true;
                used[k] = false;
            }
            return false;
        }

        /// <summary>Tool repair: two damaged copies of the same tool combine durability (+5% bonus).</summary>
        private static ItemStack Repair(ItemStack[] grid, int count)
        {
            if (count != 2) return ItemStack.Empty;
            ItemStack a = null, b = null;
            foreach (var s in grid)
            {
                if (s == null || s.IsEmpty) continue;
                if (a == null) a = s; else b = s;
            }
            if (a == null || b == null || a.Id != b.Id) return ItemStack.Empty;
            var def = a.Def;
            if (def == null || def.MaxDamage <= 0 || def.MaxStack != 1) return ItemStack.Empty;
            int max = def.MaxDamage;
            int durability = (max - a.Damage) + (max - b.Damage) + max * 5 / 100;
            return new ItemStack(a.Id, 1, Math.Max(0, max - durability));
        }
    }
}
