using System;
using BepInEx.Configuration;

namespace SlimeCraft.HudUI
{
    /// <summary>BepInEx config entries of the Hud module (section "Hud").</summary>
    internal static class HudConfig
    {
        public static ConfigEntry<int> GuiScale;
        public static ConfigEntry<bool> McHudEnabled;
        public static ConfigEntry<bool> ShowClock;
        public static ConfigEntry<bool> TitleScreenExtras;
        public static ConfigEntry<bool> HandleDropKey;
        public static ConfigEntry<bool> VacAmmoStrip;
        public static ConfigEntry<string> PlayerName;
        public static ConfigEntry<float> ChatBackgroundOpacity;
        public static ConfigEntry<bool> StarterKit;
        public static ConfigEntry<bool> CraftingTableInStarterKit;
        public static ConfigEntry<bool> VacpackIconFromSR;
        public static ConfigEntry<bool> VacpackPixelIcon;

        public static void Bind()
        {
            var c = SC.Config;
            if (c == null) return;
            try
            {
                GuiScale = c.Bind("Hud", "GuiScale", 0,
                    "Minecraft GUI scale. 0 = auto (largest integer s with 320*s <= screen width and 240*s <= screen height), like Minecraft's 'Auto'.");
                McHudEnabled = c.Bind("Hud", "McHudEnabled", true,
                    "Show the Minecraft HUD (hotbar, hearts, hunger, XP) and hide Slime Rancher's HUD. Toggle in game with the HUD key (F8).");
                ShowClock = c.Bind("Hud", "ShowClock", true,
                    "Show a Minecraft-style 'Day N  HH:MM' clock in the top-right corner (only while Slime Rancher's own day/time display is hidden, so there are never two clocks).");
                TitleScreenExtras = c.Bind("Hud", "TitleScreenExtras", true,
                    "On Slime Rancher's main menu show a yellow Minecraft-style splash text anchored to the Slime Rancher logo and the SlimeCraft version line. " +
                    "While enabled, the Hud's splash replaces Core's fixed-position splash for the session ([Core] ShowSplash is switched off in memory only).");
                HandleDropKey = c.Bind("Hud", "HandleDropKey", true,
                    "The Hud module handles the drop key (throws the selected item, Ctrl = whole stack). Disable if another module does it.");
                VacAmmoStrip = c.Bind("Hud", "VacAmmoStrip", true,
                    "When the vacpack is selected, show Slime Rancher's vac ammo slots as a Minecraft-styled strip above the hotbar.");
                PlayerName = c.Bind("Hud", "PlayerName", "Beatrix", "Name shown for your own chat messages (<name> message).");
                ChatBackgroundOpacity = c.Bind("Hud", "ChatBackgroundOpacity", 0.5f,
                    "Chat line background opacity (Minecraft 'Text Background Opacity', default 50%).");
                StarterKit = c.Bind("Hud", "StarterKit", true, "Give the Minecraft starter kit when a save has no SlimeCraft inventory yet.");
                CraftingTableInStarterKit = c.Bind("Hud", "CraftingTableInStarterKit", true,
                    "Also put a crafting table into the starter kit (handy to try the 3x3 crafting screen in survival).");
                VacpackIconFromSR = c.Bind("Hud", "VacpackIconFromSR", false,
                    "Draw the vacpack item with the Slimepedia \"Vacing\" sprite directly instead of the SC.ItemVisuals icon (which already resolves an SR vacpack sprite).");
                VacpackPixelIcon = c.Bind("Hud", "VacpackPixelIcon", true,
                    "Draw the vacpack item with a Minecraft-style 16x16 pixel-art vacpack (orange tank, glass barrel with cyan rings) instead of the SC.ItemVisuals icon. Ignored when VacpackIconFromSR is true.");
            }
            catch (Exception e)
            {
                SC.Log?.LogError("[Hud] config bind failed: " + e);
            }
        }

        public static int GuiScaleValue => GuiScale?.Value ?? 0;
        public static bool McHud { get => McHudEnabled?.Value ?? true; set { if (McHudEnabled != null) McHudEnabled.Value = value; } }
        public static bool Clock => ShowClock?.Value ?? true;
        public static bool TitleExtras => TitleScreenExtras?.Value ?? true;
        public static bool DropKey => HandleDropKey?.Value ?? true;
        public static bool AmmoStrip => VacAmmoStrip?.Value ?? true;
        public static string Name => string.IsNullOrEmpty(PlayerName?.Value) ? "Beatrix" : PlayerName.Value;
        public static float ChatBgOpacity => ChatBackgroundOpacity?.Value ?? 0.5f;
        public static bool GiveStarterKit => StarterKit?.Value ?? true;
        public static bool KitCraftingTable => CraftingTableInStarterKit?.Value ?? true;
        public static bool SRVacpackIcon => VacpackIconFromSR?.Value ?? false;
        public static bool PixelVacpackIcon => VacpackPixelIcon?.Value ?? true;
    }
}
