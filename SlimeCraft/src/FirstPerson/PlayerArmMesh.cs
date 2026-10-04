using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>
    /// Builds the first-person right arm from a 64x64 player skin: the arm box and, optionally, the slightly larger
    /// sleeve box from the skin's second layer. The faces are unwrapped exactly like Minecraft's skin layout so any
    /// skin made for the game lines up. The mesh is static; posing happens in ItemInHand.ArmPose and the rig.
    ///
    /// Model frame (1 unit = 1 skin pixel, origin at the shoulder pivot): +Y runs down the arm toward the hand, the
    /// front of the body faces -Z and -X is the outer side of the right arm. Unity local = (x, y, -z) / 16.
    /// </summary>
    internal static class PlayerArmMesh
    {
        private const float SkinSize = 64f;
        private const float PixelsPerBlock = 16f;
        private const float SleeveGrow = 0.25f;

        // Corner selectors: a set bit picks the box's max coordinate on that axis, a clear bit its min.
        private const int MaxX = 1, MaxY = 2, MaxZ = 4;

        /// <summary>
        /// One face of a box in the skin unwrap. The face's texel rectangle starts at
        /// (U + UStartD*D + UStartW*W, V + VStartD*D) and spans either W or D texels across and D or H texels down.
        /// <c>TopLeft</c>, <c>TopRight</c> and <c>BottomLeft</c> name the box corners that land on those corners of the
        /// rectangle; the fourth corner follows from them.
        /// </summary>
        private struct FaceLayout
        {
            public int TopLeft, TopRight, BottomLeft;
            public int UStartD, UStartW;
            public bool WideAcross;     // true: W texels across, false: D texels across
            public int VStartD;
            public bool TallDown;       // true: H texels down, false: D texels down
            public Vector3 UnityNormal;

            public FaceLayout(int topLeft, int topRight, int bottomLeft, int uStartD, int uStartW, bool wideAcross,
                              int vStartD, bool tallDown, Vector3 unityNormal)
            {
                TopLeft = topLeft; TopRight = topRight; BottomLeft = bottomLeft;
                UStartD = uStartD; UStartW = uStartW; WideAcross = wideAcross;
                VStartD = vStartD; TallDown = tallDown; UnityNormal = unityNormal;
            }
        }

        // The six faces: two caps in the top row of the unwrap, then the four sides wrapping around the arm
        // left to right (outer, front, inner, back) so the texture is continuous across every vertical seam.
        private static readonly FaceLayout[] Faces =
        {
            // shoulder cap (min Y): +u along +x, +v toward -z
            new FaceLayout(MaxZ,               MaxX | MaxZ,        0,                  1, 0, true,  0, false, new Vector3(0f, -1f, 0f)),
            // hand cap (max Y): same orientation as the shoulder cap
            new FaceLayout(MaxY | MaxZ,        MaxX | MaxY | MaxZ, MaxY,               1, 1, true,  0, false, new Vector3(0f, 1f, 0f)),
            // outer side (min X): +u toward -z, +v down the arm
            new FaceLayout(MaxZ,               0,                  MaxY | MaxZ,        0, 0, false, 1, true,  new Vector3(-1f, 0f, 0f)),
            // front (min Z): +u along +x
            new FaceLayout(0,                  MaxX,               MaxY,               1, 0, true,  1, true,  new Vector3(0f, 0f, 1f)),
            // inner side (max X): +u toward +z
            new FaceLayout(MaxX,               MaxX | MaxZ,        MaxX | MaxY,        1, 1, false, 1, true,  new Vector3(1f, 0f, 0f)),
            // back (max Z): +u toward -x
            new FaceLayout(MaxX | MaxZ,        MaxZ,               MaxX | MaxY | MaxZ, 2, 1, true,  1, true,  new Vector3(0f, 0f, -1f)),
        };

        /// <summary>Returns a new arm mesh (the caller owns it). Slim = 3-pixel-wide arm, otherwise 4 pixels.</summary>
        public static Mesh Build(bool slim, bool sleeve)
        {
            int width = slim ? 3 : 4;
            float minX = slim ? -2f : -3f;
            var origin = new Vector3(minX, -2f, -2f);
            var size = new Vector3(width, 12f, 4f);

            var builder = new MeshBuilder();
            AddBox(builder, origin, size, 0f, 40, 16);
            // In first person the sleeve's inner faces always sit behind the opaque arm, so they are not generated:
            // a coincident inward copy would z-fight with the arm surface (and could win and look darker, because the
            // Slime Rancher material we render with may not cull back faces).
            if (sleeve) AddBox(builder, origin, size, SleeveGrow, 40, 32);
            return builder.ToMesh(slim ? "SlimeCraft_RightArmSlim" : "SlimeCraft_RightArm");
        }

        /// <summary>
        /// Adds one box (six outward-facing quads). <paramref name="grow"/> moves every face outward but leaves the
        /// texel rectangles at the box's nominal size.
        /// </summary>
        private static void AddBox(MeshBuilder builder, Vector3 origin, Vector3 size, float grow, int texU, int texV)
        {
            Vector3 lo = origin - new Vector3(grow, grow, grow);
            Vector3 hi = origin + size + new Vector3(grow, grow, grow);
            float w = size.x, h = size.y, d = size.z;

            for (int i = 0; i < Faces.Length; i++)
            {
                FaceLayout f = Faces[i];
                float u0 = texU + f.UStartD * d + f.UStartW * w;
                float u1 = u0 + (f.WideAcross ? w : d);
                float v0 = texV + f.VStartD * d;
                float v1 = v0 + (f.TallDown ? h : d);

                int bottomRight = f.TopLeft ^ f.TopRight ^ f.BottomLeft;
                builder.Quad(
                    Corner(lo, hi, f.TopLeft), Corner(lo, hi, f.TopRight), Corner(lo, hi, bottomRight), Corner(lo, hi, f.BottomLeft),
                    Texel(u0, v0), Texel(u1, v0), Texel(u1, v1), Texel(u0, v1),
                    f.UnityNormal, false);
            }
        }

        /// <summary>Box corner chosen by the selector bits, converted to Unity local space (pixels to blocks, Z flipped).</summary>
        private static Vector3 Corner(Vector3 lo, Vector3 hi, int select)
        {
            float x = (select & MaxX) != 0 ? hi.x : lo.x;
            float y = (select & MaxY) != 0 ? hi.y : lo.y;
            float z = (select & MaxZ) != 0 ? hi.z : lo.z;
            return new Vector3(x / PixelsPerBlock, y / PixelsPerBlock, -z / PixelsPerBlock);
        }

        /// <summary>Skin texel (origin top-left, v down) to Unity UV (origin bottom-left).</summary>
        private static Vector2 Texel(float u, float v)
        {
            return new Vector2(u / SkinSize, 1f - v / SkinSize);
        }
    }
}
