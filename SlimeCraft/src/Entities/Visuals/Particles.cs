using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Lightweight particle system for the Minecraft-style entity effects: explosion puffs (single or a scattered
    /// 8-tick burst), poof clouds, smoke, flames, critical-hit sparks and portal specks.
    ///
    /// One shared engine drives every effect from a table of behaviour rows (lifetime, size, colour, velocity,
    /// gravity, drag, sprite sequence, size over life, motion). Sprites come from the player's Minecraft install
    /// and are packed at runtime into a small atlas; all live particles are camera-facing quads in a single dynamic
    /// mesh (one draw call), sorted back to front each frame. Simulation runs at 20 ticks per second and positions
    /// are interpolated between ticks. Units: metres (= blocks) and blocks per tick.
    /// </summary>
    internal sealed class Particles : MonoBehaviour
    {
        public static Particles I;

        // ------------------------------------------------------------------ behaviour table

        private enum Effect : byte { Explosion, Poof, Smoke, Flame, Crit, Portal }
        private enum Motion : byte { Static, Ballistic, Converge }
        private enum Growth : byte { Constant, PopIn, ShrinkToHalf, GrowIn }

        /// <summary>Everything that distinguishes one effect from another.</summary>
        private sealed class Behaviour
        {
            // lifetime = max(1, floor(LifeNumerator * scale / U(LifeLow, LifeHigh)) + LifeBase + I(LifeRandom))
            public float LifeNumerator, LifeLow, LifeHigh;
            public int LifeBase, LifeRandom;
            // colour = brightness U(BrightLow, BrightHigh) times Tint
            public float BrightLow, BrightHigh;
            public Vector3 Tint = Vector3.one;
            // half size = SizeBase * (Clustered ? 1 + 6*U*U : U(SizeLow, SizeHigh)) * (1 - ParamShrink * param) * scale
            public float SizeBase, SizeLow, SizeHigh, ParamShrink;
            public bool Clustered;
            // velocity = CallerShare * vel + SpreadShare * Spread(SpreadFollowsCaller ? vel : 0) + per-axis jitter
            public float CallerShare, SpreadShare, Jitter;
            public bool SpreadFollowsCaller;
            public float Gravity, Drag = 1f;
            public Motion Motion;
            public Growth Growth;
            // sprites: FirstSprite .. FirstSprite + SpriteCount - 1, played over the lifetime (optionally backwards)
            public int FirstSprite, SpriteCount = 1;
            public bool Backwards, RandomFrame;
        }

        private const int ExplosionSprites = 0;   // explosion_0 .. explosion_15
        private const int GenericSprites = 16;    // generic_0 .. generic_7
        private const int FlameSprite = 24;
        private const int CritSprite = 25;

        private static readonly Behaviour[] Behaviours = BuildBehaviours();

        private static Behaviour[] BuildBehaviours()
        {
            var table = new Behaviour[6];
            table[(int)Effect.Explosion] = new Behaviour
            {
                LifeBase = 6, LifeRandom = 4,
                BrightLow = 0.4f, BrightHigh = 1f,
                SizeBase = 2f, SizeLow = 1f, SizeHigh = 1f, ParamShrink = 0.5f,
                Motion = Motion.Static, Growth = Growth.Constant,
                FirstSprite = ExplosionSprites, SpriteCount = 16,
            };
            table[(int)Effect.Poof] = new Behaviour
            {
                LifeNumerator = 16f, LifeLow = 0.2f, LifeHigh = 1f, LifeBase = 2,
                BrightLow = 0.7f, BrightHigh = 1f,
                SizeBase = 0.1f, Clustered = true,
                CallerShare = 1f, Jitter = 0.05f,
                Gravity = -0.1f, Drag = 0.9f,
                Motion = Motion.Ballistic, Growth = Growth.Constant,
                FirstSprite = GenericSprites, SpriteCount = 8, Backwards = true,
            };
            table[(int)Effect.Smoke] = new Behaviour
            {
                LifeNumerator = 8f, LifeLow = 0.2f, LifeHigh = 1f,
                BrightLow = 0f, BrightHigh = 0.3f,
                SizeBase = 0.75f, SizeLow = 0.1f, SizeHigh = 0.2f,
                CallerShare = 1f, SpreadShare = 0.1f,
                Gravity = -0.1f, Drag = 0.96f,
                Motion = Motion.Ballistic, Growth = Growth.PopIn,
                FirstSprite = GenericSprites, SpriteCount = 8, Backwards = true,
            };
            table[(int)Effect.Flame] = new Behaviour
            {
                LifeNumerator = 8f, LifeLow = 0.2f, LifeHigh = 1f, LifeBase = 4,
                BrightLow = 1f, BrightHigh = 1f,
                SizeBase = 1f, SizeLow = 0.1f, SizeHigh = 0.2f,
                CallerShare = 1f, SpreadShare = 0.01f, SpreadFollowsCaller = true,
                Gravity = 0f, Drag = 0.96f,
                Motion = Motion.Ballistic, Growth = Growth.ShrinkToHalf,
                FirstSprite = FlameSprite,
            };
            table[(int)Effect.Crit] = new Behaviour
            {
                LifeNumerator = 6f, LifeLow = 0.6f, LifeHigh = 1.4f,
                BrightLow = 0.6f, BrightHigh = 0.9f,
                SizeBase = 0.75f, SizeLow = 0.1f, SizeHigh = 0.2f,
                CallerShare = 0.4f, SpreadShare = 0.1f,
                Gravity = 0.5f, Drag = 0.7f,
                Motion = Motion.Ballistic, Growth = Growth.Constant,
                FirstSprite = CritSprite,
            };
            table[(int)Effect.Portal] = new Behaviour
            {
                LifeBase = 40, LifeRandom = 10,
                BrightLow = 0.4f, BrightHigh = 1f, Tint = new Vector3(0.9f, 0.3f, 1f),
                SizeBase = 0.1f, SizeLow = 0.5f, SizeHigh = 0.7f,
                Motion = Motion.Converge, Growth = Growth.GrowIn,
                FirstSprite = GenericSprites, SpriteCount = 8, RandomFrame = true,
            };
            return table;
        }

        // ------------------------------------------------------------------ atlas facts

        private static readonly string[] SpritePaths = BuildSpritePaths();
        private const int Cell = 32;
        private const int Columns = 8;

        private static string[] BuildSpritePaths()
        {
            var paths = new string[26];
            for (int i = 0; i < 16; i++) paths[ExplosionSprites + i] = "particle/explosion_" + i;
            for (int i = 0; i < 8; i++) paths[GenericSprites + i] = "particle/generic_" + i;
            paths[FlameSprite] = "particle/flame";
            paths[CritSprite] = "particle/critical_hit";
            return paths;
        }

        // ------------------------------------------------------------------ state

        private int capacity;
        private int live;
        private Vector3[] position, previous, velocity, anchor;
        private float[] halfSize;
        private int[] age, lifetime;
        private Color32[] colour;
        private byte[] effect, fixedSprite;

        private struct Burst { public Vector3 Center; public int TicksRun; }
        private readonly List<Burst> bursts = new List<Burst>();

        private float tickClock;

        // render buffers
        private Vector3[] meshVerts;
        private Vector2[] meshUvs;
        private Color32[] meshColours;
        private Vector3[] drawAt;
        private float[] depthKey;
        private int[] drawOrder;
        private int quadsLastFrame;

        private Rect[] spriteUv;
        private Mesh mesh;
        private MeshRenderer meshRenderer;
        private bool resourcesReady, resourcesFailed;

        // ------------------------------------------------------------------ lifecycle

        public static void Ensure()
        {
            if (I != null) return;
            var go = new GameObject("SC_Particles");
            DontDestroyOnLoad(go);
            I = go.AddComponent<Particles>();
        }

        private void Awake()
        {
            int wanted = EConfig.MaxParticles != null ? EConfig.MaxParticles.Value : 1536;
            capacity = Mathf.Clamp(wanted, 64, 8192);

            position = new Vector3[capacity];
            previous = new Vector3[capacity];
            velocity = new Vector3[capacity];
            anchor = new Vector3[capacity];
            halfSize = new float[capacity];
            age = new int[capacity];
            lifetime = new int[capacity];
            colour = new Color32[capacity];
            effect = new byte[capacity];
            fixedSprite = new byte[capacity];

            meshVerts = new Vector3[capacity * 4];
            meshUvs = new Vector2[capacity * 4];
            meshColours = new Color32[capacity * 4];
            drawAt = new Vector3[capacity];
            depthKey = new float[capacity];
            drawOrder = new int[capacity];
        }

        private void OnDestroy() { if (I == this) I = null; }

        /// <summary>Removes every live particle and every pending burst (GPU resources are kept).</summary>
        public void Clear()
        {
            live = 0;
            bursts.Clear();
        }

        /// <summary>Builds the atlas, material and mesh once Minecraft assets are available. Never retried after a failure.</summary>
        private bool PrepareResources()
        {
            if (resourcesReady) return true;
            if (resourcesFailed || !EMat.AssetsReady) return false;
            try
            {
                var shader = EMat.SpriteShader;
                if (shader == null) { resourcesFailed = true; return false; }

                var atlas = BuildAtlas();
                var mat = new Material(shader) { name = "SC_Particles", mainTexture = atlas, renderQueue = 3100 };
                EMat.PrepareSpriteMaterial(mat);

                mesh = new Mesh { name = "SC_ParticleMesh" };
                mesh.MarkDynamic();
                mesh.vertices = meshVerts;
                mesh.uv = meshUvs;
                mesh.colors32 = meshColours;
                var indices = new int[capacity * 6];
                for (int q = 0; q < capacity; q++)
                {
                    int v = q * 4, t = q * 6;
                    indices[t] = v; indices[t + 1] = v + 1; indices[t + 2] = v + 2;
                    indices[t + 3] = v; indices[t + 4] = v + 2; indices[t + 5] = v + 3;
                }
                mesh.triangles = indices;
                mesh.bounds = HugeBounds;

                gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                meshRenderer = gameObject.AddComponent<MeshRenderer>();
                meshRenderer.sharedMaterial = mat;
                meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                meshRenderer.receiveShadows = false;
                meshRenderer.SetPropertyBlock(EMat.SpriteBlock(Color.white));
                meshRenderer.enabled = false;

                resourcesReady = true;
                return true;
            }
            catch (Exception e)
            {
                ELog.Error("Particle atlas", e);
                resourcesFailed = true;
                return false;
            }
        }

        private static readonly Bounds HugeBounds = new Bounds(Vector3.zero, Vector3.one * 100000f);

        /// <summary>Packs every sprite into the bottom-left corner of its own 32x32 cell (8 columns).</summary>
        private Texture2D BuildAtlas()
        {
            int rows = (SpritePaths.Length + Columns - 1) / Columns;
            int width = Columns * Cell;
            int height = Mathf.NextPowerOfTwo(rows * Cell);
            var pixels = new Color32[width * height];   // starts fully transparent
            spriteUv = new Rect[SpritePaths.Length];

            for (int s = 0; s < SpritePaths.Length; s++)
            {
                int cellX = (s % Columns) * Cell;
                int cellY = (s / Columns) * Cell;
                int usedW = 1, usedH = 1;
                var src = EMat.Tex(SpritePaths[s]);
                if (src != null)
                {
                    int sw = src.width, sh = src.height;
                    // Fit large textures into the cell by nearest-neighbour sampling (the default sprites are 16 px or less; resource packs may be larger).
                    float shrink = Mathf.Min(1f, Cell / (float)Mathf.Max(sw, sh));
                    usedW = Mathf.Clamp(Mathf.RoundToInt(sw * shrink), 1, Cell);
                    usedH = Mathf.Clamp(Mathf.RoundToInt(sh * shrink), 1, Cell);
                    Color32[] srcPixels = null;
                    try { srcPixels = src.GetPixels32(); } catch { srcPixels = null; }
                    if (srcPixels != null)
                    {
                        for (int y = 0; y < usedH; y++)
                        {
                            int sy = usedH == sh ? y : Mathf.Min(sh - 1, y * sh / usedH);
                            for (int x = 0; x < usedW; x++)
                            {
                                int sx = usedW == sw ? x : Mathf.Min(sw - 1, x * sw / usedW);
                                pixels[(cellY + y) * width + cellX + x] = srcPixels[sy * sw + sx];
                            }
                        }
                    }
                }
                const float inset = 0.01f;   // keeps neighbouring cells from bleeding in
                spriteUv[s] = Rect.MinMaxRect((cellX + inset) / width, (cellY + inset) / height,
                                              (cellX + usedW - inset) / width, (cellY + usedH - inset) / height);
            }

            var atlas = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = "SC_ParticleAtlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            };
            atlas.SetPixels32(pixels);
            atlas.Apply(false, false);
            return atlas;
        }

        // ------------------------------------------------------------------ spawning

        public static void Explosion(Vector3 pos, float sizeParam) { if (I != null) I.Spawn(Effect.Explosion, pos, Vector3.zero, sizeParam, 1f); }

        public static void ExplosionEmitter(Vector3 pos)
        {
            if (I == null || !I.PrepareResources()) return;
            I.bursts.Add(new Burst { Center = pos, TicksRun = 0 });
        }

        public static void Poof(Vector3 pos, Vector3 vel) { if (I != null) I.Spawn(Effect.Poof, pos, vel, 0f, 1f); }
        public static void Smoke(Vector3 pos, Vector3 vel, float scale = 1f) { if (I != null) I.Spawn(Effect.Smoke, pos, vel, 0f, scale); }
        public static void Flame(Vector3 pos, Vector3 vel) { if (I != null) I.Spawn(Effect.Flame, pos, vel, 0f, 1f); }
        public static void Crit(Vector3 pos, Vector3 vel) { if (I != null) I.Spawn(Effect.Crit, pos, vel, 0f, 1f); }

        /// <summary>Purple speck that starts about a block above <paramref name="pos"/> + <paramref name="offset"/> and glides into <paramref name="pos"/>.</summary>
        public static void Portal(Vector3 pos, Vector3 offset) { if (I != null) I.Spawn(Effect.Portal, pos, offset, 0f, 1f); }

        /// <summary>Cloud of 20 poofs over a dying mob's body.</summary>
        public static void DeathPoof(Vector3 feet, float width, float height)
        {
            if (I == null) return;
            for (int i = 0; i < 20; i++)
            {
                var at = feet + new Vector3(width * (Rng.F() * 2f - 1f), height * Rng.F(), width * (Rng.F() * 2f - 1f));
                var drift = new Vector3(Rng.Gaussian() * 0.02f, Rng.Gaussian() * 0.02f, Rng.Gaussian() * 0.02f);
                Poof(at, drift);
            }
        }

        private static float Between(float low, float high) => low + (high - low) * Rng.F();

        /// <summary>Small random kick: biased direction plus jitter, length triangular in [0.06, 0.18), then a lift of 0.1.</summary>
        private static Vector3 Spread(Vector3 bias)
        {
            var dir = bias + new Vector3(Between(-0.4f, 0.4f), Between(-0.4f, 0.4f), Between(-0.4f, 0.4f));
            dir = dir.sqrMagnitude > 1e-12f ? dir.normalized : Vector3.up;
            var kick = dir * (0.06f * (1f + Rng.F() + Rng.F()));
            kick.y += 0.1f;
            return kick;
        }

        private void Spawn(Effect kind, Vector3 pos, Vector3 vel, float param, float scale)
        {
            if (!PrepareResources() || live >= capacity) return;
            var b = Behaviours[(int)kind];
            int i = live++;

            int life = b.LifeBase + (b.LifeRandom > 0 ? Rng.I(b.LifeRandom) : 0);
            if (b.LifeNumerator > 0f) life += Mathf.FloorToInt(b.LifeNumerator * scale / Between(b.LifeLow, b.LifeHigh));
            lifetime[i] = Mathf.Max(1, life);
            age[i] = 0;

            float bright = Between(b.BrightLow, b.BrightHigh);
            colour[i] = new Color32(ToByte(bright * b.Tint.x), ToByte(bright * b.Tint.y), ToByte(bright * b.Tint.z), 255);

            float sizeRoll = b.Clustered ? 1f + 6f * Rng.F() * Rng.F() : Between(b.SizeLow, b.SizeHigh);
            halfSize[i] = b.SizeBase * sizeRoll * (1f - b.ParamShrink * param) * scale;

            effect[i] = (byte)kind;
            fixedSprite[i] = (byte)(b.RandomFrame ? b.FirstSprite + Rng.I(b.SpriteCount) : b.FirstSprite);

            position[i] = pos;
            previous[i] = pos;
            if (b.Motion == Motion.Converge)
            {
                anchor[i] = pos;
                velocity[i] = vel;   // the starting displacement
            }
            else
            {
                Vector3 v = vel * b.CallerShare;
                if (b.SpreadShare > 0f) v += Spread(b.SpreadFollowsCaller ? vel : Vector3.zero) * b.SpreadShare;
                if (b.Jitter > 0f) v += new Vector3(Between(-b.Jitter, b.Jitter), Between(-b.Jitter, b.Jitter), Between(-b.Jitter, b.Jitter));
                velocity[i] = v;
            }
        }

        private static byte ToByte(float f) => (byte)Mathf.Clamp(Mathf.RoundToInt(f * 255f), 0, 255);

        // ------------------------------------------------------------------ simulation

        private void Update()
        {
            try
            {
                if (live == 0 && bursts.Count == 0)
                {
                    if (meshRenderer != null && meshRenderer.enabled) meshRenderer.enabled = false;
                    return;
                }
                if (!PrepareResources())
                {
                    Clear();
                    return;
                }

                float dt = Time.deltaTime;
                if (dt > 0f)
                {
                    tickClock += dt * Mc.TPS;
                    int ran = 0;
                    while (tickClock >= 1f && ran < 5)
                    {
                        tickClock -= 1f;
                        Tick();
                        ran++;
                    }
                    if (tickClock >= 1f) tickClock = 0f;   // long hitch: drop the backlog instead of spiralling
                }
                Render();
            }
            catch (Exception e) { ELog.Error("Particles.Update", e); }
        }

        private void Tick()
        {
            // Bursts: 6 explosion puffs per tick for 8 ticks, later ones smaller, scattered up to 4 blocks away.
            for (int k = bursts.Count - 1; k >= 0; k--)
            {
                var burst = bursts[k];
                float shrink = burst.TicksRun / 8f;
                for (int n = 0; n < 6; n++)
                {
                    var at = burst.Center + new Vector3(Rng.Diff() * 4f, Rng.Diff() * 4f, Rng.Diff() * 4f);
                    Spawn(Effect.Explosion, at, Vector3.zero, shrink, 1f);
                }
                burst.TicksRun++;
                if (burst.TicksRun >= 8) bursts.RemoveAt(k);
                else bursts[k] = burst;
            }

            int i = 0;
            while (i < live)
            {
                previous[i] = position[i];
                if (age[i]++ >= lifetime[i])
                {
                    MoveLastInto(i);
                    continue;
                }
                var b = Behaviours[effect[i]];
                switch (b.Motion)
                {
                    case Motion.Ballistic:
                        {
                            Vector3 v = velocity[i];
                            v.y -= 0.04f * b.Gravity;
                            position[i] += v;
                            velocity[i] = v * b.Drag;
                            break;
                        }
                    case Motion.Converge:
                        {
                            // Swings slightly outward, then glides down and in to land on the anchor at the end of life.
                            float f = age[i] / (float)lifetime[i];
                            position[i] = anchor[i] + (1f - f) * ((1f + 2f * f) * velocity[i] + Vector3.up);
                            break;
                        }
                }
                i++;
            }
        }

        private void MoveLastInto(int slot)
        {
            int last = --live;
            if (slot == last) return;
            position[slot] = position[last];
            previous[slot] = previous[last];
            velocity[slot] = velocity[last];
            anchor[slot] = anchor[last];
            halfSize[slot] = halfSize[last];
            age[slot] = age[last];
            lifetime[slot] = lifetime[last];
            colour[slot] = colour[last];
            effect[slot] = effect[last];
            fixedSprite[slot] = fixedSprite[last];
        }

        // ------------------------------------------------------------------ rendering

        private void Render()
        {
            Camera cam = SC.SR != null ? SC.SR.MainCamera : Camera.main;
            if (cam == null || live == 0)
            {
                meshRenderer.enabled = false;
                return;
            }

            float partial = Mathf.Clamp01(tickClock);
            Transform view = cam.transform;
            Vector3 eye = view.position, ahead = view.forward, right = view.right, up = view.up;

            for (int i = 0; i < live; i++)
            {
                drawAt[i] = Vector3.LerpUnclamped(previous[i], position[i], partial);
                depthKey[i] = -Vector3.Dot(drawAt[i] - eye, ahead);   // ascending order = farthest first
                drawOrder[i] = i;
            }
            Array.Sort(depthKey, drawOrder, 0, live);

            for (int q = 0; q < live; q++)
            {
                int i = drawOrder[q];
                var b = Behaviours[effect[i]];
                float lifeFraction = Mathf.Clamp01((age[i] + partial) / lifetime[i]);
                float h = halfSize[i] * GrowthFactor(b.Growth, lifeFraction);
                Rect uv = spriteUv[SpriteFor(b, i)];

                Vector3 c = drawAt[i];
                Vector3 r = right * h, u = up * h;
                int v = q * 4;
                meshVerts[v] = c - r - u;        // bottom-left
                meshVerts[v + 1] = c - r + u;    // top-left
                meshVerts[v + 2] = c + r + u;    // top-right
                meshVerts[v + 3] = c + r - u;    // bottom-right
                meshUvs[v] = new Vector2(uv.xMin, uv.yMin);
                meshUvs[v + 1] = new Vector2(uv.xMin, uv.yMax);
                meshUvs[v + 2] = new Vector2(uv.xMax, uv.yMax);
                meshUvs[v + 3] = new Vector2(uv.xMax, uv.yMin);
                Color32 col = colour[i];
                meshColours[v] = col; meshColours[v + 1] = col; meshColours[v + 2] = col; meshColours[v + 3] = col;
            }

            // Collapse quads that were used last frame but not now.
            for (int q = live; q < quadsLastFrame; q++)
            {
                int v = q * 4;
                meshVerts[v] = meshVerts[v + 1] = meshVerts[v + 2] = meshVerts[v + 3] = Vector3.zero;
            }
            quadsLastFrame = live;

            mesh.vertices = meshVerts;
            mesh.uv = meshUvs;
            mesh.colors32 = meshColours;
            mesh.bounds = HugeBounds;
            meshRenderer.enabled = true;
        }

        private static float GrowthFactor(Growth g, float f)
        {
            switch (g)
            {
                case Growth.PopIn: return Mathf.Min(1f, f * 32f);
                case Growth.ShrinkToHalf: return 1f - f * f * 0.5f;
                case Growth.GrowIn: return f * (2f - f);
                default: return 1f;
            }
        }

        private int SpriteFor(Behaviour b, int i)
        {
            if (b.RandomFrame || b.SpriteCount <= 1) return fixedSprite[i];
            int n = b.SpriteCount;
            int frame = Mathf.Clamp(age[i] * (n - 1) / Mathf.Max(1, lifetime[i]), 0, n - 1);
            return b.FirstSprite + (b.Backwards ? n - 1 - frame : frame);
        }
    }
}
