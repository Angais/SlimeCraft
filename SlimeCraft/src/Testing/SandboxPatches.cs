using System;
using System.Collections;
using System.IO;
using HarmonyLib;
using MonomiPark.SlimeRancher;
using MonomiPark.SlimeRancher.Persist;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>
    /// Harmony patches that only exist during an automated test run: every class has a Prepare() returning
    /// <see cref="TestConfig.Active"/>, so for normal players nothing in Slime Rancher is patched by this module.
    /// Counters are reported in report.json to prove the sandbox really was in effect.
    /// </summary>
    internal static class SandboxStats
    {
        public static int SavePathCalls;
        public static int OptionsOverrides;
        public static int TutorialsSuppressed;
        public static int PopupsSuppressed;
        public static int IntrosSkipped;
        public static int AchievementsBlocked;
        public static string LastSavePath;
        private static string ensuredDir;

        /// <summary>Sandbox folder (created on demand). Never falls back to the real SR folder.</summary>
        public static string Dir()
        {
            if (ensuredDir != null) return ensuredDir;
            string dir;
            try
            {
                dir = TestConfig.SandboxDir;
                Directory.CreateDirectory(dir);
            }
            catch (Exception e)
            {
                dir = Path.Combine(Path.GetTempPath(), "SlimeCraft_test_saves");
                try { Directory.CreateDirectory(dir); } catch { }
                SC.Log?.LogWarning("Sandbox folder failed (" + e.Message + "), using " + dir);
            }
            ensuredDir = dir;
            return dir;
        }
    }

    /// <summary>
    /// FileStorageProvider.SavePath() (private) is the single place where SR resolves
    /// its folder for saves (*.sav), the profile (slimerancher.prf) AND the settings (slimerancher.cfg).
    /// Redirecting it keeps the user's saves, profile and options completely untouched during a test run.
    /// </summary>
    [HarmonyPatch(typeof(FileStorageProvider), "SavePath")]
    internal static class SandboxSavePathPatch
    {
        private static bool Prepare() => TestConfig.Active;

        private static bool Prefix(ref string __result)
        {
            // Must never fall through to the original while a test is active.
            try { __result = SandboxStats.Dir(); }
            catch { __result = Path.Combine(Path.GetTempPath(), "SlimeCraft_test_saves"); }
            SandboxStats.SavePathCalls++;
            SandboxStats.LastSavePath = __result;
            return false;
        }
    }

    /// <summary>
    /// SavedProfile.PushOptions(OptionsV12) (private) applies the
    /// loaded (here: fresh sandbox) options. A fresh profile defaults to 800x600 fullscreen and LOWEST quality, so we
    /// force the test window size, windowed mode, no tutorials and a decent quality preset.
    /// </summary>
    [HarmonyPatch(typeof(SavedProfile), "PushOptions")]
    internal static class SandboxOptionsPatch
    {
        private static bool Prepare() => TestConfig.Active;

        private static void Prefix(OptionsV12 options)
        {
            try
            {
                if (options == null) return;
                options.screenWidth = Mathf.Max(800, TestConfig.Width.Value);
                options.screenHeight = Mathf.Max(600, TestConfig.Height.Value);
                options.fullScreen = false;
                options.enabledTutorials = OptionsDirector.EnabledTutorials.NONE;
                SandboxStats.OptionsOverrides++;
            }
            catch (Exception e) { SC.Log?.LogWarning("Test options override failed: " + e.Message); }
        }

        private static void Postfix()
        {
            try
            {
                int q = TestConfig.Quality.Value;
                if (q >= 0 && q <= 4) SRQualitySettings.CurrentLevel = (SRQualitySettings.Level)q;
            }
            catch (Exception e) { SC.Log?.LogWarning("Test quality override failed: " + e.Message); }
        }
    }

    /// <summary>No tutorial popups during a test (TutorialDirector.MaybeShowPopup is skipped).</summary>
    [HarmonyPatch(typeof(TutorialDirector), nameof(TutorialDirector.MaybeShowPopup))]
    internal static class SandboxTutorialPatch
    {
        private static bool Prepare() => TestConfig.Active;

        private static bool Prefix()
        {
            SandboxStats.TutorialsSuppressed++;
            return false;
        }
    }

    /// <summary>No mail/pedia/blueprint/upgrade popups during a test (PopupDirector.QueueForPopup is skipped).</summary>
    [HarmonyPatch(typeof(PopupDirector), nameof(PopupDirector.QueueForPopup))]
    internal static class SandboxPopupPatch
    {
        private static bool Prepare() => TestConfig.Active;

        private static bool Prefix()
        {
            SandboxStats.PopupsSuppressed++;
            return false;
        }
    }

    /// <summary>
    /// The new-game intro (IntroUI) is a ~13 s animated sequence that suppresses tutorials/popups and pauses
    /// SFX until it closes itself. We skip the sequence and close it two frames later: by then AutoSaveDirector has
    /// registered its Destroyer.Monitor callback, so closing through BaseUI.Close keeps every suppressor balanced.
    /// </summary>
    [HarmonyPatch(typeof(IntroUI), "AnimateIntro")]
    internal static class SandboxIntroPatch
    {
        private static bool Prepare() => TestConfig.Active;

        private static bool Prefix(IntroUI __instance)
        {
            try
            {
                __instance.StartCoroutine(CloseSoon(__instance));
                SandboxStats.IntrosSkipped++;
                return false;
            }
            catch (Exception e)
            {
                SC.Log?.LogWarning("Intro skip failed, intro plays normally: " + e.Message);
                return true;
            }
        }

        private static IEnumerator CloseSoon(IntroUI ui)
        {
            yield return null;
            yield return null;
            CloseIntro(ui);
        }

        /// <summary>
        /// Marks the intro as finished and closes it, so everything SR suppressed while it was showing is released
        /// as if the player had watched it to the end.
        /// </summary>
        public static void CloseIntro(IntroUI ui)
        {
            try
            {
                if (ui == null) return;
                Traverse.Create(ui).Field("endReached").SetValue(true);
                ui.Close();
            }
            catch (Exception e) { SC.Log?.LogWarning("Closing intro failed: " + e.Message); }
        }
    }

    /// <summary>Test games never award Steam achievements (private AchievementsDirector.AwardAchievement is skipped).</summary>
    [HarmonyPatch(typeof(AchievementsDirector), "AwardAchievement")]
    internal static class SandboxAchievementPatch
    {
        private static bool Prepare() => TestConfig.Active;

        private static bool Prefix(ref bool __result)
        {
            __result = false;
            SandboxStats.AchievementsBlocked++;
            return false;
        }
    }

    // NOTE: do NOT patch SteamDirector. Harmony runs a patched type's static constructor while patching; SteamDirector's
    // cctor dereferences GameContext.Instance, which does not exist yet at plugin load. The failed cctor is then rethrown
    // inside SceneContext.Start (AchievementsDirector.SyncAchievements) and the world never finishes loading.
}
