using System;
using System.Collections.Generic;
using System.Diagnostics;
using BepInEx.Logging;
using UnityEngine;

namespace SlimeCraft.Testing
{
    /// <summary>
    /// Collects every error/exception logged during a test run:
    ///  * Unity log (Application.logMessageReceivedThreaded) – SR + Unity + uncaught exceptions with stack traces,
    ///  * BepInEx log sources other than "Unity Log" (e.g. SlimeCraft modules logging through SC.Log).
    /// Thread-safe; keeps counts for everything and details for the first <see cref="MaxUnique"/> unique messages.
    /// </summary>
    internal sealed class LogCollector : ILogListener
    {
        public const int MaxUnique = 200;

        public sealed class Entry
        {
            public string Type;
            public string Source;
            public string Message;
            public string Stack;
            public string FirstStep;
            public double FirstTime;
            public int Count;
        }

        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> unique = new Dictionary<string, Entry>();
        private readonly List<Entry> ordered = new List<Entry>();
        private readonly Dictionary<string, int> perStep = new Dictionary<string, int>();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private bool installed;

        public int Errors, Exceptions, Asserts, Warnings, ModErrors, DroppedUnique;
        /// <summary>Name of the running step (set by the runner, read from any thread).</summary>
        public volatile string CurrentStep = "startup";

        public int TotalErrors { get { lock (gate) return Errors + Exceptions + Asserts + ModErrors; } }

        public void Install()
        {
            if (installed) return;
            installed = true;
            Application.logMessageReceivedThreaded += OnUnityLog;
            try { BepInEx.Logging.Logger.Listeners.Add(this); }
            catch (Exception e) { SC.Log?.LogWarning("Could not attach BepInEx log listener: " + e.Message); }
        }

        public void Dispose()
        {
            if (!installed) return;
            installed = false;
            Application.logMessageReceivedThreaded -= OnUnityLog;
            try { BepInEx.Logging.Logger.Listeners.Remove(this); } catch { }
        }

        private void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            try
            {
                switch (type)
                {
                    case LogType.Warning:
                        lock (gate) Warnings++;
                        return;
                    case LogType.Error:
                        Record("Error", "Unity", condition, stackTrace);
                        return;
                    case LogType.Exception:
                        Record("Exception", "Unity", condition, stackTrace);
                        return;
                    case LogType.Assert:
                        Record("Assert", "Unity", condition, stackTrace);
                        return;
                }
            }
            catch { /* never throw from a log callback */ }
        }

        /// <summary>BepInEx listener: catches SC.Log.LogError & friends (Unity's own log is handled above).</summary>
        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            try
            {
                if (eventArgs == null) return;
                string src = eventArgs.Source != null ? eventArgs.Source.SourceName : "?";
                if (src == "Unity Log") return; // duplicates of Application.logMessageReceived
                if ((eventArgs.Level & (LogLevel.Error | LogLevel.Fatal)) != 0)
                    Record((eventArgs.Level & LogLevel.Fatal) != 0 ? "Fatal" : "ModError", src, Convert.ToString(eventArgs.Data), null);
                else if ((eventArgs.Level & LogLevel.Warning) != 0)
                    lock (gate) Warnings++;
            }
            catch { }
        }

        private void Record(string type, string source, string message, string stack)
        {
            message = message ?? "";
            string firstStackLine = "";
            if (!string.IsNullOrEmpty(stack))
            {
                int nl = stack.IndexOf('\n');
                firstStackLine = nl >= 0 ? stack.Substring(0, nl) : stack;
            }
            string key = type + "|" + (message.Length > 300 ? message.Substring(0, 300) : message) + "|" + firstStackLine;
            lock (gate)
            {
                switch (type)
                {
                    case "Error": Errors++; break;
                    case "Exception": Exceptions++; break;
                    case "Assert": Asserts++; break;
                    default: ModErrors++; break;
                }
                string step = CurrentStep ?? "?";
                perStep.TryGetValue(step, out int stepCount);
                perStep[step] = stepCount + 1;
                if (unique.TryGetValue(key, out var e)) { e.Count++; return; }
                if (ordered.Count >= MaxUnique) { DroppedUnique++; return; }
                e = new Entry
                {
                    Type = type,
                    Source = source,
                    Message = message.Length > 4000 ? message.Substring(0, 4000) + "..." : message,
                    Stack = stack != null && stack.Length > 6000 ? stack.Substring(0, 6000) + "..." : stack,
                    FirstStep = CurrentStep,
                    FirstTime = clock.Elapsed.TotalSeconds,
                    Count = 1
                };
                unique[key] = e;
                ordered.Add(e);
            }
        }

        /// <summary>Copy of the unique entries in order of first occurrence.</summary>
        public List<Entry> Snapshot()
        {
            lock (gate) return new List<Entry>(ordered);
        }

        /// <summary>Errors/exceptions logged while the given step was running.</summary>
        public int CountForStep(string step)
        {
            lock (gate) return perStep.TryGetValue(step ?? "?", out int n) ? n : 0;
        }
    }
}
