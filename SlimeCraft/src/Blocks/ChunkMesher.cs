using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    /// <summary>Reusable vertex/index buffers (no per-rebuild allocations once warmed up).</summary>
    internal sealed class MeshBuf
    {
        public readonly List<Vector3> V = new List<Vector3>(4096);
        public readonly List<Vector3> N = new List<Vector3>(4096);
        public readonly List<Vector2> UV = new List<Vector2>(4096);
        public readonly List<Color32> C = new List<Color32>(4096);
        public readonly List<int> T = new List<int>(6144);

        public void Clear() { V.Clear(); N.Clear(); UV.Clear(); C.Clear(); T.Clear(); }
        public int Quads => V.Count / 4;
    }

    /// <summary>
    /// Builds the render meshes (opaque+cutout, translucent, light-emitting) and the collider meshes of one chunk:
    /// per-face culling like Minecraft, per-face atlas UVs with MC texture orientation, normals and
    /// optional baked directional shading. Faces of light-emitting blocks (BlockDef.Light &gt; 0, not translucent)
    /// go to <see cref="Emis"/>, rendered with the full-bright atlas material so they glow at night like in MC.
    /// </summary>
    internal sealed class ChunkMesher
    {
        public readonly MeshBuf Opaque = new MeshBuf();
        public readonly MeshBuf Trans = new MeshBuf();
        public readonly MeshBuf Emis = new MeshBuf();
        public readonly MeshBuf[] Cols = { new MeshBuf(), new MeshBuf(), new MeshBuf(), new MeshBuf() };
        public readonly List<Vector3> TransCenters = new List<Vector3>(512);
        private readonly Chunk[] nb = new Chunk[6];
        private static readonly Color32[] shadeColors = new Color32[6];

        static ChunkMesher()
        {
            for (int d = 0; d < 6; d++)
            {
                byte s = (byte)Mathf.RoundToInt(Dirs.Shade[d] * 255f);
                shadeColors[d] = new Color32(s, s, s, 255);
            }
        }

        public void Build(BlockWorld w, Chunk c, bool render, bool collide, bool shadeCutout, bool shadeTrans, bool shadeEmis)
        {
            Opaque.Clear(); Trans.Clear(); Emis.Clear(); TransCenters.Clear();
            for (int g = 0; g < 4; g++) Cols[g].Clear();
            for (int d = 0; d < 6; d++)
                nb[d] = w.GetChunkAt(c.Cx + Dirs.Vec[d].x, c.Cy + Dirs.Vec[d].y, c.Cz + Dirs.Vec[d].z);

            var pal = w.Palette;
            var ids = c.Ids;
            for (int ly = 0; ly < Chunk.Size; ly++)
            for (int lz = 0; lz < Chunk.Size; lz++)
            for (int lx = 0; lx < Chunk.Size; lx++)
            {
                int i = (ly << 8) | (lz << 4) | lx;
                ushort id = ids[i];
                if (id == 0) continue;
                var pe = pal[id];
                byte facing = c.Facing[i];
                for (int d = 0; d < 6; d++)
                {
                    ushort nid = Neighbor(c, lx, ly, lz, d);
                    if (collide && nid == 0) AddColliderQuad(Cols[(int)pe.Phys], lx, ly, lz, d);
                    if (!render) continue;
                    if (nid != 0)
                    {
                        var ne = pal[nid];
                        if (ne.Opaque) continue;                 // hidden behind a full opaque block
                        if (nid == id && !pe.Opaque) continue;   // glass-glass, leaves-leaves, ice-ice...
                    }
                    Dirs.ResolveFace(pe.Def, facing, d, out int slot, out int up);
                    if (!pe.UvOk[slot]) continue;
                    if (pe.Layer == RenderLayer.Translucent)
                    {
                        AddQuad(Trans, lx, ly, lz, d, up, pe.Uv[slot], shadeTrans);
                        TransCenters.Add(new Vector3(lx + 0.5f, ly + 0.5f, lz + 0.5f) + Dirs.VecF[d] * 0.5f);
                    }
                    else if (pe.Emissive) AddQuad(Emis, lx, ly, lz, d, up, pe.Uv[slot], shadeEmis);
                    else AddQuad(Opaque, lx, ly, lz, d, up, pe.Uv[slot], shadeCutout);
                }
            }
        }

        private ushort Neighbor(Chunk c, int lx, int ly, int lz, int d)
        {
            var v = Dirs.Vec[d];
            int nx = lx + v.x, ny = ly + v.y, nz = lz + v.z;
            if ((uint)nx < Chunk.Size && (uint)ny < Chunk.Size && (uint)nz < Chunk.Size)
                return c.Ids[(ny << 8) | (nz << 4) | nx];
            var n = nb[d];
            if (n == null) return 0;
            return n.Ids[((ny & 15) << 8) | ((nz & 15) << 4) | (nx & 15)];
        }

        private static void AddQuad(MeshBuf b, int lx, int ly, int lz, int d, int up, Rect uv, bool shade)
        {
            var cs = Dirs.Corners(d, up);
            int baseIdx = b.V.Count;
            var p = new Vector3(lx, ly, lz);
            var n = Dirs.VecF[d];
            b.V.Add(p + cs[0]); b.V.Add(p + cs[1]); b.V.Add(p + cs[2]); b.V.Add(p + cs[3]);
            b.N.Add(n); b.N.Add(n); b.N.Add(n); b.N.Add(n);
            // TL, BL, BR, TR (Unity UV: yMax = top of the texture)
            b.UV.Add(new Vector2(uv.xMin, uv.yMax));
            b.UV.Add(new Vector2(uv.xMin, uv.yMin));
            b.UV.Add(new Vector2(uv.xMax, uv.yMin));
            b.UV.Add(new Vector2(uv.xMax, uv.yMax));
            if (shade)
            {
                var col = shadeColors[d];
                b.C.Add(col); b.C.Add(col); b.C.Add(col); b.C.Add(col);
            }
            b.T.Add(baseIdx); b.T.Add(baseIdx + 3); b.T.Add(baseIdx + 2);
            b.T.Add(baseIdx); b.T.Add(baseIdx + 2); b.T.Add(baseIdx + 1);
        }

        private static void AddColliderQuad(MeshBuf b, int lx, int ly, int lz, int d)
        {
            var cs = Dirs.Corners(d, Dirs.DefaultUp(d));
            int baseIdx = b.V.Count;
            var p = new Vector3(lx, ly, lz);
            b.V.Add(p + cs[0]); b.V.Add(p + cs[1]); b.V.Add(p + cs[2]); b.V.Add(p + cs[3]);
            b.T.Add(baseIdx); b.T.Add(baseIdx + 3); b.T.Add(baseIdx + 2);
            b.T.Add(baseIdx); b.T.Add(baseIdx + 2); b.T.Add(baseIdx + 1);
        }

        /// <summary>Writes translucent triangles of the chunk sorted back-to-front for the camera (chunk-local position).</summary>
        public static void SortTranslucent(Chunk c, Vector3 camLocal, List<int> tmpTris)
        {
            int q = c.TransCenters.Count;
            if (q == 0 || c.TransMesh == null) return;
            if (c.SortKeys.Length < q) { c.SortKeys = new float[q * 2]; c.SortOrder = new int[q * 2]; }
            for (int i = 0; i < q; i++)
            {
                c.SortKeys[i] = -(c.TransCenters[i] - camLocal).sqrMagnitude; // farthest first
                c.SortOrder[i] = i;
            }
            System.Array.Sort(c.SortKeys, c.SortOrder, 0, q);
            tmpTris.Clear();
            for (int k = 0; k < q; k++)
            {
                int b = c.SortOrder[k] * 4;
                tmpTris.Add(b); tmpTris.Add(b + 3); tmpTris.Add(b + 2);
                tmpTris.Add(b); tmpTris.Add(b + 2); tmpTris.Add(b + 1);
            }
            c.TransMesh.SetTriangles(tmpTris, 0, false);
            c.LastSortCam = camLocal;
        }
    }
}
