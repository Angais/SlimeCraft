using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>Logging helpers; errors from per-frame code are rate limited per key so the log is never spammed.</summary>
    internal static class FpLog
    {
        private static readonly Dictionary<string, float> lastLog = new Dictionary<string, float>();

        public static void Info(string msg) { SC.Log?.LogInfo("[FirstPerson] " + msg); }
        public static void Warn(string msg) { SC.Log?.LogWarning("[FirstPerson] " + msg); }

        private static bool Allow(string key, float interval)
        {
            float now = Time.realtimeSinceStartup;
            if (lastLog.TryGetValue(key, out float t) && now - t < interval) return false;
            lastLog[key] = now;
            return true;
        }

        public static void Error(string key, Exception e, float interval = 10f)
        {
            if (Allow(key, interval)) SC.Log?.LogError("[FirstPerson] " + key + " failed: " + e);
        }

        public static void WarnLimited(string key, string msg, float interval = 30f)
        {
            if (Allow(key, interval)) SC.Log?.LogWarning("[FirstPerson] " + msg);
        }
    }
}
