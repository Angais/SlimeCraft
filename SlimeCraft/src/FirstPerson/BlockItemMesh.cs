using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>
    /// Held block-item cube with the same texture layout as a full Minecraft block model (from [0,0,0] to
    /// [16,16,16], every face unrotated, default UVs): north = -Z, east = +X, side textures upright and unmirrored seen
    /// from outside, top texture "up" towards north, bottom "up" towards south. The cube is centered on the origin
    /// (models are displayed around their centre) and expressed in the hand rig's Unity space, i.e. Minecraft model space mirrored on Z
    /// (see <see cref="McPose.ApplyTo"/>), so with block/block's firstperson_righthand ([0,45,0], 0.4) the visible faces
    /// are exactly Minecraft's: top, west (large, left) and south (narrow, right). IItemVisuals' block cube follows the
    /// block-world convention instead (north = Unity -Z), which after our Z mirror would put the front texture of
    /// furnaces / pumpkins / crafting tables on the wrong side, hence this dedicated mesh.
    /// </summary>
    internal static class BlockItemMesh
    {
        private enum F { Up = 0, Down = 1, North = 2, South = 3, East = 4, West = 5 }

        /// <summary>BlockDef.Faces order: Up, Down, North, South, East, West.</summary>
        public static Mesh Build(BlockDef def, IBlockAtlas atlas)
        {
            if (def == null || atlas == null || def.Faces == null || def.Faces.Length < 6) return null;
            var b = new MeshBuilder();
            // Corners in Minecraft model pixels (0..16), order: texture top-left, top-right, bottom-right, bottom-left
            // as seen from outside the face (u → right, v → down), i.e. the default full-face UV layout.
            Face(b, atlas, def.Faces[(int)F.North], new Vector3(0f, 0f, -1f), V(16, 16, 0), V(0, 16, 0), V(0, 0, 0), V(16, 0, 0));
            Face(b, atlas, def.Faces[(int)F.South], new Vector3(0f, 0f, 1f), V(0, 16, 16), V(16, 16, 16), V(16, 0, 16), V(0, 0, 16));
            Face(b, atlas, def.Faces[(int)F.West], new Vector3(-1f, 0f, 0f), V(0, 16, 0), V(0, 16, 16), V(0, 0, 16), V(0, 0, 0));
            Face(b, atlas, def.Faces[(int)F.East], new Vector3(1f, 0f, 0f), V(16, 16, 16), V(16, 16, 0), V(16, 0, 0), V(16, 0, 16));
            Face(b, atlas, def.Faces[(int)F.Up], new Vector3(0f, 1f, 0f), V(0, 16, 0), V(16, 16, 0), V(16, 16, 16), V(0, 16, 16));
            Face(b, atlas, def.Faces[(int)F.Down], new Vector3(0f, -1f, 0f), V(0, 0, 16), V(16, 0, 16), V(16, 0, 0), V(0, 0, 0));
            return b.ToMesh("SlimeCraft_HeldBlock_" + def.Id);
        }

        private static Vector3 V(float x, float y, float z) { return new Vector3(x, y, z); }

        private static void Face(MeshBuilder b, IBlockAtlas atlas, string spec, Vector3 mcNormal, Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl)
        {
            Rect r = string.IsNullOrEmpty(spec) ? new Rect(0f, 0f, 0f, 0f) : atlas.GetUV(spec);
            // atlas rects are Unity uv space (yMax = top row of the tile)
            Vector2 uTL = new Vector2(r.xMin, r.yMax), uTR = new Vector2(r.xMax, r.yMax);
            Vector2 uBR = new Vector2(r.xMax, r.yMin), uBL = new Vector2(r.xMin, r.yMin);
            b.Quad(ToUnity(tl), ToUnity(tr), ToUnity(br), ToUnity(bl), uTL, uTR, uBR, uBL,
                new Vector3(mcNormal.x, mcNormal.y, -mcNormal.z), false);
        }

        /// <summary>Model pixels → blocks centered on the origin → Unity (z mirrored).</summary>
        private static Vector3 ToUnity(Vector3 px)
        {
            return new Vector3(px.x / 16f - 0.5f, px.y / 16f - 0.5f, -(px.z / 16f - 0.5f));
        }
    }
}
