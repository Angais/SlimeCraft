using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>
    /// Eating crumbs: small camera-facing squares cut from the eaten item's texture pop out in front of the mouth,
    /// fall, slide a little on the ground and vanish after a short random life, matching Minecraft's eating-crumb
    /// look and physics. They live in world space (drawn by SR's main camera, no shadows), are simulated on the
    /// module's 20 Hz tick, interpolated per frame, and come from a fixed pool built on first use.
    /// Purely cosmetic.
    /// </summary>
    internal sealed class EatParticles
    {
        private const int PoolSize = 40;              // 8 bursts of 5
        private const float Gravity = 0.04f;          // units / tick^2
        private const float AirKeep = 0.98f;          // velocity kept per tick (all axes)
        private const float GroundKeep = 0.7f;        // extra horizontal velocity kept per tick on the ground
        private const float SurfaceGap = 0.02f;       // distance kept from a surface after a hit
        private const float FloorNormalY = 0.6f;      // a hit normal pointing up more than this counts as floor
        private const float MinMove = 1e-5f;

        /// <summary>Per-slot state plus the slot's scene objects.</summary>
        private sealed class Crumb
        {
            public Transform Tf;
            public MeshRenderer Renderer;
            public Mesh Mesh;

            public bool Alive;
            public int Age;
            public int Life;
            public bool OnGround;
            public Vector3 Pos;
            public Vector3 LastPos;
            public Vector3 Vel;

            public int LifeLeft { get { return Life - Age; } }
        }

        private readonly HeldItemVisuals visuals;
        private readonly System.Random rng = new System.Random();
        private readonly Dictionary<string, Material> materialByTexture = new Dictionary<string, Material>();
        private readonly Vector2[] uvScratch = new Vector2[4];

        private GameObject root;
        private Crumb[] slots;
        private int liveCount;

        public EatParticles(HeldItemVisuals visuals)
        {
            this.visuals = visuals;
        }

        private float Rand() { return (float)rng.NextDouble(); }

        // ===================================================================================================
        //  Spawning
        // ===================================================================================================

        /// <summary>
        /// Emits <paramref name="count"/> crumbs of <paramref name="def"/>'s texture in front of the eye.
        /// <paramref name="pitch"/> is positive when looking down, <paramref name="yaw"/> is the camera's euler Y.
        /// </summary>
        public void Spawn(ItemDef def, Vector3 eye, float pitch, float yaw, int count)
        {
            try
            {
                if (count <= 0) return;
                Material mat = MaterialFor(def);
                if (mat == null || !PoolReady()) return;

                Quaternion look = Quaternion.Euler(pitch, yaw, 0f);
                Vector3 side = look * Vector3.right;
                Vector3 up = look * Vector3.up;
                Vector3 ahead = look * Vector3.forward;

                for (int n = 0; n < count; n++)
                {
                    Crumb c = FreeSlot();
                    if (c == null) break;
                    Launch(c, mat, eye, look, side, up, ahead);
                }
            }
            catch (Exception e) { FpLog.Error("EatParticles.Spawn", e, 30f); }
        }

        private void Launch(Crumb c, Material mat, Vector3 eye, Quaternion look, Vector3 side, Vector3 up, Vector3 ahead)
        {
            // start around the chin: 0.6 ahead of the eye, 0.3..0.9 below it along the view's up axis
            Vector3 pos = eye + side * ((Rand() - 0.5f) * 0.3f) - up * (0.3f + 0.6f * Rand()) + ahead * 0.6f;

            // push up out of the mouth (in the look frame), a little world lift, and a faint random kick
            Vector3 vel = side * ((Rand() - 0.5f) * 0.1f) + up * (0.1f + 0.1f * Rand());
            vel.y += 0.05f;
            Vector3 kick = new Vector3(Rand() * 2f - 1f, Rand() * 2f - 1f, Rand() * 2f - 1f);
            float kickLen = kick.magnitude;
            kick = kickLen < MinMove ? Vector3.up : kick / kickLen;
            kick *= 0.06f * (1f + Rand() + Rand());
            kick.y += 0.1f;
            vel += kick * 0.1f;

            c.Pos = pos;
            c.LastPos = pos;
            c.Vel = vel;
            c.Age = 0;
            c.Life = (int)(4f / (0.1f + 0.9f * Rand())); // 4..40 ticks, mostly short
            c.OnGround = false;

            float edge = 0.2f * (0.5f + 0.5f * Rand());
            SetTexturePiece(c.Mesh, 3f * Rand(), 3f * Rand());

            if (c.Renderer.sharedMaterial != mat) c.Renderer.sharedMaterial = mat;
            c.Tf.SetPositionAndRotation(pos, look);
            c.Tf.localScale = new Vector3(edge, edge, edge);
            c.Renderer.enabled = true;
            c.Alive = true;
            liveCount++;
        }

        /// <summary>
        /// Shows a quarter-by-quarter window of the texture; <paramref name="col"/> and <paramref name="row"/> are
        /// continuous offsets in quarter-texture units (row counted from the top). The window is mirrored left/right.
        /// </summary>
        private void SetTexturePiece(Mesh mesh, float col, float row)
        {
            float uLeft = (col + 1f) * 0.25f, uRight = col * 0.25f;
            float vTop = 1f - row * 0.25f, vBottom = 1f - (row + 1f) * 0.25f;
            // vertex order of the quad: bottom-left, bottom-right, top-right, top-left
            uvScratch[0] = new Vector2(uLeft, vBottom);
            uvScratch[1] = new Vector2(uRight, vBottom);
            uvScratch[2] = new Vector2(uRight, vTop);
            uvScratch[3] = new Vector2(uLeft, vTop);
            mesh.uv = uvScratch;
        }

        /// <summary>A free slot, or (pool full) the live crumb closest to the end of its life, retired on the spot.</summary>
        private Crumb FreeSlot()
        {
            Crumb oldest = null;
            for (int i = 0; i < slots.Length; i++)
            {
                Crumb c = slots[i];
                if (c == null || c.Renderer == null || c.Tf == null || c.Mesh == null) continue;
                if (!c.Alive) return c;
                if (oldest == null || c.LifeLeft < oldest.LifeLeft) oldest = c;
            }
            if (oldest != null)
            {
                oldest.Alive = false;
                liveCount--;
            }
            return oldest;
        }

        /// <summary>The item's lit cutout material (shared, never modified or destroyed here), cached per texture path.</summary>
        private Material MaterialFor(ItemDef def)
        {
            if (def == null || string.IsNullOrEmpty(def.Texture)) return null;
            Material mat;
            if (materialByTexture.TryGetValue(def.Texture, out mat) && mat != null) return mat;

            var assets = SC.Assets;
            if (assets == null || !assets.Ready || visuals == null) return null;
            Texture2D tex = assets.GetTexture(def.Texture);
            if (tex == null) return null;
            mat = visuals.LitMaterial(tex);
            if (mat == null) return null;
            materialByTexture[def.Texture] = mat;
            return mat;
        }

        // ===================================================================================================
        //  Pool
        // ===================================================================================================

        /// <summary>Builds the pool on first use, and again if the scene took the old root with it.</summary>
        private bool PoolReady()
        {
            if (slots != null && root != null) return true;

            ReleaseMeshes();
            liveCount = 0;
            root = new GameObject("SlimeCraft_EatParticles");
            slots = new Crumb[PoolSize];
            for (int i = 0; i < PoolSize; i++) slots[i] = CreateSlot(root.transform);
            return true;
        }

        private static Crumb CreateSlot(Transform parent)
        {
            var go = new GameObject("Crumb");
            go.transform.SetParent(parent, false);
            var filter = go.AddComponent<MeshFilter>();
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.enabled = false;

            Mesh mesh = CreateQuad();
            filter.sharedMesh = mesh;
            return new Crumb { Tf = go.transform, Renderer = renderer, Mesh = mesh };
        }

        /// <summary>Unit square in the XY plane, visible from -Z (the camera side once rotated like the camera).</summary>
        private static Mesh CreateQuad()
        {
            var mesh = new Mesh { name = "SlimeCraft_Crumb" };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            };
            Vector3 towardCamera = new Vector3(0f, 0f, -1f);
            mesh.normals = new[] { towardCamera, towardCamera, towardCamera, towardCamera };
            mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
            // clockwise as seen from -Z, so the front faces the camera
            mesh.triangles = new[] { 0, 3, 2, 0, 2, 1 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private void ReleaseMeshes()
        {
            if (slots == null) return;
            for (int i = 0; i < slots.Length; i++)
            {
                Crumb c = slots[i];
                if (c != null && c.Mesh != null) UnityEngine.Object.Destroy(c.Mesh);
            }
            slots = null;
        }

        // ===================================================================================================
        //  20 Hz simulation
        // ===================================================================================================

        public void Tick()
        {
            if (liveCount <= 0 || slots == null) return;
            try
            {
                ISRBridge world = SC.SR;
                for (int i = 0; i < slots.Length; i++)
                {
                    Crumb c = slots[i];
                    if (c == null || !c.Alive) continue;

                    c.LastPos = c.Pos;
                    if (c.Age >= c.Life)
                    {
                        Retire(c);
                        continue;
                    }
                    c.Age++;
                    c.Vel.y -= Gravity;
                    MoveWithCollision(c, world);
                    c.Vel *= AirKeep;
                    if (c.OnGround)
                    {
                        c.Vel.x *= GroundKeep;
                        c.Vel.z *= GroundKeep;
                    }
                }
            }
            catch (Exception e) { FpLog.Error("EatParticles.Tick", e, 30f); }
        }

        /// <summary>
        /// Moves by the velocity. On a hit the crumb rests just off the surface and keeps only the velocity along it;
        /// a floor-like hit also stops it vertically and marks it grounded for this tick.
        /// </summary>
        private static void MoveWithCollision(Crumb c, ISRBridge world)
        {
            c.OnGround = false;
            Vector3 step = c.Vel;
            float dist = step.magnitude;
            RaycastHit hit;
            if (world != null && dist >= MinMove && world.RaycastWorld(new Ray(c.Pos, step / dist), dist, out hit))
            {
                Vector3 n = hit.normal;
                c.Pos = hit.point + n * SurfaceGap;
                c.Vel -= n * Vector3.Dot(c.Vel, n);
                if (n.y > FloorNormalY)
                {
                    c.OnGround = true;
                    c.Vel.y = 0f;
                }
                return;
            }
            c.Pos += step;
        }

        private void Retire(Crumb c)
        {
            c.Alive = false;
            liveCount--;
            if (c.Renderer != null) c.Renderer.enabled = false;
        }

        // ===================================================================================================
        //  Per frame
        // ===================================================================================================

        /// <summary>Places every live crumb between its last two tick positions and turns it to face the camera.</summary>
        public void Frame(float partial, Transform cam)
        {
            if (liveCount <= 0 || slots == null || cam == null) return;
            try
            {
                Quaternion facing = cam.rotation;
                for (int i = 0; i < slots.Length; i++)
                {
                    Crumb c = slots[i];
                    if (c == null || !c.Alive) continue;
                    if (c.Tf == null)
                    {
                        // its object vanished with the scene; free the slot so counting stays right
                        c.Alive = false;
                        liveCount--;
                        continue;
                    }
                    c.Tf.SetPositionAndRotation(Vector3.LerpUnclamped(c.LastPos, c.Pos, partial), facing);
                }
            }
            catch (Exception e) { FpLog.Error("EatParticles.Frame", e, 30f); }
        }

        // ===================================================================================================
        //  Lifecycle
        // ===================================================================================================

        /// <summary>Hides every crumb; the pool objects are kept for reuse.</summary>
        public void Clear()
        {
            if (slots != null)
            {
                for (int i = 0; i < slots.Length; i++)
                {
                    Crumb c = slots[i];
                    if (c == null) continue;
                    c.Alive = false;
                    if (c.Renderer != null) c.Renderer.enabled = false;
                }
            }
            liveCount = 0;
        }

        /// <summary>World unload: hides everything and destroys the pool (meshes and objects; materials are shared and kept).</summary>
        public void Destroy()
        {
            try
            {
                Clear();
                ReleaseMeshes();
                if (root != null) UnityEngine.Object.Destroy(root);
            }
            catch (Exception e) { FpLog.Error("EatParticles.Destroy", e, 60f); }
            finally
            {
                slots = null;
                root = null;
                materialByTexture.Clear();
            }
        }
    }
}
