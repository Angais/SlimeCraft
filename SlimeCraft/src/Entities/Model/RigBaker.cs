using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Entities.Model
{
    /// <summary>One bone of a baked rig: name, parent index, rest pose and one mesh holding all of its boxes.</summary>
    internal sealed class BakedBone
    {
        public string Name;
        /// <summary>Index of the parent in <see cref="BakedRig.Bones"/>, -1 for the rig origin.</summary>
        public int Parent;
        /// <summary>Rest pose exactly as declared (pixels and radians, rig space).</summary>
        public BonePose Rest;
        /// <summary>The bone's boxes merged in bone-local Unity space; null when the bone has no boxes.</summary>
        public Mesh Mesh;
    }

    /// <summary>A rig turned into meshes, shared by every mob that uses the same key.</summary>
    internal sealed class BakedRig
    {
        public string Key;
        /// <summary>Bones in declaration order (parents before children).</summary>
        public BakedBone[] Bones;
        public int SkinWidth, SkinHeight;
    }

    /// <summary>
    /// Turns <see cref="RigSpec"/>s into Unity meshes and holds the pose math used at runtime.
    ///
    /// Rigs are authored in pixels with +Y down and the face towards -Z. Showing that in Unity's frame reduces to a
    /// point reflection (x, y, z) -> (-x, -y, -z) plus a division by 16: bone rotations stay as declared,
    /// RigInstance negates the pivots and the box vertices are negated here. A point reflection flips handedness,
    /// so the triangle order is flipped as well to keep faces pointing outward.
    ///
    /// Boxes use the classic "unfolded box" skin layout, so the player's own mob textures map exactly.
    /// </summary>
    internal static class RigBaker
    {
        private static readonly Dictionary<string, BakedRig> cache = new Dictionary<string, BakedRig>();

        // Squared cross-product areas (pixel units) below this count as zero: that is how zero-thickness boxes
        // collapse into two-sided cards.
        private const float DegenerateAreaSq = 1e-10f;
        private const float PixelsToUnits = 1f / 16f;

        // ------------------------------------------------------------------ face table

        /// <summary>
        /// One face of a box as data: the axis it is perpendicular to and on which end, the axes that run along the
        /// skin's u (left to right) and v (top to bottom) directions and from which end they start, and where its
        /// rectangle sits inside the unfolded layout (multiples of the box depth and width).
        /// </summary>
        private struct FaceSpec
        {
            public int Normal;       // 0 = x, 1 = y, 2 = z
            public int End;          // 0 = the "low" plane of that axis, 1 = the "high" plane
            public int AlongU;       // axis that runs left -> right on the skin
            public int StartU;       // end of AlongU found at the left edge of the rectangle
            public int AlongV;       // axis that runs top -> bottom on the skin
            public int StartV;       // end of AlongV found at the top edge of the rectangle
            public int DepthSteps;   // rectangle left edge = U + DepthSteps * D + WidthSteps * W
            public int WidthSteps;
            public bool SideRow;     // false: the cap row (starts at V); true: the side row (starts at V + D)

            public FaceSpec(int normal, int end, int alongU, int startU, int alongV, int startV, int depthSteps, int widthSteps, bool sideRow)
            {
                Normal = normal; End = end;
                AlongU = alongU; StartU = startU;
                AlongV = alongV; StartV = startV;
                DepthSteps = depthSteps; WidthSteps = widthSteps;
                SideRow = sideRow;
            }
        }

        // The unfolded box (u to the right, v down, origin at the box's skin offset):
        //   cap row  (height D): [ top face  W wide at D ][ bottom face W wide at D+W ]
        //   side row (height H): [ low-x side D wide at 0 ][ front W at D ][ high-x side D at D+W ][ back W at 2D+W ]
        // Both caps put the back edge (high z) at the top of their rectangle.
        private static readonly FaceSpec[] Faces =
        {
            new FaceSpec(1, 0, 0, 0, 2, 1, 1, 0, false), // y low  (top of an upright bone)
            new FaceSpec(1, 1, 0, 0, 2, 1, 1, 1, false), // y high (underside)
            new FaceSpec(0, 0, 2, 1, 1, 0, 0, 0, true),  // x low side
            new FaceSpec(2, 0, 0, 0, 1, 0, 1, 0, true),  // z low  (front)
            new FaceSpec(0, 1, 2, 0, 1, 0, 1, 1, true),  // x high side
            new FaceSpec(2, 1, 0, 1, 1, 0, 2, 1, true),  // z high (back)
        };

        // Scratch state reused while emitting faces (baking happens on the main thread only).
        private static Vector3 lowCorner, highCorner, edges;
        private static readonly Vector3[] quad = new Vector3[4];
        private static readonly Vector2[] quadUv = new Vector2[4];

        // ------------------------------------------------------------------ public API

        /// <summary>Returns the rig cached under <paramref name="key"/>, baking it from <paramref name="source"/> once.</summary>
        public static BakedRig Get(string key, System.Func<RigSpec> source)
        {
            if (cache.TryGetValue(key, out var known) && known != null) return known;

            RigSpec spec = source();
            // Pixels to units plus the point reflection into Unity's frame (which flips handedness).
            Matrix4x4 toUnity = Matrix4x4.Scale(new Vector3(-PixelsToUnits, -PixelsToUnits, -PixelsToUnits));
            var bones = new BakedBone[spec.Bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                BoneSpec b = spec.Bones[i];
                bones[i] = new BakedBone
                {
                    Name = b.Name,
                    Parent = b.Parent,
                    Rest = b.Rest,
                    Mesh = b.Boxes.Length == 0 ? null : MeshOfBoxes(key + "/" + b.Name, b.Boxes, spec, toUnity, true),
                };
            }

            var rig = new BakedRig { Key = key, Bones = bones, SkinWidth = spec.SkinWidth, SkinHeight = spec.SkinHeight };
            cache[key] = rig;
            return rig;
        }

        /// <summary>
        /// Creates a mesh named "SC_" + name from the given lists (not modified): UV channel 0, one submesh, opaque
        /// white vertex colours (needed by the overlay pass), recalculated normals and bounds.
        /// </summary>
        public static Mesh MakeMesh(string name, List<Vector3> verts, List<Vector2> uvs, List<int> tris)
        {
            var mesh = new Mesh { name = "SC_" + name };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            var white = new Color32(255, 255, 255, 255);
            var colours = new Color32[verts.Count];
            for (int i = 0; i < colours.Length; i++) colours[i] = white;
            mesh.colors32 = colours;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// Rotation for a bone's Euler angles (radians): applied to a vector it turns about X first, then Y, then Z.
        /// Allocation free; called every frame for every animated bone.
        /// </summary>
        public static Quaternion EulerToRotation(Vector3 euler)
        {
            Quaternion q = Quaternion.identity;
            if (euler.z != 0f) q = Quaternion.AngleAxis(euler.z * Mathf.Rad2Deg, Vector3.forward);
            if (euler.y != 0f) q *= Quaternion.AngleAxis(euler.y * Mathf.Rad2Deg, Vector3.up);
            if (euler.x != 0f) q *= Quaternion.AngleAxis(euler.x * Mathf.Rad2Deg, Vector3.right);
            return q;
        }

        /// <summary>Local transform of a pose in rig space: scale, then rotation, then the pivot offset (pixels / 16).</summary>
        public static Matrix4x4 PoseMatrix(BonePose p)
            => Matrix4x4.TRS(p.Offset * PixelsToUnits, EulerToRotation(p.Euler), p.Scale);

        /// <summary>
        /// Collapses a whole static rig into one mesh with every bone transform applied (the origin pose included),
        /// then mapped through <paramref name="toOut"/>. Pass <paramref name="reverseWinding"/> = true when
        /// <paramref name="toOut"/> mirrors space. Not cached.
        /// </summary>
        public static Mesh BakeFlat(string name, RigSpec rig, Matrix4x4 toOut, bool reverseWinding)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            Matrix4x4 origin = PoseMatrix(rig.Origin);
            var placed = new Matrix4x4[rig.Bones.Length];
            Matrix4x4 pixelScale = Matrix4x4.Scale(new Vector3(PixelsToUnits, PixelsToUnits, PixelsToUnits));
            for (int i = 0; i < rig.Bones.Length; i++)
            {
                BoneSpec b = rig.Bones[i];
                Matrix4x4 above = b.Parent < 0 ? origin : placed[b.Parent];
                placed[i] = above * PoseMatrix(b.Rest);
                Matrix4x4 boxToOut = toOut * placed[i] * pixelScale;
                for (int k = 0; k < b.Boxes.Length; k++)
                    AddBox(b.Boxes[k], rig.SkinWidth, rig.SkinHeight, boxToOut, reverseWinding, verts, uvs, tris);
            }
            return MakeMesh(name, verts, uvs, tris);
        }

        /// <summary>
        /// Appends the visible faces of one box to the given lists. Corners (pixels) go through <paramref name="m"/>
        /// as affine points. Each face gets its own four vertices (hard edges) and two triangles. Faces are wound so
        /// they are seen from outside the box when <paramref name="m"/> preserves handedness; with
        /// <paramref name="reverseWinding"/> the opposite order is emitted, for matrices that mirror space.
        /// </summary>
        public static void AddBox(RigBox box, int skinW, int skinH, Matrix4x4 m, bool reverseWinding,
                                  List<Vector3> verts, List<Vector2> uvs, List<int> tris)
        {
            // Geometric extents (padded) and skin extents (never padded).
            Vector3 pad = new Vector3(box.Pad, box.Pad, box.Pad);
            lowCorner = box.Min - pad;
            highCorner = box.Min + box.Size + pad;
            edges = box.Size;

            float skinWidth = skinW;
            float skinHeight = skinH * box.SkinStretchV;
            for (int f = 0; f < Faces.Length; f++)
                EmitFace(Faces[f], box, skinWidth, skinHeight, m, reverseWinding, verts, uvs, tris);
        }

        // ------------------------------------------------------------------ internals

        private static Mesh MeshOfBoxes(string name, RigBox[] boxes, RigSpec spec, Matrix4x4 m, bool reverseWinding)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            for (int i = 0; i < boxes.Length; i++)
                AddBox(boxes[i], spec.SkinWidth, spec.SkinHeight, m, reverseWinding, verts, uvs, tris);
            return MakeMesh(name, verts, uvs, tris);
        }

        /// <summary>
        /// Coordinate of one end of an axis as seen by the skin mapping. A flipped box reflects its texture
        /// assignment across its own centre plane on X, which is the same as reading the two X ends swapped.
        /// </summary>
        private static float SkinnedEnd(int axis, int end, bool flipped)
        {
            if (axis == 0 && flipped) end = 1 - end;
            return end == 0 ? lowCorner[axis] : highCorner[axis];
        }

        private static void EmitFace(FaceSpec face, RigBox box, float skinWidth, float skinHeight, Matrix4x4 m, bool reverseWinding,
                                     List<Vector3> verts, List<Vector2> uvs, List<int> tris)
        {
            bool flipped = box.Flip;
            float depth = box.Size.z, width = box.Size.x;

            // Pixel rectangle of this face on the skin.
            float rectLeft = box.SkinU + face.DepthSteps * depth + face.WidthSteps * width;
            float rectTop = box.SkinV + (face.SideRow ? depth : 0f);
            float rectWidth = edges[face.AlongU];
            float rectHeight = edges[face.AlongV];

            // Corners in skin order: left-top, left-bottom, right-bottom, right-top.
            float plane = SkinnedEnd(face.Normal, face.End, flipped);
            for (int k = 0; k < 4; k++)
            {
                int uSide = (k == 0 || k == 1) ? 0 : 1;   // 0 = left edge, 1 = right edge
                int vSide = (k == 0 || k == 3) ? 0 : 1;   // 0 = top edge, 1 = bottom edge
                int uEnd = uSide == 0 ? face.StartU : 1 - face.StartU;
                int vEnd = vSide == 0 ? face.StartV : 1 - face.StartV;

                Vector3 p = Vector3.zero;
                p[face.Normal] = plane;
                p[face.AlongU] = SkinnedEnd(face.AlongU, uEnd, flipped);
                p[face.AlongV] = SkinnedEnd(face.AlongV, vEnd, flipped);
                quad[k] = p;

                float px = rectLeft + uSide * rectWidth;
                float py = rectTop + vSide * rectHeight;
                quadUv[k] = new Vector2(px / skinWidth, 1f - py / skinHeight);
            }

            // Skip faces without area (zero-thickness boxes keep only their two broad faces).
            Vector3 areaNormal = Vector3.Cross(quad[1] - quad[0], quad[3] - quad[0]);
            if (areaNormal.sqrMagnitude < DegenerateAreaSq) return;

            // Outward direction of the plane this face actually lies on (after the flip swap on X).
            int realEnd = (face.Normal == 0 && flipped) ? 1 - face.End : face.End;
            float outward = realEnd == 1 ? 1f : -1f;

            // In Unity the order (0,1,2)/(0,2,3) is front-facing from the side that Cross(q1 - q0, q2 - q0) points
            // to. Compare that side with the outward direction, then honour the caller's handedness flag.
            Vector3 frontOfDefault = Vector3.Cross(quad[1] - quad[0], quad[2] - quad[0]);
            bool defaultFacesOut = frontOfDefault[face.Normal] * outward > 0f;
            bool keepDefault = defaultFacesOut != reverseWinding;

            int first = verts.Count;
            for (int k = 0; k < 4; k++)
            {
                verts.Add(m.MultiplyPoint3x4(quad[k]));
                uvs.Add(quadUv[k]);
            }
            if (keepDefault)
            {
                tris.Add(first); tris.Add(first + 1); tris.Add(first + 2);
                tris.Add(first); tris.Add(first + 2); tris.Add(first + 3);
            }
            else
            {
                tris.Add(first); tris.Add(first + 2); tris.Add(first + 1);
                tris.Add(first); tris.Add(first + 3); tris.Add(first + 2);
            }
        }
    }
}
