using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Single-line text input drawn with the HUD font (no frame or background of its own). Features: a blinking
    /// caret (an underscore while appending, a thin bar while inserting or when full), a highlighted selection,
    /// horizontal scrolling that keeps the caret visible, word-wise movement and erasing, clipboard shortcuts, an
    /// edit callback and a grey inline completion hint.
    /// </summary>
    internal sealed class TextField
    {
        /// <summary>What <see cref="Erase"/> removes when nothing is selected.</summary>
        public enum Unit { Char, Word }

        private const uint HintInk = 0xFF808080u;
        private const uint SelectionInk = 0xFF3355FFu;
        private const int BlinkStepMs = 300;
        private const bool Shadowed = true;

        /// <summary>Top-left of the text and the width of the visible window, in GUI pixels.</summary>
        public int X, Y, Width;
        /// <summary>Longest text accepted.</summary>
        public int Limit = 32;
        public bool Shown = true;
        public uint Ink = 0xFFE0E0E0u;
        /// <summary>Grey completion drawn right after the text while the caret is at the end (null = none).</summary>
        public string Ghost;
        /// <summary>Runs with the new text after every edit and after <see cref="Replace"/>.</summary>
        public Action<string> Edited;

        private readonly McFont font;
        private string text = "";
        private int caret;
        private int anchor;      // other end of the selection; equals the caret when nothing is selected
        private int viewStart;   // index of the leftmost visible character
        private float blinkZero;
        private bool focus;
        /// <summary>Pen offsets of the characters shown this frame (entry k = width of the first k), reused.</summary>
        private readonly List<int> pens = new List<int>(64);

        public TextField(McFont font, int x, int y, int width)
        {
            this.font = font;
            X = x;
            Y = y;
            Width = width;
            RestartBlink();
        }

        public bool HasFocus
        {
            get => focus;
            set
            {
                if (value && !focus) RestartBlink();
                focus = value;
            }
        }

        public string Text => text;

        public int Caret => caret;

        private bool HasSelection => caret != anchor;
        private int SelLow => Mathf.Clamp(Math.Min(caret, anchor), 0, text.Length);
        private int SelHigh => Mathf.Clamp(Math.Max(caret, anchor), 0, text.Length);

        /// <summary>The selected part of the text ("" when nothing is selected).</summary>
        public string Selection => text.Substring(SelLow, SelHigh - SelLow);

        private bool FontReady => font != null && font.Loaded;

        // ------------------------------------------------------------------ editing

        /// <summary>Replaces the whole text (cut to <see cref="Limit"/>), puts the caret at the end and always reports the edit.</summary>
        public void Replace(string value)
        {
            string s = value ?? "";
            int max = Math.Max(0, Limit);
            Commit(s.Length > max ? s.Substring(0, max) : s, int.MaxValue);
        }

        /// <summary>Types text at the caret, replacing the selection. Characters the chat does not allow are dropped.</summary>
        public void Insert(string typed)
        {
            int lo = SelLow, hi = SelHigh;
            int free = Limit - lo - (text.Length - hi); // what the text around the selection leaves for new characters
            if (free <= 0) return;
            string piece = CutToFit(Filter(typed ?? ""), free);
            Commit(text.Substring(0, lo) + piece + text.Substring(hi), lo + piece.Length);
        }

        /// <summary>
        /// <paramref name="s"/> shortened to at most <paramref name="max"/> units; a shortened result never ends on the
        /// first half of a surrogate pair.
        /// </summary>
        private static string CutToFit(string s, int max)
        {
            if (s.Length <= max) return s;
            int keep = char.IsHighSurrogate(s[max - 1]) ? max - 1 : max;
            return s.Substring(0, keep);
        }

        /// <summary>
        /// Removes the selection, or else one character / word before (<paramref name="direction"/> &lt; 0) or after the caret.
        /// </summary>
        public void Erase(Unit unit, int direction)
        {
            if (text.Length == 0) return;
            if (HasSelection)
            {
                Cut(SelLow, SelHigh);
                return;
            }
            int edge = unit == Unit.Word ? WordEdge(caret, direction) : CharEdge(caret, direction);
            Cut(Math.Min(caret, edge), Math.Max(caret, edge));
        }

        private void Cut(int from, int to)
        {
            from = Mathf.Clamp(from, 0, text.Length);
            to = Mathf.Clamp(to, 0, text.Length);
            if (to > from) Commit(text.Remove(from, to - from), from);
        }

        /// <summary>Stores new text, collapses the selection at <paramref name="newCaret"/> and reports the edit.</summary>
        private void Commit(string newText, int newCaret)
        {
            text = newText ?? "";
            SetCaret(newCaret, false);
            RestartBlink();
            Report();
        }

        private static bool Allowed(char c) => c >= ' ' && c != '§' && c != (char)127;

        private static string Filter(string s)
        {
            int bad = 0;
            foreach (char c in s) if (!Allowed(c)) bad++;
            if (bad == 0) return s;
            var sb = new StringBuilder(s.Length - bad);
            foreach (char c in s) if (Allowed(c)) sb.Append(c);
            return sb.ToString();
        }

        private void Report()
        {
            var listener = Edited;
            if (listener == null) return;
            try { listener(text); }
            catch (Exception e) { HudModule.LogLimited("textfield", "text field callback failed: " + e); }
        }

        // ------------------------------------------------------------------ caret and scrolling

        /// <summary>Index one character before / after <paramref name="pos"/>, never splitting a surrogate pair.</summary>
        private int CharEdge(int pos, int direction)
        {
            int step = direction < 0 ? -1 : 1;
            int next = Mathf.Clamp(pos + step, 0, text.Length);
            bool insidePair = next > 0 && next < text.Length && char.IsLowSurrogate(text[next]) && char.IsHighSurrogate(text[next - 1]);
            if (insidePair) next += step;
            return next;
        }

        /// <summary>
        /// Word boundary next to <paramref name="pos"/>: forwards, past the rest of the current word and the spaces after
        /// it; backwards, past the spaces before the caret and the word before them.
        /// </summary>
        private int WordEdge(int pos, int direction)
        {
            int i = Mathf.Clamp(pos, 0, text.Length);
            if (direction < 0)
            {
                while (i > 0 && text[i - 1] == ' ') i--;
                while (i > 0 && text[i - 1] != ' ') i--;
            }
            else
            {
                while (i < text.Length && text[i] != ' ') i++;
                while (i < text.Length && text[i] == ' ') i++;
            }
            return i;
        }

        /// <summary>Moves the caret (restarting the blink); with <paramref name="extend"/> the selection grows instead of collapsing.</summary>
        public void MoveCaretTo(int pos, bool extend)
        {
            SetCaret(pos, extend);
            RestartBlink();
        }

        private void SetCaret(int pos, bool extend)
        {
            caret = Mathf.Clamp(pos, 0, text.Length);
            if (!extend) anchor = caret;
            if (FontReady) viewStart = Mathf.Clamp(ScrollFor(caret, Math.Min(viewStart, text.Length)), 0, text.Length);
        }

        /// <summary>
        /// Index of the leftmost visible character once the caret is at <paramref name="pos"/>, given the current one
        /// (<paramref name="start"/>). A caret strictly inside the window leaves it alone; a caret left of the window
        /// becomes its new left end; a caret on the left end or past the right end pulls the window along so that the
        /// caret ends up at its right end, showing up to one field width of the text before it.
        /// </summary>
        private int ScrollFor(int pos, int start)
        {
            if (pos < start) return pos;
            bool inside = pos > start && pos - start <= font.FitAhead(text, start, Width);
            return inside ? start : pos - font.FitBehind(text, pos, Width);
        }

        private void RestartBlink() => blinkZero = Time.unscaledTime;

        private bool BlinkOn => ((long)(Time.unscaledTime * 1000f) - (long)(blinkZero * 1000f)) / BlinkStepMs % 2 == 0;

        // ------------------------------------------------------------------ input

        private enum Command { None, SelectAll, Copy, Paste, CutSelection, EraseAhead, EraseWordAhead, Left, Right, WordLeft, WordRight, Start, End }

        private static Command CommandFor(KeyCode key, bool ctrl)
        {
            if (ctrl)
            {
                switch (key)
                {
                    case KeyCode.A: return Command.SelectAll;
                    case KeyCode.C: return Command.Copy;
                    case KeyCode.V: return Command.Paste;
                    case KeyCode.X: return Command.CutSelection;
                }
            }
            switch (key)
            {
                case KeyCode.Delete: return ctrl ? Command.EraseWordAhead : Command.EraseAhead;
                case KeyCode.LeftArrow: return ctrl ? Command.WordLeft : Command.Left;
                case KeyCode.RightArrow: return ctrl ? Command.WordRight : Command.Right;
                case KeyCode.Home: return Command.Start;
                case KeyCode.End: return Command.End;
                default: return Command.None;
            }
        }

        /// <summary>Handles editing and navigation keys; false when the key means nothing to the field.</summary>
        public bool HandleKey(KeyCode key, bool ctrl, bool shift)
        {
            if (!Shown || !focus) return false;
            var command = CommandFor(key, ctrl);
            switch (command)
            {
                case Command.None:
                    return false;
                case Command.SelectAll:
                    SetCaret(text.Length, true);
                    anchor = 0;
                    RestartBlink();
                    break;
                case Command.Copy:
                    GUIUtility.systemCopyBuffer = Selection;
                    break;
                case Command.Paste:
                    Insert(GUIUtility.systemCopyBuffer ?? "");
                    break;
                case Command.CutSelection:
                    GUIUtility.systemCopyBuffer = Selection;
                    Cut(SelLow, SelHigh);
                    break;
                case Command.EraseAhead:
                    Erase(Unit.Char, 1);
                    break;
                case Command.EraseWordAhead:
                    Erase(Unit.Word, 1);
                    break;
                case Command.Left:
                case Command.Right:
                    MoveCaretTo(CharEdge(caret, command == Command.Left ? -1 : 1), shift);
                    break;
                case Command.WordLeft:
                case Command.WordRight:
                    MoveCaretTo(WordEdge(caret, command == Command.WordLeft ? -1 : 1), shift);
                    break;
                case Command.Start:
                    MoveCaretTo(0, shift);
                    break;
                case Command.End:
                    MoveCaretTo(text.Length, shift);
                    break;
            }
            return true;
        }

        /// <summary>A typed character. Backspace arrives here (not as a key); Enter is left to the owner.</summary>
        public bool HandleChar(char c)
        {
            if (!Shown || !focus) return false;
            switch (c)
            {
                case '\b':
                    bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                    Erase(ctrl ? Unit.Word : Unit.Char, -1);
                    return true;
                case (char)127: // what Windows sends for Ctrl+Backspace
                    Erase(Unit.Word, -1);
                    return true;
                case '\n':
                case '\r':
                    return false;
            }
            if (!Allowed(c)) return false;
            Insert(c.ToString());
            return true;
        }

        // ------------------------------------------------------------------ drawing

        /// <summary>
        /// Paints the field. The characters that fit the window are measured once into <see cref="pens"/>; the text
        /// runs, the selection band, the caret mark and the completion hint are all placed from that table.
        /// </summary>
        public void Draw(Gui g)
        {
            if (!Shown || !FontReady) return;

            int from = Math.Min(viewStart, text.Length);
            string shown = text.Substring(from, font.FitAhead(text, from, Width));
            font.PrefixWidths(shown, pens);
            int slot = caret - from;                     // caret position among the shown characters
            bool onScreen = slot >= 0 && slot <= shown.Length;
            bool appending = caret >= text.Length && text.Length < Limit;

            if (onScreen) PaintSelection(g, from, shown.Length, slot);
            PaintRuns(g, shown, onScreen ? slot : shown.Length);
            if (appending && !string.IsNullOrEmpty(Ghost)) g.Text(Ghost, HintX(onScreen, slot, shown.Length), Y, HintInk, Shadowed);
            if (focus && onScreen && BlinkOn) PaintCaret(g, slot, shown.Length, appending);
        }

        /// <summary>The shown text as up to two runs split at <paramref name="split"/>, each starting at its own pen offset.</summary>
        private void PaintRuns(Gui g, string shown, int split)
        {
            if (split > 0) g.Text(shown.Substring(0, split), X + pens[0], Y, Ink, Shadowed);
            if (split < shown.Length) g.Text(shown.Substring(split), X + pens[split], Y, Ink, Shadowed);
        }

        /// <summary>Band between the caret and the selection anchor (the anchor is pinned to the shown range).</summary>
        private void PaintSelection(Gui g, int from, int count, int slot)
        {
            int other = Mathf.Clamp(anchor - from, 0, count);
            if (other != slot) g.Fill(BandEdge(other), Y - 1, BandEdge(slot), Y + 10, SelectionInk);
        }

        /// <summary>Selection boundary before shown character k: one pixel left of its pen offset, at most the field's right end.</summary>
        private int BandEdge(int k) => Math.Min(X + pens[k] - 1, X + Width);

        /// <summary>
        /// Left x of the caret mark. A bar stands on the pen offset of the character after the caret, an underscore
        /// one pixel further right; with nothing shown both sit one pixel further left.
        /// </summary>
        private int CaretMarkX(int slot, int count, bool underscore)
        {
            int x = X + pens[slot] + (underscore ? 1 : 0);
            return count > 0 ? x : x - 1;
        }

        private void PaintCaret(Gui g, int slot, int count, bool underscore)
        {
            int x = CaretMarkX(slot, count, underscore);
            if (underscore) g.Text("_", x, Y, Ink, Shadowed);
            else g.Fill(x, Y - 1, x + 1, Y + 10, Ink);
        }

        /// <summary>
        /// The completion hint begins one pixel left of where the underscore caret stands. With the caret scrolled out
        /// of view it begins one pixel left of the side the caret went to.
        /// </summary>
        private int HintX(bool onScreen, int slot, int count)
        {
            if (onScreen) return CaretMarkX(slot, count, true) - 1;
            return (slot > 0 ? X + Width : X) - 1;
        }

        /// <summary>Screen x of the boundary before character <paramref name="index"/>, honouring the scroll position.</summary>
        public int XOf(int index)
        {
            int from = Math.Min(viewStart, text.Length);
            bool measurable = FontReady && index >= from && index <= text.Length;
            return measurable ? X + font.Width(text.Substring(from, index - from)) : X;
        }
    }
}
