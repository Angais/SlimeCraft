using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>Fixed-capacity buffer that overwrites its oldest entry once full.</summary>
    internal sealed class Ring<T>
    {
        private readonly T[] items;
        private int head;   // where the next entry goes
        private int count;

        public Ring(int capacity)
        {
            items = new T[Math.Max(1, capacity)];
        }

        public int Count => count;

        public void Push(T item)
        {
            items[head] = item;
            head = (head + 1) % items.Length;
            if (count < items.Length) count++;
        }

        /// <summary>Entry by age: 0 is the newest, Count - 1 the oldest.</summary>
        public T ByAge(int age) => items[(head - 1 - age + 2 * items.Length) % items.Length];

        /// <summary>Entry in arrival order: 0 is the oldest.</summary>
        public T InOrder(int i) => ByAge(count - 1 - i);

        public void Clear()
        {
            Array.Clear(items, 0, items.Length);
            head = 0;
            count = 0;
        }
    }

    /// <summary>
    /// Chat message store and chat overlay in Minecraft's chat style.
    ///
    /// Keeps the latest messages and their wrapped rows in ring buffers and draws the rows bottom-left above the
    /// hotbar on translucent strips. While the chat page is closed rows fade out about ten seconds after they
    /// arrived; while it is open a taller page is shown that can be scrolled (a view offset counted in rows from the
    /// newest one) with a thin scroll bar. Also remembers the lines the player sent (for Up/Down recall) and copies
    /// every message to the BepInEx log. Input is handled by the chat page.
    /// </summary>
    internal sealed class ChatHistory
    {
        private const int Capacity = 100;
        private const int WrapWidth = 320;
        private const int RowHeight = 9;
        private const int OpenHeight = 180;
        private const int ClosedHeight = 91;
        private const int FadeTicks = 200;

        private struct Message
        {
            public string Text;
            public int Tick;
        }

        private struct Row
        {
            public string Text;
            public int Tick;
        }

        private readonly Ring<Message> messages = new Ring<Message>(Capacity);
        private readonly Ring<Row> rows = new Ring<Row>(Capacity);
        private readonly Ring<string> sent = new Ring<string>(Capacity);

        /// <summary>How many rows the view is scrolled up from the newest row.</summary>
        private int viewOffset;
        /// <summary>New rows arrived while the view was scrolled up (the scroll bar turns red).</summary>
        private bool unseenBelow;

        // ------------------------------------------------------------------ sent lines

        public int SentCount => sent.Count;

        /// <summary>A sent line in sending order (0 = oldest).</summary>
        public string SentAt(int i) => sent.InOrder(i);

        /// <summary>Remembers a sent line; repeating the newest entry stores nothing.</summary>
        public void RememberSent(string line)
        {
            if (sent.Count > 0 && string.Equals(sent.ByAge(0), line, StringComparison.Ordinal)) return;
            sent.Push(line);
        }

        // ------------------------------------------------------------------ content

        public void Wipe(bool sentToo)
        {
            messages.Clear();
            rows.Clear();
            viewOffset = 0;
            unseenBelow = false;
            if (sentToo) sent.Clear();
        }

        public void Post(string message, int tick, McFont font, bool open)
        {
            if (message == null) return;
            SC.Log?.LogInfo("[CHAT] " + PlainText(message));
            messages.Push(new Message { Text = message, Tick = tick });
            AddRows(message, tick, font, open);
        }

        /// <summary>Wraps every stored message again (used once the real font has loaded).</summary>
        public void Reflow(McFont font)
        {
            rows.Clear();
            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages.InOrder(i);
                AddRows(m.Text, m.Tick, font, false);
            }
        }

        private static List<string> Wrap(string text, McFont font)
        {
            if (font != null && font.Loaded) return font.Split(text, WrapWidth);
            string clean = text.IndexOf('\r') >= 0 ? text.Replace("\r", "") : text;
            return new List<string>(clean.Split('\n'));
        }

        private void AddRows(string text, int tick, McFont font, bool open)
        {
            var lines = Wrap(text, font);
            int rowsBefore = rows.Count;
            for (int k = 0; k < lines.Count; k++)
            {
                if (open && viewOffset > 0)
                {
                    // keep the rows the player is reading in place while new ones arrive underneath; the bound
                    // counts every row of this message added so far
                    unseenBelow = true;
                    SetView(viewOffset + 1, rowsBefore + k);
                }
                rows.Push(new Row { Text = lines[k], Tick = tick });
            }
        }

        /// <summary>Removes every § code together with the character after it (a trailing lone § is kept).</summary>
        private static string PlainText(string s)
        {
            if (s.IndexOf('§') < 0) return s;
            var sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                bool code = s[i] == '§' && i + 1 < s.Length;
                if (!code) sb.Append(s[i]);
                i += code ? 2 : 1;
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ scrolling

        public int RowsPerPage(bool open) => (open ? OpenHeight : ClosedHeight) / RowHeight;

        /// <summary>Scrolls the open view by <paramref name="delta"/> rows (positive = towards older rows).</summary>
        public void ScrollBy(int delta) => SetView(viewOffset + delta, rows.Count);

        public void ScrollToNewest() => SetView(0, rows.Count);

        private void SetView(int offset, int rowCount)
        {
            int highest = Math.Max(0, rowCount - RowsPerPage(true));
            viewOffset = Mathf.Clamp(offset, 0, highest);
            if (viewOffset == 0) unseenBelow = false;
        }

        // ------------------------------------------------------------------ drawing

        /// <summary>Closed view: rows stay solid for 9 s, then fade out with a squared curve over the last second.</summary>
        private static float Opacity(int rowTick, int now, bool open)
        {
            if (open) return 1f;
            float left = Mathf.Clamp01((1f - (now - rowTick) / (float)FadeTicks) * 10f);
            return left * left;
        }

        public void Draw(Gui g, int ticks, bool open)
        {
            if (rows.Count == 0) return;
            int shown = Math.Min(RowsPerPage(open), rows.Count - viewOffset);
            if (shown <= 0) return;

            g.LayerBreak();
            g.Push();
            g.Translate(4f, 0f);

            int bottom = g.Height - 40;
            float backAlpha = Mathf.Clamp01(HudConfig.ChatBgOpacity);

            // every strip before any text, so no strip ever covers a glyph
            int painted = 0;
            for (int line = 0; line < shown; line++)
            {
                var row = rows.ByAge(viewOffset + line);
                float a = Opacity(row.Tick, ticks, open);
                if (a <= 1e-5f) continue;
                int rowBottom = bottom - line * RowHeight;
                g.Fill(-4, rowBottom - RowHeight, WrapWidth + 8, rowBottom, Argb.BlackA(a * backAlpha));
                painted++;
            }
            for (int line = 0; line < shown; line++)
            {
                var row = rows.ByAge(viewOffset + line);
                float a = Opacity(row.Tick, ticks, open);
                if (a <= 1e-5f) continue;
                g.Text(row.Text, 0f, bottom - line * RowHeight - 8, Argb.WhiteA(a), true);
            }

            if (open && painted > 0 && ThumbSpan(bottom, painted, out int thumbTop, out int thumbBottom))
                PaintThumb(g, thumbTop, thumbBottom);
            g.Pop();
        }

        /// <summary>
        /// Vertical extent of the scroll thumb of the open view. The thumb stands for the painted rows among all rows:
        /// its length is the page height scaled by that share, and it rises from the page bottom by the view offset
        /// scaled the same way. False when every row is on the page (no thumb).
        /// </summary>
        private bool ThumbSpan(int pageBottom, int painted, out int top, out int bottom)
        {
            int all = rows.Count;
            top = bottom = pageBottom;
            if (painted == all) return false;
            int pagePixels = painted * RowHeight;
            bottom = pageBottom - viewOffset * pagePixels / all;
            top = bottom - pagePixels * painted / all;
            return true;
        }

        /// <summary>Two-pixel thumb at x 324..326 with a light right column; red while unseen rows wait below.</summary>
        private void PaintThumb(Gui g, int top, int bottom)
        {
            const int left = 324, width = 2;
            int alpha = bottom < 0 ? 170 : 96;
            uint body = unseenBelow ? 0xCC3333u : 0x3333AAu;
            g.Fill(left, top, left + width, bottom, Argb.Color(alpha, body));
            g.Fill(left + width - 1, top, left + width, bottom, Argb.Color(alpha, 0xCCCCCCu));
        }
    }
}
