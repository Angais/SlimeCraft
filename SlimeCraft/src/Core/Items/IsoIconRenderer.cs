using System;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// CPU renderer for Minecraft-style inventory block icons. Reproduces the vanilla "block/block" gui display
    /// transform (rotation [30, 225, 0], scale 0.625, orthographic, model y up): the visible faces are
    /// up (top), east (left on screen) and north (right on screen), each mapped with Minecraft's default face UVs
    /// (up: u=x v=z; east: u=1-z v=1-y; north: u=1-x v=1-y). Faces are shaded top 1.0 / left 0.8 / right 0.6.
    /// Every output pixel is inverse-mapped to the face texel under its center (crisp, no filtering).
    /// </summary>
    internal static class IsoIconRenderer
    {
        private struct Face
        {
            public Vector2 O, U, V;  // screen-space origin (texel 0,0 corner) and axes (y down)
            public float Shade;
            public int Slot;         // BlockDef.Faces index
            public float Det;
        }

        public static Texture2D Render(BlockDef def, TextureSpec specs, int size)
        {
            size = Mathf.Clamp(size, 16, 512);
            bool translucent = def.Layer == RenderLayer.Translucent;
            // MC model space: x east, y up, z south, unit cube [0,1]^3. Faces: origin/U/V in model space.
            var faces = new Face[3];
            faces[0] = Make(size, new Vector3(0, 1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1), 1.0f, 0); // up
            faces[1] = Make(size, new Vector3(1, 1, 1), new Vector3(0, 0, -1), new Vector3(0, -1, 0), 0.8f, 4); // east → left
            faces[2] = Make(size, new Vector3(1, 1, 0), new Vector3(-1, 0, 0), new Vector3(0, -1, 0), 0.6f, 2); // north → right

            var img = new PixelImage(size, size);
            var tex = new PixelImage[3];
            for (int i = 0; i < 3; i++) tex[i] = specs.Evaluate(def.Faces[faces[i].Slot] ?? def.Faces[0]);

            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    var p = new Vector2(px + 0.5f, py + 0.5f);
                    for (int f = 0; f < 3; f++)
                    {
                        var fc = faces[f];
                        // solve p = O + u*U + v*V
                        var d = p - fc.O;
                        float u = (d.x * fc.V.y - d.y * fc.V.x) / fc.Det;
                        float v = (fc.U.x * d.y - fc.U.y * d.x) / fc.Det;
                        if (u < 0f || u >= 1f || v < 0f || v >= 1f) continue;
                        var t = tex[f];
                        int tx = Mathf.Clamp((int)(u * t.W), 0, t.W - 1);
                        int ty = Mathf.Clamp((int)(v * t.H), 0, t.H - 1);
                        var c = t.GetTopDown(tx, ty);
                        if (!translucent) { if (c.a < 128) break; c.a = 255; }
                        else if (c.a == 0) break;
                        c.r = (byte)(c.r * fc.Shade);
                        c.g = (byte)(c.g * fc.Shade);
                        c.b = (byte)(c.b * fc.Shade);
                        img.SetTopDown(px, py, c);
                        break;
                    }
                }
            }
            return Pixels.ToTexture(img, "icon:" + def.Id);
        }

        /// <summary>Projects a face (model-space origin + unit edge axes) into icon pixels.</summary>
        private static Face Make(int size, Vector3 origin, Vector3 uAxis, Vector3 vAxis, float shade, int slot)
        {
            Vector2 o = Project(origin, size);
            Vector2 u = Project(origin + uAxis, size) - o;
            Vector2 v = Project(origin + vAxis, size) - o;
            return new Face { O = o, U = u, V = v, Shade = shade, Slot = slot, Det = u.x * v.y - u.y * v.x };
        }

        /// <summary>Model point (0..1 cube) → icon pixel coordinates (x right, y down).</summary>
        private static Vector2 Project(Vector3 m, int size)
        {
            var c = m - new Vector3(0.5f, 0.5f, 0.5f);
            // rotate 225° about Y first, then 30° about X (right-handed rotations, X applied last to the vector)
            const float a = 225f * Mathf.Deg2Rad, b = 30f * Mathf.Deg2Rad;
            float ca = Mathf.Cos(a), sa = Mathf.Sin(a), cb = Mathf.Cos(b), sb = Mathf.Sin(b);
            float x1 = c.x * ca + c.z * sa;
            float z1 = -c.x * sa + c.z * ca;
            float y1 = c.y;
            float y2 = y1 * cb - z1 * sb;
            float scale = 0.625f * size;
            return new Vector2(size * 0.5f + x1 * scale, size * 0.5f - y2 * scale);
        }
    }
}
