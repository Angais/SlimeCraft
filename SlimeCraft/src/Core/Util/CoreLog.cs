using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Logging helpers for Core. Per-frame code must never spam the BepInEx log, so errors coming from
    /// Update/patch bodies go through <see cref="Rate"/> which logs a given key at most once per interval
    /// (and reports how many repeats were swallowed).
    /// </summary>
    internal static class CoreLog
    {
        private sealed class Slot { public float Next; public int Suppressed; }
        private static readonly Dictionary<string, Slot> slots = new Dictionary<string, Slot>();
        private static readonly HashSet<string> once = new HashSet<string>();
        private static readonly object gate = new object();

        public static void Info(string msg) { SC.Log?.LogInfo("[Core] " + msg); }
        public static void Warn(string msg) { SC.Log?.LogWarning("[Core] " + msg); }
        public static void Error(string msg) { SC.Log?.LogError("[Core] " + msg); }
        public static void Debug(string msg) { if (CoreConfig.VerboseLog) SC.Log?.LogInfo("[Core:dbg] " + msg); }

        /// <summary>Logs an error for <paramref name="key"/> at most once every <paramref name="interval"/> seconds.</summary>
        public static void Rate(string key, Exception e, float interval = 10f) { Rate(key, e?.ToString(), interval); }

        public static void Rate(string key, string msg, float interval = 10f)
        {
            float now;
            try { now = Time.realtimeSinceStartup; } catch { now = 0f; } // not callable off the main thread
            lock (gate)
            {
                if (!slots.TryGetValue(key, out var s)) { s = new Slot(); slots[key] = s; }
                if (now < s.Next) { s.Suppressed++; return; }
                int sup = s.Suppressed;
                s.Suppressed = 0;
                s.Next = now + interval;
                SC.Log?.LogError("[Core] " + key + ": " + msg + (sup > 0 ? " (+" + sup + " similar suppressed)" : ""));
            }
        }

        /// <summary>Logs a warning only the first time this key is seen.</summary>
        public static void WarnOnce(string key, string msg)
        {
            lock (gate) { if (!once.Add(key)) return; }
            SC.Log?.LogWarning("[Core] " + msg);
        }

        public static void InfoOnce(string key, string msg)
        {
            lock (gate) { if (!once.Add(key)) return; }
            SC.Log?.LogInfo("[Core] " + msg);
        }
    }
}
