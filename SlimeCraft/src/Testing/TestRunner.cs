using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>One scenario step: id ("05"), name ("tnt_explosion"), timeout and its coroutine body.</summary>
    internal sealed class StepDef
    {
        public string Id;
        public string Name;
        public float Timeout;
        public Func<StepResult, IEnumerator> Body;
        public StepDef(string id, string name, float timeout, Func<StepResult, IEnumerator> body)
        { Id = id; Name = name; Timeout = timeout; Body = body; }
    }

    /// <summary>
    /// Orchestrates a test run: sandbox checks → main menu → new classic game → scenario steps (each with
    /// try/catch + timeout, failures never stop the run) → report.json/report.txt → quit.
    /// </summary>
    internal sealed class TestRunner
    {
        private readonly MonoBehaviour host;
        public readonly TestReport Report = new TestReport();
        public readonly LogCollector Logs = new LogCollector();
        public bool Finished { get; private set; }

        private StepResult current;
        /// <summary>Set by the boot step once the sandbox game's world is loaded (the scenario only runs then).</summary>
        private bool worldReady;
        private bool skipNextFrameSample;
        private float sampleWindowStart = -1f;
        private int sampleFrames;
        private readonly float runStart;

        public TestRunner(MonoBehaviour host)
        {
            this.host = host;
            runStart = Time.realtimeSinceStartup;
        }

        public string OutputDir => TestConfig.OutputDir;

        // ------------------------------------------------------------------ main flow
        public IEnumerator Run()
        {
            Logs.Install();
            Report.Scenario = TestConfig.Scenario.Value;
            Report.Log("SlimeCraft automated test run started (scenario '" + Report.Scenario + "')");
            try { PrepareOutputDir(); }
            catch (Exception e) { SC.Log?.LogError("Test output folder not usable: " + e); }

            if (!SandboxIsSafe(out string why))
            {
                Report.AbortReason = why;
                Report.Log("ABORT: " + why);
                Finish();
                yield break;
            }
            try { CleanSandbox(); }
            catch (Exception e) { Report.Log("Sandbox cleanup failed: " + e.Message); }

            var boot = new StepDef("00", "boot", 600f, Boot);
            yield return RunStep(boot);
            var bootResult = Report.Steps[Report.Steps.Count - 1];
            if (!worldReady || Report.AbortReason != null)
            {
                Report.AbortReason = Report.AbortReason ?? ("boot step " + bootResult.Status + ": no sandbox game to test");
            }
            else
            {
                var scenario = new Scenario(this);
                foreach (var step in scenario.Select(Report.Scenario))
                    yield return RunStep(step);
            }
            FillEnvironment();
            Finish();
        }

        /// <summary>Runs one step: drives its (nested) enumerators manually so exceptions and timeouts are caught per step.</summary>
        private IEnumerator RunStep(StepDef def)
        {
            var r = new StepResult { Id = def.Id, Name = def.Name, Timeout = def.Timeout };
            Report.Steps.Add(r);
            current = r;
            Logs.CurrentStep = r.Key;
            WriteProgress("running " + r.Key);
            Report.Log("Step " + r.Key + " started");
            float start = Time.realtimeSinceStartup;
            r.StartTime = start - runStart;
            if (def.Id != "00" && G.EnsureUnpaused()) r.Note("SR pause menu was open – resumed");

            var stack = new Stack<IEnumerator>();
            IEnumerator body = null;
            try { body = def.Body(r); }
            catch (Exception e) { r.Status = "error"; r.Error = e.ToString(); }
            if (body != null) stack.Push(body);

            while (stack.Count > 0)
            {
                if (Time.realtimeSinceStartup - start > def.Timeout)
                {
                    r.Status = "timeout";
                    r.Error = "step timed out after " + def.Timeout.ToString("0", CultureInfo.InvariantCulture) + " s";
                    break;
                }
                object yielded = null;
                bool moved;
                try
                {
                    moved = stack.Peek().MoveNext();
                    if (moved) yielded = stack.Peek().Current;
                }
                catch (Exception e)
                {
                    r.Status = "error";
                    r.Error = e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace;
                    SC.Log?.LogWarning("[Test] step " + r.Key + " threw: " + e);
                    break;
                }
                if (!moved) { stack.Pop(); continue; }
                if (yielded is IEnumerator nested) { stack.Push(nested); continue; }
                yield return yielded;
            }
            while (stack.Count > 0)
            {
                try { (stack.Pop() as IDisposable)?.Dispose(); } catch { }
            }

            r.Duration = Time.realtimeSinceStartup - start;
            r.LogErrors = Logs.CountForStep(r.Key);
            r.FinishStatus();
            current = null;
            Logs.CurrentStep = "between_steps";
            Report.Log("Step " + r.Key + " -> " + r.Status.ToUpperInvariant() + " (" + r.Duration.ToString("0.0", CultureInfo.InvariantCulture) + " s)");

            // hygiene so a failed step cannot break the next one
            if (def.Id != "00")
            {
                // never leave a simulated button held / the SR vac forced on (also after a timeout or exception)
                try { TI.Clear(); } catch { }
                try { VacDriver.ReleaseAll(); } catch { }
                try { if (SC.Hud != null && SC.Hud.ScreenOpen && SC.Commands != null) SC.Commands.Execute("/screen none"); } catch { }
                G.Heal();
            }
        }

        // ------------------------------------------------------------------ boot: main menu → new game
        private IEnumerator Boot(StepResult r)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!G.AtMainMenu)
            {
                if (G.InGame)
                {
                    r.Check("test started from the main menu", false, "a game is already loaded – refusing to test inside it");
                    Report.AbortReason = "a game was already loaded when the test started";
                    yield break;
                }
                if (Time.realtimeSinceStartup - t0 > 240f)
                {
                    r.Fail("main menu reached within 240 s");
                    yield break;
                }
                yield return G.Wait(0.5f);
            }
            r.Check("main menu reached", true, (Time.realtimeSinceStartup - runStart).ToString("0.0", CultureInfo.InvariantCulture) + " s after plugin start");

            // Prove the sandbox is active BEFORE creating anything: ask SR's own storage provider for its folder.
            var gc = SRSingleton<GameContext>.Instance;
            string srPath = null;
            try
            {
                var provider = gc.AutoSaveDirector.StorageProvider;
                r.Note("SR storage provider: " + (provider == null ? "null" : provider.GetType().Name));
                if (provider is FileStorageProvider)
                    srPath = Traverse.Create(provider).Method("SavePath").GetValue<string>();
            }
            catch (Exception e) { r.Note("could not query SR save path: " + e.Message); }
            bool redirected = srPath != null && SamePath(srPath, SandboxStats.Dir()) && !SamePath(srPath, TestConfig.RealSaveDir);
            if (!r.Check("SR save folder redirected to the sandbox", redirected, srPath ?? "unknown"))
            {
                Report.AbortReason = "save redirection not active – refusing to start a game (your saves stay untouched)";
                yield break;
            }
            r.Note("real SR save folder (never written): " + TestConfig.RealSaveDir);

            yield return G.Wait(2.5f); // menu fade-in, splash text
            yield return Shot(r, "00_main_menu");

            bool loadError = false;
            gc.AutoSaveDirector.LoadNewGame(TestConfig.TestGameName, Identifiable.Id.PINK_SLIME, PlayerState.GameMode.CLASSIC, () => loadError = true);
            Report.Log("New classic game '" + TestConfig.TestGameName + "' requested");
            t0 = Time.realtimeSinceStartup;
            yield return G.Wait(1f);
            float rawSince = -1f; // when SR itself reported the world ready (in case SC.SR.InGame never does)
            while (true)
            {
                if (loadError) { r.Fail("Slime Rancher created the new game without errors"); yield break; }
                if (G.InGameRaw)
                {
                    if (rawSince < 0f) rawSince = Time.realtimeSinceStartup;
                    if (G.InGame) break;
                    if (Time.realtimeSinceStartup - rawSince > 20f)
                    {
                        r.Check("SC.SR.InGame turns true once the world is loaded", false, "SR world ready for 20 s but SC.SR.InGame is still false");
                        break;
                    }
                }
                if (Time.realtimeSinceStartup - t0 > 300f) { r.Fail("world loaded within 300 s"); yield break; }
                yield return G.Wait(0.25f);
            }
            worldReady = true;
            r.Check("world loaded", true, (Time.realtimeSinceStartup - t0).ToString("0.0", CultureInfo.InvariantCulture) + " s");

            // New-game intro: our AnimateIntro patch closes it after 2 frames; force-close if it is still there.
            float ti = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - ti < 8f)
            {
                var intro = UnityEngine.Object.FindObjectOfType<IntroUI>();
                if (intro == null) { if (Time.realtimeSinceStartup - ti > 1.5f) break; }
                else if (Time.realtimeSinceStartup - ti > 3f) SandboxIntroPatch.CloseIntro(intro);
                yield return null;
            }
            r.Check("new-game intro closed", UnityEngine.Object.FindObjectOfType<IntroUI>() == null);
            yield return G.Wait(3f); // let SR settle (regions wake up, first slimes spawn)
            G.EnsureUnpaused();

            var sc = SRSingleton<SceneContext>.Instance;
            if (sc != null && sc.GameModel != null)
                r.Check("classic game mode", sc.GameModel.currGameMode == PlayerState.GameMode.CLASSIC, sc.GameModel.currGameMode.ToString());
            if (SC.SR == null) r.Note("SC.SR is null (Core module missing) – most steps will fail");
            else
                r.Note("save id '" + SC.SR.SaveGameId + "', zone '" + SC.SR.ZoneName + "', day " + SC.SR.DayNumber +
                       ", health " + SC.SR.Health + "/" + SC.SR.MaxHealth + ", energy " + SC.SR.Energy + "/" + SC.SR.MaxEnergy);
            try
            {
                var names = ScenicSpots.DebugDestinationNames();
                r.Note("DebugTeleportDestinations in world: " + (names.Count == 0 ? "none" : string.Join("; ", names.ToArray())));
            }
            catch (Exception e) { r.Note("listing debug teleports failed: " + e.Message); }
            FillEnvironment();
        }

        // ------------------------------------------------------------------ screenshots
        /// <summary>Waits one frame (UI layout) and the end of the next frame (everything incl. IMGUI drawn), then captures.</summary>
        public IEnumerator Shot(StepResult r, string name)
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            CaptureNow(r, name);
        }

        private void CaptureNow(StepResult r, string name)
        {
            string file = name + ".png";
            string path = Path.Combine(OutputDir, file);
            Texture2D tex = null;
            try
            {
                Directory.CreateDirectory(OutputDir);
                tex = ScreenCapture.CaptureScreenshotAsTexture();
                float lum = AverageLuminance(tex);
                byte[] png = tex.EncodeToPNG();
                File.WriteAllBytes(path, png);
                r.Screenshots.Add(file);
                r.Check("screenshot " + file, lum > 0.01f,
                    tex.width + "x" + tex.height + ", " + (png.Length / 1024) + " KB, avg luminance " + lum.ToString("0.000", CultureInfo.InvariantCulture) +
                    (lum <= 0.01f ? " (frame is black)" : ""));
            }
            catch (Exception e)
            {
                // fallback: Unity writes the file itself at the end of the frame
                try { ScreenCapture.CaptureScreenshot(path); r.Screenshots.Add(file); r.Note("screenshot via CaptureScreenshot fallback: " + e.Message); }
                catch (Exception e2) { r.Check("screenshot " + file, false, e.Message + " / " + e2.Message); }
            }
            finally
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                skipNextFrameSample = true; // PNG encoding hitch is not a game FPS sample
            }
        }

        private static float AverageLuminance(Texture2D tex)
        {
            if (tex == null || tex.width < 8 || tex.height < 8) return 0f;
            float sum = 0f;
            int n = 0;
            for (int y = 1; y < 8; y++)
                for (int x = 1; x < 8; x++)
                {
                    Color c = tex.GetPixel(tex.width * x / 8, tex.height * y / 8);
                    sum += 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                    n++;
                }
            return n > 0 ? sum / n : 0f;
        }

        // ------------------------------------------------------------------ per-frame (called from TestingModule.Update)
        public void Tick(float dt)
        {
            if (Finished) return;
            if (skipNextFrameSample) { skipNextFrameSample = false; return; }
            if (current != null)
            {
                current.SampleFrame(dt);
                if (current.Id != "00")
                {
                    Report.TotalFrames++;
                    Report.TotalFrameTime += dt;
                    if (dt > Report.MaxFrameTime) Report.MaxFrameTime = dt;
                }
            }
            float now = Time.realtimeSinceStartup;
            if (sampleWindowStart < 0f) sampleWindowStart = now;
            sampleFrames++;
            if (now - sampleWindowStart >= 1f)
            {
                if (Report.FpsSamples.Count < 900)
                    Report.FpsSamples.Add(new TestReport.FpsSample
                    {
                        T = now - runStart,
                        Fps = sampleFrames / (now - sampleWindowStart),
                        Step = current != null ? current.Key : Logs.CurrentStep
                    });
                sampleFrames = 0;
                sampleWindowStart = now;
            }
        }

        // ------------------------------------------------------------------ end of run
        /// <summary>Watchdog / interruption: finalize with whatever we have.</summary>
        public void Abort(string reason, bool quit)
        {
            if (Finished) return;
            Report.AbortReason = reason;
            Report.Log("ABORT: " + reason);
            if (current != null && current.Status == "pending")
            {
                current.Status = "timeout";
                current.Error = reason;
                current.Duration = Time.realtimeSinceStartup - runStart - current.StartTime;
            }
            FillEnvironment();
            Finish(quit);
        }

        private void Finish(bool allowQuit = true)
        {
            if (Finished) return;
            Finished = true;
            try { TI.Clear(); } catch { }
            try { VacDriver.ReleaseAll(); } catch { }
            Report.FinishedUtc = DateTime.UtcNow;
            Report.Outcome = Report.AbortReason != null ? "aborted" : (Report.AllPassed ? "passed" : "failed");
            Logs.CurrentStep = "finish";
            Report.Log(string.Format(CultureInfo.InvariantCulture, "Run finished: {0} ({1} passed, {2} failed, {3} timeout, {4} error, {5} skipped; {6} log errors)",
                Report.Outcome, Report.Count("pass"), Report.Count("fail"), Report.Count("timeout"), Report.Count("error"), Report.Count("skip"), Logs.TotalErrors));
            try
            {
                Report.Write(OutputDir, Logs);
                SC.Log?.LogInfo("[Test] report written to " + Path.Combine(OutputDir, "report.json"));
            }
            catch (Exception e) { SC.Log?.LogError("[Test] writing the report failed: " + e); }
            WriteProgress("done " + Report.Outcome);
            Logs.Dispose();

            if (allowQuit && TestConfig.QuitWhenDone.Value)
            {
                if (host != null && host.isActiveAndEnabled) host.StartCoroutine(QuitSoon());
                else Application.Quit();
            }
            else
            {
                try { SC.Hud?.ShowTitle("Test " + Report.Outcome, Report.Count("pass") + "/" + Report.Steps.Count + " steps passed", 8f); } catch { }
            }
        }

        private static IEnumerator QuitSoon()
        {
            yield return null;
            yield return new WaitForSecondsRealtime(0.5f);
            SC.Log?.LogInfo("[Test] quitting Slime Rancher (Testing.QuitWhenDone)");
            Application.Quit();
        }

        // ------------------------------------------------------------------ files
        private void PrepareOutputDir()
        {
            Directory.CreateDirectory(OutputDir);
            foreach (var f in Directory.GetFiles(OutputDir, "*.png")) TryDelete(f);
            foreach (var n in new[] { "report.json", "report.json.tmp", "report.txt", "progress.txt" }) TryDelete(Path.Combine(OutputDir, n));
        }

        private void WriteProgress(string text)
        {
            try
            {
                File.WriteAllText(Path.Combine(OutputDir, "progress.txt"),
                    DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + text + Environment.NewLine);
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        internal static bool SamePath(string a, string b)
        {
            try { return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static bool IsInside(string child, string parent)
        {
            try
            {
                string c = Norm(child) + Path.DirectorySeparatorChar, p = Norm(parent) + Path.DirectorySeparatorChar;
                return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        /// <summary>The sandbox must be a dedicated folder: neither SR's real save folder nor inside/above it.</summary>
        private static bool SandboxIsSafe(out string why)
        {
            why = null;
            string sandbox = SandboxStats.Dir(), real = TestConfig.RealSaveDir;
            if (IsInside(sandbox, real) || IsInside(real, sandbox))
            {
                why = "sandbox folder '" + sandbox + "' overlaps the real SR save folder '" + real + "'";
                return false;
            }
            return true;
        }

        /// <summary>Removes old sandbox test games and SlimeCraft per-save data of previous test games.</summary>
        private void CleanSandbox()
        {
            string sandbox = SandboxStats.Dir();
            int n = 0;
            foreach (var pattern in new[] { "*.sav", "*.tmp" })
                foreach (var f in Directory.GetFiles(sandbox, pattern)) { TryDelete(f); n++; }
            // SlimeCraft per-save data of earlier test games: <SR save name>.json = "yyyyMMddHHmmss_SlimeCraftTest[_n].json"
            // plus Core's atomic-write leftovers (.json.bak after a re-save, .json.tmp after a crash)
            string modSaves = Path.Combine(SC.DataDir ?? "", "saves");
            var testSave = new System.Text.RegularExpressions.Regex(@"^\d{14}_" + TestConfig.TestGameName + @"(_\d+)?\.json(\.bak|\.tmp)?$");
            if (!string.IsNullOrEmpty(SC.DataDir) && Directory.Exists(modSaves))
                foreach (var f in Directory.GetFiles(modSaves, "*" + TestConfig.TestGameName + "*.json"))
                    if (testSave.IsMatch(Path.GetFileName(f))) { TryDelete(f); n++; }
            Report.Log("Sandbox " + sandbox + " ready (" + n + " old test files removed)");
        }

        private void FillEnvironment()
        {
            try
            {
                var i = Report.Info;
                i["unity"] = Application.unityVersion;
                i["slimeRancher"] = Application.version;
                i["resolution"] = Screen.width + "x" + Screen.height + (Screen.fullScreen ? " fullscreen" : " windowed");
                i["quality"] = QualitySettings.names != null && QualitySettings.GetQualityLevel() < QualitySettings.names.Length
                    ? QualitySettings.names[QualitySettings.GetQualityLevel()] : QualitySettings.GetQualityLevel().ToString();
                i["gpu"] = SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsDeviceType + ")";
                i["cpu"] = SystemInfo.processorType + " x" + SystemInfo.processorCount;
                i["ramMB"] = SystemInfo.systemMemorySize.ToString(CultureInfo.InvariantCulture);
                i["os"] = SystemInfo.operatingSystem;
                i["monoHeapMB"] = (GC.GetTotalMemory(false) / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
                i["minecraft"] = SC.Assets != null ? (SC.Assets.McVersion + (SC.Assets.Ready ? " (ready)" : " (NOT ready)") + " from " + SC.Assets.MinecraftDir) : "SC.Assets missing";
                i["mcAudio"] = SC.Audio != null ? (SC.Audio.Ready ? "ready" : "not ready") : "missing";
                var missing = new List<string>();
                if (SC.Assets == null) missing.Add("Assets");
                if (SC.Audio == null) missing.Add("Audio");
                if (SC.SR == null) missing.Add("SR");
                if (SC.Input == null) missing.Add("Input");
                if (SC.Persistence == null) missing.Add("Persistence");
                if (SC.ItemVisuals == null) missing.Add("ItemVisuals");
                if (SC.Commands == null) missing.Add("Commands");
                if (SC.Inventory == null) missing.Add("Inventory");
                if (SC.Hud == null) missing.Add("Hud");
                if (SC.FirstPerson == null) missing.Add("FirstPerson");
                if (SC.Blocks == null) missing.Add("Blocks");
                if (SC.Entities == null) missing.Add("Entities");
                if (SC.Explosions == null) missing.Add("Explosions");
                i["missingServices"] = missing.Count == 0 ? "none" : string.Join(", ", missing.ToArray());
                if (SC.Commands != null)
                {
                    var names = new List<string>(SC.Commands.Names);
                    names.Sort(StringComparer.Ordinal);
                    i["commands"] = string.Join(" ", names.ToArray());
                }
                if (SC.Entities != null) i["mobIds"] = string.Join(" ", new List<string>(SC.Entities.MobIds).ToArray());
            }
            catch (Exception e) { Report.Info["environmentError"] = e.Message; }
        }
    }
}
