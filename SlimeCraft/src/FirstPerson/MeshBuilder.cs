using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>Small helper collecting quads (Unity space) and emitting a Mesh. Winding is derived from the wanted normal.</summary>
    internal sealed class MeshBuilder
    {
        private readonly List<Vector3> verts = new List<Vector3>();
        private readonly List<Vector3> normals = new List<Vector3>();
        private readonly List<Vector2> uvs = new List<Vector2>();
        private readonly List<int> tris = new List<int>();

        public int VertexCount => verts.Count;

        /// <summary>
        /// Adds quad a-b-c-d (in order around the face) facing <paramref name="normal"/>. Unity treats a triangle as
        /// front-facing when Cross(b - a, c - a) points to the viewer, so the winding is flipped when needed.
        /// </summary>
        public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Vector3 normal, bool doubleSided)
        {
            AddOneSide(a, b, c, d, ua, ub, uc, ud, normal);
            if (doubleSided) AddOneSide(a, b, c, d, ua, ub, uc, ud, -normal);
        }

        private void AddOneSide(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Vector3 normal)
        {
            int i = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            uvs.Add(ua); uvs.Add(ub); uvs.Add(uc); uvs.Add(ud);
            normals.Add(normal); normals.Add(normal); normals.Add(normal); normals.Add(normal);
            Vector3 geo = Vector3.Cross(b - a, c - a);
            if (Vector3.Dot(geo, normal) >= 0f)
            {
                tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
            }
            else
            {
                tris.Add(i); tris.Add(i + 2); tris.Add(i + 1);
                tris.Add(i); tris.Add(i + 3); tris.Add(i + 2);
            }
        }

        public Mesh ToMesh(string name)
        {
            var m = new Mesh { name = name };
            if (verts.Count > 65000) m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.SetVertices(verts);
            m.SetNormals(normals);
            m.SetUVs(0, uvs);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }
    }
}
