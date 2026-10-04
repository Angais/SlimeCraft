using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SlimeCraft.Core
{
    /// <summary>
    /// <see cref="IPersistence"/>: one JSON file per SR game (SC.DataDir/saves/&lt;SaveGameId&gt;.json) holding every
    /// registered section as a string. Loaded right after WorldLoaded (sections get null for a new game), saved
    /// whenever SR saves, before WorldUnloading and on application quit. Writes are atomic (temp file + replace,
    /// previous version kept as .bak). Sections of modules that are not loaded are preserved untouched.
    /// </summary>
    internal sealed class SavePersistence : IPersistence
    {
        private sealed class Section { public Func<string> Save; public Action<string> Load; }

        private readonly SRBridge bridge;
        private readonly Dictionary<string, Section> sections = new Dictionary<string, Section>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> stored = new Dictionary<string, string>(StringComparer.Ordinal);
        private string loadedFor;     // save id whose data is in 'stored'
        private bool loadPhaseDone;

        public SavePersistence(SRBridge bridge)
        {
            this.bridge = bridge;
            bridge.AfterWorldLoaded += LoadAll;
            bridge.BeforeWorldUnloading += () => { SaveNow(); loadedFor = null; loadPhaseDone = false; stored.Clear(); };
            bridge.AfterSaving += SaveNow;
        }

        public void Register(string key, Func<string> save, Action<string> load)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("key");
            sections[key] = new Section { Save = save, Load = load };
            if (loadPhaseDone && load != null) // late registration: give it its data right away
            {
                stored.TryGetValue(key, out var payload);
                SC.Safe(() => load(payload), "Persistence load '" + key + "'");
            }
        }

        private static string Sanitize(string id)
        {
            var sb = new StringBuilder(id.Length);
            foreach (char c in id) sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
            return sb.ToString();
        }

        private static string FileFor(string id) => Path.Combine(Path.Combine(SC.DataDir, "saves"), Sanitize(id) + ".json");

        private void LoadAll()
        {
            stored.Clear();
            loadPhaseDone = false;
            string id = bridge.SaveGameId;
            loadedFor = id;
            if (id != null)
            {
                string path = FileFor(id);
                try
                {
                    string text = null;
                    if (File.Exists(path)) text = File.ReadAllText(path, Encoding.UTF8);
                    else if (File.Exists(path + ".bak")) { text = File.ReadAllText(path + ".bak", Encoding.UTF8); CoreLog.Warn("Restored SlimeCraft data from backup " + path + ".bak"); }
                    if (text != null)
                    {
                        var root = JsonNode.Parse(text);
                        if (root == null && File.Exists(path + ".bak")) root = JsonNode.Parse(File.ReadAllText(path + ".bak", Encoding.UTF8));
                        if (root != null)
                            foreach (var kv in root["sections"].Members)
                                if (kv.Value.IsString) stored[kv.Key] = kv.Value.AsString();
                        CoreLog.Info("Loaded SlimeCraft data for '" + id + "' (" + stored.Count + " sections)");
                    }
                    else CoreLog.Info("No SlimeCraft data yet for '" + id + "' (new game or first time with SlimeCraft)");
                }
                catch (Exception e) { CoreLog.Error("Could not read " + path + ": " + e.Message); }
            }
            else CoreLog.Warn("No SR save id available - SlimeCraft data will not be persisted for this session");

            foreach (var kv in sections)
            {
                if (kv.Value.Load == null) continue;
                stored.TryGetValue(kv.Key, out var payload);
                var key = kv.Key; var load = kv.Value.Load;
                SC.Safe(() => load(payload), "Persistence load '" + key + "'");
            }
            loadPhaseDone = true;
        }

        public void SaveNow()
        {
            string id = bridge.SaveGameId;
            if (id == null || !loadPhaseDone || id != loadedFor) return;
            foreach (var kv in sections)
            {
                if (kv.Value.Save == null) continue;
                try { stored[kv.Key] = kv.Value.Save(); }
                catch (Exception e) { CoreLog.Error("Persistence save '" + kv.Key + "' failed: " + e); }
            }
            var root = JsonNode.NewObject();
            root["format"] = JsonNode.Of(1);
            root["mod"] = JsonNode.Of(SC.Name + " " + SC.Version);
            root["saveGameId"] = JsonNode.Of(id);
            root["savedAt"] = JsonNode.Of(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            var secs = JsonNode.NewObject();
            foreach (var kv in stored) if (kv.Value != null) secs[kv.Key] = JsonNode.Of(kv.Value);
            root["sections"] = secs;
            WriteAtomic(FileFor(id), root.ToString());
        }

        private static void WriteAtomic(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try { File.Replace(tmp, path, path + ".bak", true); }
                    catch (Exception)
                    {
                        // File.Replace is not supported on every filesystem: fall back to copy-to-bak + move
                        File.Copy(path, path + ".bak", true);
                        File.Delete(path);
                        File.Move(tmp, path);
                    }
                }
                else File.Move(tmp, path);
                CoreLog.Debug("Saved SlimeCraft data " + path);
            }
            catch (Exception e) { CoreLog.Error("Could not write " + path + ": " + e.Message); }
        }

        /// <summary>Application quit: last chance (SR's own quit save also triggers SaveNow via the Saving hook).</summary>
        public void OnApplicationQuit()
        {
            try { SaveNow(); } catch (Exception e) { CoreLog.Error("Save on quit failed: " + e); }
        }
    }
}
