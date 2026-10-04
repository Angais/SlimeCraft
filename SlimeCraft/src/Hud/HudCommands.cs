using System;
using System.Linq;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Commands owned by the Hud module. "/screen crafting" is how other modules (Blocks: right-clicking a
    /// crafting table) and tests open Minecraft screens without referencing this module.
    /// </summary>
    internal static class HudCommands
    {
        public static void Register(HudModule hud)
        {
            var c = SC.Commands;
            if (c == null) return;
            Safe(() => c.Register("screen", "/screen <inventory|creative|crafting|chat|f3|none>", args => ScreenCmd(hud, args)));
            Safe(() => c.Register("guiscale", "/guiscale <0-8>  (0 = auto)", args => GuiScaleCmd(args)));
            Safe(() => c.Register("hud", "/hud <mc|sr|toggle>  (Minecraft HUD or Slime Rancher HUD)", args => HudCmd(hud, args)));
            Safe(() => c.Register("say", "/say <message>", args => { hud.AddChat("[" + HudConfig.Name + "] " + string.Join(" ", args)); return null; }));
            bool debugTaken = false;
            try { debugTaken = c.Names.Contains("debug"); } catch { debugTaken = false; }
            if (!debugTaken)
            {
                Safe(() => c.Register("debug", "/debug [on|off|toggle]  (F3 debug screen)", args => DebugCmd(hud, args, 0)));
                Safe(() => c.RegisterCompleter("debug", (i, a) => i == 0 ? new[] { "on", "off", "toggle" } : new string[0]));
            }
            Safe(() => c.RegisterCompleter("screen", (i, a) =>
                i == 0 ? new[] { "inventory", "survival_inventory", "creative", "crafting", "chat", "f3", "none" }
                : (i == 1 && a != null && a.Length > 0 && IsF3(a[0]) ? new[] { "on", "off", "toggle" } : new string[0])));
            Safe(() => c.RegisterCompleter("hud", (i, a) => i == 0 ? new[] { "mc", "sr", "toggle" } : new string[0]));
            Safe(() => c.RegisterCompleter("guiscale", (i, a) => i == 0 ? new[] { "0", "1", "2", "3", "4" } : new string[0]));
        }

        private static void Safe(Action a)
        {
            try { a(); } catch (Exception e) { SC.Log?.LogWarning("[Hud] command registration: " + e.Message); }
        }

        private static bool IsF3(string s)
        {
            s = (s ?? "").ToLowerInvariant();
            return s == "f3" || s == "debug";
        }

        private static string ScreenCmd(HudModule hud, string[] args)
        {
            string which = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (IsF3(which)) return DebugCmd(hud, args, 1);
            if (SC.SR == null || !SC.SR.InGame) return "§cScreens are only available in a world";
            switch (which)
            {
                case "inventory":
                    if (hud.PInv.Creative) hud.OpenScreen(new CreativePaletteScreen()); else hud.OpenScreen(new SurvivalInventoryScreen());
                    return null;
                case "survival_inventory":
                    hud.OpenScreen(new SurvivalInventoryScreen());
                    return null;
                case "creative":
                    hud.OpenScreen(new CreativePaletteScreen());
                    return null;
                case "crafting":
                case "crafting_table":
                    hud.OpenScreen(new WorkbenchScreen());
                    return null;
                case "chat":
                    hud.OpenChat(args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "");
                    return null;
                case "none":
                case "close":
                    hud.CloseScreen();
                    return null;
                default:
                    return "§cUsage: /screen <inventory|creative|crafting|chat|f3|none>";
            }
        }

        /// <summary>"/debug [on|off|toggle]" and "/screen f3 [on|off|toggle]": the F3 overlay (no argument = toggle).</summary>
        private static string DebugCmd(HudModule hud, string[] args, int argIndex)
        {
            string a = args != null && args.Length > argIndex ? args[argIndex].ToLowerInvariant() : "toggle";
            switch (a)
            {
                case "on": case "show": case "true": case "1": hud.DebugScreenVisible = true; break;
                case "off": case "hide": case "false": case "0": hud.DebugScreenVisible = false; break;
                case "toggle": hud.DebugScreenVisible = !hud.DebugScreenVisible; break;
                default: return "§cUsage: /debug [on|off|toggle]";
            }
            return hud.DebugScreenVisible ? "Debug screen shown" : "Debug screen hidden";
        }

        private static string GuiScaleCmd(string[] args)
        {
            if (args == null || args.Length == 0 || !int.TryParse(args[0], out int v) || v < 0 || v > 8)
                return "§cUsage: /guiscale <0-8>  (current: " + HudConfig.GuiScaleValue + ")";
            if (HudConfig.GuiScale != null) HudConfig.GuiScale.Value = v;
            return "GUI scale set to " + (v == 0 ? "Auto" : v.ToString());
        }

        /// <summary>"/hud mc" (also on/minecraft), "/hud sr" (also off/slimerancher/vanilla), "/hud toggle" or no argument.</summary>
        private static string HudCmd(HudModule hud, string[] args)
        {
            string a = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "toggle";
            switch (a)
            {
                case "mc": case "minecraft": case "on": hud.McHudEnabled = true; break;
                case "sr": case "slimerancher": case "slime_rancher": case "vanilla": case "off": hud.McHudEnabled = false; break;
                case "toggle": hud.McHudEnabled = !hud.McHudEnabled; break;
                default: return "§cUsage: /hud <mc|sr|toggle>";
            }
            return hud.McHudEnabled ? "Minecraft HUD enabled" : "Slime Rancher HUD enabled";
        }
    }
}
