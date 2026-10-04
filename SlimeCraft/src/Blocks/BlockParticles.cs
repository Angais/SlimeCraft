using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SlimeCraft.BlocksMod
{
    /// <summary>
    /// Little textured debris squares for SlimeCraft blocks and items: the burst when a block (or a harvested piece of
    /// Slime Rancher terrain) is destroyed, the single chip knocked off on each mining hit, and food crumbs while eating.
    /// Every piece faces the camera, shows a random quarter-size window of its texture, falls with gravity and air drag,
    /// lands on our voxel blocks or on a fixed floor height sampled from the SR world once at spawn, and fades after a
    /// short random lifetime. The look and motion are tuned to match Minecraft's feel.
    ///
    /// Pieces sharing a material live in one pooled "flock" drawn by a single dynamic mesh (one draw call per material).
    /// After a flock is created nothing is allocated while spawning or simulating.
    /// </summary>
    internal sealed class BlockParticles
    {
        // ---- motion, expressed per second (the reference values are per 1/20 s game tick)
        private const float TickRate = 20f;
        private const float FallAcceleration = 16f;   // m/s² (0.04 blocks per tick²)
        private const float DragPerTick = 0.98f;      // all axes, in the air
        private const float GroundGripPerTick = 0.7f; // horizontal only, while resting on something
        private const float UpwardKick = 0.1f;        // blocks per tick, never scaled by the burst power

        // ---- size and spawn tuning
        private const int PoolSize = 512;
        private const float HitChipScale = 0.6f;
        private const float HitChipPower = 0.2f;
        private const float HitChipLift = 0.05f;      // pushed off the surface along its normal
        private const float CrumbOffsetScale = 0.6f;  // keeps food crumbs close to the face

        // ---- food crumbs, in the camera's frame (placement before CrumbOffsetScale; speeds in blocks per tick)
        private const float CrumbAhead = 0.6f;
        private const float CrumbHalfWidth = 0.15f;
        private const float CrumbDropNear = 0.3f;
        private const float CrumbDropFar = 0.9f;
        private const float CrumbSideDrift = 0.05f;
        private const float CrumbRiseMin = 0.1f;
        private const float CrumbRiseMax = 0.2f;
        private const int MaxBurst = 128;
        private const int DefaultBurst = 24;
        private const float FloorProbeHeight = 0.5f;
        private const float FloorProbeLength = 8f;
        private const float BoundsSize = 4000f;

        private static readonly Color32 DebrisTint = new Color32(153, 153, 153, 255);
        private static readonly Color32 CrumbTint = new Color32(255, 255, 255, 255);
        private static readonly Rect WholeTexture = new Rect(0f, 0f, 1f, 1f);
        private static readonly RaycastHit[] floorHits = new RaycastHit[16];

        /// <summary>One live piece of debris. Velocity is in m/s, times in seconds.</summary>
        private struct Speck
        {
            public Vector3 Position;
            public Vector3 Velocity;
            public Rect Window;
            public float Half;
            public float Age;
            public float Lifetime;
            public float FloorY;
            public Color32 Tint;
        }

        /// <summary>All pieces that share one material, plus the mesh that draws them.</summary>
        private sealed class Flock
        {
            public Material Material;
            public GameObject Holder;
            public MeshRenderer Renderer;
            public Mesh Mesh;
            public readonly Speck[] Specks = new Speck[PoolSize];
            public readonly Vector3[] Corners = new Vector3[PoolSize * 4];
            public readonly Vector3[] Normals = new Vector3[PoolSize * 4];
            public readonly Vector2[] Uvs = new Vector2[PoolSize * 4];
            public readonly Color32[] Colors = new Color32[PoolSize * 4];
            public int Count;
            /// <summary>How many quads the mesh held the last time it was uploaded.</summary>
            public int QuadsUploaded;
            /// <summary>Next slot to overwrite when the pool is full.</summary>
            public int Recycle;
            public bool Visible;
        }

        private readonly BlockWorld world;
        private readonly Dictionary<Material, Flock> flockByMaterial = new Dictionary<Material, Flock>();
        private readonly List<Flock> flocks = new List<Flock>();
        private readonly Dictionary<string, Material> itemMaterials = new Dictionary<string, Material>();
        private GameObject container;

        public BlockParticles(BlockWorld world)
        {
            this.world = world;
        }

        // =============================================================== spawning

        public void SpawnBreak(BlockDef def, byte facing, Vector3Int pos)
        {
            // The facing does not change which texture the debris shows.
            SpawnBreakAt(def, BlockWorld.Center(pos), 1f);
        }

        /// <summary>A burst of debris filling a cube of side <paramref name="extent"/> around <paramref name="center"/>.</summary>
        public void SpawnBreakAt(BlockDef def, Vector3 center, float extent)
        {
            int amount = Mathf.Clamp(BlocksConfig.BreakParticles?.Value ?? DefaultBurst, 0, MaxBurst);
            if (amount == 0) return;
            if (!TryBlockTexture(def, out Material mat, out Rect tile)) return;
            var flock = FlockFor(mat);
            if (flock == null) return;

            // Spawn points sit on the centres of a small lattice inside the cube; each piece picks a lattice cell at
            // random and flies away from the middle.
            int lattice = 2;
            while (lattice * lattice * lattice < amount) lattice++;
            float step = 1f / lattice;
            float floorY = SampleFloor(center);

            for (int i = 0; i < amount; i++)
            {
                var offset = new Vector3(
                    (Rng.R.Next(lattice) + 0.5f) * step - 0.5f,
                    (Rng.R.Next(lattice) + 0.5f) * step - 0.5f,
                    (Rng.R.Next(lattice) + 0.5f) * step - 0.5f);
                Add(flock, center + offset * extent, Toss(offset, 1f), PickWindow(tile), RandomHalfSize(),
                    DebrisTint, floorY);
            }
        }

        /// <summary>One chip just outside face <paramref name="face"/> (a <see cref="Dirs"/> index) of the block at <paramref name="pos"/>.</summary>
        public void SpawnHit(BlockDef def, Vector3Int pos, int face)
        {
            if (face < 0 || face >= Dirs.VecF.Length) return;
            Vector3 normal = Dirs.VecF[face];

            // Somewhere on the face, kept away from its edges, 0.1 m in front of the face plane.
            var local = new Vector3(Rng.Range(0.1f, 0.9f), Rng.Range(0.1f, 0.9f), Rng.Range(0.1f, 0.9f));
            if (normal.x != 0f) local.x = normal.x > 0f ? 1.1f : -0.1f;
            else if (normal.y != 0f) local.y = normal.y > 0f ? 1.1f : -0.1f;
            else local.z = normal.z > 0f ? 1.1f : -0.1f;

            SpawnHitAt(def, new Vector3(pos.x, pos.y, pos.z) + local, normal);
        }

        /// <summary>One chip at an arbitrary surface point (also used for Slime Rancher terrain).</summary>
        public void SpawnHitAt(BlockDef def, Vector3 point, Vector3 normal)
        {
            if (!TryBlockTexture(def, out Material mat, out Rect tile)) return;
            var flock = FlockFor(mat);
            if (flock == null) return;
            float floorY = SampleFloor(point);
            // No base direction: the chip just pops up a little and drops back near the face.
            Add(flock, point + normal * HitChipLift, Toss(Vector3.zero, HitChipPower), PickWindow(tile),
                RandomHalfSize() * HitChipScale, DebrisTint, floorY);
        }

        /// <summary>Crumbs of the item's texture puffing out in front of the player's mouth.</summary>
        public void SpawnItemCrumbs(ItemDef item, int count)
        {
            if (item == null || count <= 0) return;
            var sr = SC.SR;
            if (sr == null) return;
            var flock = FlockFor(MaterialForItem(item));
            if (flock == null) return;

            var mouth = MouthFrame.In(sr.EyePosition, sr.LookDirection);
            for (int i = 0; i < count; i++)
            {
                Vector3 position = mouth.Spot(Rng.Range(-CrumbHalfWidth, CrumbHalfWidth),
                    Rng.Range(CrumbDropNear, CrumbDropFar));
                Vector3 velocity = mouth.Puff(Rng.Range(-CrumbSideDrift, CrumbSideDrift),
                    Rng.Range(CrumbRiseMin, CrumbRiseMax));
                // No floor sample: crumbs only settle on our voxel blocks.
                Add(flock, position, velocity, PickWindow(WholeTexture), RandomHalfSize(), CrumbTint,
                    float.NegativeInfinity);
            }
        }

        /// <summary>
        /// Camera-aligned axes hanging in front of the player's face, used to scatter eating crumbs. Placement is
        /// given as a sideways offset and a drop below the line of sight (both shrunk by <see cref="CrumbOffsetScale"/>, at a
        /// fixed distance ahead); launch speed as a sideways drift and a rise, in blocks per tick.
        /// </summary>
        private struct MouthFrame
        {
            private Vector3 centre;
            private Vector3 side;
            private Vector3 lift;

            public static MouthFrame In(Vector3 eye, Vector3 look)
            {
                Quaternion view = look.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(look, Vector3.up) : Quaternion.identity;
                return new MouthFrame
                {
                    centre = eye + view * Vector3.forward * (CrumbAhead * CrumbOffsetScale),
                    side = view * Vector3.right,
                    lift = view * Vector3.up
                };
            }

            public Vector3 Spot(float across, float below) => centre + (side * across - lift * below) * CrumbOffsetScale;

            public Vector3 Puff(float across, float rise) => (side * across + lift * rise) * TickRate;
        }

        /// <summary>Removes every live piece (world unload). Flocks, meshes and cached materials are kept.</summary>
        public void Clear()
        {
            for (int i = 0; i < flocks.Count; i++)
            {
                var f = flocks[i];
                f.Count = 0;
                if (f.Renderer != null) f.Renderer.enabled = false;
                f.Visible = false;
            }
        }

        // =============================================================== spawn helpers

        /// <summary>
        /// Launch velocity in m/s: a jittered direction (base + up to ±0.4 per axis), a random speed of 0.06..0.18
        /// blocks per tick (most often around 0.12) scaled by <paramref name="power"/>, plus a fixed upward kick.
        /// </summary>
        private static Vector3 Toss(Vector3 bias, float power)
        {
            var dir = new Vector3(bias.x + Rng.Range(-0.4f, 0.4f), bias.y + Rng.Range(-0.4f, 0.4f), bias.z + Rng.Range(-0.4f, 0.4f));
            float length = dir.magnitude;
            dir = length >= 1e-4f ? dir / length : Vector3.up;
            float speed = 0.06f * (1f + Rng.F() + Rng.F()) * power;
            return new Vector3(dir.x * speed, dir.y * speed + UpwardKick, dir.z * speed) * TickRate;
        }

        /// <summary>A quarter-by-quarter window of <paramref name="tile"/> starting anywhere in its first three quarters.</summary>
        private static Rect PickWindow(Rect tile)
        {
            float w = tile.width * 0.25f, h = tile.height * 0.25f;
            return new Rect(tile.x + w * Rng.F() * 3f, tile.y + h * Rng.F() * 3f, w, h);
        }

        /// <summary>Distance from the quad centre to its edge: 0.05..0.10 m.</summary>
        private static float RandomHalfSize() => 0.05f + 0.05f * Rng.F();

        /// <summary>About 0.2 s to 2 s, most pieces short-lived.</summary>
        private static float RandomLifetime() => 4f / (0.1f + 0.9f * Rng.F()) / TickRate;

        private void Add(Flock flock, Vector3 position, Vector3 velocity, Rect window, float half, Color32 tint, float floorY)
        {
            int slot;
            if (flock.Count < PoolSize)
            {
                slot = flock.Count++;
            }
            else
            {
                // Pool full: overwrite an existing piece instead of growing.
                slot = flock.Recycle;
                flock.Recycle = (flock.Recycle + 1) % PoolSize;
            }
            flock.Specks[slot] = new Speck
            {
                Position = position,
                Velocity = velocity,
                Window = window,
                Half = half,
                Age = 0f,
                Lifetime = RandomLifetime(),
                FloorY = floorY,
                Tint = tint
            };
        }

        /// <summary>
        /// The atlas material and the face texture that block debris shows. Blocks with a composited overlay on
        /// their side (grass) crumble with their bottom texture; front-facing blocks (furnace...) use their back so
        /// the front never shows; everything else uses its plain side.
        /// </summary>
        private bool TryBlockTexture(BlockDef def, out Material mat, out Rect tile)
        {
            mat = null;
            tile = default(Rect);
            if (def == null) return false;
            mat = world.CutoutMaterial;
            if (mat == null) return false;

            int slot;
            var faces = def.Faces;
            bool overlaySide = faces != null && faces.Length > 2 && faces[Dirs.North] != null
                               && faces[Dirs.North].IndexOf('|') >= 0;
            if (overlaySide) slot = Dirs.Down;
            else if (def.Rotation == BlockRotation.Horizontal) slot = Dirs.South;
            else slot = Dirs.North;

            return world.TryGetFaceUV(def, slot, out tile);
        }

        /// <summary>
        /// Lit cutout material for an item's own texture, cached per item id. A missing result is looked up again on
        /// the next call, so a texture that was not loaded yet is picked up later.
        /// </summary>
        private Material MaterialForItem(ItemDef item)
        {
            string key = item.Id ?? "";
            if (itemMaterials.TryGetValue(key, out Material known) && known != null) return known;

            Material found = null;
            if (item.IsBlock)
            {
                found = world.CutoutMaterial;
            }
            else if (!string.IsNullOrEmpty(item.Texture))
            {
                var assets = SC.Assets;
                if (assets != null && assets.Ready)
                {
                    var tex = assets.GetTexture(item.Texture);
                    var visuals = SC.ItemVisuals;
                    if (tex != null && visuals != null) found = visuals.CreateLitMaterial(tex, true);
                }
            }
            itemMaterials[key] = found;
            return found;
        }

        /// <summary>
        /// Height of the Slime Rancher ground under <paramref name="at"/> (static geometry only), or negative infinity
        /// when there is none. Sampled once per burst or chip, never per frame.
        /// </summary>
        private float SampleFloor(Vector3 at)
        {
            var sr = SC.SR;
            if (sr == null) return float.NegativeInfinity;

            int hitCount = Physics.RaycastNonAlloc(at + Vector3.up * FloorProbeHeight, Vector3.down, floorHits,
                FloorProbeLength, sr.WorldMask, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            float floorY = float.NegativeInfinity;
            for (int i = 0; i < hitCount; i++)
            {
                var col = floorHits[i].collider;
                // Our own blocks are handled by the voxel grid; anything with a rigidbody can move away.
                if (col == null || world.IsBlockCollider(col) || col.attachedRigidbody != null) continue;
                if (floorHits[i].distance < nearest)
                {
                    nearest = floorHits[i].distance;
                    floorY = floorHits[i].point.y;
                }
            }
            return floorY;
        }

        // =============================================================== flocks

        private Flock FlockFor(Material mat)
        {
            if (mat == null) return null;
            if (flockByMaterial.TryGetValue(mat, out Flock existing))
            {
                if (existing.Holder != null) return existing;
                // Its GameObject was destroyed (scene change): start over with a fresh one.
                flockByMaterial.Remove(mat);
                flocks.Remove(existing);
                if (existing.Mesh != null) Object.Destroy(existing.Mesh);
            }
            var created = BuildFlock(mat);
            flockByMaterial[mat] = created;
            flocks.Add(created);
            return created;
        }

        private Flock BuildFlock(Material mat)
        {
            if (container == null)
            {
                container = new GameObject("SlimeCraft_BlockParticles");
                Object.DontDestroyOnLoad(container);
            }

            var flock = new Flock { Material = mat };
            var go = new GameObject("particles_" + mat.name);
            go.transform.SetParent(container.transform, false);
            go.layer = world.RenderLayer;
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = false;

            // Two triangles per quad, corners stored as top-left, bottom-left, bottom-right, top-right. This winding
            // is clockwise seen from the camera, which Unity treats as the front face. Built once, never changed.
            var indices = new int[PoolSize * 6];
            for (int q = 0; q < PoolSize; q++)
            {
                int v = q * 4, t = q * 6;
                indices[t] = v;
                indices[t + 1] = v + 3;
                indices[t + 2] = v + 2;
                indices[t + 3] = v;
                indices[t + 4] = v + 2;
                indices[t + 5] = v + 1;
            }

            var mesh = new Mesh { name = "SC_particles" };
            mesh.MarkDynamic();
            mesh.vertices = flock.Corners;
            mesh.normals = flock.Normals;
            mesh.uv = flock.Uvs;
            mesh.colors32 = flock.Colors;
            mesh.triangles = indices;
            filter.sharedMesh = mesh;

            flock.Holder = go;
            flock.Renderer = renderer;
            flock.Mesh = mesh;
            return flock;
        }

        // =============================================================== per frame

        public void LateUpdate()
        {
            if (flocks.Count == 0) return;
            var cam = CamUtil.Main;
            if (cam == null) return;

            float dt = Time.deltaTime;
            float ticks = dt * TickRate;
            float airKeep = Mathf.Pow(DragPerTick, ticks);
            float groundKeep = Mathf.Pow(GroundGripPerTick, ticks);
            bool anyBlocks = world.Count > 0;

            var camTransform = cam.transform;
            Vector3 right = camTransform.right;
            Vector3 up = camTransform.up;
            Vector3 towardCamera = -camTransform.forward;
            Vector3 camPos = camTransform.position;

            for (int i = 0; i < flocks.Count; i++)
            {
                var flock = flocks[i];
                if (flock.Holder == null) continue;
                if (flock.Count == 0 && !flock.Visible) continue; // idle
                Simulate(flock, dt, airKeep, groundKeep, anyBlocks);
                Upload(flock, right, up, towardCamera, camPos);
            }
        }

        private void Simulate(Flock flock, float dt, float airKeep, float groundKeep, bool anyBlocks)
        {
            var specks = flock.Specks;
            int live = flock.Count;
            int i = 0;
            while (i < live)
            {
                ref Speck s = ref specks[i];
                s.Age += dt;
                if (s.Age >= s.Lifetime)
                {
                    // Expired: move the last live piece into this slot and look at it next.
                    live--;
                    specks[i] = specks[live];
                    continue;
                }

                Vector3 vel = s.Velocity;
                vel.y -= FallAcceleration * dt;
                vel *= airKeep;

                Vector3 from = s.Position;
                Vector3 to = from + vel * dt;
                bool resting = false;

                if (anyBlocks && world.IsSolid(Vector3Int.FloorToInt(to)))
                {
                    // Settle against our blocks one axis at a time: vertical, then x, then z. Each test uses the
                    // axes already settled and the old position on the axes still to come.
                    if (world.IsSolid(Vector3Int.FloorToInt(new Vector3(from.x, to.y, from.z))))
                    {
                        if (vel.y < 0f) resting = true;
                        to.y = from.y;
                        vel.y = 0f;
                    }
                    if (world.IsSolid(Vector3Int.FloorToInt(new Vector3(to.x, to.y, from.z))))
                    {
                        to.x = from.x;
                        vel.x = 0f;
                    }
                    if (world.IsSolid(Vector3Int.FloorToInt(to)))
                    {
                        to.z = from.z;
                        vel.z = 0f;
                    }
                }

                // Slime Rancher ground sampled at spawn time (negative infinity = none).
                if (to.y - s.Half < s.FloorY)
                {
                    to.y = s.FloorY + s.Half;
                    if (vel.y < 0f) vel.y = 0f;
                    resting = true;
                }

                if (resting)
                {
                    vel.x *= groundKeep;
                    vel.z *= groundKeep;
                }

                s.Position = to;
                s.Velocity = vel;
                i++;
            }
            flock.Count = live;
        }

        private static void Upload(Flock flock, Vector3 right, Vector3 up, Vector3 towardCamera, Vector3 camPos)
        {
            int live = flock.Count;
            if (live == 0)
            {
                // Hide once; the flock is skipped until something spawns into it again.
                if (flock.Renderer != null) flock.Renderer.enabled = false;
                flock.Visible = false;
                return;
            }

            var corners = flock.Corners;
            var normals = flock.Normals;
            var uvs = flock.Uvs;
            var colors = flock.Colors;
            var specks = flock.Specks;

            for (int q = 0; q < live; q++)
            {
                ref Speck s = ref specks[q];
                Vector3 r = right * s.Half;
                Vector3 u = up * s.Half;
                Vector3 c = s.Position;
                Rect w = s.Window;
                int v = q * 4;

                corners[v] = c - r + u;     // top-left
                corners[v + 1] = c - r - u; // bottom-left
                corners[v + 2] = c + r - u; // bottom-right
                corners[v + 3] = c + r + u; // top-right

                uvs[v] = new Vector2(w.xMin, w.yMax);
                uvs[v + 1] = new Vector2(w.xMin, w.yMin);
                uvs[v + 2] = new Vector2(w.xMax, w.yMin);
                uvs[v + 3] = new Vector2(w.xMax, w.yMax);

                for (int k = 0; k < 4; k++)
                {
                    normals[v + k] = towardCamera;
                    colors[v + k] = s.Tint;
                }
            }

            // Quads that were drawn before but are no longer live shrink to nothing.
            for (int q = live; q < flock.QuadsUploaded; q++)
            {
                int v = q * 4;
                corners[v] = corners[v + 1] = corners[v + 2] = corners[v + 3] = Vector3.zero;
            }
            flock.QuadsUploaded = live;

            var mesh = flock.Mesh;
            mesh.vertices = corners;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.colors32 = colors;
            // Huge bounds around the camera so the flock is never culled.
            mesh.bounds = new Bounds(camPos, new Vector3(BoundsSize, BoundsSize, BoundsSize));

            if (!flock.Visible)
            {
                if (flock.Renderer != null) flock.Renderer.enabled = true;
                flock.Visible = true;
            }
        }
    }
}
