using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>
    /// Extruded item sprite with the look of Minecraft's flat "generated" item models: a 1x1 block quad on each side of a
    /// 1/16 block thick slab plus one side face for every pixel edge that borders a transparent pixel. Centered on the
    /// origin. Convention (same as Minecraft's model after the Z mirror into Unity): the front ("south") face looks
    /// towards -Z and shows the texture upright (u → +x, v → +y).
    /// Used for the bow draw stages (bow_pulling_0..2) and as a fallback when IItemVisuals has no mesh.
    /// </summary>
    internal static class SpriteMeshBuilder
    {
        public static Mesh Build(Texture2D tex, string name)
        {
            if (tex == null) return null;
            int w = tex.width, h = tex.height;
            Color32[] px;
            try { px = tex.GetPixels32(); }
            catch { px = null; } // not readable: front/back only

            var b = new MeshBuilder();
            const float half = 0.5f / 16f; // half thickness: 1 model pixel total
            // front (-Z) and back (+Z)
            b.Quad(new Vector3(-0.5f, -0.5f, -half), new Vector3(-0.5f, 0.5f, -half), new Vector3(0.5f, 0.5f, -half), new Vector3(0.5f, -0.5f, -half),
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector3(0f, 0f, -1f), false);
            b.Quad(new Vector3(-0.5f, -0.5f, half), new Vector3(-0.5f, 0.5f, half), new Vector3(0.5f, 0.5f, half), new Vector3(0.5f, -0.5f, half),
                new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f), new Vector3(0f, 0f, 1f), false);

            if (px != null && px.Length == w * h)
            {
                float sx = 1f / w, sy = 1f / h;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        if (px[y * w + x].a == 0) continue;
                        // uv at the pixel center: side faces show the edge pixel's color (point filtered)
                        var uv = new Vector2((x + 0.5f) * sx, (y + 0.5f) * sy);
                        float x0 = x * sx - 0.5f, x1 = (x + 1) * sx - 0.5f;
                        float y0 = y * sy - 0.5f, y1 = (y + 1) * sy - 0.5f;
                        if (Transparent(px, w, h, x - 1, y))
                            b.Quad(new Vector3(x0, y0, -half), new Vector3(x0, y1, -half), new Vector3(x0, y1, half), new Vector3(x0, y0, half), uv, uv, uv, uv, Vector3.left, false);
                        if (Transparent(px, w, h, x + 1, y))
                            b.Quad(new Vector3(x1, y0, -half), new Vector3(x1, y1, -half), new Vector3(x1, y1, half), new Vector3(x1, y0, half), uv, uv, uv, uv, Vector3.right, false);
                        if (Transparent(px, w, h, x, y - 1))
                            b.Quad(new Vector3(x0, y0, -half), new Vector3(x1, y0, -half), new Vector3(x1, y0, half), new Vector3(x0, y0, half), uv, uv, uv, uv, Vector3.down, false);
                        if (Transparent(px, w, h, x, y + 1))
                            b.Quad(new Vector3(x0, y1, -half), new Vector3(x1, y1, -half), new Vector3(x1, y1, half), new Vector3(x0, y1, half), uv, uv, uv, uv, Vector3.up, false);
                    }
                }
            }
            return b.ToMesh(name);
        }

        private static bool Transparent(Color32[] px, int w, int h, int x, int y)
        {
            if (x < 0 || y < 0 || x >= w || y >= h) return true;
            return px[y * w + x].a == 0;
        }
    }
}
