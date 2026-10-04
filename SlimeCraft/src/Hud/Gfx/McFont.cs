using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Bitmap text engine of the HUD, matching the look and metrics of Minecraft's GUI font.
    ///
    /// The glyph table is built at runtime from the font definitions and font page images inside the player's own
    /// Minecraft client jar (nothing is bundled). Font pages are packed into the shared GUI atlas so text batches
    /// together with sprites and fills. Supports the section-sign (§) colour and style codes, the one-pixel drop
    /// shadow, measuring, cutting plain strings to a width, and word wrapping that carries formatting across lines.
    /// </summary>
    internal sealed class McFont
    {
        public const int LineHeight = 9;

        private const char Section = '§';

        /// <summary>RGB of the sixteen colour codes 0-9 / a-f.</summary>
        private static readonly uint[] CodeColors =
        {
            0x000000, 0x0000AA, 0x00AA00, 0x00AAAA, 0xAA0000, 0xAA00AA, 0xFFAA00, 0xAAAAAA,
            0x555555, 0x5555FF, 0x55FF55, 0x55FFFF, 0xFF5555, 0xFF55FF, 0xFFFF55, 0xFFFFFF,
        };
        private const string HexDigits = "0123456789abcdef";

        /// <summary>One drawable (or invisible spacing) character.</summary>
        private sealed class Glyph
        {
            public bool Visible;
            public int Advance;
            public GuiRegion Region;
            public float Width, Height, Top;
            public float U0, VTop, U1, VBottom;
        }

        private static readonly Glyph Blank = new Glyph { Visible = false, Advance = 0 };

        private readonly Glyph[] latin = new Glyph[256];
        private readonly Dictionary<int, Glyph> others = new Dictionary<int, Glyph>();
        /// <summary>Visible printable-ASCII glyphs grouped by advance, used by the scrambled (§k) style.</summary>
        private readonly Dictionary<int, List<Glyph>> scramblePools = new Dictionary<int, List<Glyph>>();
        private Glyph questionMark;
        private int definedCount;
        private bool gaveUp;
        private uint scrambleState = 0x9E3779B9u;

        public bool Loaded { get; private set; }

        // ================================================================== loading

        /// <summary>
        /// Builds the glyph table once the assets and the atlas exist. Cheap "not yet" answer otherwise; after one
        /// real attempt the result is final (a broken jar is not retried every frame).
        /// </summary>
        public bool TryLoad(GuiAtlas atlas)
        {
            if (Loaded) return true;
            var assets = SC.Assets;
            if (gaveUp || assets == null || !assets.Ready || atlas == null || !atlas.Created) return false;
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                ReadFontFile("minecraft:default", atlas, seen);

                if (Lookup(' ') == null) Define(' ', new Glyph { Visible = false, Advance = 4 });
                if (Lookup('A') == null) LoadClassicAsciiGrid(atlas);
                questionMark = Lookup('?');

                if (Lookup('A') != null)
                {
                    Loaded = true;
                    SC.Log?.LogInfo("[Hud] Minecraft font loaded (" + definedCount + " glyphs)");
                }
                else
                {
                    gaveUp = true;
                    SC.Log?.LogError("[Hud] Minecraft font could not be loaded");
                }
            }
            catch (Exception e)
            {
                gaveUp = true;
                SC.Log?.LogError("[Hud] font load failed: " + e);
            }
            return Loaded;
        }

        /// <summary>
        /// Reads one font definition and walks its providers in order. Earlier definitions of a code point win, and
        /// references are expanded in place, so the resulting priority matches the game's.
        /// </summary>
        private void ReadFontFile(string fontId, GuiAtlas atlas, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(fontId)) return;
            int colon = fontId.IndexOf(':');
            string path = colon >= 0 ? fontId.Substring(colon + 1) : fontId;
            if (path.IndexOf("unifont", StringComparison.Ordinal) >= 0) return; // huge .hex fallback, not supported
            if (!seen.Add(path)) return;

            var json = SC.Assets.ReadJarJson("assets/minecraft/font/" + path + ".json");
            if (json == null) return;

            foreach (var provider in json["providers"].Items)
            {
                switch (provider["type"].AsString(""))
                {
                    case "reference":
                        ReadFontFile(provider["id"].AsString(null), atlas, seen);
                        break;
                    case "space":
                        foreach (var entry in provider["advances"].Members)
                        {
                            if (string.IsNullOrEmpty(entry.Key)) continue;
                            int cp = FirstCodePoint(entry.Key);
                            if (Lookup(cp) != null) continue;
                            int advance = (int)Math.Floor(entry.Value.AsFloat(0f) + 0.5f);
                            Define(cp, new Glyph { Visible = false, Advance = advance });
                        }
                        break;
                    case "bitmap":
                        ReadBitmapProvider(provider, atlas);
                        break;
                    // other provider kinds (ttf, unihex, ...) and provider filters are ignored
                }
            }
        }

        private void ReadBitmapProvider(JsonNode provider, GuiAtlas atlas)
        {
            string file = provider["file"].AsString(null);
            if (string.IsNullOrEmpty(file)) return;
            string texture = file.Substring(file.IndexOf(':') + 1);
            if (texture.EndsWith(".png", StringComparison.Ordinal)) texture = texture.Substring(0, texture.Length - 4);

            var sheet = new FontSheet
            {
                Texture = texture,
                Height = provider["height"].AsInt(8),
                Ascent = provider["ascent"].AsInt(7),
            };
            foreach (var row in provider["chars"].Items) sheet.Layout.Add(CodePoints(row.AsString("")));
            ImportSheet(sheet, atlas);
        }

        /// <summary>The traditional 16x16 ascii sheet where cell (row, col) holds code point row*16 + col.</summary>
        private void LoadClassicAsciiGrid(GuiAtlas atlas)
        {
            var sheet = new FontSheet { Texture = "font/ascii", Height = 8, Ascent = 7 };
            for (int row = 0; row < 16; row++)
            {
                var cps = new int[16];
                for (int col = 0; col < 16; col++) cps[col] = row * 16 + col;
                sheet.Layout.Add(cps);
            }
            ImportSheet(sheet, atlas);
        }

        /// <summary>
        /// A bitmap font page: an image divided into equal cells (as many columns as the first layout row lists,
        /// as many rows as the layout has) with one code point per cell (0 marks an unused cell).
        /// </summary>
        private sealed class FontSheet
        {
            public string Texture;
            public int Height, Ascent;
            public readonly List<int[]> Layout = new List<int[]>();

            public int Columns => Layout.Count > 0 ? Layout[0].Length : 0;
        }

        /// <summary>
        /// Loads a sheet's image, measures the ink of every cell and defines a glyph for each listed code point that is
        /// not defined yet.
        /// </summary>
        private void ImportSheet(FontSheet sheet, GuiAtlas atlas)
        {
            if (sheet.Layout.Count == 0 || sheet.Columns == 0) return;
            var assets = SC.Assets;
            if (!assets.TextureExists(sheet.Texture))
            {
                SC.Log?.LogWarning("[Hud] font page " + sheet.Texture + " is missing from the jar");
                return;
            }
            var region = atlas.Get(sheet.Texture);
            if (region == null) return;
            Texture2D image = assets.GetTexture(sheet.Texture);
            if (image == null) return;
            Color32[] pixels;
            try
            {
                pixels = image.GetPixels32();
            }
            catch (Exception e)
            {
                SC.Log?.LogWarning("[Hud] font page " + sheet.Texture + " not readable: " + e.Message);
                return;
            }

            int imageW = image.width, imageH = image.height;
            int cellW = imageW / sheet.Columns, cellH = imageH / sheet.Layout.Count;
            if (cellW <= 0 || cellH <= 0) return;
            int[,] ink = InkExtents(pixels, imageW, imageH, cellW, cellH, sheet.Layout.Count, LongestRow(sheet.Layout));
            float toGui = sheet.Height / (float)cellH;

            for (int row = 0; row < sheet.Layout.Count; row++)
            {
                int[] codes = sheet.Layout[row];
                for (int col = 0; col < codes.Length; col++)
                {
                    if (codes[col] == 0 || Lookup(codes[col]) != null) continue;
                    var glyph = new Glyph
                    {
                        Visible = true,
                        // inked width in GUI pixels, rounded half up, plus one pixel of letter spacing
                        Advance = (int)Math.Floor(ink[row, col] * toGui + 0.5f) + 1,
                        Region = region,
                        Width = cellW * toGui,
                        Height = cellH * toGui,
                        Top = 7 - sheet.Ascent,
                    };
                    region.UV(col * cellW, row * cellH, cellW, cellH, imageW, imageH,
                        out glyph.U0, out glyph.VTop, out glyph.U1, out glyph.VBottom);
                    Define(codes[col], glyph);
                    AddToScramblePool(codes[col], glyph);
                }
            }
        }

        private static int LongestRow(List<int[]> layout)
        {
            int longest = 0;
            foreach (var r in layout) longest = Math.Max(longest, r.Length);
            return longest;
        }

        /// <summary>
        /// One pass over the image: for every cell of the rows x columns grid, how far its ink reaches from the
        /// cell's left edge (one past the rightmost column holding a pixel with any alpha; 0 for an empty cell).
        /// Cells reaching past the image edge only count the pixels inside it.
        /// </summary>
        private static int[,] InkExtents(Color32[] pixels, int imageW, int imageH, int cellW, int cellH, int rows, int columns)
        {
            var reach = new int[rows, columns];
            int spanW = Math.Min(imageW, columns * cellW);
            int spanH = Math.Min(imageH, rows * cellH);
            for (int top = 0; top < spanH; top++)
            {
                int stored = (imageH - 1 - top) * imageW; // Unity keeps the bottom row first
                int row = top / cellH;
                for (int x = 0; x < spanW; x++)
                {
                    if (pixels[stored + x].a == 0) continue;
                    int col = x / cellW;
                    int extent = x - col * cellW + 1;
                    if (extent > reach[row, col]) reach[row, col] = extent;
                }
            }
            return reach;
        }

        /// <summary>Printable ASCII glyphs are grouped by advance so the scrambled style can swap equal-width glyphs.</summary>
        private void AddToScramblePool(int cp, Glyph glyph)
        {
            if (cp < 33 || cp > 126) return;
            if (!scramblePools.TryGetValue(glyph.Advance, out var pool))
            {
                pool = new List<Glyph>();
                scramblePools[glyph.Advance] = pool;
            }
            pool.Add(glyph);
        }

        private void Define(int cp, Glyph glyph)
        {
            if (cp >= 0 && cp < latin.Length)
            {
                if (latin[cp] != null) return;
                latin[cp] = glyph;
            }
            else
            {
                if (others.ContainsKey(cp)) return;
                others[cp] = glyph;
            }
            definedCount++;
        }

        private Glyph Lookup(int cp)
        {
            if (cp >= 0 && cp < latin.Length) return latin[cp];
            return others.TryGetValue(cp, out var g) ? g : null;
        }

        /// <summary>Glyph for a code point, falling back to '?' and then to an empty spacer.</summary>
        private Glyph GlyphFor(int cp)
        {
            return Lookup(cp) ?? questionMark ?? Blank;
        }

        private static int FirstCodePoint(string s)
        {
            if (s.Length > 1 && char.IsHighSurrogate(s[0]) && char.IsLowSurrogate(s[1])) return char.ConvertToUtf32(s[0], s[1]);
            return s[0];
        }

        private static int[] CodePoints(string s)
        {
            var list = new List<int>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    list.Add(char.ConvertToUtf32(c, s[i + 1]));
                    i++;
                }
                else list.Add(c);
            }
            return list.ToArray();
        }

        // ================================================================== formatting codes

        /// <summary>Index into the colour table for a lower-case code character, or -1.</summary>
        private static int ColorIndex(char code)
        {
            if (code >= '0' && code <= '9') return code - '0';
            if (code >= 'a' && code <= 'f') return code - 'a' + 10;
            return -1;
        }

        /// <summary>Bold state after applying one code (only bold affects measuring).</summary>
        private static bool BoldAfter(char code, bool bold)
        {
            if (code == 'l') return true;
            if (code == 'r' || ColorIndex(code) >= 0) return false;
            return bold;
        }

        /// <summary>Code point at <paramref name="i"/>, combining a surrogate pair; <paramref name="units"/> = 1 or 2.</summary>
        private static int CodePointAt(string s, int i, out int units)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                units = 2;
                return char.ConvertToUtf32(c, s[i + 1]);
            }
            units = 1;
            return c;
        }

        // ================================================================== measuring

        /// <summary>Horizontal step of one code point (6 before the font is loaded).</summary>
        public int Advance(int cp, bool bold)
        {
            if (!Loaded) return 6;
            return GlyphFor(cp).Advance + (bold ? 1 : 0);
        }

        /// <summary>Pixel width of formatted text: codes count 0, bold glyphs count one extra pixel, no shadow pixel.</summary>
        public int Width(string s)
        {
            if (!Loaded || string.IsNullOrEmpty(s)) return 0;
            int total = 0;
            bool bold = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == Section && i + 1 < s.Length)
                {
                    bold = BoldAfter(char.ToLowerInvariant(s[i + 1]), bold);
                    i++;
                    continue;
                }
                int cp = CodePointAt(s, i, out int units);
                total += GlyphFor(cp).Advance + (bold ? 1 : 0);
                i += units - 1;
            }
            return total;
        }

        /// <summary>
        /// How many characters, starting at <paramref name="from"/> and going right, fit side by side in
        /// <paramref name="room"/> pixels. Plain measuring: § is an ordinary character and every UTF-16 unit counts
        /// alone. Before the font is loaded everything fits.
        /// </summary>
        public int FitAhead(string s, int from, int room)
        {
            if (string.IsNullOrEmpty(s) || from >= s.Length) return 0;
            if (!Loaded) return s.Length - from;
            int i = from;
            for (int left = room; i < s.Length; i++)
            {
                left -= GlyphFor(s[i]).Advance;
                if (left < 0) break;
            }
            return i - from;
        }

        /// <summary>Like <see cref="FitAhead"/>, but for the characters just before index <paramref name="end"/>, going left.</summary>
        public int FitBehind(string s, int end, int room)
        {
            if (string.IsNullOrEmpty(s) || end <= 0) return 0;
            end = Math.Min(end, s.Length);
            if (!Loaded) return end;
            int i = end;
            for (int left = room; i > 0; i--)
            {
                left -= GlyphFor(s[i - 1]).Advance;
                if (left < 0) break;
            }
            return end - i;
        }

        /// <summary>
        /// Pen offsets of formatted text: <paramref name="into"/>[k] becomes the width of the first k characters
        /// measured on their own (k = 0 .. Length), exactly as <see cref="Width"/> would report for that piece.
        /// A code or surrogate pair cut in half by a piece boundary counts as the lone character it starts with.
        /// </summary>
        public void PrefixWidths(string s, List<int> into)
        {
            into.Clear();
            into.Add(0);
            if (string.IsNullOrEmpty(s)) return;
            int pen = 0;
            bool bold = false;
            int i = 0;
            while (i < s.Length)
            {
                int extra = bold ? 1 : 0;
                bool isCode = s[i] == Section && i + 1 < s.Length;
                int cp = isCode ? s[i] : CodePointAt(s, i, out _);
                int units = isCode || cp > 0xFFFF ? 2 : 1;
                if (units == 2)
                {
                    // the piece that ends between the two units sees the first one as an ordinary character
                    into.Add(pen + (Loaded ? GlyphFor(s[i]).Advance + extra : 0));
                }
                if (isCode) bold = BoldAfter(char.ToLowerInvariant(s[i + 1]), bold);
                else if (Loaded) pen += GlyphFor(cp).Advance + extra;
                into.Add(pen);
                i += units;
            }
        }

        /// <summary>
        /// Shortest code string that recreates the formatting active at the end of <paramref name="s"/>:
        /// the colour first, then the styles in the order k, l, m, n, o. Empty when nothing is active.
        /// </summary>
        public static string ActiveFormat(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int color = -1;
            bool scramble = false, bold = false, strike = false, under = false, italic = false;
            for (int i = 0; i + 1 < s.Length; i++)
            {
                if (s[i] != Section) continue;
                char code = char.ToLowerInvariant(s[i + 1]);
                i++;
                int ci = ColorIndex(code);
                if (ci >= 0)
                {
                    color = ci;
                    scramble = bold = strike = under = italic = false;
                    continue;
                }
                switch (code)
                {
                    case 'k': scramble = true; break;
                    case 'l': bold = true; break;
                    case 'm': strike = true; break;
                    case 'n': under = true; break;
                    case 'o': italic = true; break;
                    case 'r':
                        color = -1;
                        scramble = bold = strike = under = italic = false;
                        break;
                }
            }
            if (color < 0 && !scramble && !bold && !strike && !under && !italic) return "";
            var sb = new StringBuilder(12);
            if (color >= 0) sb.Append(Section).Append(HexDigits[color]);
            if (scramble) sb.Append(Section).Append('k');
            if (bold) sb.Append(Section).Append('l');
            if (strike) sb.Append(Section).Append('m');
            if (under) sb.Append(Section).Append('n');
            if (italic) sb.Append(Section).Append('o');
            return sb.ToString();
        }

        // ================================================================== word wrap

        /// <summary>
        /// Wraps formatted text to <paramref name="maxWidth"/>. Explicit newlines start new paragraphs, and every
        /// paragraph and continuation line begins with the codes that were active where it starts, so colours carry
        /// over. Lines break at the last space when possible, else inside the word; each line keeps at least one
        /// visible character.
        /// </summary>
        public List<string> Split(string text, int maxWidth)
        {
            var output = new List<string>();
            if (text == null)
            {
                output.Add("");
                return output;
            }
            string clean = text.IndexOf('\r') >= 0 ? text.Replace("\r", "") : text;
            string carried = "";
            foreach (string piece in clean.Split('\n'))
            {
                string paragraph = carried + piece;
                WrapParagraph(paragraph, maxWidth, output);
                carried = ActiveFormat(paragraph);
            }
            return output;
        }

        private void WrapParagraph(string para, int maxWidth, List<string> output)
        {
            if (para.Length == 0)
            {
                output.Add("");
                return;
            }

            int lineStart = 0;
            string lead = "";
            while (true)
            {
                bool bold = lead.IndexOf("§l", StringComparison.Ordinal) >= 0;
                int width = 0;
                int candidateSpace = -1;   // last space after the line's first visible character
                bool anyVisible = false;
                int cut = -1, resume = -1;

                int i = lineStart;
                while (i < para.Length)
                {
                    char c = para[i];
                    if (c == Section && i + 1 < para.Length)
                    {
                        bold = BoldAfter(char.ToLowerInvariant(para[i + 1]), bold);
                        i += 2;
                        continue;
                    }
                    int units = char.IsHighSurrogate(c) && i + 1 < para.Length && char.IsLowSurrogate(para[i + 1]) ? 2 : 1;
                    int step = 0;
                    for (int u = 0; u < units; u++) step += GlyphFor(para[i + u]).Advance + (bold ? 1 : 0);

                    if (anyVisible && width + step > maxWidth)
                    {
                        if (c == ' ') { cut = i; resume = i + 1; }
                        else if (candidateSpace >= 0) { cut = candidateSpace; resume = candidateSpace + 1; }
                        else { cut = i; resume = i; }
                        break;
                    }
                    if (c == ' ' && anyVisible) candidateSpace = i;
                    width += step;
                    anyVisible = true;
                    i += units;
                }

                if (cut < 0)
                {
                    output.Add(lead + para.Substring(lineStart));
                    return;
                }
                output.Add(lead + para.Substring(lineStart, cut - lineStart));
                if (!HasVisibleFrom(para, resume)) return; // nothing but codes (or nothing) left
                lead = ActiveFormat(para.Substring(0, resume));
                lineStart = resume;
            }
        }

        private static bool HasVisibleFrom(string s, int start)
        {
            for (int i = start; i < s.Length; i++)
            {
                if (s[i] == Section && i + 1 < s.Length) { i++; continue; }
                return true;
            }
            return false;
        }

        // ================================================================== drawing

        /// <summary>
        /// Draws one line of formatted text with its top-left at (x, y). With a shadow, the whole line is drawn once
        /// one pixel down and right in a quarter-brightness tint first. Returns the end x (+1 with a shadow).
        /// </summary>
        public float Draw(Gui g, string s, float x, float y, uint color, bool shadow)
        {
            if (!Loaded || g == null || string.IsNullOrEmpty(s)) return x;
            if ((color & 0xFC000000u) == 0) return x; // alpha below 4: invisible
            uint scrambleSeed = NextScrambleSeed();
            if (!shadow) return DrawRun(g, s, x, y, color, false, scrambleSeed);
            DrawRun(g, s, x + 1f, y + 1f, color, true, scrambleSeed);
            return DrawRun(g, s, x, y, color, false, scrambleSeed) + 1f;
        }

        private uint NextScrambleSeed()
        {
            uint v = scrambleState;
            v ^= v << 13;
            v ^= v >> 17;
            v ^= v << 5;
            scrambleState = v;
            return v;
        }

        /// <summary>One pass over the string (shadow or main). Both passes share the seed so scrambled glyphs match.</summary>
        private float DrawRun(Gui g, string s, float x, float y, uint baseColor, bool asShadow, uint scrambleSeed)
        {
            int alpha = Argb.A(baseColor);
            uint textColor = baseColor;
            bool bold = false, italic = false, under = false, strike = false, scramble = false;
            bool canFill = g.Atlas != null && g.Atlas.White != null;
            uint rng = scrambleSeed;
            float pen = x;

            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == Section && i + 1 < s.Length)
                {
                    char code = char.ToLowerInvariant(s[i + 1]);
                    i++;
                    int ci = ColorIndex(code);
                    if (ci >= 0)
                    {
                        textColor = Argb.Color(alpha, CodeColors[ci]);
                        bold = italic = under = strike = scramble = false;
                        continue;
                    }
                    switch (code)
                    {
                        case 'l': bold = true; break;
                        case 'o': italic = true; break;
                        case 'n': under = true; break;
                        case 'm': strike = true; break;
                        case 'k': scramble = true; break;
                        case 'r':
                            textColor = baseColor;
                            bold = italic = under = strike = scramble = false;
                            break;
                    }
                    continue;
                }

                int cp = CodePointAt(s, i, out int units);
                i += units - 1;
                Glyph glyph = GlyphFor(cp);
                if (scramble && glyph.Visible && scramblePools.TryGetValue(glyph.Advance, out var pool) && pool.Count > 0)
                {
                    rng = rng * 1664525u + 1013904223u;
                    glyph = pool[(int)((rng >> 8) % (uint)pool.Count)];
                }

                uint ink = asShadow ? Argb.ScaleRGB(textColor, 0.25f) : textColor;
                if (glyph.Visible)
                {
                    EmitGlyph(g, glyph, pen, y, ink, italic);
                    if (bold) EmitGlyph(g, glyph, pen + 1f, y, ink, italic);
                }
                int step = glyph.Advance + (bold ? 1 : 0);
                if (canFill)
                {
                    if (strike) g.Fill(pen - 1f, y + 3.5f, pen + step, y + 4.5f, ink);
                    if (under) g.Fill(pen - 1f, y + 8f, pen + step, y + 9f, ink);
                }
                pen += step;
            }
            return pen;
        }

        private static void EmitGlyph(Gui g, Glyph glyph, float left, float y, uint color, bool italic)
        {
            float top = y + glyph.Top;
            float bottom = top + glyph.Height;
            float skewTop = 0f, skewBottom = 0f;
            if (italic)
            {
                skewTop = 1f - 0.25f * glyph.Top;
                skewBottom = 1f - 0.25f * (glyph.Top + glyph.Height);
            }
            g.Quad(glyph.Region.Tex, left, top, left + glyph.Width, bottom,
                glyph.U0, glyph.VTop, glyph.U1, glyph.VBottom, color, skewTop, skewBottom);
        }
    }
}
