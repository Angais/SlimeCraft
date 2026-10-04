using BepInEx.Configuration;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>All BepInEx config entries of the Core module (section "Core").</summary>
    internal static class CoreConfig
    {
        private const string S = "Core";

        // Minecraft install
        public static ConfigEntry<string> MinecraftDir;
        public static ConfigEntry<string> MinecraftVersion;

        // Keys
        public static ConfigEntry<KeyCode> InventoryKey, ChatKey, CommandKey, DropKey, DebugKey, HudToggleKey, SneakKey;
        public static ConfigEntry<bool> SuppressSRSlotKeys;

        // Rendering
        public static ConfigEntry<int> IconSize;
        public static ConfigEntry<float> AtlasUvInsetTexels;
        public static ConfigEntry<int> PaintDepthGray;
        public static ConfigEntry<bool> VacpackIconRender, VacpackIconGlass, VacpackIconOutline;

        // Audio
        public static ConfigEntry<float> MasterVolume;
        public static ConfigEntry<int> AudioSources;

        // SR interplay
        public static ConfigEntry<float> SRDamageMultiplier;
        public static ConfigEntry<bool> WeaponsDamageSRActors;

        // Title screen
        public static ConfigEntry<bool> ShowSplash;
        public static ConfigEntry<float> SplashOffsetX, SplashOffsetY;

        public static ConfigEntry<bool> Verbose;
        public static bool VerboseLog => Verbose != null && Verbose.Value;

        public static void Bind()
        {
            var c = SC.Config;
            MinecraftDir = c.Bind(S, "MinecraftDir", "%APPDATA%/.minecraft",
                "Folder of your Minecraft Java Edition install (contains versions/ and assets/). Environment variables are expanded.");
            MinecraftVersion = c.Bind(S, "MinecraftVersion", "auto",
                "Minecraft version whose client jar is used. 'auto' = 26.1.2 if installed, else the newest installed RELEASE (modded/snapshot versions are skipped).");

            InventoryKey = c.Bind(S, "InventoryKey", KeyCode.I, "Opens the Minecraft inventory (E is Slime Rancher's interact key).");
            ChatKey = c.Bind(S, "ChatKey", KeyCode.Y, "Opens the Minecraft chat (T is Slime Rancher's gadget mode key).");
            CommandKey = c.Bind(S, "CommandKey", KeyCode.None, "Opens the chat pre-filled with '/'. None = use the chat key and type '/' (Slash opens SR's Slimepedia).");
            DropKey = c.Bind(S, "DropKey", KeyCode.Z, "Drops the selected Minecraft item (Q is Slime Rancher's pulse wave).");
            DebugKey = c.Bind(S, "DebugKey", KeyCode.F3, "Toggles the Minecraft F3 debug screen.");
            HudToggleKey = c.Bind(S, "HudToggleKey", KeyCode.F8, "Toggles Minecraft HUD <-> original Slime Rancher HUD.");
            SneakKey = c.Bind(S, "SneakKey", KeyCode.LeftControl, "Minecraft sneak: no slime-block bounce, and right-clicking a crafting table with an item places against it (Shift is Slime Rancher's sprint).");
            SuppressSRSlotKeys = c.Bind(S, "SuppressSRSlotKeys", true,
                "Disable Slime Rancher's 1-5 vac slot keys (the Minecraft hotbar uses 1-9). The mouse wheel still cycles vac slots while the vacpack is selected.");

            IconSize = c.Bind(S, "IconSize", 64, "Pixel size of the generated isometric block icons (32..256).");
            AtlasUvInsetTexels = c.Bind(S, "AtlasUvInsetTexels", 0.02f,
                "UV inset (in texels) applied to block atlas tiles. The atlas has 2px extruded padding so a tiny inset is enough; 0.5 = classic half-texel inset (makes edge pixels look thinner).");
            PaintDepthGray = c.Bind(S, "PaintDepthGray", 128,
                "Gray level (0-255) of the neutral _Depth texture given to every SR Paintlight material SlimeCraft creates (blocks, items, mobs, arm). 128 = neutral paint lighting.");
            VacpackIconRender = c.Bind(S, "VacpackIconRender", true,
                "Render the real Slime Rancher vacpack model into the vacpack hotbar icon when a world loads (cached in vacpack_icon.png). Off = SR shop sprite / pixel-art placeholder.");
            VacpackIconGlass = c.Bind(S, "VacpackIconGlass", true, "Include the vacpack's glass tube in the rendered icon.");
            VacpackIconOutline = c.Bind(S, "VacpackIconOutline", true, "Give the rendered vacpack icon a 1px dark outline (better readability on the hotbar).");

            MasterVolume = c.Bind(S, "SoundVolume", 1f, "Volume multiplier for all Minecraft sounds (also scaled by Slime Rancher's SFX volume).");
            AudioSources = c.Bind(S, "AudioSources", 24, "Number of pooled AudioSources for Minecraft sounds.");

            SRDamageMultiplier = c.Bind(S, "SRDamageMultiplier", 4f, "Minecraft weapon damage x this = damage dealt to Slime Rancher actors that have health.");
            WeaponsDamageSRActors = c.Bind(S, "WeaponsDamageSRActors", true, "Minecraft weapons damage Slime Rancher actors that have health (otherwise only knockback + agitation).");

            ShowSplash = c.Bind(S, "ShowSplash", true, "Show a Minecraft splash text on the Slime Rancher title screen.");
            SplashOffsetX = c.Bind(S, "SplashOffsetX", 123f, "Splash text X offset from the screen center, in Minecraft GUI units (default 123, where Minecraft puts it).");
            SplashOffsetY = c.Bind(S, "SplashOffsetY", 69f, "Splash text Y offset from the top, in Minecraft GUI units (default 69, where Minecraft puts it).");

            Verbose = c.Bind(S, "VerboseLog", false, "Extra diagnostic logging from the Core module.");
        }
    }
}
