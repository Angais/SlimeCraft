using System;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Keeps Slime Rancher's UI in English while SlimeCraft is installed (config Core.ForceEnglish, default true),
    /// so the merged game reads consistently with Minecraft's en_us strings. Every language change in SR
    /// (startup system language, saved profile language, options dropdown) funnels through
    /// MessageDirector.SetCulture(CultureInfo, bool), so patching that one method is enough.
    /// The user's saved language preference is not modified.
    /// </summary>
    [HarmonyPatch(typeof(MessageDirector), "SetCulture", new[] { typeof(CultureInfo), typeof(bool) })]
    internal static class ForceEnglishPatch
    {
        private static ConfigEntry<bool> forceEnglish;

        private static bool Enabled
        {
            get
            {
                if (forceEnglish == null && SC.Config != null)
                    forceEnglish = SC.Config.Bind("Core", "ForceEnglish", true,
                        "Show Slime Rancher's own UI in English while SlimeCraft is installed (does not change your saved language setting).");
                return forceEnglish == null || forceEnglish.Value;
            }
        }

        private static void Prefix(ref CultureInfo culture)
        {
            try
            {
                if (Enabled) culture = MessageDirector.GetCultureInfo(MessageDirector.Lang.EN);
            }
            catch (Exception e)
            {
                SC.Log?.LogWarning("ForceEnglish patch failed: " + e.Message);
            }
        }
    }
}
