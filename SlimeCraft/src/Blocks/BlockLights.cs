using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    /// <summary>
    /// Unity point lights for light-emitting blocks (glowstone, sea lantern, jack o'lantern...). The block itself is drawn
    /// full-bright by the chunk's emissive renderer; these lights make it light up nearby blocks and SR terrain.
    /// Only the nearest <see cref="BlocksConfig.MaxLights"/> emitters get a pooled light (re-evaluated every 0.5 s and
    /// whenever an emitter is added/removed); the nearest <see cref="BlocksConfig.PixelLights"/> are forced per-pixel so
    /// SR's low pixel-light quality setting cannot drop them.
    /// A light sits 0.6 m from the block centre towards its open (non-opaque) sides, so it also reaches the surfaces
    /// around the block (a light in the exact centre cannot light faces coplanar with the block, e.g. the ground under
    /// a glowstone placed on SR terrain).
    /// </summary>
    internal sealed class BlockLights
    {
        // Minecraft block light is warm-white; this approximates the torch/glowstone tint.
        private static readonly Color Warm = new Color(1f, 0.85f, 0.62f);
        private const float Offset = 0.6f;

        private readonly Dictionary<Vector3Int, int> emitters = new Dictionary<Vector3Int, int>();
        private readonly List<Light> pool = new List<Light>();
        private readonly List<Vector3Int> candidates = new List<Vector3Int>(256);
        private readonly List<float> candDist = new List<float>(256);
        private GameObject root;
        private float nextEval;
        private bool changed;
        private bool loggedFirst;

        /// <summary>Block world used to find the open sides of an emitter (set by the module).</summary>
        public BlockWorld World;

        public int EmitterCount => emitters.Count;
        public int ActiveLights { get; private set; }

        public void Add(Vector3Int pos, int level) { emitters[pos] = level; changed = true; }
        public void Remove(Vector3Int pos) { if (emitters.Remove(pos)) changed = true; }

        /// <summary>Forget all emitters (world unload / bulk load) and switch the pooled lights off.</summary>
        public void Clear()
        {
            emitters.Clear();
            for (int i = 0; i < pool.Count; i++) if (pool[i] != null) pool[i].enabled = false;
            ActiveLights = 0;
            changed = true;
            loggedFirst = false;
        }

        public void Update()
        {
            if (Time.unscaledTime < nextEval && !changed) return;
            nextEval = Time.unscaledTime + 0.5f;
            changed = false;

            int max = Mathf.Clamp(BlocksConfig.MaxLights?.Value ?? 24, 0, 128);
            var cam = CamUtil.Main;
            Vector3 cp = cam != null ? cam.transform.position : (SC.SR != null ? SC.SR.EyePosition : Vector3.zero);
            float maxRange = Mathf.Max(1f, BlocksConfig.PointLightRange?.Value ?? 12f);

            // nearest-N selection (insertion into a small sorted list); emitters farther than ~6 ranges are skipped
            candidates.Clear(); candDist.Clear();
            if (max > 0)
            {
                float cull = maxRange * 6f;
                cull *= cull;
                foreach (var kv in emitters)
                {
                    var p = kv.Key;
                    float d = (BlockWorld.Center(p) - cp).sqrMagnitude;
                    if (d > cull) continue;
                    int i = candidates.Count;
                    while (i > 0 && candDist[i - 1] > d) i--;
                    if (i >= max) continue;
                    candidates.Insert(i, p); candDist.Insert(i, d);
                    if (candidates.Count > max) { candidates.RemoveAt(max); candDist.RemoveAt(max); }
                }
            }

            EnsurePool(candidates.Count);
            float intensity = Mathf.Max(0f, BlocksConfig.PointLightIntensity?.Value ?? 1.2f);
            int pixel = Mathf.Max(0, BlocksConfig.PixelLights?.Value ?? 8);
            int active = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                var l = pool[i];
                if (l == null) continue;
                if (i < candidates.Count)
                {
                    var p = candidates[i];
                    int level = emitters[p];
                    l.transform.position = LightPosition(p);
                    l.range = Mathf.Max(1f, maxRange * Mathf.Clamp(level, 1, 15) / 15f);
                    l.intensity = intensity;
                    l.color = Warm;
                    l.renderMode = i < pixel ? LightRenderMode.ForcePixel : LightRenderMode.Auto;
                    l.enabled = true;
                    active++;
                }
                else l.enabled = false;
            }
            ActiveLights = active;
            if (active > 0 && !loggedFirst)
            {
                loggedFirst = true;
                var l0 = pool[0];
                SC.Log?.LogInfo("[Blocks] block lights: " + active + " active of " + emitters.Count + " emitters (range " + RateLog.F(l0.range, "0.#")
                    + " m, intensity " + RateLog.F(intensity, "0.##") + ", " + Mathf.Min(pixel, active) + " forced per-pixel); nearest at "
                    + RateLog.P(l0.transform.position));
            }
        }

        /// <summary>Block centre pushed towards the open sides (horizontal + up; down only if nothing else is open).</summary>
        private Vector3 LightPosition(Vector3Int p)
        {
            var c = BlockWorld.Center(p);
            var w = World;
            if (w == null) return c;
            Vector3 dir = Vector3.zero;
            for (int d = 0; d < 6; d++)
            {
                if (d == Dirs.Down) continue;
                if (!IsOpaque(w, p + Dirs.Vec[d])) dir += Dirs.VecF[d];
            }
            if (dir.sqrMagnitude < 1e-4f)
            {
                // nothing (or only opposite sides) open sideways/up: hanging light → below, else stay centred
                if (!IsOpaque(w, p + Dirs.Vec[Dirs.Down]) && IsOpaque(w, p + Dirs.Vec[Dirs.Up])) dir = Vector3.down;
                else return c;
            }
            return c + dir.normalized * Offset;
        }

        private static bool IsOpaque(BlockWorld w, Vector3Int q)
        {
            ushort id = w.GetIndex(q.x, q.y, q.z);
            return id != 0 && w.Palette[id].Opaque;
        }

        private void EnsurePool(int n)
        {
            if (root == null)
            {
                root = new GameObject("SlimeCraft_BlockLights");
                Object.DontDestroyOnLoad(root);
                pool.Clear();
            }
            while (pool.Count < n)
            {
                var go = new GameObject("blocklight");
                go.transform.SetParent(root.transform, false);
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.shadows = LightShadows.None;
                l.renderMode = LightRenderMode.Auto;
                l.enabled = false;
                pool.Add(l);
            }
        }
    }
}
