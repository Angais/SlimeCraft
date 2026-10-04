using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Tiny CPU text rasteriser used for the title-screen splash. It reads the player's own Minecraft
    /// <c>font/ascii.png</c> sheet at runtime (nothing is bundled), measures every glyph once, and turns a short
    /// string into a point-filtered texture with the one-pixel drop shadow that gives Minecraft text its look.
    ///
    /// The sheet is a 16 x 16 grid where the cell index equals the character code. Glyphs are always laid out in
    /// 8 x 8 "font units" no matter how large the sheet is, so high-resolution resource-pack fonts work too.
    ///
    /// Not to be confused with the HUD's font class in the Hud module; this one lives in <c>SlimeCraft.Core</c>.
    /// </summary>
    internal sealed class McFont
    {
        private const string SheetPath = "font/ascii";
        private const int GridSize = 16;         // cells per row and per column
        private const int Units = 8;             // logical glyph size in font units
        private const int SpaceAdvance = 4;
        private const int ShadowOffset = 1;
        private const char Fallback = '?';

        private readonly PixelImage sheet;
        private readonly int cellWidth;
        private readonly int cellHeight;
        private readonly int[] advance = new int[256];

        private McFont(PixelImage sheet)
        {
            this.sheet = sheet;
            cellWidth = sheet.W / GridSize;
            cellHeight = sheet.H / GridSize;
            for (int code = 0; code < advance.Length; code++)
                advance[code] = code == ' ' ? SpaceAdvance : MeasureAdvance(code);
        }

        /// <summary>Loads the font from the Minecraft jar, or returns null if it is missing or unusable.</summary>
        public static McFont Load(McAssets assets)
        {
            if (assets == null || !assets.Ready) return null;
            var img = assets.GetPixels(SheetPath);
            if (img == null || img.W < GridSize || img.H < GridSize) return null;
            return new McFont(img);
        }

        /// <summary>True when every character is printable ASCII (space through tilde).</summary>
        public static bool CanRender(string s)
        {
            if (s == null) return true;
            foreach (char c in s)
                if (c < 0x20 || c > 0x7E) return false;
            return true;
        }

        /// <summary>Total advance of <paramref name="s"/> in font units (the shadow column is not included).</summary>
        public int Width(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int total = 0;
            foreach (char c in s) total += advance[CodeOf(c)];
            return total;
        }

        /// <summary>
        /// Draws <paramref name="s"/> in <paramref name="color"/> over a quarter-brightness shadow shifted one unit right
        /// and down. The texture is (Width + 1) x 9 pixels, transparent elsewhere; the caller owns and destroys it.
        /// </summary>
        public Texture2D Render(string s, Color32 color)
        {
            s = s ?? string.Empty;
            var canvas = new PixelImage(Mathf.Max(1, Width(s) + 1), Units + 1);
            var shadow = new Color32((byte)(color.r / 4), (byte)(color.g / 4), (byte)(color.b / 4), color.a);

            Stamp(canvas, s, ShadowOffset, ShadowOffset, shadow);
            Stamp(canvas, s, 0, 0, color);

            return Pixels.ToTexture(canvas, "mctext");
        }

        // ------------------------------------------------------------------ internals

        private static int CodeOf(char c) => c < 256 ? c : Fallback;

        /// <summary>
        /// Advance of one glyph: the inked width of its cell (up to the right-most column with any visible pixel),
        /// converted to font units with halves rounded up, plus one unit of spacing.
        /// </summary>
        private int MeasureAdvance(int code)
        {
            int left = (code % GridSize) * cellWidth;
            int top = (code / GridSize) * cellHeight;

            int lastInkColumn = -1;
            for (int col = 0; col < cellWidth; col++)
            {
                for (int row = 0; row < cellHeight; row++)
                {
                    if (sheet.GetTopDown(left + col, top + row).a != 0)
                    {
                        lastInkColumn = col;
                        break;
                    }
                }
            }
            int inked = lastInkColumn + 1;

            // round(inked * 8 / cellHeight) with ties going up, done in integers: floor((2*inked*8 + h) / (2*h))
            int numerator = 2 * inked * Units + cellHeight;
            int scaled = numerator / (2 * cellHeight);
            return scaled + 1;
        }

        /// <summary>Writes the glyph masks of <paramref name="s"/> into <paramref name="canvas"/> as solid colour.</summary>
        private void Stamp(PixelImage canvas, string s, int originX, int originY, Color32 ink)
        {
            int pen = originX;
            foreach (char ch in s)
            {
                int code = CodeOf(ch);
                if (code != ' ') StampGlyph(canvas, code, pen, originY, ink);
                pen += advance[code];
            }
        }

        private void StampGlyph(PixelImage canvas, int code, int destX, int destY, Color32 ink)
        {
            int left = (code % GridSize) * cellWidth;
            int top = (code / GridSize) * cellHeight;

            for (int gy = 0; gy < Units; gy++)
            {
                int py = destY + gy;
                if (py < 0 || py >= canvas.H) continue;
                int sy = top + gy * cellHeight / Units;
                for (int gx = 0; gx < Units; gx++)
                {
                    int px = destX + gx;
                    if (px < 0 || px >= canvas.W) continue;
                    int sx = left + gx * cellWidth / Units;
                    if (sheet.GetTopDown(sx, sy).a != 0) canvas.SetTopDown(px, py, ink);
                }
            }
        }
    }
}
