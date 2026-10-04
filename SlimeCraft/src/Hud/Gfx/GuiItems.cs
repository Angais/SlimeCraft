using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Item-related drawing for <see cref="Gui"/>: 16x16 item icons, the per-cell overlays (durability bar and
    /// stack count), hover tooltips in the Minecraft tooltip style, and the text lines those tooltips show.
    /// Everything is drawn through the Gui primitives so the current transform applies.
    /// </summary>
    internal sealed partial class Gui
    {
        /// <summary>Optional source of a Slime Rancher sprite for the vacpack item (null result = not available).</summary>
        public Func<Sprite> VacpackIcon;
        /// <summary>Optional source of the built-in pixel-art vacpack region (null result = not available).</summary>
        public Func<GuiRegion> VacpackPixelIcon;

        private const string TooltipBackground = "gui/sprites/tooltip/background";
        private const string TooltipFrame = "gui/sprites/tooltip/frame";

        /// <summary>Icons found without the item-visuals service, keyed by item id (null = known to be missing).</summary>
        private readonly Dictionary<string, Texture2D> backupIcons = new Dictionary<string, Texture2D>();
        private static string[] smallNumbers;

        // ------------------------------------------------------------------ icons

        /// <summary>Draws the icon of an item id filling the square (x, y)..(x + size, y + size).</summary>
        public void DrawItem(float x, float y, string id, float size = 16f)
        {
            if (string.IsNullOrEmpty(id)) return;

            if (id == Content.Vacpack)
            {
                if (VacpackIcon != null)
                {
                    Sprite sprite = null;
                    try { sprite = VacpackIcon(); } catch { sprite = null; }
                    if (sprite != null)
                    {
                        DrawSRSprite(sprite, x, y, size, size);
                        return;
                    }
                }
                if (VacpackPixelIcon != null)
                {
                    GuiRegion pixels = null;
                    try { pixels = VacpackPixelIcon(); } catch { pixels = null; }
                    if (pixels != null)
                    {
                        DrawRegion(pixels, x, y, size, size, 0, 0, pixels.SW, pixels.SH, pixels.SW, pixels.SH);
                        return;
                    }
                }
            }

            Texture2D icon = null;
            try
            {
                var visuals = SC.ItemVisuals;
                if (visuals != null) icon = visuals.GetIcon(id);
            }
            catch { icon = null; }
            if (icon == null) icon = BackupIcon(id);
            if (icon != null) DrawTexture(icon, x, y, size, size, 0f, 0f, 1f, 1f);
        }

        public void DrawItem(float x, float y, ItemStack st)
        {
            if (st == null || st.IsEmpty) return;
            DrawItem(x, y, st.Id, 16f);
        }

        /// <summary>
        /// Plain texture for an item when the shared icon service gives nothing: the item's own texture, or the
        /// front (else top) face of its block. A miss is only remembered once the Minecraft assets are ready.
        /// </summary>
        private Texture2D BackupIcon(string id)
        {
            if (backupIcons.TryGetValue(id, out var cached)) return cached;
            var assets = SC.Assets;
            bool ready = assets != null && assets.Ready;
            Texture2D found = null;
            try
            {
                var def = Content.Item(id);
                string path = def?.Texture;
                if (path == null && def != null && def.IsBlock)
                {
                    var block = Content.Block(def.BlockId);
                    var faces = block?.Faces;
                    if (faces != null)
                    {
                        string spec = faces.Length > 2 ? faces[2] : null;
                        if (spec == null && faces.Length > 0) spec = faces[0];
                        path = BaseTexture(spec);
                    }
                }
                if (path != null && ready) found = assets.GetTexture(path);
            }
            catch { found = null; }
            if (found != null || ready) backupIcons[id] = found;
            return found;
        }

        /// <summary>Strips the overlay ("|...") and tint ("@RRGGBB") parts of a block face spec.</summary>
        private static string BaseTexture(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            int cut = spec.IndexOf('|');
            if (cut >= 0) spec = spec.Substring(0, cut);
            cut = spec.IndexOf('@');
            if (cut >= 0) spec = spec.Substring(0, cut);
            return spec.Length > 0 ? spec : null;
        }

        // ------------------------------------------------------------------ cell overlays

        /// <summary>
        /// What goes over the 16x16 icon square at (x, y): the wear gauge of a used tool, and the stack size (or
        /// <paramref name="countLabel"/> when given) right-aligned along the square's lower edge. Single items show
        /// no number unless a label is given.
        /// </summary>
        public void DrawItemOverlay(int x, int y, ItemStack st, string countLabel = null)
        {
            if (st == null || st.IsEmpty) return;
            var def = st.Def;
            bool worn = def != null && def.MaxDamage > 0 && st.Damage > 0;
            if (worn) DrawWearGauge(x + 2, y + 13, BarWidth(st, def), BarColor(st, def));

            string label = countLabel ?? (st.Count == 1 ? null : CountString(st.Count));
            if (label == null) return;
            int rightEdge = x + 17;
            Text(label, rightEdge - (Font != null ? Font.Width(label) : 0), y + 9, Argb.White, true);
        }

        /// <summary>A 13x2 black gauge at (left, top) whose upper row is lit for <paramref name="litPixels"/> pixels.</summary>
        private void DrawWearGauge(int left, int top, int litPixels, uint litColor)
        {
            Fill(left, top, left + 13, top + 2, Argb.Black);
            if (litPixels > 0) Fill(left, top, left + litPixels, top + 1, litColor);
        }

        /// <summary>Decimal text of a number; 0..999 come from a lazily filled table so drawing counts does not allocate.</summary>
        public static string CountString(int n)
        {
            if (n < 0 || n >= 1000) return n.ToString(CultureInfo.InvariantCulture);
            if (smallNumbers == null) smallNumbers = new string[1000];
            return smallNumbers[n] ?? (smallNumbers[n] = n.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Remaining durability as 0..13 pixels (rounded half up).</summary>
        public static int BarWidth(ItemStack st, ItemDef def)
        {
            int max = def != null ? def.MaxDamage : 0;
            if (st == null || max <= 0) return 13;
            double px = Math.Floor(13.0 - st.Damage * 13.0 / max + 0.5);
            if (px < 0) return 0;
            if (px > 13) return 13;
            return (int)px;
        }

        /// <summary>Green at full durability through yellow to red when used up.</summary>
        public static uint BarColor(ItemStack st, ItemDef def)
        {
            int max = def != null ? def.MaxDamage : 0;
            float left = 1f;
            if (st != null && max > 0) left = Math.Max(0f, (max - st.Damage) / (float)max);
            return Argb.Hsv(left / 3f, 1f, 1f);
        }

        // ------------------------------------------------------------------ tooltips

        /// <summary>
        /// Tooltip panel near the mouse: placed to the lower right of the pointer, flipped to the left side when it
        /// would leave the screen, and pushed up when it would run past the bottom edge.
        /// </summary>
        public void DrawTooltip(int mouseX, int mouseY, List<string> lines)
        {
            if (lines == null || lines.Count == 0 || Font == null || !Font.Loaded) return;
            LayerBreak();

            int contentW = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                int lw = Font.Width(lines[i]);
                if (lw > contentW) contentW = lw;
            }
            int contentH = lines.Count * 10 - (lines.Count == 1 ? 2 : 0);

            int cx = mouseX + 12, cy = mouseY - 12;
            if (cx + contentW > Width) cx = Math.Max(mouseX + 12 - 24 - contentW, 4);
            if (cy + contentH + 3 > Height) cy = Height - (contentH + 3);

            // 3 px padding plus 9 px of sprite margin around the text block
            int panelX = cx - 12, panelY = cy - 12, panelW = contentW + 24, panelH = contentH + 24;
            DrawSprite(TooltipBackground, panelX, panelY, panelW, panelH);
            DrawSprite(TooltipFrame, panelX, panelY, panelW, panelH);

            int lineY = cy;
            for (int i = 0; i < lines.Count; i++)
            {
                Text(lines[i], cx, lineY, Argb.White, true);
                lineY += i == 0 ? 12 : 10; // the name line is followed by a small gap
            }
        }

        /// <summary>Text lines describing a stack (name with rarity colour, optional category line, attributes).</summary>
        public static List<string> TooltipLines(ItemStack st, string extraBlueLine = null)
        {
            var lines = new List<string>();
            if (st == null || st.IsEmpty) return lines;

            string name;
            try { name = Content.DisplayName(st.Id); } catch { name = st.Id; }
            lines.Add(st.Id == Content.Vacpack ? "§e" + name : name);
            if (extraBlueLine != null) lines.Add("§9" + extraBlueLine);

            ItemDef def;
            try { def = st.Def; } catch { def = null; }
            if (def == null) return lines;

            if (def.Special == "vacpack")
            {
                lines.Add("§7Slime Rancher Vacpack");
                lines.Add("§7Right click: vac  ·  Left click: shoot");
                return lines;
            }

            bool weaponLike = def.Kind == ItemKind.Tool || def.Kind == ItemKind.Weapon;
            if (weaponLike && def.Tool != ToolType.None)
            {
                lines.Add("");
                lines.Add("§7" + Tr("item.modifiers.mainhand", "Held in the main hand:"));
                lines.Add("§2 " + StatText(def.AttackDamage) + " " + Tr("attribute.name.attack_damage", "damage"));
                lines.Add("§2 " + StatText(def.AttackSpeed) + " " + Tr("attribute.name.attack_speed", "attacks per second"));
            }
            return lines;
        }

        /// <summary>At most two decimals, no trailing zeros, invariant culture ("7", "1.6", "0.5").</summary>
        private static string StatText(float value)
        {
            double rounded = Math.Round((double)value, 2, MidpointRounding.AwayFromZero);
            return rounded.ToString("0.##", CultureInfo.InvariantCulture);
        }

        /// <summary>Text from the player's Minecraft language data, or <paramref name="fallback"/> when unknown.</summary>
        internal static string Tr(string key, string fallback)
        {
            try
            {
                var assets = SC.Assets;
                if (key != null && assets != null && assets.Ready)
                {
                    string text = assets.Translate(key);
                    if (!string.IsNullOrEmpty(text) && text != key) return text;
                }
            }
            catch { }
            return fallback;
        }
    }
}
