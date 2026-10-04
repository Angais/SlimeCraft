using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace SlimeCraft.Core
{
    /// <summary>
    /// <see cref="IMcAudio"/>: Minecraft sound events from the user's asset index. sounds.json is parsed on a
    /// worker thread; OGG files are loaded on demand with UnityWebRequestMultimedia (kept Vorbis-compressed in
    /// memory) and played on a pool of AudioSources configured like Minecraft's OpenAL channels: linear
    /// attenuation to 0 at attenuation_distance (16) * max(1, volume), gain clamp 0..1, pitch clamp 0.5..2.
    /// Volume follows Slime Rancher's SFX bus (options slider) and world sounds pause while SR is paused.
    /// </summary>
    internal sealed class McAudio : IMcAudio
    {
        private sealed class Voice
        {
            public AudioSource Src;
            public float BaseVolume;
            public bool World;          // 3D world sound (pauses with the game)
            public Transform Follow;
            public bool Attached;
            public bool PausedByUs;
            public string PendingFile;  // waiting for its clip
            public float PendingSince;
            public float StartedAt;
        }

        private struct PendingPlay
        {
            public string File;
            public Vector3 Pos;
            public float Volume, Pitch, MaxDist;
            public bool World;
            public float Time;
        }

        private const int MaxConcurrentLoads = 6;
        private const float PendingTimeout = 0.75f;

        private readonly McAssets assets;
        private readonly MonoBehaviour host;
        private volatile SoundRegistry registry;
        private readonly System.Random rng = new System.Random();
        private readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        private readonly HashSet<string> failed = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> loading = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> loadQueue = new Queue<string>();
        private readonly List<PendingPlay> pending = new List<PendingPlay>();
        private readonly List<string> preloadBeforeReady = new List<string>();
        private readonly List<Voice> voices = new List<Voice>();
        private GameObject root;
        private int activeLoads;
        private bool gamePaused;
        private object sfxBus; // SECTR_AudioBus (kept as object to avoid hard failures if SECTR changes)
        private float busVolume = 1f;
        private bool worldMuted, worldBusPaused;
        private float nextBusLookup;

        public bool Ready => registry != null;

        public McAudio(McAssets assets, MonoBehaviour host)
        {
            this.assets = assets;
            this.host = host;
        }

        /// <summary>Creates the source pool and starts parsing sounds.json in the background.</summary>
        public void Init()
        {
            root = new GameObject("SlimeCraftAudio");
            root.transform.SetParent(SC.Root != null ? SC.Root.transform : null, false);
            int n = Mathf.Clamp(CoreConfig.AudioSources.Value, 4, 64);
            for (int i = 0; i < n; i++)
            {
                var go = new GameObject("voice" + i);
                go.transform.SetParent(root.transform, false);
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.rolloffMode = AudioRolloffMode.Linear;
                s.minDistance = 0.01f;
                s.maxDistance = 16f;
                s.dopplerLevel = 0f;
                s.spread = 0f;
                s.priority = 96;
                voices.Add(new Voice { Src = s });
            }

            if (!assets.Ready) { CoreLog.Warn("Minecraft sounds disabled (no Minecraft install)."); return; }
            string path = assets.ResolveIndexedAsset("minecraft/sounds.json");
            if (path == null) { CoreLog.Error("minecraft/sounds.json not found in the asset index - Minecraft sounds disabled. Launch Minecraft " + assets.McVersion + " once to download its assets."); return; }
            var t = new Thread(() =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var reg = SoundRegistry.Parse(JsonNode.Parse(File.ReadAllText(path)));
                    registry = reg;
                    CoreLog.Info("Parsed sounds.json: " + reg.Count + " sound events in " + sw.ElapsedMilliseconds + " ms");
                }
                catch (Exception e) { CoreLog.Error("sounds.json parse failed: " + e); }
            }) { IsBackground = true, Name = "SlimeCraft sounds.json" };
            t.Start();
        }

        // ------------------------------------------------------------------ public API
        public void Play(string soundEvent, Vector3 position, float volume = 1f, float pitch = 1f) => PlayInternal(soundEvent, position, volume, pitch, true);

        public void Play2D(string soundEvent, float volume = 1f, float pitch = 1f) => PlayInternal(soundEvent, Vector3.zero, volume, pitch, false);

        public AudioSource PlayAttached(string soundEvent, Transform follow, float volume = 1f, float pitch = 1f, bool loop = false)
        {
            try
            {
                var reg = registry;
                if (reg == null || follow == null) return null;
                var ev = reg.Resolve(soundEvent);
                if (ev == null) { CoreLog.WarnOnce("nosound " + soundEvent, "Unknown sound event '" + soundEvent + "'"); return null; }
                if (!reg.Pick(ev, rng, out var v)) return null;
                var voice = Acquire(true);
                if (voice == null) return null;
                float inst = volume * v.Volume;
                var s = voice.Src;
                s.Stop();
                s.loop = loop;
                s.spatialBlend = 1f;
                s.pitch = Mathf.Clamp(pitch * v.Pitch, 0.5f, 2f);
                s.maxDistance = v.Attenuation * Mathf.Max(1f, inst);
                s.transform.position = follow.position;
                voice.BaseVolume = Mathf.Clamp01(inst);
                voice.World = true;
                voice.Follow = follow;
                voice.Attached = true;
                voice.PausedByUs = false;
                voice.StartedAt = Time.unscaledTime;
                if (clips.TryGetValue(v.File, out var clip) && clip != null)
                {
                    voice.PendingFile = null;
                    s.clip = clip;
                    ApplyVolume(voice);
                    s.Play();
                }
                else
                {
                    s.clip = null;
                    voice.PendingFile = v.File;
                    voice.PendingSince = Time.unscaledTime;
                    RequestLoad(v.File, v.Stream);
                }
                return s;
            }
            catch (Exception e) { CoreLog.Rate("PlayAttached", e); return null; }
        }

        public void Preload(params string[] soundEvents)
        {
            if (soundEvents == null) return;
            var reg = registry;
            if (reg == null) { lock (preloadBeforeReady) preloadBeforeReady.AddRange(soundEvents); return; }
            var files = new List<string>();
            foreach (var e in soundEvents)
            {
                var ev = reg.Resolve(e);
                if (ev != null) reg.CollectFiles(ev, files);
            }
            foreach (var f in files) RequestLoad(f, false);
        }

        // ------------------------------------------------------------------ internals
        private void PlayInternal(string soundEvent, Vector3 pos, float volume, float pitch, bool world)
        {
            try
            {
                var reg = registry;
                if (reg == null || string.IsNullOrEmpty(soundEvent)) return;
                var ev = reg.Resolve(soundEvent);
                if (ev == null) { CoreLog.WarnOnce("nosound " + soundEvent, "Unknown sound event '" + soundEvent + "'"); return; }
                if (!reg.Pick(ev, rng, out var v)) return;
                float inst = volume * v.Volume;
                var p = new PendingPlay
                {
                    File = v.File, Pos = pos, World = world, Time = Time.unscaledTime,
                    Volume = Mathf.Clamp01(inst),
                    Pitch = Mathf.Clamp(pitch * v.Pitch, 0.5f, 2f),
                    MaxDist = v.Attenuation * Mathf.Max(1f, inst)
                };
                if (clips.TryGetValue(v.File, out var clip) && clip != null) StartVoice(p, clip);
                else if (!failed.Contains(v.File))
                {
                    pending.Add(p);
                    RequestLoad(v.File, v.Stream);
                }
            }
            catch (Exception e) { CoreLog.Rate("Play " + soundEvent, e); }
        }

        private void StartVoice(PendingPlay p, AudioClip clip)
        {
            if (p.World && gamePaused) return; // Minecraft drops sounds started while paused too
            var voice = Acquire(false);
            if (voice == null) return;
            var s = voice.Src;
            s.Stop();
            s.clip = clip;
            s.loop = false;
            s.spatialBlend = p.World ? 1f : 0f;
            s.pitch = p.Pitch;
            s.maxDistance = Mathf.Max(0.1f, p.MaxDist);
            s.transform.position = p.Pos;
            voice.BaseVolume = p.Volume;
            voice.World = p.World;
            voice.Follow = null;
            voice.Attached = false;
            voice.PausedByUs = false;
            voice.PendingFile = null;
            voice.StartedAt = Time.unscaledTime;
            ApplyVolume(voice);
            s.Play();
        }

        /// <summary>Free voice, else steal the oldest non-attached one.</summary>
        private Voice Acquire(bool forAttached)
        {
            Voice oldest = null;
            foreach (var v in voices)
            {
                // a voice the caller stopped (or that finished) is free again, attached or not
                bool busy = v.Src.isPlaying || v.PausedByUs || v.PendingFile != null;
                if (!busy) { v.Attached = false; v.Follow = null; return v; }
                if (v.Attached) continue;
                if (oldest == null || v.StartedAt < oldest.StartedAt) oldest = v;
            }
            return oldest;
        }

        private void RequestLoad(string file, bool stream)
        {
            if (file == null || clips.ContainsKey(file) || failed.Contains(file) || loading.Contains(file)) return;
            loading.Add(file);
            loadQueue.Enqueue(file);
            PumpLoads();
        }

        private void PumpLoads()
        {
            while (activeLoads < MaxConcurrentLoads && loadQueue.Count > 0)
            {
                var f = loadQueue.Dequeue();
                activeLoads++;
                host.StartCoroutine(LoadClip(f));
            }
        }

        private IEnumerator LoadClip(string file)
        {
            string path = assets.ResolveIndexedAsset(file);
            UnityWebRequest req = null;
            if (path != null)
            {
                try
                {
                    req = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, AudioType.OGGVORBIS);
                    var dh = req.downloadHandler as DownloadHandlerAudioClip;
                    if (dh != null) dh.compressed = true; // keep Vorbis in memory, decode on play
                }
                catch (Exception e) { CoreLog.Rate("audio request", e); req = null; }
            }
            if (req != null)
            {
                yield return req.SendWebRequest();
                try
                {
                    if (req.isNetworkError || req.isHttpError) throw new IOException(req.error);
                    var clip = DownloadHandlerAudioClip.GetContent(req);
                    if (clip == null) throw new IOException("no clip");
                    clip.name = file;
                    clips[file] = clip;
                }
                catch (Exception e)
                {
                    failed.Add(file);
                    CoreLog.Rate("audio load", file + ": " + e.Message, 30f);
                }
                finally { req.Dispose(); }
            }
            else
            {
                failed.Add(file);
                CoreLog.Rate("audio missing", "sound file not in asset index: " + file, 60f);
            }
            loading.Remove(file);
            activeLoads--;
            OnClipReady(file);
            PumpLoads();
        }

        private void OnClipReady(string file)
        {
            clips.TryGetValue(file, out var clip);
            float now = Time.unscaledTime;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var p = pending[i];
                if (p.File != file) continue;
                pending.RemoveAt(i);
                if (clip != null && now - p.Time <= PendingTimeout) StartVoice(p, clip);
            }
            foreach (var v in voices)
            {
                if (v.PendingFile != file) continue;
                v.PendingFile = null;
                if (clip == null || v.Follow == null || now - v.PendingSince > PendingTimeout * 2f) { v.Attached = false; continue; }
                v.Src.clip = clip;
                ApplyVolume(v);
                v.Src.Play();
            }
        }

        private void ApplyVolume(Voice v)
        {
            float vol = v.BaseVolume * busVolume * Mathf.Max(0f, CoreConfig.MasterVolume.Value);
            if (v.World && worldMuted) vol = 0f;
            v.Src.volume = vol;
        }

        /// <summary>Per frame: pause handling, SR volume, attached sources, preload queue, stale pending plays.</summary>
        public void Tick(bool srPaused)
        {
            if (preloadBeforeReady.Count > 0 && registry != null)
            {
                string[] arr;
                lock (preloadBeforeReady) { arr = preloadBeforeReady.ToArray(); preloadBeforeReady.Clear(); }
                Preload(arr);
            }

            UpdateBus();
            bool paused = srPaused || worldBusPaused;
            if (paused != gamePaused)
            {
                gamePaused = paused;
                foreach (var v in voices)
                {
                    if (!v.World) continue;
                    if (paused && v.Src.isPlaying) { v.Src.Pause(); v.PausedByUs = true; }
                    else if (!paused && v.PausedByUs) { v.Src.UnPause(); v.PausedByUs = false; }
                }
            }

            float now = Time.unscaledTime;
            foreach (var v in voices)
            {
                if (v.Attached)
                {
                    if (v.Follow == null)
                    {
                        if (v.Src.isPlaying || v.PausedByUs) v.Src.Stop();
                        v.Attached = false; v.PausedByUs = false; v.PendingFile = null;
                    }
                    else v.Src.transform.position = v.Follow.position;
                }
                if (v.PendingFile != null && now - v.PendingSince > 5f) { v.PendingFile = null; v.Attached = false; }
                if (v.Src.isPlaying) ApplyVolume(v);
            }
            if (pending.Count > 0)
                for (int i = pending.Count - 1; i >= 0; i--)
                    if (now - pending[i].Time > 5f) pending.RemoveAt(i);
        }

        /// <summary>Reads SR's SECTR "SFX" bus volume (the options slider) and the world-SFX mute/pause state.</summary>
        private void UpdateBus()
        {
            try
            {
                var sys = SECTR_AudioSystem.System;
                if (sys == null || sys.MasterBus == null) { busVolume = 1f; worldMuted = false; worldBusPaused = false; return; }
                if (sfxBus == null && Time.unscaledTime >= nextBusLookup)
                {
                    nextBusLookup = Time.unscaledTime + 5f;
                    sfxBus = FindBus(sys.MasterBus, "SFX", 0);
                    if (sfxBus != null) CoreLog.Info("Minecraft sounds follow Slime Rancher's 'SFX' audio bus volume");
                }
                var bus = sfxBus as SECTR_AudioBus;
                if (bus == null) { busVolume = sys.MasterBus.EffectiveVolume; worldMuted = false; worldBusPaused = false; return; }
                busVolume = bus.EffectiveVolume;
                bool muted = false, bp = false;
                foreach (var child in bus.Children)
                {
                    if (child == null || child.name == "UI" || child.name == "Pause Transition") continue;
                    if (child.Muted) muted = true;
                    if (child.Paused) bp = true;
                }
                worldMuted = muted;
                worldBusPaused = bp;
            }
            catch (Exception e)
            {
                CoreLog.Rate("audio bus", e, 60f);
                busVolume = 1f;
            }
        }

        private static SECTR_AudioBus FindBus(SECTR_AudioBus bus, string name, int depth)
        {
            if (bus == null || depth > 6) return null;
            if (bus.name == name) return bus;
            foreach (var c in bus.Children)
            {
                var f = FindBus(c, name, depth + 1);
                if (f != null) return f;
            }
            return null;
        }

        /// <summary>Stops every world sound (world unloading).</summary>
        public void StopAllWorld()
        {
            foreach (var v in voices)
            {
                if (!v.World) continue;
                v.Src.Stop();
                v.Attached = false; v.Follow = null; v.PausedByUs = false; v.PendingFile = null;
            }
            pending.Clear();
        }

        /// <summary>Clips worth having in memory before the first explosion/block break.</summary>
        public void PreloadCommon()
        {
            var list = new List<string>
            {
                "entity.generic.explode", "entity.tnt.primed", "entity.creeper.primed", "entity.item.pickup", "entity.player.hurt",
                "entity.player.attack.strong", "entity.player.attack.weak", "entity.player.attack.sweep", "entity.player.burp", "entity.generic.eat",
                "item.flintandsteel.use", "ui.button.click", "entity.item.break", "block.stone.hit"
            };
            var groups = new HashSet<string>();
            foreach (var b in Content.Blocks) if (!string.IsNullOrEmpty(b.Sound)) groups.Add(b.Sound);
            foreach (var g in new[] { "stone", "grass", "wood", "gravel", "sand" })
                if (groups.Contains(g)) { list.Add("block." + g + ".break"); list.Add("block." + g + ".place"); list.Add("block." + g + ".step"); }
            Preload(list.ToArray());
        }
    }
}
