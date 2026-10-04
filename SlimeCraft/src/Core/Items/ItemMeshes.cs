using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Item meshes, 1 unit = 1 block, centered on the origin.
    /// Block items: unit cube with atlas UVs (North = -Z, East = +X, textures unmirrored seen from outside,
    /// side textures upright, top texture "up" towards north, bottom towards south — same convention as the
    /// block world). Flat items are extruded sprites with Minecraft's look: a front quad (facing -Z, texture
    /// unmirrored), a back quad, and a 1-pixel side face for every opaque pixel edge that borders transparency,
    /// 1/16 thick (z = ±1/32). Side UVs are pulled 0.1 px inwards so they sample the edge pixel and never bleed.
    /// </summary>
    internal static class ItemMeshes
    {
        private sealed class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<int> T = new List<int>();

            /// <summary>Quad a,b,c,d (in order around the face) with outward normal n; winding fixed for Unity (clockwise = front).</summary>
            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Vector3 n)
            {
                int i = V.Count;
                V.Add(a); V.Add(b); V.Add(c); V.Add(d);
                UV.Add(ua); UV.Add(ub); UV.Add(uc); UV.Add(ud);
                N.Add(n); N.Add(n); N.Add(n); N.Add(n);
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), n) >= 0f) { T.Add(i); T.Add(i + 1); T.Add(i + 2); T.Add(i); T.Add(i + 2); T.Add(i + 3); }
                else { T.Add(i); T.Add(i + 2); T.Add(i + 1); T.Add(i); T.Add(i + 3); T.Add(i + 2); }
            }

            public Mesh Build(string name)
            {
                var m = new Mesh { name = name };
                m.SetVertices(V);
                m.SetNormals(N);
                m.SetUVs(0, UV);
                m.SetTriangles(T, 0);
                m.RecalculateBounds();
                return m;
            }
        }

        private static readonly Vector3[] Normals =
        {
            Vector3.up, Vector3.down, new Vector3(0, 0, -1), new Vector3(0, 0, 1), Vector3.right, Vector3.left
        };

        /// <summary>Texture "up" direction for each face (BlockDef.Faces order Up, Down, North, South, East, West).</summary>
        private static readonly Vector3[] TexUp =
        {
            new Vector3(0, 0, -1), new Vector3(0, 0, 1), Vector3.up, Vector3.up, Vector3.up, Vector3.up
        };

        public static Mesh BlockCube(BlockDef def, IBlockAtlas atlas)
        {
            var b = new Builder();
            for (int f = 0; f < 6; f++)
            {
                Vector3 n = Normals[f], up = TexUp[f];
                Vector3 right = Vector3.Cross(up, -n); // viewer looks along -n; Unity: right = up x forward
                Vector3 c = n * 0.5f;
                Vector3 tl = c - right * 0.5f + up * 0.5f, tr = c + right * 0.5f + up * 0.5f;
                Vector3 br = c + right * 0.5f - up * 0.5f, bl = c - right * 0.5f - up * 0.5f;
                Rect r = atlas != null ? atlas.GetUV(def.Faces[f]) : new Rect(0, 0, 1, 1);
                b.Quad(bl, tl, tr, br,
                    new Vector2(r.xMin, r.yMin), new Vector2(r.xMin, r.yMax), new Vector2(r.xMax, r.yMax), new Vector2(r.xMax, r.yMin), n);
            }
            return b.Build("block:" + def.Id);
        }

        /// <summary>Extruded sprite from a readable texture (any alpha above 0 counts as opaque).</summary>
        public static Mesh Extruded(Texture2D tex, string name)
        {
            var b = new Builder();
            int w = tex.width, h = tex.height;
            const float t = 1f / 32f;
            // front (facing -Z) and back (facing +Z, mirrored UVs = same pixels seen from behind)
            b.Quad(new Vector3(-0.5f, -0.5f, -t), new Vector3(-0.5f, 0.5f, -t), new Vector3(0.5f, 0.5f, -t), new Vector3(0.5f, -0.5f, -t),
                new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector3(0, 0, -1));
            b.Quad(new Vector3(0.5f, -0.5f, t), new Vector3(0.5f, 0.5f, t), new Vector3(-0.5f, 0.5f, t), new Vector3(-0.5f, -0.5f, t),
                new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1), new Vector2(0, 0), new Vector3(0, 0, 1));

            Color32[] px;
            try { px = tex.GetPixels32(); }
            catch { return b.Build("item:" + name); }
            if (w > 64 || h > 64) return b.Build("item:" + name); // big (non-Minecraft) icons: flat card only

            // pixel (x, yTop) → opaque?
            bool Opaque(int x, int yTop) => x >= 0 && yTop >= 0 && x < w && yTop < h && px[(h - 1 - yTop) * w + x].a != 0;
            const float shrink = 0.1f;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!Opaque(x, y)) continue;
                    float x0 = (float)x / w - 0.5f, x1 = (float)(x + 1) / w - 0.5f;
                    float yT = 0.5f - (float)y / h, yB = 0.5f - (float)(y + 1) / h;
                    float u0 = (x + shrink) / w, u1 = (x + 1 - shrink) / w;
                    float vT = 1f - (y + shrink) / h, vB = 1f - (y + 1 - shrink) / h;
                    var uvA = new Vector2(u0, vT); var uvB = new Vector2(u1, vT); var uvC = new Vector2(u1, vB); var uvD = new Vector2(u0, vB);
                    if (!Opaque(x, y - 1)) // up
                        b.Quad(new Vector3(x0, yT, -t), new Vector3(x0, yT, t), new Vector3(x1, yT, t), new Vector3(x1, yT, -t), uvA, uvA, uvB, uvB, Vector3.up);
                    if (!Opaque(x, y + 1)) // down
                        b.Quad(new Vector3(x0, yB, -t), new Vector3(x1, yB, -t), new Vector3(x1, yB, t), new Vector3(x0, yB, t), uvD, uvC, uvC, uvD, Vector3.down);
                    if (!Opaque(x - 1, y)) // left
                        b.Quad(new Vector3(x0, yB, -t), new Vector3(x0, yB, t), new Vector3(x0, yT, t), new Vector3(x0, yT, -t), uvD, uvD, uvA, uvA, Vector3.left);
                    if (!Opaque(x + 1, y)) // right
                        b.Quad(new Vector3(x1, yB, -t), new Vector3(x1, yT, -t), new Vector3(x1, yT, t), new Vector3(x1, yB, t), uvC, uvB, uvB, uvC, Vector3.right);
                }
            }
            return b.Build("item:" + name);
        }
    }
}
