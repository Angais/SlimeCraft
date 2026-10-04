using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// <see cref="IMcAssets"/>: the user's Minecraft client jar (own zip reader) + asset index (sounds, sounds.json),
    /// texture cache (point filtered, first animation frame), English lang file and the lazily built block atlas.
    /// </summary>
    internal sealed class McAssets : IMcAssets
    {
        private ZipReader jar;
        private McInstall install;
        private readonly Dictionary<string, string> indexHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> resolvedPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        private readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private Dictionary<string, string> lang;
        private readonly object langLock = new object();
        private BlockAtlas atlas;
        private Texture2D missingTex;

        public bool Ready { get; private set; }
        public string McVersion => install?.Version;
        public string MinecraftDir => install?.Root;
        internal McInstall Install => install;

        /// <summary>Discovers the install, opens the jar and parses the asset index (synchronous, ~100 ms).</summary>
        public void Load()
        {
            install = McInstall.Discover(CoreConfig.MinecraftDir.Value, CoreConfig.MinecraftVersion.Value);
            if (!install.Valid)
            {
                CoreLog.Error("MINECRAFT NOT FOUND - SlimeCraft needs a local Minecraft Java Edition install. " + install.Problem +
                              ". Install Minecraft (launch " + McInstall.PreferredVersion + " or any release once) or set Core.MinecraftDir / Core.MinecraftVersion in the BepInEx config. All Minecraft textures will show as missing and sounds are disabled.");
                return;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                jar = new ZipReader(install.JarPath);
            }
            catch (Exception e)
            {
                CoreLog.Error("Could not open Minecraft jar " + install.JarPath + ": " + e.Message);
                return;
            }
            try
            {
                var idx = JsonNode.Parse(File.ReadAllText(install.AssetIndexPath));
                foreach (var kv in idx["objects"].Members)
                {
                    string h = kv.Value["hash"].AsString(null);
                    if (h != null) indexHashes[kv.Key] = h;
                }
            }
            catch (Exception e)
            {
                CoreLog.Error("Could not parse asset index " + install.AssetIndexPath + ": " + e.Message);
            }
            Ready = jar != null;
            CoreLog.Info("Minecraft " + install.Version + " found at " + install.Root + " (jar " + jar.Count + " entries, asset index '" + install.AssetIndexId + "' " + indexHashes.Count + " objects) in " + sw.ElapsedMilliseconds + " ms");
        }

        // ------------------------------------------------------------------ jar access
        public bool JarExists(string jarPath) => jar != null && jar.Contains(jarPath);

        public byte[] ReadJarBytes(string jarPath)
        {
            if (jar == null || jarPath == null) return null;
            try { return jar.Read(jarPath); }
            catch (Exception e) { CoreLog.Rate("jar read " + jarPath, e.Message, 60f); return null; }
        }

        public string ReadJarText(string jarPath)
        {
            var b = ReadJarBytes(jarPath);
            if (b == null) return null;
            int off = (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) ? 3 : 0;
            return Encoding.UTF8.GetString(b, off, b.Length - off);
        }

        public JsonNode ReadJarJson(string jarPath)
        {
            var t = ReadJarText(jarPath);
            return t == null ? null : JsonNode.Parse(t);
        }

        public IEnumerable<string> ListJar(string prefix) => jar == null ? (IEnumerable<string>)Array.Empty<string>() : jar.List(prefix);

        public string ResolveIndexedAsset(string logicalName)
        {
            if (install == null || !install.Valid || logicalName == null) return null;
            lock (resolvedPaths)
            {
                if (resolvedPaths.TryGetValue(logicalName, out var cached)) return cached;
                string path = null;
                if (indexHashes.TryGetValue(logicalName, out var hash) && hash.Length >= 2)
                {
                    var p = System.IO.Path.Combine(System.IO.Path.Combine(install.ObjectsDir, hash.Substring(0, 2)), hash);
                    if (File.Exists(p)) path = p;
                }
                resolvedPaths[logicalName] = path;
                return path;
            }
        }

        // ------------------------------------------------------------------ textures
        private static string NormTexPath(string path)
        {
            if (path == null) return null;
            if (path.StartsWith("minecraft:", StringComparison.Ordinal)) path = path.Substring(10);
            if (path.EndsWith(".png", StringComparison.Ordinal)) path = path.Substring(0, path.Length - 4);
            return path;
        }

        public bool TextureExists(string path)
        {
            path = NormTexPath(path);
            return path != null && JarExists("assets/minecraft/textures/" + path + ".png");
        }

        internal Texture2D MissingTexture
        {
            get
            {
                if (missingTex == null) { missingTex = Pixels.ToTexture(Pixels.Missing(), "missingno"); }
                return missingTex;
            }
        }

        public Texture2D GetTexture(string path)
        {
            path = NormTexPath(path);
            if (path == null) return MissingTexture;
            if (textures.TryGetValue(path, out var t) && t != null) return t;
            if (!Ready) return MissingTexture; // don't cache: nothing to load anyway
            t = LoadTexture(path) ?? MissingTexture;
            textures[path] = t;
            return t;
        }

        /// <summary>CPU pixels of a texture (first animation frame), or null if missing.</summary>
        internal PixelImage GetPixels(string path)
        {
            var t = GetTexture(path);
            if (t == null || t == missingTex) return null;
            return new PixelImage(t.width, t.height, t.GetPixels32());
        }

        private Texture2D LoadTexture(string path)
        {
            string entry = "assets/minecraft/textures/" + path + ".png";
            var bytes = ReadJarBytes(entry);
            if (bytes == null)
            {
                CoreLog.Debug("missing texture " + path);
                return null;
            }
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (!tex.LoadImage(bytes, false))
                {
                    UnityEngine.Object.Destroy(tex);
                    CoreLog.Rate("png decode", "could not decode " + entry, 30f);
                    return null;
                }
                tex.name = path;
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.anisoLevel = 0;

                // Animated textures: keep only the first frame (honours mcmeta frames/width/height).
                var meta = JarExists(entry + ".mcmeta") ? ReadJarJson(entry + ".mcmeta") : null;
                var anim = meta?["animation"];
                if (anim != null && anim.IsObject)
                {
                    int fw = anim["width"].AsInt(0), fh = anim["height"].AsInt(0);
                    if (fw <= 0 && fh <= 0) { fw = fh = Math.Min(tex.width, tex.height); }
                    else if (fw <= 0) fw = tex.width;
                    else if (fh <= 0) fh = tex.height;
                    int framesPerRow = Math.Max(1, tex.width / fw);
                    int frameCount = framesPerRow * Math.Max(1, tex.height / fh);
                    int first = 0;
                    var frames = anim["frames"];
                    if (frames.IsArray && frames.Count > 0)
                        first = frames[0].IsObject ? frames[0]["index"].AsInt(0) : frames[0].AsInt(0);
                    first = Mathf.Clamp(first, 0, frameCount - 1);
                    if (fw != tex.width || fh != tex.height)
                    {
                        int fx = (first % framesPerRow) * fw;
                        int fyTop = (first / framesPerRow) * fh;
                        var px = tex.GetPixels32();
                        var crop = new PixelImage(fw, fh);
                        int srcW = tex.width, srcH = tex.height;
                        for (int y = 0; y < fh; y++)
                            for (int x = 0; x < fw; x++)
                                crop.SetTopDown(x, y, px[(srcH - 1 - (fyTop + y)) * srcW + fx + x]);
                        UnityEngine.Object.Destroy(tex);
                        tex = Pixels.ToTexture(crop, path);
                    }
                }
                else
                {
                    tex.Apply(false, false);
                }
                return tex;
            }
            catch (Exception e)
            {
                CoreLog.Rate("texture load", path + ": " + e.Message, 30f);
                return null;
            }
        }

        public Sprite GetSprite(string path)
        {
            path = NormTexPath(path) ?? "";
            if (sprites.TryGetValue(path, out var s) && s != null) return s;
            var t = GetTexture(path);
            s = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);
            s.name = path;
            if (Ready) sprites[path] = s;
            return s;
        }

        // ------------------------------------------------------------------ atlas & lang
        public IBlockAtlas BlockAtlas
        {
            get
            {
                if (atlas == null)
                {
                    try { atlas = new BlockAtlas(this); atlas.Build(); }
                    catch (Exception e) { CoreLog.Error("Block atlas build failed: " + e); }
                }
                return atlas;
            }
        }

        internal BlockAtlas AtlasImpl => (BlockAtlas)BlockAtlas;

        public string Translate(string key)
        {
            if (key == null) return "";
            EnsureLang();
            return lang != null && lang.TryGetValue(key, out var v) ? v : key;
        }

        private void EnsureLang()
        {
            if (lang != null || !Ready) return;
            lock (langLock)
            {
                if (lang != null) return;
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                try
                {
                    var j = ReadJarJson("assets/minecraft/lang/en_us.json");
                    if (j != null) foreach (var kv in j.Members) d[kv.Key] = kv.Value.AsString("");
                }
                catch (Exception e) { CoreLog.Error("en_us.json: " + e.Message); }
                lang = d;
                CoreLog.Info("Loaded " + d.Count + " English translations");
            }
        }

        /// <summary>Loads lang in the background so the first tooltip does not hitch.</summary>
        internal void WarmUpAsync()
        {
            if (!Ready) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { EnsureLang(); } catch (Exception e) { CoreLog.Rate("lang warmup", e); } });
        }
    }
}
