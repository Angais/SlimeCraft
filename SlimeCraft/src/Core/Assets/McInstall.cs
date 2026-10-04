using System;
using System.Globalization;
using System.IO;

namespace SlimeCraft.Core
{
    /// <summary>Locates the user's Minecraft Java install: .minecraft folder, client jar and asset index.</summary>
    internal sealed class McInstall
    {
        public const string PreferredVersion = "26.1.2";

        public string Root;          // .minecraft
        public string Version;       // e.g. 26.1.2
        public string JarPath;       // versions/<v>/<v>.jar
        public string AssetIndexId;  // e.g. "30"
        public string AssetIndexPath;
        public string ObjectsDir;    // assets/objects
        public string Problem;       // human readable reason when discovery failed

        public bool Valid => Problem == null;

        public static McInstall Discover(string configuredDir, string configuredVersion)
        {
            var r = new McInstall();
            try
            {
                string dir = string.IsNullOrWhiteSpace(configuredDir) ? "%APPDATA%/.minecraft" : configuredDir.Trim();
                dir = Environment.ExpandEnvironmentVariables(dir);
                if (dir.Contains("%APPDATA%")) // not expanded (unusual environments)
                    dir = dir.Replace("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
                dir = System.IO.Path.GetFullPath(dir);
                r.Root = dir;
                if (!Directory.Exists(dir)) { r.Problem = "Minecraft folder not found: " + dir; return r; }

                string versions = System.IO.Path.Combine(dir, "versions");
                if (!Directory.Exists(versions)) { r.Problem = "No 'versions' folder in " + dir; return r; }

                string v = (configuredVersion ?? "auto").Trim();
                if (v.Length == 0 || v.Equals("auto", StringComparison.OrdinalIgnoreCase))
                    v = HasJar(versions, PreferredVersion) ? PreferredVersion : NewestRelease(versions);
                if (v == null) { r.Problem = "No installed Minecraft release version (with client jar) found in " + versions; return r; }
                if (!HasJar(versions, v)) { r.Problem = "Minecraft version '" + v + "' has no client jar at " + JarOf(versions, v); return r; }

                r.Version = v;
                r.JarPath = JarOf(versions, v);

                string jsonPath = System.IO.Path.Combine(System.IO.Path.Combine(versions, v), v + ".json");
                var json = File.Exists(jsonPath) ? JsonNode.Parse(File.ReadAllText(jsonPath)) : null;
                string idx = json?["assetIndex"]["id"].AsString(null) ?? json?["assets"].AsString(null);
                if (idx == null)
                {
                    // inherited (modded) version jsons keep the asset index in the parent
                    string parent = json?["inheritsFrom"].AsString(null);
                    if (parent != null)
                    {
                        var pj = Path2Json(versions, parent);
                        idx = pj?["assetIndex"]["id"].AsString(null);
                    }
                }
                if (idx == null) { r.Problem = "Could not read assetIndex id from " + jsonPath; return r; }
                r.AssetIndexId = idx;
                r.AssetIndexPath = System.IO.Path.Combine(System.IO.Path.Combine(System.IO.Path.Combine(dir, "assets"), "indexes"), idx + ".json");
                r.ObjectsDir = System.IO.Path.Combine(System.IO.Path.Combine(dir, "assets"), "objects");
                if (!File.Exists(r.AssetIndexPath)) { r.Problem = "Asset index missing: " + r.AssetIndexPath + " (launch Minecraft " + v + " once so the launcher downloads it)"; return r; }
            }
            catch (Exception e)
            {
                r.Problem = "Minecraft discovery failed: " + e.Message;
            }
            return r;
        }

        private static string JarOf(string versions, string v) => System.IO.Path.Combine(System.IO.Path.Combine(versions, v), v + ".jar");
        private static bool HasJar(string versions, string v) => File.Exists(JarOf(versions, v));

        private static JsonNode Path2Json(string versions, string v)
        {
            string p = System.IO.Path.Combine(System.IO.Path.Combine(versions, v), v + ".json");
            return File.Exists(p) ? JsonNode.Parse(File.ReadAllText(p)) : null;
        }

        /// <summary>Newest vanilla release: version folder with jar + json of "type":"release", no inheritsFrom.</summary>
        private static string NewestRelease(string versions)
        {
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var d in Directory.GetDirectories(versions))
            {
                string v = System.IO.Path.GetFileName(d);
                string lower = v.ToLowerInvariant();
                if (lower.Contains("forge") || lower.Contains("fabric") || lower.Contains("quilt") || lower.Contains("optifine")
                    || lower.Contains("snapshot") || lower.Contains("-pre") || lower.Contains("-rc")) continue;
                if (!HasJar(versions, v)) continue;
                try
                {
                    var j = Path2Json(versions, v);
                    if (j == null || j["type"].AsString("") != "release" || j.Has("inheritsFrom")) continue;
                    DateTime t;
                    string rt = j["releaseTime"].AsString(null) ?? j["time"].AsString(null);
                    if (rt == null || !DateTime.TryParse(rt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                        t = Directory.GetLastWriteTimeUtc(d);
                    if (best == null || t > bestTime) { best = v; bestTime = t; }
                }
                catch (Exception e)
                {
                    CoreLog.Warn("Skipping Minecraft version '" + v + "': " + e.Message);
                }
            }
            return best;
        }
    }
}
