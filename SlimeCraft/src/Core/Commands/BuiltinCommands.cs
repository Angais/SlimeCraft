using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Built-in commands modelled on Minecraft's. Feedback that has a Minecraft counterpart is read at runtime from
    /// the player's own language file (<see cref="McLang"/>); everything else, and every fallback, is our own wording.
    /// </summary>
    internal static class BuiltinCommands
    {
        private const string PlayerName = "Beatrix";
        private const int FillLimit = 32768;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static void Register(CommandRegistry r, SRBridge sr)
        {
            r.Register("help", "/help [command]", a => Help(r, a));
            r.RegisterCompleter("help", (i, a) => i == 0 ? r.Names : Enumerable.Empty<string>());
            r.Register("give", "/give <item> [count]", Give);
            r.RegisterCompleter("give", (i, a) => i == 0 ? ItemIds() : Enumerable.Empty<string>());
            r.Register("gamemode", "/gamemode <survival|creative>", GameMode);
            r.RegisterCompleter("gamemode", (i, a) => i == 0 ? new[] { "survival", "creative" } : Enumerable.Empty<string>());
            r.Register("tp", "/tp <x> <y> <z>", a => Teleport(sr, a));
            r.Register("teleport", "/teleport <x> <y> <z>", a => Teleport(sr, a));
            r.Register("time", "/time <set|add|query> <day|noon|night|midnight|ticks|daytime|day>", a => Time(sr, a));
            r.RegisterCompleter("time", (i, a) => i == 0 ? new[] { "set", "add", "query" }
                : i == 1 && a[0] == "set" ? new[] { "day", "noon", "night", "midnight" }
                : i == 1 && a[0] == "query" ? new[] { "daytime", "day", "gametime" } : new string[0]);
            r.Register("summon", "/summon <entity> [x y z]", a => Summon(sr, a));
            r.RegisterCompleter("summon", (i, a) => i == 0 ? SummonIds(sr) : Enumerable.Empty<string>());
            r.Register("kill", "/kill [@e|@s]", Kill);
            r.RegisterCompleter("kill", (i, a) => i == 0 ? new[] { "@e", "@s" } : new string[0]);
            r.Register("clear", "/clear [item]", Clear);
            r.RegisterCompleter("clear", (i, a) => i == 0 ? ItemIds() : Enumerable.Empty<string>());
            r.Register("setblock", "/setblock <x> <y> <z> <block>", a => SetBlock(sr, a));
            r.RegisterCompleter("setblock", (i, a) => i == 3 ? BlockIds() : new[] { "~" });
            r.Register("fill", "/fill <x1> <y1> <z1> <x2> <y2> <z2> <block|air>", a => Fill(sr, a));
            r.RegisterCompleter("fill", (i, a) => i == 6 ? BlockIds() : new[] { "~" });
            r.Register("heal", "/heal [amount]", a => Heal(sr, a));
            r.Register("newbucks", "/newbucks <amount>", a => Newbucks(sr, a));
            r.Register("seed", "/seed", a => Seed(sr));
        }

        // ------------------------------------------------------------------ helpers
        private static IEnumerable<string> ItemIds() => Content.Items.Select(i => i.Id.StartsWith("minecraft:") ? i.Id.Substring(10) : i.Id).OrderBy(s => s);
        private static IEnumerable<string> BlockIds() => new[] { "air" }.Concat(Content.Blocks.Select(b => b.Id.Substring(b.Id.IndexOf(':') + 1)).OrderBy(s => s));

        private static IEnumerable<string> SummonIds(SRBridge sr)
        {
            var mobs = SC.Entities != null ? SC.Entities.MobIds : MobIds.All;
            foreach (var m in mobs) yield return m.Substring(m.IndexOf(':') + 1);
            foreach (var id in sr.SpawnableIds()) yield return "slimerancher:" + id;
        }

        private static string Name(string id) => Content.DisplayName(Content.Norm(id));

        private static int ParseInt(string s, string what, int min, int max)
        {
            if (!int.TryParse(s, NumberStyles.Integer, Inv, out int v))
                throw new CommandException(McLang.Format("parsing.int.invalid", "'{0}' is not a whole number", s));
            if (v < min) throw new CommandException(McLang.Format("argument.integer.low", "{1} is too small (the minimum is {0})", min, v));
            if (v > max) throw new CommandException(McLang.Format("argument.integer.big", "{1} is too large (the maximum is {0})", max, v));
            return v;
        }

        private static double ParseDouble(string s)
        {
            if (!double.TryParse(s, NumberStyles.Float, Inv, out double v)) throw new CommandException("'" + s + "' is not a valid coordinate");
            return v;
        }

        /// <summary>Parses 3 coordinates (absolute, ~relative to the feet, or ^local left/up/forward).</summary>
        private static Vector3 ParsePos(SRBridge sr, string[] a, int i)
        {
            if (a.Length < i + 3) throw new CommandException(McLang.Format("argument.pos3d.incomplete", "Three coordinates are needed (x y z)"));
            var feet = sr.PlayerFeet;
            if (a[i].StartsWith("^") || a[i + 1].StartsWith("^") || a[i + 2].StartsWith("^"))
            {
                if (!(a[i].StartsWith("^") && a[i + 1].StartsWith("^") && a[i + 2].StartsWith("^")))
                    throw new CommandException(McLang.Format("argument.pos.mixed", "Use ^ on all three coordinates or on none of them"));
                var fwd = sr.LookDirection; var up = Vector3.up;
                var left = Vector3.Cross(fwd, up).normalized; // Unity: Cross(forward, up) points left
                var upLocal = Vector3.Cross(left, fwd).normalized;
                float l = Local(a[i]), u = Local(a[i + 1]), f = Local(a[i + 2]);
                return sr.EyePosition + left * l + upLocal * u + fwd * f;
            }
            return new Vector3(Coord(a[i], feet.x), Coord(a[i + 1], feet.y), Coord(a[i + 2], feet.z));
        }

        private static float Local(string s) => s.Length == 1 ? 0f : (float)ParseDouble(s.Substring(1));

        private static float Coord(string s, float rel)
        {
            if (s.StartsWith("~")) return rel + (s.Length == 1 ? 0f : (float)ParseDouble(s.Substring(1)));
            return (float)ParseDouble(s);
        }

        private static Vector3Int ParseBlockPos(SRBridge sr, string[] a, int i)
        {
            var p = ParsePos(sr, a, i);
            return new Vector3Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), Mathf.FloorToInt(p.z));
        }

        private static void RequireWorld(SRBridge sr) { if (!sr.InGame) throw new CommandException("You must be in a Slime Rancher world to use this command"); }

        // ------------------------------------------------------------------ commands
        private static string Help(CommandRegistry r, string[] a)
        {
            if (a.Length > 0)
            {
                var u = r.Usage(a[0]);
                return u ?? McLang.Format("commands.help.failed", "No command called '{0}' (type /help for the list)", a[0]);
            }
            var sb = new StringBuilder();
            foreach (var n in r.Names)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(r.Usage(n));
            }
            return sb.ToString();
        }

        private static string Give(string[] a)
        {
            var inv = SC.Inventory;
            if (inv == null) throw new CommandException("The Minecraft inventory is not available");
            int i = 0;
            // a leading target selector or player name is accepted and ignored ("/give @p tnt")
            if (a.Length > 1 && (a[0].StartsWith("@") || a[0].Equals(PlayerName, StringComparison.OrdinalIgnoreCase))) i = 1;
            if (a.Length <= i) throw new CommandException("Incomplete command: /give <item> [count]");
            string raw = a[i];
            string id = Content.Norm(raw.ToLowerInvariant());
            var def = Content.Item(id);
            if (def == null) throw new CommandException("Unknown item '" + id + "'");
            int max = def.MaxStack * 100;
            int count = a.Length > i + 1 ? ParseInt(a[i + 1], "count", 1, int.MaxValue) : 1;
            if (count > max) throw new CommandException(McLang.Format("commands.give.failed.toomanyitems", "At most {0} of {1} can be given at once", max, "[" + Name(id) + "]"));
            int left = count;
            int dropped = 0;
            while (left > 0)
            {
                int n = Math.Min(left, def.MaxStack);
                int rest = inv.Give(new ItemStack(id, n));
                if (rest > 0)
                {
                    dropped += rest;
                    var ent = SC.Entities;
                    if (ent != null && SC.SR != null && SC.SR.InGame)
                        ent.SpawnItem(new ItemStack(id, rest), SC.SR.EyePosition + SC.SR.LookDirection * 0.3f - Vector3.up * 0.3f, SC.SR.LookDirection * 2f, 2f);
                }
                left -= n;
            }
            SC.Audio?.Play2D("entity.item.pickup", 0.2f, 1.4f + UnityEngine.Random.Range(-0.2f, 0.2f));
            return McLang.Format("commands.give.success.single", "Gave {0} x {1} to {2}", count, "[" + Name(id) + "]", PlayerName)
                + (dropped > 0 ? " (" + dropped + " dropped, inventory full)" : "");
        }

        private static string GameMode(string[] a)
        {
            var inv = SC.Inventory;
            if (inv == null) throw new CommandException("The Minecraft inventory is not available");
            if (a.Length == 0) throw new CommandException("Incomplete command: /gamemode <survival|creative>");
            bool creative;
            switch (a[0].ToLowerInvariant())
            {
                case "creative": case "c": case "1": creative = true; break;
                case "survival": case "s": case "0": creative = false; break;
                case "adventure": case "spectator": case "a": case "sp": case "2": case "3":
                    throw new CommandException("Only survival and creative exist in Slime Rancher (the slimes would be sad)");
                default: throw new CommandException("Unknown game mode: " + a[0]);
            }
            inv.Creative = creative;
            string mode = creative ? McLang.Format("gameMode.creative", "Creative") : McLang.Format("gameMode.survival", "Survival");
            return McLang.Format("commands.gamemode.success.self", "Game mode changed to {0}", mode);
        }

        private static string Teleport(SRBridge sr, string[] a)
        {
            RequireWorld(sr);
            int i = a.Length == 4 ? 1 : 0; // "/tp @s x y z"
            var p = ParsePos(sr, a, i);
            sr.TeleportPlayer(p);
            return McLang.Format("commands.teleport.success.location.single", "Moved {0} to {1}, {2}, {3}",
                PlayerName, p.x.ToString("F6", Inv), p.y.ToString("F6", Inv), p.z.ToString("F6", Inv));
        }

        /// <summary>Minecraft day ticks (0 = 6:00) for a SR hour.</summary>
        private static int TicksOf(float hour) => (int)(((hour - 6f + 24f) % 24f) * 1000f);

        private static string Time(SRBridge sr, string[] a)
        {
            RequireWorld(sr);
            if (a.Length == 0) throw new CommandException("Incomplete command: /time <set|add|query>");
            string sub = a[0].ToLowerInvariant();
            if (sub == "query")
            {
                string what = a.Length > 1 ? a[1].ToLowerInvariant() : "daytime";
                int value;
                if (what == "day") value = sr.DayNumber - 1;
                else if (what == "gametime") value = (int)((sr.DayNumber - 1) * 24000 + TicksOf(sr.DayFraction * 24f));
                else value = TicksOf(sr.DayFraction * 24f);
                return McLang.Format("commands.time.query", "Current time: {0}", value);
            }
            if (a.Length < 2) throw new CommandException("Incomplete command: /time " + sub + " <value>");
            float hour;
            if (sub == "set")
            {
                switch (a[1].ToLowerInvariant())
                {
                    case "day": hour = 7f; break;        // 1000 ticks
                    case "noon": hour = 12f; break;      // 6000
                    case "night": hour = 19f; break;     // 13000
                    case "midnight": hour = 0f; break;   // 18000
                    default: hour = (6f + ParseTicks(a[1]) / 1000f) % 24f; break;
                }
            }
            else if (sub == "add")
            {
                float add = ParseTicks(a[1]) / 1000f;
                if (add <= 0f) throw new CommandException("Slime Rancher time can only move forward");
                hour = (sr.DayFraction * 24f + add) % 24f;
            }
            else throw new CommandException("Unknown /time action '" + sub + "'");
            if (!sr.FastForwardToHour(hour, out double skipped)) throw new CommandException("Time is not available");
            int ticks = TicksOf(hour);
            return McLang.Format("commands.time.set", "Time changed to {0}", ticks)
                + " (fast-forwarding " + (skipped / 3600.0).ToString("0.#", Inv) + " Slime Rancher hours)";
        }

        private static float ParseTicks(string s)
        {
            s = s.ToLowerInvariant();
            float mul = 1f;
            if (s.EndsWith("d")) { mul = 24000f; s = s.Substring(0, s.Length - 1); }
            else if (s.EndsWith("s")) { mul = 20f; s = s.Substring(0, s.Length - 1); }
            else if (s.EndsWith("t")) s = s.Substring(0, s.Length - 1);
            if (!float.TryParse(s, NumberStyles.Float, Inv, out float v)) throw new CommandException("Invalid time '" + s + "'");
            return v * mul;
        }

        private static string Summon(SRBridge sr, string[] a)
        {
            RequireWorld(sr);
            if (a.Length == 0) throw new CommandException("Incomplete command: /summon <entity> [x y z]");
            string raw = a[0].ToLowerInvariant();
            Vector3 pos;
            if (a.Length >= 4) pos = ParsePos(sr, a, 1);
            else
            {
                var f = sr.LookDirection; f.y = 0f;
                if (f.sqrMagnitude < 0.01f) f = Vector3.forward;
                pos = sr.PlayerFeet + f.normalized * 2.5f + Vector3.up * 0.2f;
            }

            string srId = null;
            if (raw.StartsWith("slimerancher:")) srId = raw.Substring(13);
            else
            {
                string mob = Content.Norm(raw);
                var ent = SC.Entities;
                var known = ent != null ? ent.MobIds : MobIds.All;
                if (known.Contains(mob))
                {
                    if (ent == null) throw new CommandException("Minecraft mobs are not available");
                    var go = ent.SpawnMob(mob, pos);
                    if (go == null) throw new CommandException(McLang.Format("commands.summon.failed", "Could not spawn that mob"));
                    return McLang.Format("commands.summon.success", "Spawned {0}", EntityName(mob));
                }
                if (!raw.Contains(":")) srId = raw; // "/summon pink_slime" also works
            }
            if (srId == null) throw new CommandException("Unknown mob or actor '" + raw + "'");
            var actor = sr.SpawnSRActor(srId.ToUpperInvariant(), pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            if (actor == null) throw new CommandException("Can't find Slime Rancher actor '" + srId + "'");
            return McLang.Format("commands.summon.success", "Spawned {0}", Pretty(srId));
        }

        /// <summary>"minecraft:iron_golem" → "Iron Golem" (lang key entity.minecraft.*).</summary>
        private static string EntityName(string mob)
        {
            string path = mob.Substring(mob.IndexOf(':') + 1);
            string key = "entity.minecraft." + path;
            var t = SC.Assets != null ? SC.Assets.Translate(key) : key;
            return t != key ? t : Pretty(path);
        }

        private static string Pretty(string id) => string.Join(" ", id.ToLowerInvariant().Split('_').Select(w => w.Length == 0 ? w : char.ToUpper(w[0]) + w.Substring(1)));

        private static string Kill(string[] a)
        {
            string target = a.Length > 0 ? a[0].ToLowerInvariant() : "@e";
            if (target == "@s" || target == "@p" || target == "me" || target == PlayerName.ToLowerInvariant())
            {
                if (SC.SR == null || !SC.SR.InGame) throw new CommandException(McLang.Format("argument.entity.notfound.player", "There is no player to target right now"));
                SC.SR.DamagePlayer(Math.Max(1000, SC.SR.MaxHealth * 10), null);
                return McLang.Format("commands.kill.success.single", "{0} was knocked out", PlayerName);
            }
            var ent = SC.Entities;
            if (ent == null) throw new CommandException(McLang.Format("argument.entity.notfound.entity", "There are no entities to target"));
            int n = ent.LiveMobCount;
            ent.ClearAll();
            return n == 1 ? "Killed 1 entity" : "Killed " + n + " entities (and every dropped item, arrow and TNT)";
        }

        private static string Clear(string[] a)
        {
            var inv = SC.Inventory;
            if (inv == null) throw new CommandException("The Minecraft inventory is not available");
            string only = null;
            foreach (var s in a) if (!s.StartsWith("@") && !s.Equals(PlayerName, StringComparison.OrdinalIgnoreCase)) { only = Content.Norm(s.ToLowerInvariant()); break; }
            int removed = 0;
            var invModel = inv.Inv;
            for (int i = 0; i < invModel.Size; i++)
            {
                var st = invModel.Get(i);
                if (st.IsEmpty || st.Id == Content.Vacpack) continue; // never lose the Slime Rancher vacpack
                if (only != null && st.Id != only) continue;
                removed += st.Count;
                invModel.Set(i, ItemStack.Empty);
            }
            if (removed == 0) throw new CommandException(McLang.Format("clear.failed.single", "{0} has no items to clear", PlayerName));
            return McLang.Format("commands.clear.success.single", "Cleared {0} item(s) from {1}", removed, PlayerName);
        }

        private static string BlockArg(string s)
        {
            string id = s.ToLowerInvariant();
            if (id == "air" || id == "minecraft:air") return null;
            id = Content.Norm(id);
            if (Content.Block(id) == null) throw new CommandException("Unknown block type '" + id + "'");
            return id;
        }

        private static string SetBlock(SRBridge sr, string[] a)
        {
            RequireWorld(sr);
            var blocks = SC.Blocks;
            if (blocks == null) throw new CommandException("Minecraft blocks are not available");
            if (a.Length < 4) throw new CommandException("Incomplete command: /setblock <x> <y> <z> <block>");
            var p = ParseBlockPos(sr, a, 0);
            string id = BlockArg(a[3]);
            string notPlaced = McLang.Format("commands.setblock.failed", "The block could not be placed there");
            if (blocks.GetBlock(p) == id) throw new CommandException(notPlaced);
            if (!blocks.SetBlock(p, id, BlockFacing.North, false)) throw new CommandException(notPlaced);
            return McLang.Format("commands.setblock.success", "Placed the block at {0}, {1}, {2}", p.x, p.y, p.z);
        }

        private static string Fill(SRBridge sr, string[] a)
        {
            RequireWorld(sr);
            var blocks = SC.Blocks;
            if (blocks == null) throw new CommandException("Minecraft blocks are not available");
            if (a.Length < 7) throw new CommandException("Incomplete command: /fill <x1> <y1> <z1> <x2> <y2> <z2> <block>");
            var p1 = ParseBlockPos(sr, a, 0);
            var p2 = ParseBlockPos(sr, a, 3);
            string id = BlockArg(a[6]);
            var min = Vector3Int.Min(p1, p2);
            var max = Vector3Int.Max(p1, p2);
            long volume = (long)(max.x - min.x + 1) * (max.y - min.y + 1) * (max.z - min.z + 1);
            if (volume > FillLimit) throw new CommandException(McLang.Format("commands.fill.toobig", "That area holds {1} blocks; the limit is {0}", FillLimit, volume));
            int changed = 0;
            for (int y = min.y; y <= max.y; y++)
                for (int z = min.z; z <= max.z; z++)
                    for (int x = min.x; x <= max.x; x++)
                    {
                        var p = new Vector3Int(x, y, z);
                        if (blocks.GetBlock(p) == id) continue;
                        if (blocks.SetBlock(p, id, BlockFacing.North, false)) changed++;
                    }
            if (changed == 0) throw new CommandException(McLang.Format("commands.fill.failed", "Nothing changed: every block already matched"));
            return McLang.Format("commands.fill.success", "Filled {0} block(s)", changed);
        }

        private static string Heal(SRBridge sr, string[] a)
        {
            RequireWorld(sr);
            int amount = a.Length > 0 ? ParseInt(a[0], "amount", 1, 100000) : Math.Max(1, sr.MaxHealth - sr.Health);
            sr.HealPlayer(amount);
            if (a.Length == 0) sr.AddEnergy(Math.Max(0, sr.MaxEnergy - sr.Energy));
            return "Healed " + PlayerName + " (" + sr.Health + "/" + sr.MaxHealth + " health, " + sr.Energy + "/" + sr.MaxEnergy + " energy)";
        }

        private static string Newbucks(SRBridge sr, string[] a)
        {
            RequireWorld(sr);
            if (a.Length == 0) return PlayerName + " has " + sr.Newbucks + " newbucks";
            int amount = ParseInt(a[0], "amount", -10000000, 10000000);
            sr.AddNewbucks(amount);
            return (amount >= 0 ? "Gave " + amount + " newbucks to " : "Took " + (-amount) + " newbucks from ") + PlayerName + " (now " + sr.Newbucks + ")";
        }

        private static string Seed(SRBridge sr)
        {
            // Slime Rancher has no world seed, so derive a stable "seed" from the save and the Far, Far Range.
            string basis = (sr.SaveGameId ?? "the far, far range") + "|plort";
            long h = 1125899906842597L;
            foreach (char c in basis) h = 31 * h + c;
            string[] quips =
            {
                "(grown from a carrot patch)", "(contains 0% Tarr)", "(certified by Ogden Ortiz)", "(the Largos approve)",
                "(do not feed to a Gold Slime)", "(now with extra plorts)", "(Mochi says it's fine)"
            };
            return "Seed: [" + h + "] " + quips[(int)((h & 0x7FFFFFFF) % quips.Length)];
        }
    }
}
