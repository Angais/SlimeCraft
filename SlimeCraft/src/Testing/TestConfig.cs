using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>
    /// Settings and folder locations of the automated test harness.
    /// <para>
    /// The config entries are bound lazily because the Harmony patch classes of this module ask
    /// <see cref="Active"/> from their <c>Prepare()</c> method while <c>Plugin.Awake</c> applies patches, which
    /// happens before any module's <c>Init</c>. <c>SC.Config</c>, <c>SC.Log</c> and <c>SC.DataDir</c> are already
    /// available at that point.
    /// </para>
    /// </summary>
    internal static class TestConfig
    {
        public const string Section = "Testing";

        /// <summary>Name given to the throw-away game the harness creates (also used to find its leftovers).</summary>
        public const string TestGameName = "SlimeCraftTest";

        public static ConfigEntry<bool> AutoTest;
        public static ConfigEntry<string> Scenario;
        public static ConfigEntry<bool> QuitWhenDone;
        public static ConfigEntry<int> Width;
        public static ConfigEntry<int> Height;
        public static ConfigEntry<int> Quality;
        public static ConfigEntry<int> MaxMinutes;
        public static ConfigEntry<bool> KeepAutoTestEnabled;

        // Folder names Slime Rancher's data lives under (Unity company / product names of the game).
        private const string CompanyFolderName = "Monomi Park";
        private const string ProductFolderName = "Slime Rancher";

        private static bool entriesBound;

        // Session decision: computed on first access, then frozen for the lifetime of the process.
        private static bool sessionDecided;
        private static bool sessionIsTestRun;

        /// <summary>
        /// Registers all entries of the <c>[Testing]</c> section. Safe to call repeatedly; only the first call
        /// with a usable config file does any work.
        /// </summary>
        public static void Bind()
        {
            if (entriesBound) return;
            ConfigFile cfg = SC.Config;
            if (cfg == null) return; // try again on a later call

            AutoTest = cfg.Bind(Section, "AutoTest", false,
                "Run the automated SlimeCraft test on the next game start: SR saves are redirected to a sandbox folder, " +
                "a new game is started from the main menu, a scripted scenario runs and screenshots + report.json/report.txt " +
                "are written to BepInEx/SlimeCraft_test/. One-shot: reset to false as soon as the run starts " +
                "(see KeepAutoTestEnabled).");

            Scenario = cfg.Bind(Section, "Scenario", "full",
                "Which steps to run: 'full' (all), 'smoke' (01,02,03,06,09), 'interact' (01,11-19: mining, placing, " +
                "harvesting, eating, sword, slime food, vacpack, drop key), 'persist' (01,03,13,20: save/quit/reload), " +
                "'boot' (only start a game + screenshot) or a comma separated list of step ids/names, e.g. '01,03,05' " +
                "or 'tnt_explosion,screens'.");

            QuitWhenDone = cfg.Bind(Section, "QuitWhenDone", true,
                "Quit Slime Rancher when the test run has finished.");

            Width = cfg.Bind(Section, "Width", 1600,
                "Window width used during a test run (windowed).");

            Height = cfg.Bind(Section, "Height", 900,
                "Window height used during a test run (windowed).");

            Quality = cfg.Bind(Section, "Quality", 3,
                "Slime Rancher quality preset for the sandbox profile during a test run (0 lowest .. 4 very high, -1 keep).");

            MaxMinutes = cfg.Bind(Section, "MaxMinutes", 9,
                "Watchdog: a test run is aborted (report written, game quit) after this many minutes.");

            KeepAutoTestEnabled = cfg.Bind(Section, "KeepAutoTestEnabled", false,
                "If true AutoTest is NOT reset to false when a run starts (every launch runs the test).");

            entriesBound = true;
        }

        /// <summary>
        /// True when this game session is an automated test run. Decided once (from <see cref="AutoTest"/>) and
        /// then kept, so the one-shot reset of the flag at the start of a run does not switch the harness off
        /// mid-session. Never throws.
        /// </summary>
        public static bool Active
        {
            get
            {
                if (!sessionDecided) DecideSession();
                return sessionIsTestRun;
            }
        }

        private static void DecideSession()
        {
            bool result = false;
            try
            {
                Bind();
                ConfigEntry<bool> flag = AutoTest;
                result = flag != null && flag.Value;
            }
            catch (Exception e)
            {
                result = false;
                try { SC.Log?.LogError("Testing config failed: " + e); }
                catch { /* logging must never break the caller */ }
            }
            sessionIsTestRun = result;
            sessionDecided = true;
        }

        /// <summary>
        /// Folder the sandboxed Slime Rancher saves, profile and settings go to during a test run
        /// (normally <c>BepInEx/config/SlimeCraft/test_saves</c>). Not created here.
        /// </summary>
        public static string SandboxDir
        {
            get
            {
                string dataRoot = string.IsNullOrEmpty(SC.DataDir)
                    ? Path.Combine(Paths.ConfigPath, SC.Name)
                    : SC.DataDir;
                return Path.GetFullPath(Path.Combine(dataRoot, "test_saves"));
            }
        }

        /// <summary>Folder that receives screenshots, report.json/report.txt and progress.txt. Not created here.</summary>
        public static string OutputDir
        {
            get { return Path.Combine(Paths.BepInExRootPath, "SlimeCraft_test"); }
        }

        /// <summary>
        /// The folder an unmodded Slime Rancher uses for its <c>*.sav</c> files, <c>slimerancher.prf</c> profile and
        /// <c>slimerancher.cfg</c> settings — i.e. the player's real data. The harness never writes there; it only
        /// compares against it to prove that saving really goes to the sandbox and that the sandbox folder and
        /// this folder do not overlap.
        /// <para>
        /// Worked out from Unity's persistent data path on its own (not by asking the game's storage code, which
        /// is redirected during a test run). On Windows and Linux that path already is the save folder. On macOS
        /// Unity names it after the bundle identifier (<c>unity.&lt;company&gt;.&lt;product&gt;</c>), while the
        /// game keeps its data in a nested <c>&lt;company&gt;/&lt;product&gt;</c> folder beside it.
        /// </para>
        /// Returns null when the location cannot be determined; callers treat that as unsafe.
        /// </summary>
        public static string RealSaveDir
        {
            get
            {
                try
                {
                    string persistent = Application.persistentDataPath;
                    if (string.IsNullOrEmpty(persistent)) return null;

                    string trimmed = persistent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (trimmed.Length == 0) trimmed = persistent; // the path was just a root separator

                    string folder = trimmed;
                    string leafName = Path.GetFileName(trimmed);
                    string parent = Path.GetDirectoryName(trimmed);
                    if (!string.IsNullOrEmpty(parent) && string.Equals(leafName, MacBundleFolderName, StringComparison.Ordinal))
                        folder = Path.Combine(Path.Combine(parent, CompanyFolderName), ProductFolderName);

                    return Path.GetFullPath(folder);
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>Folder name Unity uses for the persistent data path on macOS (bundle identifier form).</summary>
        private static string MacBundleFolderName
        {
            get { return "unity." + CompanyFolderName + "." + ProductFolderName; }
        }
    }
}
