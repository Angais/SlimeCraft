using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Creative-mode inventory with the look and feel of Minecraft's creative inventory: category tabs above and
    /// below a 195x136 window, a scrolling 9x5 palette of every SlimeCraft item in the category, a search tab with a
    /// text field, and a survival-inventory tab with a bin cell that deletes items and the player figure. Palette
    /// items are free; the Slime Rancher vacpack can never be destroyed from here. What clicks do is decided by the
    /// creative rule tables of <see cref="ClickResolver"/> (palette, bin and storage roles).
    /// </summary>
    internal sealed class CreativePaletteScreen : PanelScreen
    {
        private enum Purpose { Category, Search, Inventory }

        /// <summary>Axis-aligned box in window coordinates.</summary>
        private struct Box
        {
            public int Left, Top, Right, Bottom;
            /// <summary>Whether points lying on the right and bottom edges count as inside.</summary>
            public bool ClosedEdges;

            public Box(int left, int top, int width, int height, bool closedEdges)
            {
                Left = left;
                Top = top;
                Right = left + width;
                Bottom = top + height;
                ClosedEdges = closedEdges;
            }

            public bool Holds(double x, double y) =>
                x >= Left && y >= Top && (ClosedEdges ? x <= Right && y <= Bottom : x < Right && y < Bottom);
        }

        /// <summary>One tab of the strip (fixed data plus the items classified into it).</summary>
        private sealed class CategoryPage
        {
            /// <summary>Window-relative geometry, refreshed by <see cref="MeasureTabs"/>: click box, tooltip box and art origin.</summary>
            public Box Click, Tip;
            public int ArtX, ArtY;

            public string Title;
            public bool OnBottomRow;
            public int Column;
            public bool RightAligned;
            public string IconItem;
            public string IconTexture;
            public Purpose Purpose;
            public string Background;
            public string SpriteSelected, SpriteUnselected;
            public readonly List<string> Items = new List<string>();
            /// <summary>Whether the icon texture exists in the jar: 0 not checked yet, 1 yes, 2 no.</summary>
            public int TextureCheck;

            public bool Scrolls => Purpose != Purpose.Inventory;
            public bool ShowsTitle => Purpose != Purpose.Inventory;
        }

        private const int Columns = 9, Rows = 5, PaletteSize = Columns * Rows;
        private const int SearchWidth = 89;
        /// <summary>The scroll bar's track (window coordinates): 14 px wide at x 175, 112 px tall at y 18.</summary>
        private static readonly Box Track = new Box(175, 18, 14, 112, false);
        private const string SpriteRoot = "gui/sprites/container/creative_inventory/";
        private const string BackgroundRoot = "gui/container/creative_inventory/";

        // palette tabs: 45 palette cells (0..44) then the hotbar (45..53); shift on the hotbar clears it
        private static readonly TransferRoute[] PaletteRoutes = { TransferRoute.Clearing(PaletteSize, PaletteSize + 8) };
        // inventory tab: 27 main cells (0..26), the hotbar (27..35) and the bin (36)
        private static readonly TransferRoute[] InventoryRoutes =
        {
            TransferRoute.From(0, 26, new CellRange(27, 36)),
            TransferRoute.From(27, 35, new CellRange(0, 27)),
        };

        private static readonly HashSet<string> NaturalBlocks = new HashSet<string>(StringComparer.Ordinal)
        {
            "grass_block", "dirt", "coarse_dirt", "podzol", "moss_block", "stone", "deepslate", "calcite", "tuff", "sand",
            "red_sand", "gravel", "clay", "oak_log", "birch_log", "spruce_log", "cherry_log", "oak_leaves", "cherry_leaves",
            "ice", "packed_ice", "blue_ice", "snow_block", "pumpkin", "carved_pumpkin", "jack_o_lantern", "melon", "hay_block",
            "coal_ore", "iron_ore", "gold_ore", "diamond_ore", "emerald_ore", "obsidian", "crying_obsidian", "netherrack",
            "glowstone", "shroomlight", "end_stone", "bedrock", "amethyst_block",
        };
        private static readonly HashSet<string> FunctionalBlocks = new HashSet<string>(StringComparer.Ordinal)
        {
            "crafting_table", "furnace", "bookshelf", "sea_lantern",
        };
        private static readonly HashSet<string> RedstoneThings = new HashSet<string>(StringComparer.Ordinal)
        {
            "redstone_block", "redstone_lamp", "tnt", "slime_block", "honey_block", "redstone",
        };

        // built once per game session
        private static List<CategoryPage> pages;
        private static Dictionary<string, string> categoryTitleOf;
        private static CategoryPage searchPage, firstPage;
        private static CategoryPage rememberedPage;

        private CategoryPage page;
        private readonly LooseStore palette = new LooseStore(PaletteSize);
        private List<string> listed = new List<string>();
        /// <summary>Rows of <see cref="listed"/> beyond the five on screen (zero or less when everything fits).</summary>
        private int hiddenRows;
        private float scroll;
        private bool draggingThumb;
        private bool swallowLeftRelease;
        private bool swallowNextChar;
        private TextField searchField;
        private Cell bin;
        private readonly List<string> singleLine = new List<string>(1);

        public override string Name => "creative";

        public override bool WantsText => page != null && page.Purpose == Purpose.Search && searchField != null && searchField.HasFocus;

        // ================================================================== tab data

        private static void BuildPagesOnce()
        {
            if (pages != null) return;
            var building = MakePage("itemGroup.buildingBlocks", "Building", false, 0, "minecraft:bricks", null, Purpose.Category);
            var colored = MakePage("itemGroup.coloredBlocks", "Colours", false, 1, "minecraft:cyan_wool", null, Purpose.Category);
            var natural = MakePage("itemGroup.natural", "Nature", false, 2, "minecraft:grass_block", null, Purpose.Category);
            var functional = MakePage("itemGroup.functional", "Useful blocks", false, 3, "minecraft:crafting_table", "item/oak_sign", Purpose.Category);
            var redstone = MakePage("itemGroup.redstone", "Redstone", false, 4, "minecraft:redstone", null, Purpose.Category);
            var search = MakePage("itemGroup.search", "Search", false, 6, null, "item/compass_00", Purpose.Search);
            var tools = MakePage("itemGroup.tools", "Tools", true, 0, "minecraft:diamond_pickaxe", null, Purpose.Category);
            var combat = MakePage("itemGroup.combat", "Combat", true, 1, "minecraft:netherite_sword", null, Purpose.Category);
            var food = MakePage("itemGroup.foodAndDrink", "Food", true, 2, "minecraft:golden_apple", null, Purpose.Category);
            var ingredients = MakePage("itemGroup.ingredients", "Materials", true, 3, "minecraft:iron_ingot", null, Purpose.Category);
            var eggs = MakePage("itemGroup.spawnEggs", "Mob eggs", true, 4, "minecraft:creeper_spawn_egg", null, Purpose.Category);
            var inventory = MakePage("itemGroup.inventory", "Your inventory", true, 6, null, "slimecraft:chest", Purpose.Inventory);

            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string id in Content.CreativeOrder)
            {
                if (id == Content.Vacpack) continue;
                var def = Content.Item(id);
                if (def == null) continue;
                string path = id.Substring(id.IndexOf(':') + 1);
                CategoryPage home;
                if (def.IsBlock)
                {
                    if (path.EndsWith("_wool", StringComparison.Ordinal) || path.EndsWith("_stained_glass", StringComparison.Ordinal) || path == "terracotta") home = colored;
                    else if (RedstoneThings.Contains(path)) home = redstone;
                    else if (FunctionalBlocks.Contains(path)) home = functional;
                    else if (NaturalBlocks.Contains(path)) home = natural;
                    else home = building;
                }
                else
                {
                    if (RedstoneThings.Contains(path)) home = redstone;
                    else if (def.Kind == ItemKind.SpawnEgg) home = eggs;
                    else if (def.IsFood) home = food;
                    else if (def.Kind == ItemKind.Weapon || def.Special == "arrow") home = combat;
                    else if (def.Kind == ItemKind.Tool) home = tools;
                    else home = ingredients;
                }
                home.Items.Add(id);
                search.Items.Add(id);
                titles[id] = home.Title;
            }

            // order used for drawing the unselected tabs and for tooltip hit tests
            pages = new List<CategoryPage> { building, colored, natural, functional, redstone, search, tools, combat, food, ingredients, eggs, inventory };
            categoryTitleOf = titles;
            searchPage = search;
            firstPage = building;
        }

        private static CategoryPage MakePage(string langKey, string english, bool bottom, int column, string iconItem, string iconTexture, Purpose purpose)
        {
            string row = bottom ? "bottom" : "top";
            int n = Mathf.Clamp(column, 0, 6) + 1;
            string background;
            switch (purpose)
            {
                case Purpose.Search: background = BackgroundRoot + "tab_item_search"; break;
                case Purpose.Inventory: background = BackgroundRoot + "tab_inventory"; break;
                default: background = BackgroundRoot + "tab_items"; break;
            }
            return new CategoryPage
            {
                Title = Gui.Tr(langKey, english),
                OnBottomRow = bottom,
                Column = column,
                RightAligned = column >= 6,
                IconItem = iconItem,
                IconTexture = iconTexture,
                Purpose = purpose,
                Background = background,
                SpriteSelected = SpriteRoot + "tab_" + row + "_selected_" + n,
                SpriteUnselected = SpriteRoot + "tab_" + row + "_unselected_" + n,
            };
        }

        // ================================================================== setup

        public override void Open(HudModule hud, int viewW, int viewH)
        {
            BuildPagesOnce();
            Hud = hud;
            panel.Width = 195;
            panel.Height = 136;
            searchField = new TextField(hud.Font, 0, 0, SearchWidth)
            {
                Limit = 50,
                Ink = Argb.White,
                Shown = false,
                Edited = OnSearchEdited,
            };
            base.Open(hud, viewW, viewH);
            ShowPage(rememberedPage ?? firstPage, true);
        }

        protected override void Arrange()
        {
            base.Arrange();
            MeasureTabs();
            if (searchField == null) return;
            searchField.X = panel.Left + 171 - searchField.Width; // the field's right edge stays at x = 171 in the window
            searchField.Y = panel.Top + 6;
        }

        /// <summary>
        /// Tab geometry relative to the window. Tabs are 27 px apart: left-aligned ones from x = 0, the right-aligned
        /// one ends at the window's right edge. Top-row tabs are clickable from 32 px above the window and drawn from
        /// 28 px above it; bottom-row tabs are clickable from the window's bottom edge and drawn from 4 px above it.
        /// </summary>
        private void MeasureTabs()
        {
            if (pages == null) return;
            foreach (var p in pages)
            {
                int x = p.RightAligned ? panel.Width - 27 * (7 - p.Column) + 1 : 27 * p.Column;
                int clickTop = p.OnBottomRow ? panel.Height : -32;
                p.Click = new Box(x, clickTop, 26, 32, true);
                p.Tip = new Box(x + 2, clickTop + 2, 23, 29, false);
                p.ArtX = x;
                p.ArtY = p.OnBottomRow ? panel.Height - 4 : -28;
            }
        }

        private void ShowPage(CategoryPage next, bool opening)
        {
            bool changed = next != page;
            StopSpread();
            page = next;
            rememberedPage = next;
            scroll = 0f;
            panel.Heading.Text = next.ShowsTitle ? next.Title : null;
            BuildCells();

            if (next.Purpose == Purpose.Search)
            {
                searchField.Shown = true;
                searchField.HasFocus = true;
                searchField.Width = SearchWidth;
                Arrange();
                // replacing the text runs the field's callback, which recomputes the results
                if (changed || opening) searchField.Replace("");
                else RefreshSearchResults();
            }
            else
            {
                searchField.Shown = false;
                searchField.HasFocus = false;
                searchField.Replace("");
                ShowItems(next.Items);
            }
        }

        private void BuildCells()
        {
            panel.Clear();
            Hovered = null;
            bin = null;
            var player = new PlayerStore(PInv.Inv);
            if (page.Purpose == Purpose.Inventory)
            {
                panel.AddPlayerRows(player, 9, 54);
                bin = panel.Add(Cell.Bin(173, 112));
                Rules = ClickResolver.CreativeInventory;
                Routes = InventoryRoutes;
                return;
            }
            for (int i = 0; i < PaletteSize; i++)
                panel.Add(Cell.PaletteEntry(palette, i, 9 + 18 * (i % Columns), 18 + 18 * (i / Columns)));
            for (int i = 0; i < 9; i++)
                panel.Add(Cell.Plain(player, i, 9 + 18 * i, 112));
            Rules = ClickResolver.CreativePalette;
            Routes = PaletteRoutes;
        }

        // ================================================================== palette and search

        /// <summary>Makes <paramref name="items"/> the palette contents and shows them from the current scroll position.</summary>
        private void ShowItems(List<string> items)
        {
            listed = items;
            hiddenRows = (items.Count + Columns - 1) / Columns - Rows;
            FillPalette();
        }

        /// <summary>The scroll bar works when the page scrolls at all and some rows are hidden.</summary>
        private bool Scrollable => page != null && page.Scrolls && hiddenRows > 0;

        /// <summary>
        /// Fills the 45 palette entries with fresh one-item stacks. The scroll position (0..1) picks the first shown row
        /// among the hidden ones, rounded to the nearest row.
        /// </summary>
        private void FillPalette()
        {
            int skipped = Columns * Math.Max(0, (int)Math.Floor(scroll * hiddenRows + 0.5f));
            for (int i = 0; i < PaletteSize; i++)
            {
                int index = skipped + i;
                palette.Raw[i] = index < listed.Count ? new ItemStack(listed[index], 1) : ItemStack.Empty;
            }
        }

        private void OnSearchEdited(string text)
        {
            if (page != null && page.Purpose == Purpose.Search) RefreshSearchResults();
        }

        /// <summary>Items whose display name or full id contains the typed text (case-insensitive), in creative order.</summary>
        private void RefreshSearchResults()
        {
            string query = (searchField.Text ?? "").Trim().ToLowerInvariant();
            var results = new List<string>();
            foreach (string id in searchPage.Items)
            {
                if (query.Length == 0 || id.ToLowerInvariant().Contains(query) || NameOf(id).ToLowerInvariant().Contains(query))
                    results.Add(id);
            }
            scroll = 0f;
            ShowItems(results);
        }

        private static string NameOf(string id)
        {
            try { return Content.DisplayName(id) ?? ""; } catch { return ""; }
        }

        // ================================================================== hit tests

        /// <summary>The tab whose click box holds the screen point, or null.</summary>
        private CategoryPage TabUnder(double mx, double my)
        {
            double rx = mx - panel.Left, ry = my - panel.Top;
            foreach (var p in pages) if (p.Click.Holds(rx, ry)) return p;
            return null;
        }

        private bool OverTrack(double mx, double my) => Track.Holds(mx - panel.Left, my - panel.Top);

        // ================================================================== drawing

        /// <summary>
        /// Paint order: the unselected tabs, then (on a new layer, so it covers them) the window sheet with its search
        /// box and scroll thumb, then the selected tab on top of the window edge, then the player figure.
        /// </summary>
        protected override void DrawWindowArt(Gui g, int mouseX, int mouseY)
        {
            DrawTabRow(g, false);
            g.LayerBreak();
            DrawWindowSheet(g);
            DrawTabRow(g, true);
            if (page.Purpose == Purpose.Inventory)
                Hud.DrawPlayerFigure(g, panel.Left + 73, panel.Top + 6, panel.Left + 105, panel.Top + 49, 1, mouseX, mouseY);
        }

        /// <summary>Either the selected tab alone or every other tab.</summary>
        private void DrawTabRow(Gui g, bool selectedOnly)
        {
            foreach (var p in pages)
                if ((p == page) == selectedOnly) DrawTab(g, p, selectedOnly);
        }

        private void DrawWindowSheet(Gui g)
        {
            g.DrawSheetPart(page.Background, panel.Left, panel.Top, panel.Width, panel.Height, 0, 0, 256, 256);
            if (searchField.Shown) searchField.Draw(g);
            if (!page.Scrolls) return;
            // the 12x15 thumb travels 95 px down the track as the scroll position goes from 0 to 1
            string thumb = Scrollable ? SpriteRoot + "scroller" : SpriteRoot + "scroller_disabled";
            g.DrawSprite(thumb, panel.Left + Track.Left, panel.Top + Track.Top + (int)Math.Floor(95f * scroll), 12, 15);
        }

        private void DrawTab(Gui g, CategoryPage p, bool selected)
        {
            int x = panel.Left + p.ArtX, y = panel.Top + p.ArtY;
            g.DrawSprite(selected ? p.SpriteSelected : p.SpriteUnselected, x, y, 26, 32);
            DrawTabIcon(g, p, x + 5, y + (p.OnBottomRow ? 7 : 9));
        }

        private void DrawTabIcon(Gui g, CategoryPage p, int x, int y)
        {
            string tex = p.IconTexture;
            if (tex != null && tex.StartsWith("slimecraft:", StringComparison.Ordinal))
            {
                // composite drawn at its native size, centred in the 16x16 icon box (retried until the atlas has it)
                var special = Hud.SpecialRegion(tex);
                if (special != null)
                    g.DrawRegion(special, x + (16 - special.W) / 2, y + (16 - special.H) / 2, special.W, special.H,
                        0, 0, special.SW, special.SH, special.SW, special.SH);
                return;
            }
            if (tex != null && IconTextureAvailable(p))
            {
                var region = g.Atlas.Get(tex);
                if (region != null)
                {
                    g.DrawRegion(region, x, y, 16, 16, 0, 0, region.SW, region.SH, region.SW, region.SH);
                    return;
                }
            }
            if (p.IconItem == null) return;
            g.LayerBreak();
            g.DrawItem(x, y, p.IconItem);
            g.LayerBreak();
        }

        /// <summary>Checks the jar once the assets are ready; until then the tab falls back to its item icon.</summary>
        private static bool IconTextureAvailable(CategoryPage p)
        {
            if (p.TextureCheck == 0)
            {
                var assets = SC.Assets;
                if (assets == null || !assets.Ready) return false;
                bool exists;
                try { exists = assets.TextureExists(p.IconTexture); } catch { exists = false; }
                p.TextureCheck = exists ? 1 : 2;
            }
            return p.TextureCheck == 1;
        }

        protected override void DrawHoverInfo(Gui g, int mouseX, int mouseY)
        {
            double rx = mouseX - panel.Left, ry = mouseY - panel.Top;
            foreach (var p in pages)
            {
                if (!p.Tip.Holds(rx, ry)) continue;
                g.DrawTooltip(mouseX, mouseY, OneLine(p.Title));
                return;
            }
            if (bin != null && panel.Near(bin.X, bin.Y, Cell.Span, Cell.Span, mouseX, mouseY))
            {
                g.DrawTooltip(mouseX, mouseY, OneLine(Gui.Tr("inventory.binSlot", "Delete item")));
                return;
            }
            base.DrawHoverInfo(g, mouseX, mouseY);
        }

        private List<string> OneLine(string text)
        {
            singleLine.Clear();
            singleLine.Add(text);
            return singleLine;
        }

        /// <summary>Plain tooltip in a category view; everywhere else a blue line names the item's category.</summary>
        protected override List<string> InfoLines(Cell c)
        {
            var st = c.Contents;
            if (c.Role == CellRole.Palette && page.Purpose == Purpose.Category) return Gui.TooltipLines(st);
            string category = null;
            if (!st.IsEmpty) categoryTitleOf.TryGetValue(st.Id, out category);
            return Gui.TooltipLines(st, category);
        }

        // ================================================================== pointer

        /// <summary>Outside the window and not on the selected tab (which sticks out of the window).</summary>
        protected override bool IsBeyondPanel(double x, double y) =>
            panel.Beyond(x, y) && !page.Click.Holds(x - panel.Left, y - panel.Top);

        public override bool PointerDown(double x, double y, int button, bool doubleClick)
        {
            if (button == 0)
            {
                if (TabUnder(x, y) != null)
                {
                    swallowLeftRelease = true; // the tab changes on release
                    return true;
                }
                if (page.Purpose != Purpose.Inventory && OverTrack(x, y))
                {
                    if (Scrollable) draggingThumb = true;
                    swallowLeftRelease = true;
                    return true;
                }
            }
            return base.PointerDown(x, y, button, doubleClick);
        }

        public override bool PointerUp(double x, double y, int button)
        {
            if (button == 0)
            {
                draggingThumb = false;
                var tab = TabUnder(x, y);
                if (tab != null)
                {
                    swallowLeftRelease = false;
                    if (tab != page) ShowPage(tab, false);
                    return true;
                }
                if (swallowLeftRelease)
                {
                    // the press went to the tab strip or the scroll bar, so its release must not act on a cell
                    swallowLeftRelease = false;
                    return true;
                }
            }
            return base.PointerUp(x, y, button);
        }

        public override bool PointerMove(double x, double y, int button)
        {
            if (!draggingThumb) return base.PointerMove(x, y, button);
            // the thumb's centre (7.5 px below its top) follows the pointer over 97 px of travel
            scroll = Mathf.Clamp01(((float)y - (panel.Top + Track.Top) - 7.5f) / 97f);
            FillPalette();
            return true;
        }

        public override bool Wheel(double x, double y, double delta)
        {
            if (!Scrollable) return false;
            // one wheel step moves one row
            scroll = Mathf.Clamp01(scroll - (float)delta / hiddenRows);
            FillPalette();
            return true;
        }

        // ================================================================== keyboard

        public override bool Key(KeyCode key, bool ctrl, bool shift)
        {
            swallowNextChar = false;
            if (page.Purpose != Purpose.Search)
            {
                KeyCode chatKey = SC.Input != null ? SC.Input.ChatKey : KeyCode.Y;
                if (chatKey != KeyCode.None && key == chatKey)
                {
                    ShowPage(searchPage, false);
                    swallowNextChar = true; // the letter of the same key press must not land in the field
                    return true;
                }
                return base.Key(key, ctrl, shift);
            }

            if (key == KeyCode.Escape)
            {
                Hud.CloseScreen();
                return true;
            }
            bool digit = key >= KeyCode.Alpha1 && key <= KeyCode.Alpha9;
            bool hoverTakesDigit = Hovered != null && (Hovered.Role != CellRole.Palette || Hovered.Holding);
            if (digit && hoverTakesDigit && TryHotbarKey(key))
            {
                swallowNextChar = true;
                return true;
            }
            return searchField.HandleKey(key, ctrl, shift);
        }

        public override void Typed(char c)
        {
            if (swallowNextChar)
            {
                swallowNextChar = false;
                return;
            }
            if (page == null || page.Purpose != Purpose.Search) return;
            searchField.HandleChar(c); // the field's callback refreshes the results
        }
    }
}
