using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SlimeCraft.BlocksMod
{
    /// <summary>Runtime palette entry: a BlockDef plus everything the mesher needs, cached.</summary>
    internal sealed class PaletteEntry
    {
        public string Id;
        public BlockDef Def;
        public RenderLayer Layer;
        public bool Opaque;
        /// <summary>Light-emitting (BlockDef.Light &gt; 0) and not translucent: meshed into the full-bright renderer.</summary>
        public bool Emissive;
        public PhysGroup Phys;
        public readonly Rect[] Uv = new Rect[6];
        public readonly bool[] UvOk = new bool[6];
    }

    /// <summary>
    /// The voxel block layer (SC.Blocks). Chunked storage (16^3, Dictionary keyed by packed chunk coords),
    /// palette of BlockDefs, meshing/collider rebuild queue, DDA raycast, break/place with sounds and particles.
    /// </summary>
    internal sealed class BlockWorld : IBlockWorld
    {
        private readonly Dictionary<long, Chunk> chunks = new Dictionary<long, Chunk>();
        private readonly List<PaletteEntry> palette = new List<PaletteEntry>();
        private readonly Dictionary<string, ushort> paletteIndex = new Dictionary<string, ushort>();
        private readonly HashSet<long> dirty = new HashSet<long>();
        private readonly HashSet<int> colliderIds = new HashSet<int>();
        private readonly ChunkMesher mesher = new ChunkMesher();
        private readonly List<int> tmpTris = new List<int>(6144);
        private readonly List<Chunk> tmpChunks = new List<Chunk>(64);
        private int count;
        private int fxFrame = -1, fxCount;
        private const int MaxBreakFxPerFrame = 6;

        // Unity side
        private GameObject root;
        private int renderLayer = 0;
        private Texture atlasTex;
        private Material matCutout, matTrans, matEmis;
        private Material srcEmis; // what the atlas returned for Emissive (may be null → cutout fallback)
        private bool shadeCutout, shadeTrans, shadeEmis;
        private static PhysicMaterial[] physMats;

        // Collaborators assigned by the module
        public BlockLights Lights;
        public BlockParticles Particles;

        public event Action<Vector3Int, string, string> BlockChanged;

        public BlockWorld()
        {
            palette.Add(new PaletteEntry { Id = null, Def = null, Layer = SlimeCraft.RenderLayer.Opaque, Opaque = false });
        }

        public List<PaletteEntry> Palette => palette;
        public IEnumerable<Chunk> Chunks => chunks.Values;
        public int Count => count;

        // ------------------------------------------------------------------ keys & lookup
        public static long ChunkKey(int cx, int cy, int cz) =>
            ((long)(cx & 0x1FFFFF) << 42) | ((long)(cy & 0x1FFFFF) << 21) | (long)(cz & 0x1FFFFF);

        public Chunk GetChunkAt(int cx, int cy, int cz)
        {
            chunks.TryGetValue(ChunkKey(cx, cy, cz), out var c);
            return c;
        }

        private Chunk GetOrCreateChunk(int cx, int cy, int cz)
        {
            long k = ChunkKey(cx, cy, cz);
            if (!chunks.TryGetValue(k, out var c))
            {
                c = new Chunk(cx, cy, cz);
                chunks[k] = c;
            }
            return c;
        }

        public ushort GetIndex(int x, int y, int z)
        {
            if (!chunks.TryGetValue(ChunkKey(x >> 4, y >> 4, z >> 4), out var c)) return 0;
            return c.Ids[Chunk.Index(x & 15, y & 15, z & 15)];
        }

        public bool IsSolid(int x, int y, int z) => GetIndex(x, y, z) != 0;
        public bool IsSolid(Vector3Int p) => GetIndex(p.x, p.y, p.z) != 0;

        public BlockDef GetDef(int x, int y, int z) => palette[GetIndex(x, y, z)].Def;
        public BlockDef GetDef(Vector3Int p) => palette[GetIndex(p.x, p.y, p.z)].Def;

        public ushort PaletteOf(BlockDef def)
        {
            if (def == null) return 0;
            if (paletteIndex.TryGetValue(def.Id, out var idx)) return idx;
            var pe = new PaletteEntry
            {
                Id = def.Id,
                Def = def,
                Layer = def.Layer,
                Opaque = def.Layer == SlimeCraft.RenderLayer.Opaque,
                Emissive = def.Light > 0 && def.Layer != SlimeCraft.RenderLayer.Translucent,
                Phys = def.Bouncy ? PhysGroup.Bouncy : def.Sticky ? PhysGroup.Sticky : def.Slipperiness > 0.9f ? PhysGroup.Slippery : PhysGroup.Normal
            };
            idx = (ushort)palette.Count;
            palette.Add(pe);
            paletteIndex[def.Id] = idx;
            ResolveUVs(pe);
            return idx;
        }

        // ------------------------------------------------------------------ IBlockWorld
        public string GetBlock(Vector3Int pos) => palette[GetIndex(pos.x, pos.y, pos.z)].Id;

        public BlockFacing GetFacing(Vector3Int pos)
        {
            if (!chunks.TryGetValue(ChunkKey(pos.x >> 4, pos.y >> 4, pos.z >> 4), out var c)) return BlockFacing.North;
            return (BlockFacing)c.Facing[Chunk.Index(pos.x & 15, pos.y & 15, pos.z & 15)];
        }

        public bool SetBlock(Vector3Int pos, string blockId, BlockFacing facing = BlockFacing.North, bool playSound = false)
        {
            BlockDef def = null;
            if (blockId != null)
            {
                def = Content.Block(blockId);
                if (def == null) return false;
            }
            ushort idx = PaletteOf(def);
            var c = idx == 0 ? GetChunkAt(pos.x >> 4, pos.y >> 4, pos.z >> 4) : GetOrCreateChunk(pos.x >> 4, pos.y >> 4, pos.z >> 4);
            if (c == null) return true; // air into nothing
            int li = Chunk.Index(pos.x & 15, pos.y & 15, pos.z & 15);
            ushort old = c.Ids[li];
            byte f = idx == 0 ? (byte)0 : (byte)facing;
            if (old == idx && c.Facing[li] == f) return true;

            c.Ids[li] = idx;
            c.Facing[li] = f;
            if (old == 0 && idx != 0) { c.Count++; count++; }
            else if (old != 0 && idx == 0) { c.Count--; count--; }

            var oldDef = palette[old].Def;
            if (Lights != null)
            {
                if (oldDef != null && oldDef.Light > 0) Lights.Remove(pos);
                if (def != null && def.Light > 0) Lights.Add(pos, def.Light);
            }
            MarkDirty(pos);
            if (playSound && def != null) BlockSounds.Place(def, Center(pos));

            var h = BlockChanged;
            if (h != null)
            {
                try { h(pos, palette[old].Id, def?.Id); }
                catch (Exception e) { RateLog.Error("BlockChanged handler", e); }
            }
            return true;
        }

        public bool BreakBlock(Vector3Int pos, bool drop)
        {
            var def = GetDef(pos);
            if (def == null) return false;
            var facing = GetFacing(pos);
            if (!SetBlock(pos, null)) return false;
            var center = Center(pos);
            // Explosions may break dozens of blocks in one frame: cap the per-frame sound/particle effects.
            if (Time.frameCount != fxFrame) { fxFrame = Time.frameCount; fxCount = 0; }
            if (fxCount++ < MaxBreakFxPerFrame)
            {
                BlockSounds.Break(def, center);
                try { Particles?.SpawnBreak(def, (byte)facing, pos); } catch (Exception e) { RateLog.Error("break particles", e); }
            }
            if (drop)
            {
                try { BlockLoot.Drop(def, pos); } catch (Exception e) { RateLog.Error("block drops", e); }
            }
            return true;
        }

        public bool IsBlockCollider(Collider c) => c != null && colliderIds.Contains(c.GetInstanceID());

        public static Vector3 Center(Vector3Int p) => new Vector3(p.x + 0.5f, p.y + 0.5f, p.z + 0.5f);

        /// <summary>Voxel DDA (Amanatides & Woo) through our block grid.</summary>
        public bool Raycast(Ray ray, float maxDistance, out BlockHit hit)
        {
            hit = default(BlockHit);
            if (count == 0 || maxDistance <= 0f) return false;
            Vector3 o = ray.origin;
            Vector3 d = ray.direction;
            if (d.sqrMagnitude < 1e-12f) return false;
            d.Normalize();

            int x = Mathf.FloorToInt(o.x), y = Mathf.FloorToInt(o.y), z = Mathf.FloorToInt(o.z);
            if (IsSolid(x, y, z))
            {
                int dom = Dirs.Dominant(-d);
                hit.Pos = new Vector3Int(x, y, z);
                hit.Normal = Dirs.Vec[dom];
                hit.Point = o;
                hit.Distance = 0f;
                return true;
            }

            int sx = d.x > 0 ? 1 : (d.x < 0 ? -1 : 0);
            int sy = d.y > 0 ? 1 : (d.y < 0 ? -1 : 0);
            int sz = d.z > 0 ? 1 : (d.z < 0 ? -1 : 0);
            float tdx = sx != 0 ? Mathf.Abs(1f / d.x) : float.PositiveInfinity;
            float tdy = sy != 0 ? Mathf.Abs(1f / d.y) : float.PositiveInfinity;
            float tdz = sz != 0 ? Mathf.Abs(1f / d.z) : float.PositiveInfinity;
            float tmx = sx > 0 ? (x + 1 - o.x) * tdx : sx < 0 ? (o.x - x) * tdx : float.PositiveInfinity;
            float tmy = sy > 0 ? (y + 1 - o.y) * tdy : sy < 0 ? (o.y - y) * tdy : float.PositiveInfinity;
            float tmz = sz > 0 ? (z + 1 - o.z) * tdz : sz < 0 ? (o.z - z) * tdz : float.PositiveInfinity;

            int guard = 0;
            while (guard++ < 4096)
            {
                float t;
                Vector3Int n;
                if (tmx < tmy && tmx < tmz) { x += sx; t = tmx; tmx += tdx; n = new Vector3Int(-sx, 0, 0); }
                else if (tmy < tmz) { y += sy; t = tmy; tmy += tdy; n = new Vector3Int(0, -sy, 0); }
                else { z += sz; t = tmz; tmz += tdz; n = new Vector3Int(0, 0, -sz); }
                if (t > maxDistance) return false;
                if (IsSolid(x, y, z))
                {
                    hit.Pos = new Vector3Int(x, y, z);
                    hit.Normal = n;
                    hit.Point = o + d * t;
                    hit.Distance = t;
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ bulk ops (persistence / unload)
        public void Clear()
        {
            foreach (var c in chunks.Values) c.DestroyObjects(colliderIds);
            chunks.Clear();
            dirty.Clear();
            colliderIds.Clear();
            count = 0;
            buildLogPending = false;
            Lights?.Clear();
        }

        /// <summary>Raw write used by the loader (no events, sounds or lights; call <see cref="FinishBulkLoad"/> after).</summary>
        public void SetRaw(int x, int y, int z, ushort idx, byte facing)
        {
            if (idx == 0 || idx >= palette.Count) return;
            var c = GetOrCreateChunk(x >> 4, y >> 4, z >> 4);
            int li = Chunk.Index(x & 15, y & 15, z & 15);
            if (c.Ids[li] == 0) c.Count++;
            c.Ids[li] = idx;
            c.Facing[li] = facing;
        }

        public void FinishBulkLoad()
        {
            count = 0;
            buildLogPending = true;
            buildLogStart = Time.realtimeSinceStartup;
            Lights?.Clear();
            foreach (var c in chunks.Values)
            {
                count += c.Count;
                c.RenderDirty = c.ColliderDirty = true;
                dirty.Add(c.Key);
                if (Lights == null) continue;
                for (int i = 0; i < Chunk.Volume; i++)
                {
                    ushort id = c.Ids[i];
                    if (id == 0) continue;
                    var def = palette[id].Def;
                    if (def != null && def.Light > 0)
                        Lights.Add(new Vector3Int(c.Cx * 16 + (i & 15), c.Cy * 16 + (i >> 8), c.Cz * 16 + ((i >> 4) & 15)), def.Light);
                }
            }
        }

        // ------------------------------------------------------------------ dirty tracking & rebuild
        private void MarkChunkDirty(Chunk c)
        {
            if (c == null) return;
            c.RenderDirty = true;
            c.ColliderDirty = true;
            dirty.Add(c.Key);
        }

        private void MarkDirty(Vector3Int p)
        {
            int cx = p.x >> 4, cy = p.y >> 4, cz = p.z >> 4;
            MarkChunkDirty(GetChunkAt(cx, cy, cz));
            int lx = p.x & 15, ly = p.y & 15, lz = p.z & 15;
            if (lx == 0) MarkChunkDirty(GetChunkAt(cx - 1, cy, cz));
            if (lx == 15) MarkChunkDirty(GetChunkAt(cx + 1, cy, cz));
            if (ly == 0) MarkChunkDirty(GetChunkAt(cx, cy - 1, cz));
            if (ly == 15) MarkChunkDirty(GetChunkAt(cx, cy + 1, cz));
            if (lz == 0) MarkChunkDirty(GetChunkAt(cx, cy, cz - 1));
            if (lz == 15) MarkChunkDirty(GetChunkAt(cx, cy, cz + 1));
        }

        public void MarkAllDirty()
        {
            foreach (var c in chunks.Values) MarkChunkDirty(c);
        }

        private bool AtlasReady
        {
            get
            {
                var a = SC.Assets;
                return a != null && a.Ready && a.BlockAtlas != null && a.BlockAtlas.Texture != null;
            }
        }

        /// <summary>Immediately rebuilds the chunk holding p (and dirty neighbours) — used for the player's own edits.</summary>
        public void RebuildAround(Vector3Int p)
        {
            if (!CanBuildObjects()) return;
            int cx = p.x >> 4, cy = p.y >> 4, cz = p.z >> 4;
            tmpChunks.Clear();
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (Mathf.Abs(dx) + Mathf.Abs(dy) + Mathf.Abs(dz) > 1) continue;
                long k = ChunkKey(cx + dx, cy + dy, cz + dz);
                if (dirty.Contains(k) && chunks.TryGetValue(k, out var c)) tmpChunks.Add(c);
            }
            // The chunk may have just been emptied and removed from the dictionary
            foreach (var c in tmpChunks) Rebuild(c);
        }

        private bool CanBuildObjects()
        {
            var sr = SC.SR;
            return sr != null && sr.InGame;
        }

        /// <summary>Per-frame: rebuild dirty chunks nearest to the camera within budget, re-sort translucent faces.</summary>
        public void Update()
        {
            if (!CanBuildObjects()) return;
            EnsureRoot();
            CheckAtlas();
            bool atlasOk = AtlasReady;
            var cam = CamUtil.Main;
            Vector3 camPos = cam != null ? cam.transform.position : (SC.SR != null ? SC.SR.EyePosition : Vector3.zero);

            if (dirty.Count > 0)
            {
                int budget = Mathf.Max(1, BlocksConfig.MeshBudgetPerFrame?.Value ?? 4);
                tmpChunks.Clear();
                // pick the 'budget' nearest dirty chunks (simple insertion selection)
                var keys = dirtyKeysBuffer;
                keys.Clear();
                foreach (var k in dirty) keys.Add(k);
                foreach (var k in keys)
                {
                    if (!chunks.TryGetValue(k, out var c)) { dirty.Remove(k); continue; }
                    if (!c.ColliderDirty && !(c.RenderDirty && atlasOk)) continue;
                    float dist = (c.Center - camPos).sqrMagnitude;
                    int pos = tmpChunks.Count;
                    while (pos > 0 && (tmpChunks[pos - 1].Center - camPos).sqrMagnitude > dist) pos--;
                    if (pos < budget)
                    {
                        tmpChunks.Insert(pos, c);
                        if (tmpChunks.Count > budget) tmpChunks.RemoveAt(tmpChunks.Count - 1);
                    }
                }
                for (int i = 0; i < tmpChunks.Count; i++) Rebuild(tmpChunks[i]);
            }
            if (buildLogPending && dirty.Count == 0) LogBuildSummary();

            // Back-to-front sorting of translucent faces for nearby chunks when the camera moved.
            int sorts = 0;
            foreach (var c in chunks.Values)
            {
                if (!c.HasTranslucent || c.TransMesh == null) continue;
                var local = camPos - c.Origin;
                if ((c.Center - camPos).sqrMagnitude > 96f * 96f) continue;
                if ((local - c.LastSortCam).sqrMagnitude < 0.25f) continue;
                ChunkMesher.SortTranslucent(c, local, tmpTris);
                if (++sorts >= 8) break;
            }
        }

        private readonly List<long> dirtyKeysBuffer = new List<long>(256);
        private bool buildLogPending;
        private float buildLogStart;

        /// <summary>One line after a (re)load finished building: shows in the log that chunks/colliders/lights came back.</summary>
        private void LogBuildSummary()
        {
            buildLogPending = false;
            int objs = 0, emis = 0, trans = 0;
            foreach (var c in chunks.Values)
            {
                if (c.Go == null) continue;
                objs++;
                if (c.EmisMR != null && c.EmisMR.enabled) emis++;
                if (c.TransMR != null && c.TransMR.enabled) trans++;
            }
            SC.Log?.LogInfo("[Blocks] world built: " + count + " blocks in " + chunks.Count + " chunks (" + objs + " with objects, "
                + colliderIds.Count + " colliders, " + emis + " with glowing faces, " + trans + " with translucent faces), "
                + (Lights != null ? Lights.EmitterCount : 0) + " light emitters, in "
                + RateLog.F(Time.realtimeSinceStartup - buildLogStart) + " s");
        }

        /// <summary>Counts for the unload log.</summary>
        public int ChunkCount => chunks.Count;

        private void Rebuild(Chunk c)
        {
            try
            {
                if (c.Count <= 0)
                {
                    c.DestroyObjects(colliderIds);
                    chunks.Remove(c.Key);
                    dirty.Remove(c.Key);
                    return;
                }
                bool render = AtlasReady && c.RenderDirty;
                bool collide = c.ColliderDirty;
                if (!render && !collide) { dirty.Remove(c.Key); return; }
                mesher.Build(this, c, render, collide, shadeCutout, shadeTrans, shadeEmis);
                EnsureChunkObjects(c);
                if (render)
                {
                    ApplyRender(c.OpaqueMesh, mesher.Opaque, c.OpaqueMR, shadeCutout);
                    ApplyRender(c.TransMesh, mesher.Trans, c.TransMR, shadeTrans);
                    ApplyRender(c.EmisMesh, mesher.Emis, c.EmisMR, shadeEmis);
                    c.TransCenters.Clear();
                    c.TransCenters.AddRange(mesher.TransCenters);
                    c.HasTranslucent = c.TransCenters.Count > 0;
                    c.LastSortCam = new Vector3(float.MaxValue, 0, 0);
                    if (c.HasTranslucent)
                    {
                        var cam = CamUtil.Main;
                        if (cam != null) ChunkMesher.SortTranslucent(c, cam.transform.position - c.Origin, tmpTris);
                    }
                    c.RenderDirty = false;
                }
                if (collide)
                {
                    for (int g = 0; g < 4; g++) ApplyCollider(c, g, mesher.Cols[g]);
                    c.ColliderDirty = false;
                }
                if (!c.RenderDirty && !c.ColliderDirty) dirty.Remove(c.Key);
                c.FailCount = 0;
            }
            catch (Exception e)
            {
                RateLog.Error("chunk rebuild", e);
                if (++c.FailCount >= 3)
                {
                    c.RenderDirty = c.ColliderDirty = false; // give up on a chunk that keeps failing
                    dirty.Remove(c.Key);
                }
            }
        }

        private static void ApplyRender(Mesh m, MeshBuf b, MeshRenderer mr, bool colors)
        {
            m.Clear();
            if (b.V.Count == 0) { mr.enabled = false; return; }
            m.SetVertices(b.V);
            m.SetNormals(b.N);
            m.SetUVs(0, b.UV);
            if (colors && b.C.Count == b.V.Count) m.SetColors(b.C);
            m.SetTriangles(b.T, 0, true);
            mr.enabled = true;
        }

        private void ApplyCollider(Chunk c, int g, MeshBuf b)
        {
            if (b.V.Count == 0)
            {
                if (c.Col[g] != null) { c.Col[g].sharedMesh = null; c.Col[g].enabled = false; }
                return;
            }
            if (c.Col[g] == null)
            {
                var go = new GameObject("col_" + (PhysGroup)g);
                go.layer = BlockLayer;
                go.transform.SetParent(c.Go.transform, false);
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMaterial = PhysMat(g);
                c.ColGo[g] = go;
                c.Col[g] = mc;
                c.ColMesh[g] = new Mesh { name = "SC_chunk_col" };
                c.ColMesh[g].indexFormat = IndexFormat.UInt32;
                colliderIds.Add(mc.GetInstanceID());
            }
            var mesh = c.ColMesh[g];
            mesh.Clear();
            mesh.SetVertices(b.V);
            mesh.SetTriangles(b.T, 0, true);
            c.Col[g].sharedMesh = null;
            c.Col[g].sharedMesh = mesh;
            c.Col[g].enabled = true;
        }

        private static PhysicMaterial PhysMat(int g)
        {
            if (physMats == null)
            {
                physMats = new PhysicMaterial[4];
                physMats[1] = new PhysicMaterial("SC_SlimeBlock")
                {
                    bounciness = 1f, bounceCombine = PhysicMaterialCombine.Maximum,
                    dynamicFriction = 0.6f, staticFriction = 0.6f
                };
                physMats[2] = new PhysicMaterial("SC_Ice")
                {
                    dynamicFriction = 0.02f, staticFriction = 0.02f, frictionCombine = PhysicMaterialCombine.Minimum
                };
                physMats[3] = new PhysicMaterial("SC_Honey")
                {
                    dynamicFriction = 1f, staticFriction = 1f, frictionCombine = PhysicMaterialCombine.Maximum,
                    bounciness = 0f, bounceCombine = PhysicMaterialCombine.Minimum
                };
            }
            return physMats[g];
        }

        private int BlockLayer
        {
            get
            {
                int l = 0;
                try { l = SC.SR != null ? SC.SR.BlockLayer : 0; } catch { }
                return (l >= 0 && l < 32) ? l : 0;
            }
        }

        private void EnsureRoot()
        {
            if (root != null) return;
            if (!CanBuildObjects()) return;
            root = new GameObject("SlimeCraft_Blocks");
            UnityEngine.Object.DontDestroyOnLoad(root);
            // Renderers must be on a layer the SR camera draws: prefer the block layer, else Default.
            int bl = BlockLayer;
            var cam = CamUtil.Main;
            renderLayer = (cam != null && (cam.cullingMask & (1 << bl)) != 0) ? bl : 0;
            if (cam != null && (cam.cullingMask & (1 << renderLayer)) == 0)
            {
                for (int i = 0; i < 32; i++) if ((cam.cullingMask & (1 << i)) != 0) { renderLayer = i; break; }
            }
            // Old chunk objects (if root was destroyed by a scene change) must be rebuilt.
            foreach (var c in chunks.Values)
            {
                if (c.Go == null && HasStaleObjects(c)) c.DestroyObjects(colliderIds);
            }
            MarkAllDirty();
        }

        /// <summary>The chunk still holds references to Unity objects although its GameObject is gone.</summary>
        private static bool HasStaleObjects(Chunk c)
        {
            if (!ReferenceEquals(c.Go, null) || c.OpaqueMesh != null || c.TransMesh != null || c.EmisMesh != null) return true;
            for (int g = 0; g < 4; g++) if (!ReferenceEquals(c.Col[g], null) || c.ColMesh[g] != null) return true;
            return false;
        }

        private void EnsureChunkObjects(Chunk c)
        {
            if (c.Go != null) return;
            EnsureRoot();
            if (HasStaleObjects(c)) c.DestroyObjects(colliderIds); // stale (GO destroyed externally)
            c.Go = new GameObject("chunk_" + c.Cx + "_" + c.Cy + "_" + c.Cz);
            c.Go.layer = renderLayer;
            c.Go.transform.SetParent(root.transform, false);
            c.Go.transform.position = c.Origin;

            var o = new GameObject("opaque");
            o.layer = renderLayer;
            o.transform.SetParent(c.Go.transform, false);
            c.OpaqueMF = o.AddComponent<MeshFilter>();
            c.OpaqueMR = o.AddComponent<MeshRenderer>();
            c.OpaqueMesh = Chunk.NewMesh("SC_chunk_opaque");
            c.OpaqueMF.sharedMesh = c.OpaqueMesh;
            c.OpaqueMR.sharedMaterial = matCutout;
            c.OpaqueMR.shadowCastingMode = ShadowCastingMode.On;
            c.OpaqueMR.receiveShadows = true;
            c.OpaqueMR.enabled = false;

            var t = new GameObject("translucent");
            t.layer = renderLayer;
            t.transform.SetParent(c.Go.transform, false);
            c.TransMF = t.AddComponent<MeshFilter>();
            c.TransMR = t.AddComponent<MeshRenderer>();
            c.TransMesh = Chunk.NewMesh("SC_chunk_translucent");
            c.TransMF.sharedMesh = c.TransMesh;
            c.TransMR.sharedMaterial = matTrans;
            c.TransMR.shadowCastingMode = ShadowCastingMode.Off;
            c.TransMR.receiveShadows = true;
            c.TransMR.enabled = false;

            // Light-emitting blocks: full-bright (unlit) atlas material, like Minecraft's light level 15 blocks.
            var e = new GameObject("emissive");
            e.layer = renderLayer;
            e.transform.SetParent(c.Go.transform, false);
            c.EmisMF = e.AddComponent<MeshFilter>();
            c.EmisMR = e.AddComponent<MeshRenderer>();
            c.EmisMesh = Chunk.NewMesh("SC_chunk_emissive");
            c.EmisMF.sharedMesh = c.EmisMesh;
            c.EmisMR.sharedMaterial = matEmis;
            c.EmisMR.shadowCastingMode = ShadowCastingMode.On;
            c.EmisMR.receiveShadows = false;
            c.EmisMR.lightProbeUsage = LightProbeUsage.Off;
            c.EmisMR.reflectionProbeUsage = ReflectionProbeUsage.Off;
            c.EmisMR.enabled = false;
        }

        // ------------------------------------------------------------------ atlas / materials
        private void CheckAtlas()
        {
            if (!AtlasReady) return;
            var atlas = SC.Assets.BlockAtlas;
            var tex = atlas.Texture;
            var cut = atlas.Cutout;
            var trans = atlas.Translucent;
            Material emis = null;
            try { emis = atlas.Emissive; }
            catch (Exception e) { RateLog.Error("atlas Emissive", e, 60f); }
            if (tex == atlasTex && cut == matCutout && trans == srcTrans && emis == srcEmis) return;

            bool texChanged = tex != atlasTex;
            bool oldShadeC = shadeCutout, oldShadeT = shadeTrans, oldShadeE = shadeEmis;
            atlasTex = tex;
            matCutout = cut;
            srcTrans = trans;
            srcEmis = emis;
            matTrans = trans != null ? trans : cut;
            matEmis = emis != null ? emis : cut;
            shadeCutout = ShowsVertexColors(matCutout);
            shadeTrans = ShowsVertexColors(matTrans);
            shadeEmis = ShowsVertexColors(matEmis);
            SC.Log?.LogInfo("[Blocks] atlas materials: cutout=" + ShaderName(matCutout) + " translucent=" + ShaderName(matTrans)
                + " emissive=" + (emis != null ? ShaderName(emis) : "null (falls back to cutout: light blocks will not glow)")
                + " faceShading=" + shadeCutout + "/" + shadeTrans + "/" + shadeEmis);
            if (texChanged) foreach (var pe in palette) ResolveUVs(pe);
            foreach (var c in chunks.Values)
            {
                if (c.OpaqueMR != null) c.OpaqueMR.sharedMaterial = matCutout;
                if (c.TransMR != null) c.TransMR.sharedMaterial = matTrans;
                if (c.EmisMR != null) c.EmisMR.sharedMaterial = matEmis;
            }
            // A material swap alone needs no re-mesh; new UVs or a different vertex-colour decision do.
            if (texChanged || oldShadeC != shadeCutout || oldShadeT != shadeTrans || oldShadeE != shadeEmis)
                foreach (var c in chunks.Values) { c.RenderDirty = true; dirty.Add(c.Key); }
        }

        private Material srcTrans;

        private static string ShaderName(Material m) => m == null ? "null" : (m.shader != null ? m.shader.name : "no shader") + " ('" + m.name + "')";

        private static bool ShowsVertexColors(Material m)
        {
            string mode = (BlocksConfig.FaceShading?.Value ?? "auto").Trim().ToLowerInvariant();
            if (mode == "on" || mode == "true") return true;
            if (mode == "off" || mode == "false") return false;
            string n = m != null && m.shader != null ? m.shader.name : "";
            // Shaders known to multiply by vertex color. SR/Paintlight/* ignores it (its vertex input has no COLOR).
            return n.StartsWith("Sprites/") || n.StartsWith("Particles/") || n.StartsWith("UI/")
                || n.StartsWith("Legacy Shaders/Particles") || n.IndexOf("VertexLit Colored", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Vertex Color", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("VertexColor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ResolveUVs(PaletteEntry pe)
        {
            for (int i = 0; i < 6; i++) pe.UvOk[i] = false;
            if (pe.Def == null || !AtlasReady) return;
            var atlas = SC.Assets.BlockAtlas;
            for (int i = 0; i < 6; i++)
            {
                string spec = pe.Def.Faces != null && i < pe.Def.Faces.Length ? pe.Def.Faces[i] : null;
                if (string.IsNullOrEmpty(spec)) continue;
                try
                {
                    pe.Uv[i] = atlas.GetUV(spec);
                    pe.UvOk[i] = true;
                }
                catch (Exception e) { RateLog.Error("atlas uv " + spec, e, 60f); }
            }
        }

        /// <summary>Atlas UV of a face slot of a block (for particles); false if unavailable.</summary>
        public bool TryGetFaceUV(BlockDef def, int slot, out Rect uv)
        {
            uv = default(Rect);
            if (def == null || !AtlasReady) return false;
            var pe = palette[PaletteOf(def)];
            if (!pe.UvOk[slot]) ResolveUVs(pe);
            uv = pe.Uv[slot];
            return pe.UvOk[slot];
        }

        public Material CutoutMaterial => AtlasReady ? SC.Assets.BlockAtlas.Cutout : null;

        /// <summary>Layer used for our renderers (one the SR camera draws).</summary>
        public int RenderLayer => renderLayer;
    }
}
