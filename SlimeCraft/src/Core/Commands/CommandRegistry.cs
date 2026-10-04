using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SlimeCraft.Core
{
    /// <summary>
    /// <see cref="ICommands"/>: Minecraft-style "/command arg arg" registry. Arguments are split on whitespace
    /// (double quotes group words). Feedback that has a Minecraft counterpart is read at runtime from the player's
    /// own language file through <see cref="McLang"/>; otherwise SlimeCraft's own wording is used.
    /// </summary>
    internal sealed class CommandRegistry : ICommands
    {
        private sealed class Cmd { public string Name, Usage; public Func<string[], string> Handler; }

        private readonly Dictionary<string, Cmd> commands = new Dictionary<string, Cmd>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Func<int, string[], IEnumerable<string>>> completers = new Dictionary<string, Func<int, string[], IEnumerable<string>>>(StringComparer.OrdinalIgnoreCase);

        public void Register(string name, string usage, Func<string[], string> handler)
        {
            if (string.IsNullOrEmpty(name) || handler == null) return;
            name = name.TrimStart('/');
            commands[name] = new Cmd { Name = name, Usage = usage ?? ("/" + name), Handler = handler };
        }

        /// <summary>Optional argument completion (argIndex, args so far) used by <see cref="Complete"/>.</summary>
        public void RegisterCompleter(string name, Func<int, string[], IEnumerable<string>> completer)
        {
            if (string.IsNullOrEmpty(name) || completer == null) return;
            completers[name.TrimStart('/')] = completer;
        }

        public IEnumerable<string> Names => commands.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

        public string Usage(string name)
        {
            if (name == null) return null;
            return commands.TryGetValue(name.TrimStart('/'), out var c) ? c.Usage : null;
        }

        public string Execute(string commandLine)
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return null;
            string line = commandLine.Trim();
            if (line.StartsWith("/")) line = line.Substring(1);
            var parts = Tokenize(line);
            if (parts.Count == 0) return null;
            string name = parts[0];
            int colon = name.IndexOf(':');
            if (colon >= 0 && name.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)) name = name.Substring(colon + 1);
            // feedback uses Minecraft formatting codes (§c red errors), rendered by the Hud's font
            if (!commands.TryGetValue(name, out var cmd))
            {
                string problem = McLang.Format("command.unknown.command", "That command doesn't exist. Type /help for the list.");
                string marker = McLang.Format("command.context.here", "  <- here");
                return "§c" + problem + "\n§7" + line + "§c§o" + marker;
            }
            var args = parts.Skip(1).ToArray();
            try
            {
                return cmd.Handler(args);
            }
            catch (CommandException ce)
            {
                return "§c" + ce.Message;
            }
            catch (Exception e)
            {
                CoreLog.Error("Command '/" + line + "' failed: " + e);
                return "§c" + McLang.Format("command.failed", "Something went wrong while running that command (details are in the log)");
            }
        }

        /// <summary>
        /// Tab completion helper: returns candidate completions for the last token of a partial command line
        /// (command names for the first token, registered argument completers afterwards).
        /// </summary>
        public IEnumerable<string> Complete(string partial)
        {
            partial = (partial ?? "").TrimStart('/');
            var parts = Tokenize(partial);
            bool trailingSpace = partial.EndsWith(" ");
            if (parts.Count == 0 || (parts.Count == 1 && !trailingSpace))
            {
                string pre = parts.Count == 0 ? "" : parts[0];
                return Names.Where(n => n.StartsWith(pre, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            string name = parts[0];
            if (name.StartsWith("minecraft:", StringComparison.OrdinalIgnoreCase)) name = name.Substring(10);
            if (!completers.TryGetValue(name, out var comp)) return Enumerable.Empty<string>();
            var args = parts.Skip(1).ToList();
            if (trailingSpace) args.Add("");
            string last = args[args.Count - 1];
            try
            {
                var cands = comp(args.Count - 1, args.ToArray());
                if (cands == null) return Enumerable.Empty<string>();
                return cands.Where(s => s != null && s.StartsWith(last, StringComparison.OrdinalIgnoreCase)).Take(50).ToList();
            }
            catch (Exception e)
            {
                CoreLog.Rate("completer " + name, e);
                return Enumerable.Empty<string>();
            }
        }

        private static List<string> Tokenize(string s)
        {
            var list = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false, any = false;
            foreach (char c in s)
            {
                if (c == '"') { quoted = !quoted; any = true; continue; }
                if (!quoted && char.IsWhiteSpace(c))
                {
                    if (any) { list.Add(sb.ToString()); sb.Length = 0; any = false; }
                    continue;
                }
                sb.Append(c); any = true;
            }
            if (any) list.Add(sb.ToString());
            return list;
        }
    }

    /// <summary>Thrown by command handlers for user errors (message is shown as-is).</summary>
    internal sealed class CommandException : Exception
    {
        public CommandException(string msg) : base(msg) { }
    }
}
