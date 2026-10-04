using System.Collections.Generic;

namespace SlimeCraft.HudUI
{
    /// <summary>A line of text drawn at a fixed spot of a panel (dark grey, no shadow).</summary>
    internal struct Caption
    {
        public string Text;
        public int X, Y;

        public Caption(int x, int y)
        {
            Text = null;
            X = x;
            Y = y;
        }

        public void Draw(Gui g)
        {
            if (!string.IsNullOrEmpty(Text)) g.Text(Text, X, Y, Argb.Label, false);
        }
    }

    /// <summary>
    /// A centred window of an item page: its size, its position on screen, its captions and its cells. Cell
    /// coordinates are relative to the window; hit tests take screen coordinates.
    /// </summary>
    internal sealed class Panel
    {
        /// <summary>Window size in GUI pixels (the background texture's size).</summary>
        public int Width = 176, Height = 166;
        /// <summary>Top-left of the window on screen.</summary>
        public int Left, Top;
        /// <summary>Upper caption (the page title).</summary>
        public Caption Heading = new Caption(8, 6);
        /// <summary>Lower caption above the player's rows ("Inventory"); placed 94 px above the window bottom.</summary>
        public Caption Footer = new Caption(8, 0);

        public readonly List<Cell> Cells = new List<Cell>();

        public int Count => Cells.Count;

        public Cell this[int number] => Cells[number];

        public bool Has(int number) => number >= 0 && number < Cells.Count;

        public void Centre(int viewW, int viewH)
        {
            Left = (viewW - Width) / 2;
            Top = (viewH - Height) / 2;
        }

        public Cell Add(Cell c)
        {
            c.Number = Cells.Count;
            Cells.Add(c);
            return c;
        }

        public void Clear() => Cells.Clear();

        /// <summary>The player's three 9-cell rows (entries 9..35, 18 px pitch), then the hotbar row 58 px below the first row.</summary>
        public void AddPlayerRows(IStackStore player, int x, int y)
        {
            for (int k = 0; k < 27; k++)
                Add(Cell.Plain(player, 9 + k, x + 18 * (k % 9), y + 18 * (k / 9)));
            for (int k = 0; k < 9; k++)
                Add(Cell.Plain(player, k, x + 18 * k, y + 58));
        }

        /// <summary>A panel-relative rectangle, grown by one pixel on every side, contains the screen point.</summary>
        public bool Near(int x, int y, int w, int h, double screenX, double screenY)
        {
            double px = screenX - Left, py = screenY - Top;
            return px >= x - 1 && px < x + w + 1 && py >= y - 1 && py < y + h + 1;
        }

        /// <summary>The first shown cell under the screen point, or null.</summary>
        public Cell CellAt(double screenX, double screenY)
        {
            foreach (var c in Cells)
                if (c.Shown && Near(c.X, c.Y, Cell.Span, Cell.Span, screenX, screenY)) return c;
            return null;
        }

        /// <summary>The screen point lies outside the window rectangle.</summary>
        public bool Beyond(double screenX, double screenY) =>
            screenX < Left || screenY < Top || screenX >= Left + Width || screenY >= Top + Height;
    }

    /// <summary>A block of cells [Start, End) of a panel, walked forwards or backwards.</summary>
    internal struct CellRange
    {
        public int Start, End;
        public bool Reverse;

        public CellRange(int start, int end, bool reverse = false)
        {
            Start = start;
            End = end;
            Reverse = reverse;
        }

        public int Length => End - Start;

        /// <summary>Cell number of the k-th step of the walk.</summary>
        public int At(int k) => Reverse ? End - 1 - k : Start + k;
    }

    /// <summary>
    /// Where shift-clicked items go when they come from cells First..Last (inclusive). Ranges are tried in order and
    /// the first one that takes anything ends the attempt. A clearing route empties the source cell instead
    /// (the vacpack is never cleared).
    /// </summary>
    internal sealed class TransferRoute
    {
        public readonly int First, Last;
        public readonly CellRange[] Into;
        public readonly bool Clears;

        private TransferRoute(int first, int last, bool clears, CellRange[] into)
        {
            First = first;
            Last = last;
            Clears = clears;
            Into = into ?? new CellRange[0];
        }

        public static TransferRoute From(int first, int last, params CellRange[] into) => new TransferRoute(first, last, false, into);

        public static TransferRoute Clearing(int first, int last) => new TransferRoute(first, last, true, null);

        public bool Covers(int number) => number >= First && number <= Last;
    }
}
