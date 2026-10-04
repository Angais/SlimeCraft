using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>Outcome of one scenario step (checks, notes, screenshots, timing, fps).</summary>
    internal sealed class StepResult
    {
        public struct CheckEntry { public string Name; public bool Ok; public string Detail; }

        public string Id;
        public string Name;
        /// <summary>pass | fail | skip | timeout | error</summary>
        public string Status = "pending";
        public string Error;
        public float Timeout;
        public double StartTime;
        public double Duration;
        public int Frames;
        public double FrameTimeSum;
        public double MaxFrameTime;
        public int Hitches; // frames > 50 ms
        public int LogErrors;
        public readonly List<CheckEntry> Checks = new List<CheckEntry>();
        public readonly List<string> Notes = new List<string>();
        public readonly List<string> Screenshots = new List<string>();
        private bool skipped;

        public string Key => Id + "_" + Name;

        /// <summary>Records a check; failed checks make the step fail.</summary>
        public bool Check(string name, bool ok, string detail = null)
        {
            Checks.Add(new CheckEntry { Name = name, Ok = ok, Detail = detail });
            string line = "  [" + Key + "] " + (ok ? "OK   " : "FAIL ") + name + (detail != null ? " (" + detail + ")" : "");
            if (ok) SC.Log?.LogInfo(line); else SC.Log?.LogWarning(line);
            return ok;
        }

        public void Note(string note)
        {
            if (note == null) return;
            Notes.Add(note);
            SC.Log?.LogInfo("  [" + Key + "] " + note);
        }

        /// <summary>Marks the step as skipped (prerequisite missing) – not counted as failure.</summary>
        public void Skip(string reason)
        {
            skipped = true;
            Note("SKIPPED: " + reason);
        }

        public void Fail(string reason)
        {
            Check(reason, false);
        }

        public void SampleFrame(float dt)
        {
            Frames++;
            FrameTimeSum += dt;
            if (dt > MaxFrameTime) MaxFrameTime = dt;
            if (dt > 0.05f) Hitches++;
        }

        public double AvgFps => FrameTimeSum > 0 ? Frames / FrameTimeSum : 0;
        public double MinFps => MaxFrameTime > 0 ? 1.0 / MaxFrameTime : 0;

        /// <summary>Computes the final status unless a timeout/error was already set.</summary>
        public void FinishStatus()
        {
            if (Status == "timeout" || Status == "error") return;
            bool anyFail = false;
            foreach (var c in Checks) if (!c.Ok) anyFail = true;
            if (anyFail) Status = "fail";
            else if (skipped) Status = "skip";
            else Status = "pass";
        }
    }

    /// <summary>Whole-run results and the report.json / report.txt writer.</summary>
    internal sealed class TestReport
    {
        public struct FpsSample { public double T; public double Fps; public string Step; }

        public readonly List<StepResult> Steps = new List<StepResult>();
        public readonly List<FpsSample> FpsSamples = new List<FpsSample>();
        public readonly List<string> Timeline = new List<string>();
        public readonly Dictionary<string, string> Info = new Dictionary<string, string>();
        public DateTime StartedUtc = DateTime.UtcNow;
        public DateTime FinishedUtc;
        public string Scenario;
        public string Outcome = "running";
        public string AbortReason;
        public double TotalFrameTime;
        public int TotalFrames;
        public double MaxFrameTime;

        public void Log(string line)
        {
            string l = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line;
            Timeline.Add(l);
            SC.Log?.LogInfo("[Test] " + line);
        }

        public int Count(string status)
        {
            int n = 0;
            foreach (var s in Steps) if (s.Status == status) n++;
            return n;
        }

        public bool AllPassed
        {
            get
            {
                foreach (var s in Steps) if (s.Status != "pass" && s.Status != "skip") return false;
                return Steps.Count > 0 && AbortReason == null;
            }
        }

        // ------------------------------------------------------------------ JSON
        private static JsonNode S(string s) => JsonNode.Of(s);
        private static JsonNode N(double d) => JsonNode.Of(Math.Round(d, 3));

        public JsonNode ToJson(LogCollector logs)
        {
            var root = JsonNode.NewObject();
            root["mod"] = S(SC.Name + " " + SC.Version);
            root["outcome"] = S(Outcome);
            if (AbortReason != null) root["abortReason"] = S(AbortReason);
            root["scenario"] = S(Scenario);
            root["startedUtc"] = S(StartedUtc.ToString("o", CultureInfo.InvariantCulture));
            root["finishedUtc"] = S(FinishedUtc.ToString("o", CultureInfo.InvariantCulture));
            root["durationSec"] = N((FinishedUtc - StartedUtc).TotalSeconds);

            var info = JsonNode.NewObject();
            foreach (var kv in Info) info[kv.Key] = S(kv.Value);
            root["environment"] = info;

            var sandbox = JsonNode.NewObject();
            sandbox["saveDir"] = S(SandboxStats.LastSavePath ?? TestConfig.SandboxDir);
            sandbox["savePathCalls"] = N(SandboxStats.SavePathCalls);
            sandbox["optionsOverrides"] = N(SandboxStats.OptionsOverrides);
            sandbox["tutorialsSuppressed"] = N(SandboxStats.TutorialsSuppressed);
            sandbox["popupsSuppressed"] = N(SandboxStats.PopupsSuppressed);
            sandbox["introsSkipped"] = N(SandboxStats.IntrosSkipped);
            sandbox["achievementsBlocked"] = N(SandboxStats.AchievementsBlocked);
            root["sandbox"] = sandbox;

            var summary = JsonNode.NewObject();
            summary["steps"] = N(Steps.Count);
            summary["passed"] = N(Count("pass"));
            summary["failed"] = N(Count("fail"));
            summary["timeouts"] = N(Count("timeout"));
            summary["errors"] = N(Count("error"));
            summary["skipped"] = N(Count("skip"));
            summary["allPassed"] = JsonNode.Of(AllPassed);
            summary["avgFps"] = N(TotalFrameTime > 0 ? TotalFrames / TotalFrameTime : 0);
            summary["minFps"] = N(MaxFrameTime > 0 ? 1.0 / MaxFrameTime : 0);
            if (logs != null)
            {
                summary["logErrors"] = N(logs.Errors);
                summary["logExceptions"] = N(logs.Exceptions);
                summary["logAsserts"] = N(logs.Asserts);
                summary["modErrors"] = N(logs.ModErrors);
                summary["logWarnings"] = N(logs.Warnings);
            }
            root["summary"] = summary;

            var steps = JsonNode.NewArray();
            foreach (var s in Steps)
            {
                var o = JsonNode.NewObject();
                o["id"] = S(s.Id);
                o["name"] = S(s.Name);
                o["status"] = S(s.Status);
                if (s.Error != null) o["error"] = S(s.Error);
                o["durationSec"] = N(s.Duration);
                o["timeoutSec"] = N(s.Timeout);
                o["avgFps"] = N(s.AvgFps);
                o["minFps"] = N(s.MinFps);
                o["hitches"] = N(s.Hitches);
                o["logErrors"] = N(s.LogErrors);
                var checks = JsonNode.NewArray();
                foreach (var c in s.Checks)
                {
                    var co = JsonNode.NewObject();
                    co["name"] = S(c.Name);
                    co["ok"] = JsonNode.Of(c.Ok);
                    if (c.Detail != null) co["detail"] = S(c.Detail);
                    checks.Add(co);
                }
                o["checks"] = checks;
                var notes = JsonNode.NewArray();
                foreach (var n in s.Notes) notes.Add(S(n));
                o["notes"] = notes;
                var shots = JsonNode.NewArray();
                foreach (var p in s.Screenshots) shots.Add(S(p));
                o["screenshots"] = shots;
                steps.Add(o);
            }
            root["steps"] = steps;

            var errs = JsonNode.NewObject();
            if (logs != null)
            {
                var list = logs.Snapshot();
                errs["total"] = N(logs.TotalErrors);
                errs["unique"] = N(list.Count + logs.DroppedUnique);
                errs["uniqueNotListed"] = N(logs.DroppedUnique);
                var arr = JsonNode.NewArray();
                foreach (var e in list)
                {
                    var eo = JsonNode.NewObject();
                    eo["type"] = S(e.Type);
                    eo["source"] = S(e.Source);
                    eo["count"] = N(e.Count);
                    eo["firstStep"] = S(e.FirstStep);
                    eo["firstTimeSec"] = N(e.FirstTime);
                    eo["message"] = S(e.Message);
                    if (!string.IsNullOrEmpty(e.Stack)) eo["stack"] = S(e.Stack);
                    arr.Add(eo);
                }
                errs["entries"] = arr;
            }
            root["errors"] = errs;

            var fps = JsonNode.NewArray();
            foreach (var f in FpsSamples)
            {
                var fo = JsonNode.NewObject();
                fo["t"] = N(f.T);
                fo["fps"] = N(f.Fps);
                fo["step"] = S(f.Step);
                fps.Add(fo);
            }
            root["fpsSamples"] = fps;

            var tl = JsonNode.NewArray();
            foreach (var l in Timeline) tl.Add(S(l));
            root["timeline"] = tl;
            return root;
        }

        // ------------------------------------------------------------------ text
        public string ToText(LogCollector logs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("SlimeCraft automated test report");
            sb.AppendLine("================================");
            sb.AppendLine("Outcome  : " + Outcome + (AbortReason != null ? " (" + AbortReason + ")" : ""));
            sb.AppendLine("Scenario : " + Scenario);
            sb.AppendLine("Started  : " + StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                          "   Duration: " + (FinishedUtc - StartedUtc).TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
            foreach (var kv in Info) sb.AppendLine(("  " + kv.Key).PadRight(24) + ": " + kv.Value);
            sb.AppendLine("Sandbox  : " + (SandboxStats.LastSavePath ?? TestConfig.SandboxDir) + "  (SavePath calls " + SandboxStats.SavePathCalls +
                          ", tutorials suppressed " + SandboxStats.TutorialsSuppressed + ", popups suppressed " + SandboxStats.PopupsSuppressed +
                          ", intros skipped " + SandboxStats.IntrosSkipped + ", achievements blocked " + SandboxStats.AchievementsBlocked + ")");
            sb.AppendLine();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Steps: {0} passed, {1} failed, {2} timeout, {3} error, {4} skipped (of {5})",
                Count("pass"), Count("fail"), Count("timeout"), Count("error"), Count("skip"), Steps.Count));
            if (logs != null)
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Log  : {0} errors, {1} exceptions, {2} asserts, {3} mod errors, {4} warnings",
                    logs.Errors, logs.Exceptions, logs.Asserts, logs.ModErrors, logs.Warnings));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "FPS  : avg {0:0.0}, min {1:0.0}",
                TotalFrameTime > 0 ? TotalFrames / TotalFrameTime : 0, MaxFrameTime > 0 ? 1.0 / MaxFrameTime : 0));
            sb.AppendLine();
            foreach (var s in Steps)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "[{0}] {1} {2}  ({3:0.0}s, avg {4:0} fps, min {5:0} fps, {6} log errors)",
                    s.Status.ToUpperInvariant().PadRight(7), s.Id, s.Name, s.Duration, s.AvgFps, s.MinFps, s.LogErrors));
                if (s.Error != null) sb.AppendLine("          ERROR: " + s.Error);
                foreach (var c in s.Checks)
                    sb.AppendLine("          " + (c.Ok ? "ok   " : "FAIL ") + c.Name + (c.Detail != null ? "  -- " + c.Detail : ""));
                foreach (var n in s.Notes) sb.AppendLine("          note: " + n);
                foreach (var p in s.Screenshots) sb.AppendLine("          shot: " + p);
            }
            if (logs != null)
            {
                var list = logs.Snapshot();
                sb.AppendLine();
                sb.AppendLine("Unique errors (" + list.Count + (logs.DroppedUnique > 0 ? " + " + logs.DroppedUnique + " not listed" : "") + "):");
                int i = 0;
                foreach (var e in list)
                {
                    i++;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "#{0} {1} x{2} [{3}] first in {4} @ {5:0.0}s", i, e.Type, e.Count, e.Source, e.FirstStep, e.FirstTime));
                    sb.AppendLine("    " + e.Message.Replace("\n", "\n    "));
                    if (!string.IsNullOrEmpty(e.Stack))
                    {
                        string st = e.Stack.TrimEnd();
                        // keep the text report readable: first 8 stack lines
                        var lines = st.Split('\n');
                        for (int k = 0; k < lines.Length && k < 8; k++) sb.AppendLine("      " + lines[k].TrimEnd());
                        if (lines.Length > 8) sb.AppendLine("      ... (" + (lines.Length - 8) + " more lines in report.json)");
                    }
                }
            }
            sb.AppendLine();
            sb.AppendLine("Timeline:");
            foreach (var l in Timeline) sb.AppendLine("  " + l);
            return sb.ToString();
        }

        /// <summary>Writes report.json (atomically: the deployment script polls for it) and report.txt.</summary>
        public void Write(string dir, LogCollector logs)
        {
            Directory.CreateDirectory(dir);
            string txt = ToText(logs);
            File.WriteAllText(Path.Combine(dir, "report.txt"), txt, new UTF8Encoding(false));
            string json = PrettyJson(ToJson(logs).ToString());
            string final = Path.Combine(dir, "report.json");
            string tmp = final + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(final)) File.Delete(final);
            File.Move(tmp, final);
        }

        /// <summary>Indents compact JSON (JsonNode writes it on one line).</summary>
        public static string PrettyJson(string json)
        {
            var sb = new StringBuilder(json.Length * 2);
            int indent = 0;
            bool inString = false, escape = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; sb.Append(c); break;
                    case '{':
                    case '[':
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']'))
                        {
                            sb.Append(c).Append(json[i + 1]); // empty container stays on one line
                            i++;
                            break;
                        }
                        sb.Append(c); indent++; sb.Append('\n').Append(' ', indent * 2); break;
                    case '}':
                    case ']':
                        indent--; sb.Append('\n').Append(' ', Math.Max(0, indent) * 2).Append(c); break;
                    case ',':
                        sb.Append(c).Append('\n').Append(' ', indent * 2); break;
                    case ':':
                        sb.Append(": "); break;
                    default:
                        sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
