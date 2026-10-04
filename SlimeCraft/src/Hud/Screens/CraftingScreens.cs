using System;
using System.Collections.Generic;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Shared part of the two survival pages with a crafting grid. The grid only exists while the page is open: its
    /// output is recomputed from the recipe set whenever the grid changes, taking the output uses up one item per
    /// filled grid cell, and closing the page hands every grid item back to the player. Shift-click destinations are
    /// plain <see cref="TransferRoute"/> data run by <see cref="PanelScreen"/>.
    /// </summary>
    internal abstract class CraftingPanelScreen : PanelScreen
    {
        private LooseStore grid;
        private LooseStore output;
        private int gridW, gridH;
        private bool recomputing;

        /// <summary>
        /// Adds the output cell (number 0) and the grid cells (row by row, 18 px apart). The storage is created once per
        /// page, so items already in the grid survive a repeated <see cref="HudScreen.Open"/>.
        /// </summary>
        protected void AddCraftingCells(int width, int height, int outX, int outY, int gridX, int gridY)
        {
            gridW = width;
            gridH = height;
            if (grid == null) grid = new LooseStore(width * height, OnGridChanged);
            if (output == null) output = new LooseStore(1);
            panel.Add(Cell.Output(output, outX, outY, OnOutputTaken));
            for (int i = 0; i < width * height; i++)
                panel.Add(Cell.Plain(grid, i, gridX + 18 * (i % width), gridY + 18 * (i / width)));
        }

        private void OnGridChanged()
        {
            if (!recomputing) RefreshOutput();
        }

        /// <summary>Replaces the output with what the grid currently makes (empty when nothing matches).</summary>
        private void RefreshOutput()
        {
            if (grid == null || output == null) return;
            recomputing = true;
            try
            {
                output.Store(0, MatchGrid());
            }
            finally
            {
                recomputing = false;
            }
        }

        private ItemStack MatchGrid()
        {
            try
            {
                return Recipes.Match(grid.Raw, gridW, gridH);
            }
            catch (Exception e)
            {
                HudModule.LogLimited("craft.match", "recipe match failed: " + e);
                return ItemStack.Empty; // never leave a stale output that could still be taken
            }
        }

        /// <summary>Taking the output: every filled grid cell loses one item (no remainders), then one recompute.</summary>
        private void OnOutputTaken(ItemStack taken)
        {
            if (grid == null) return;
            var cells = grid.Raw;
            for (int i = 0; i < cells.Length; i++)
            {
                var st = cells[i];
                if (st == null || st.IsEmpty) continue;
                if (--st.Count <= 0) cells[i] = ItemStack.Empty; // silent write: no recompute per cell
            }
            RefreshOutput();
        }

        /// <summary>Hands every grid item back to the player and clears the (derived) output.</summary>
        protected void GiveBackGrid()
        {
            if (grid != null)
            {
                var cells = grid.Raw;
                for (int i = 0; i < cells.Length; i++)
                {
                    var st = cells[i];
                    cells[i] = ItemStack.Empty;
                    try { ReturnToPlayer(st); }
                    catch (Exception e) { HudModule.LogLimited("craft.return", "returning crafting grid items failed: " + e); }
                }
            }
            if (output != null) output.Raw[0] = ItemStack.Empty;
            if (Hud != null) PInv?.Inv.NotifyChanged();
        }

        public override void Closed()
        {
            base.Closed();
            GiveBackGrid();
        }

        /// <summary>Non-empty grid stacks in cell order (never the output, which is derived from them).</summary>
        public override IEnumerable<ItemStack> ScreenOwnedStacks()
        {
            if (grid == null) yield break;
            foreach (var st in grid.Raw)
                if (st != null && !st.IsEmpty) yield return st;
        }
    }

    /// <summary>
    /// Survival inventory: the player's 36 entries, a 2x2 crafting grid, decorative armour/offhand cells and a small
    /// player figure that looks toward the mouse. Same look and layout as Minecraft's inventory.
    /// </summary>
    internal sealed class SurvivalInventoryScreen : CraftingPanelScreen
    {
        // cell numbers: 0 output, 1-4 grid, 5-8 armour art, 9-35 main rows, 36-44 hotbar, 45 offhand art
        private static readonly TransferRoute[] RouteTable =
        {
            TransferRoute.From(0, 0, new CellRange(9, 45, true)),
            TransferRoute.From(1, 4, new CellRange(9, 45)),
            TransferRoute.From(9, 35, new CellRange(36, 45)),
            TransferRoute.From(36, 44, new CellRange(9, 36)),
        };

        public override string Name => "inventory";

        public override void Open(HudModule hud, int viewW, int viewH)
        {
            Hud = hud;
            panel.Clear();
            AddCraftingCells(2, 2, 154, 28, 98, 18);
            for (int i = 0; i < 4; i++) panel.Add(Cell.Prop(8, 8 + 18 * i));
            panel.AddPlayerRows(new PlayerStore(PInv.Inv), 8, 84);
            panel.Add(Cell.Prop(77, 62));
            Routes = RouteTable;
            base.Open(hud, viewW, viewH);
            panel.Heading.Text = Gui.Tr("container.crafting", "Crafting");
            panel.Heading.X = 97;
        }

        protected override void DrawWindowArt(Gui g, int mouseX, int mouseY)
        {
            g.DrawSheetPart("gui/container/inventory", panel.Left, panel.Top, 176, 166, 0, 0, 256, 256);
            Hud.DrawPlayerFigure(g, panel.Left + 26, panel.Top + 8, panel.Left + 75, panel.Top + 78, 2, mouseX, mouseY);
        }
    }

    /// <summary>Crafting table: the player's 36 entries and a 3x3 crafting grid, laid out like Minecraft's crafting page.</summary>
    internal sealed class WorkbenchScreen : CraftingPanelScreen
    {
        // cell numbers: 0 output, 1-9 grid, 10-36 main rows, 37-45 hotbar
        private static readonly TransferRoute[] RouteTable =
        {
            TransferRoute.From(0, 0, new CellRange(10, 46, true)),
            TransferRoute.From(1, 9, new CellRange(10, 46)),
            TransferRoute.From(10, 36, new CellRange(1, 10), new CellRange(37, 46)),
            TransferRoute.From(37, 45, new CellRange(1, 10), new CellRange(10, 37)),
        };

        public override string Name => "crafting";

        public override void Open(HudModule hud, int viewW, int viewH)
        {
            Hud = hud;
            panel.Clear();
            AddCraftingCells(3, 3, 124, 35, 30, 17);
            panel.AddPlayerRows(new PlayerStore(PInv.Inv), 8, 84);
            Routes = RouteTable;
            base.Open(hud, viewW, viewH);
            panel.Heading.Text = Gui.Tr("container.crafting", "Crafting");
            panel.Heading.X = 29;
            panel.Footer.Text = Gui.Tr("container.inventory", "Inventory");
        }

        protected override void DrawWindowArt(Gui g, int mouseX, int mouseY)
        {
            g.DrawSheetPart("gui/container/crafting_table", panel.Left, panel.Top, 176, 166, 0, 0, 256, 256);
        }
    }
}
