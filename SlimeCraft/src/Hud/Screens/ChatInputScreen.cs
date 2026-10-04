using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Full-screen text entry for the chat, matching the look and feel of Minecraft's chat: a one-line field on a dark
    /// bar at the bottom, the chat log shown in its tall scrollable form, Enter to send (lines starting with "/" run
    /// as commands), Up/Down to recall sent lines, and a completion popup for commands with Tab cycling, inline ghost
    /// text and mouse support. The game keeps running while it is open; letters go to the field, not to hotkeys.
    /// </summary>
    internal sealed class ChatInputScreen : HudScreen
    {
        private const int RowPitch = 12;
        private const int MaxRowsShown = 10;
        private const uint BarColor = 0x80000000u;
        private const uint PopupColor = 0xD0000000u;
        private const uint ChosenColor = 0xFFFFFF00u;
        private const uint OtherColor = 0xFFAAAAAAu;
        private const string Namespace = "minecraft:";

        private static readonly string[] KillTargets = { "@e", "@s" };
        private static readonly string[] GameModes = { "survival", "creative" };
        private static readonly string[] TimeVerbs = { "set", "add", "query" };
        private static readonly string[] TimeNames = { "day", "noon", "night", "midnight" };
        private static readonly string[] ScreenNames = { "inventory", "creative", "crafting", "chat", "none" };
        private static readonly string[] OnOff = { "on", "off" };

        private readonly string startText;
        private TextField field;

        // sent-line recall: positions 0..Count, where Count is the line being typed now
        private int recallIndex;
        private string draft = "";

        // command completion
        private readonly List<string> options = new List<string>();
        private int chosen;
        private int completionStart;     // index in the field where the word being completed begins
        private bool popupHidden;        // dismissed with Escape (or while browsing sent lines)
        private bool writingOption;      // the field is being changed by code, not by the player
        private int widestOption = -1;   // cached pixel width of the widest option (-1 = measure again)

        public ChatInputScreen(string initial)
        {
            startText = initial ?? "";
        }

        public override string Name => "chat";

        public override bool WantsText => true;

        // ================================================================== lifecycle

        public override void Open(HudModule hud, int viewW, int viewH)
        {
            base.Open(hud, viewW, viewH);
            recallIndex = Hud.Chat.SentCount;
            field = new TextField(hud.Font, 4, viewH - 12, viewW - 4)
            {
                Limit = 256,
                HasFocus = true,
                // attached before the starting text goes in, so a starting "/" gets completions right away
                Edited = OnPlayerEdit,
            };
            field.Replace(startText);
        }

        public override void Fit(int viewW, int viewH)
        {
            base.Fit(viewW, viewH);
            if (field == null) return;
            field.X = 4;
            field.Y = viewH - 12;
            field.Width = viewW - 4;
        }

        public override void Closed()
        {
            Hud.Chat.ScrollToNewest();
        }

        // ================================================================== drawing

        public override void Draw(Gui g, int mouseX, int mouseY, float partialTick)
        {
            g.Fill(2, ViewH - 14, ViewW - 2, ViewH - 2, BarColor);
            Hud.Chat.Draw(g, Hud.TickCount, true);
            g.LayerBreak();
            field.Draw(g);
            if (PopupShown) DrawPopup(g);
        }

        private bool PopupShown => options.Count > 0 && !popupHidden && Font != null && Font.Loaded;

        private int WidestOption()
        {
            if (widestOption < 0 && Font != null && Font.Loaded)
            {
                int widest = 0;
                foreach (string o in options) widest = Math.Max(widest, Font.Width(o));
                widestOption = widest;
            }
            return Math.Max(0, widestOption);
        }

        /// <summary>Popup rectangle: sits 3 px above the field, left edge under the word being completed.</summary>
        private void PopupBounds(out int left, out int top, out int width, out int height, out int rows)
        {
            rows = Math.Min(options.Count, MaxRowsShown);
            int textW = WidestOption();
            width = textW + 1;
            height = rows * RowPitch;
            top = ViewH - 12 - 3 - height;
            int anchor = field.XOf(completionStart);
            int rightmost = field.XOf(0) + ViewW - 4 - textW;
            anchor = Math.Max(0, Math.Min(anchor, rightmost)); // keep it from running off the right side
            left = anchor - 1;
        }

        /// <summary>First option shown: scrolls only as far as needed to keep the chosen one visible.</summary>
        private int FirstShown(int rows) => Mathf.Clamp(chosen - rows + 1, 0, Math.Max(0, options.Count - rows));

        private void DrawPopup(Gui g)
        {
            PopupBounds(out int left, out int top, out int width, out int height, out int rows);
            g.Fill(left, top, left + width, top + height, PopupColor);
            int first = FirstShown(rows);
            for (int r = 0; r < rows; r++)
            {
                int i = first + r;
                g.Text(options[i], left + 1, top + 2 + RowPitch * r, i == chosen ? ChosenColor : OtherColor, true);
            }
            if (first > 0) DottedLine(g, left, top - 1, width);
            if (first + rows < options.Count) DottedLine(g, left, top + height, width);
        }

        private static void DottedLine(Gui g, int left, int y, int width)
        {
            for (int dx = 0; dx < width; dx += 2) g.Fill(left + dx, y, left + dx + 1, y + 1, Argb.White);
        }

        private bool InsidePopup(double x, double y, out int row)
        {
            row = -1;
            PopupBounds(out int left, out int top, out int width, out int height, out int rows);
            if (x < left || x >= left + width || y < top || y >= top + height) return false;
            row = FirstShown(rows) + (int)Math.Floor((y - top) / RowPitch);
            return true;
        }

        // ================================================================== input

        public override bool Key(KeyCode key, bool ctrl, bool shift)
        {
            switch (key)
            {
                case KeyCode.Escape:
                    if (PopupShown)
                    {
                        popupHidden = true;
                        field.Ghost = null;
                    }
                    else Hud.CloseScreen();
                    return true;
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    Send();
                    return true;
                case KeyCode.Tab:
                    if (options.Count > 0) TabComplete(shift);
                    return true;
                case KeyCode.UpArrow:
                    if (PopupShown) StepChoice(-1); else Recall(-1);
                    return true;
                case KeyCode.DownArrow:
                    if (PopupShown) StepChoice(1); else Recall(1);
                    return true;
                case KeyCode.PageUp:
                    Hud.Chat.ScrollBy(Hud.Chat.RowsPerPage(true) - 1);
                    return true;
                case KeyCode.PageDown:
                    Hud.Chat.ScrollBy(-(Hud.Chat.RowsPerPage(true) - 1));
                    return true;
            }
            return field.HandleKey(key, ctrl, shift);
        }

        public override void Typed(char c)
        {
            field.HandleChar(c);
        }

        public override bool Wheel(double x, double y, double delta)
        {
            int steps = delta > 0 ? 1 : (delta < 0 ? -1 : 0);
            if (steps == 0) return true;
            if (PopupShown && InsidePopup(x, y, out _))
            {
                StepChoice(-steps); // wheel up moves toward the start of the list
                return true;
            }
            bool fine = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            Hud.Chat.ScrollBy(fine ? steps : steps * 7);
            return true;
        }

        public override bool PointerDown(double x, double y, int button, bool doubleClick)
        {
            if (button != 0 || !PopupShown) return false;
            if (!InsidePopup(x, y, out int row)) return false;
            if (row >= 0 && row < options.Count)
            {
                chosen = row;
                WriteOption(options[row]);
                RebuildOptions();
            }
            return true;
        }

        /// <summary>Sends the trimmed line: commands run through the command service, anything else is echoed.</summary>
        private void Send()
        {
            string line = field.Text.Trim();
            Hud.CloseScreen();
            if (line.Length == 0) return;
            Hud.Chat.RememberSent(line);
            if (line.StartsWith("/", StringComparison.Ordinal)) Hud.RunCommand(line);
            else Hud.AddChat("<" + HudConfig.Name + "> " + line);
        }

        /// <summary>Moves through sent lines (-1 older, +1 newer); the text being typed is kept as a draft.</summary>
        private void Recall(int step)
        {
            var chat = Hud.Chat;
            int newest = chat.SentCount;
            int target = Mathf.Clamp(recallIndex + step, 0, newest);
            if (target == recallIndex) return;
            if (recallIndex == newest) draft = field.Text;
            recallIndex = target;
            if (target == newest)
            {
                field.Replace(draft); // back to the draft: a normal edit, so completions come back
                return;
            }
            field.Replace(chat.SentAt(target));
            popupHidden = true;
            field.Ghost = null;
        }

        // ================================================================== completion

        private int Wrap(int i)
        {
            int n = options.Count;
            return n == 0 ? 0 : ((i % n) + n) % n;
        }

        private string TypedWord()
        {
            string text = field.Text;
            int start = Math.Min(completionStart, text.Length);
            return text.Substring(start);
        }

        private void StepChoice(int step)
        {
            chosen = Wrap(chosen + step);
            UpdateGhostText();
        }

        /// <summary>Tab: complete to the chosen option, or advance to the next one when it is already complete.</summary>
        private void TabComplete(bool backwards)
        {
            chosen = Wrap(chosen);
            if (string.Equals(TypedWord(), options[chosen], StringComparison.Ordinal))
                chosen = Wrap(chosen + (backwards ? -1 : 1));
            WriteOption(options[chosen]);
            popupHidden = false;
            field.Ghost = null;
        }

        /// <summary>Replaces the word being completed without triggering a rebuild of the options.</summary>
        private void WriteOption(string option)
        {
            string text = field.Text;
            int start = Math.Min(completionStart, text.Length);
            writingOption = true;
            try { field.Replace(text.Substring(0, start) + option); }
            finally { writingOption = false; }
            field.Ghost = null;
        }

        /// <summary>Grey rest of the chosen option after what was typed, when the option extends the typed word.</summary>
        private void UpdateGhostText()
        {
            string ghost = null;
            if (options.Count > 0 && !popupHidden)
            {
                string word = TypedWord();
                string option = options[Wrap(chosen)];
                if (option.Length > word.Length && option.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                    ghost = option.Substring(word.Length);
            }
            field.Ghost = ghost;
        }

        private void OnPlayerEdit(string text)
        {
            if (writingOption) return;
            popupHidden = false;
            RebuildOptions();
        }

        /// <summary>
        /// Options for the last word of a "/command" line while the caret is at the end: the command service's
        /// completions plus a small local table, de-duplicated, sorted, and filtered by the typed word.
        /// </summary>
        private void RebuildOptions()
        {
            options.Clear();
            chosen = 0;
            widestOption = -1;
            field.Ghost = null;

            string text = field.Text;
            if (text.Length == 0 || text[0] != '/' || field.Caret != text.Length) return;

            string[] words = text.Substring(1).Split(' ');
            int argIndex = words.Length - 1;
            string word = words[argIndex];
            completionStart = text.Length - word.Length;

            var gathered = new List<string>();
            var service = SC.Commands;
            if (service != null)
            {
                try
                {
                    var fromService = service.Complete(text);
                    if (fromService != null) gathered.AddRange(fromService);
                }
                catch (Exception e)
                {
                    HudModule.LogLimited("complete", "command completion failed: " + e.Message);
                }
            }
            AddLocalOptions(gathered, words, argIndex);

            var unique = new HashSet<string>(StringComparer.Ordinal);
            var sorted = new List<string>(gathered.Count);
            foreach (string s in gathered)
                if (!string.IsNullOrEmpty(s) && unique.Add(s)) sorted.Add(s);
            sorted.Sort(StringComparer.Ordinal);

            foreach (string s in sorted)
            {
                if (s.IndexOf(':') < 0 && unique.Contains(Namespace + s)) continue; // keep the namespaced form only
                if (Matches(s, word)) options.Add(s);
            }
            UpdateGhostText();
        }

        private static bool Matches(string option, string word)
        {
            if (word.Length == 0) return true;
            if (option.StartsWith(word, StringComparison.OrdinalIgnoreCase) && !string.Equals(option, word, StringComparison.Ordinal))
                return true;
            // "/give app" also offers "minecraft:apple"
            return option.StartsWith(Namespace, StringComparison.Ordinal)
                   && option.Length - Namespace.Length >= word.Length
                   && string.Compare(option, Namespace.Length, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>Backup completions with full namespaced ids for the commands players use most.</summary>
        private static void AddLocalOptions(List<string> into, string[] words, int argIndex)
        {
            if (argIndex == 0)
            {
                AddCommandNames(into);
                return;
            }
            switch (words[0].ToLowerInvariant())
            {
                case "give":
                case "clear":
                    if (argIndex == 1) foreach (var item in Content.Items) into.Add(item.Id);
                    break;
                case "summon":
                    if (argIndex == 1) into.AddRange(MobIds.All);
                    break;
                case "setblock":
                    if (argIndex == 4) foreach (var block in Content.Blocks) into.Add(block.Id);
                    break;
                case "fill":
                    if (argIndex == 7)
                    {
                        foreach (var block in Content.Blocks) into.Add(block.Id);
                        into.Add("air");
                    }
                    break;
                case "kill":
                    if (argIndex == 1) into.AddRange(KillTargets);
                    break;
                case "gamemode":
                    if (argIndex == 1) into.AddRange(GameModes);
                    break;
                case "time":
                    if (argIndex == 1) into.AddRange(TimeVerbs);
                    else if (argIndex == 2 && words[1] == "set") into.AddRange(TimeNames);
                    break;
                case "screen":
                    if (argIndex == 1) into.AddRange(ScreenNames);
                    break;
                case "hud":
                    if (argIndex == 1) into.AddRange(OnOff);
                    break;
                case "help":
                    if (argIndex == 1) AddCommandNames(into);
                    break;
            }
        }

        private static void AddCommandNames(List<string> into)
        {
            try
            {
                var names = SC.Commands?.Names;
                if (names != null) into.AddRange(names);
            }
            catch { }
        }
    }
}
