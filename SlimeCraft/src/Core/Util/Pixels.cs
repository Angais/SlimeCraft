using System;
using System.Globalization;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>A CPU-side RGBA image in Unity row order (row 0 = bottom).</summary>
    internal sealed class PixelImage
    {
        public readonly int W, H;
        public readonly Color32[] Px;
        public PixelImage(int w, int h) { W = w; H = h; Px = new Color32[w * h]; }
        public PixelImage(int w, int h, Color32[] px) { W = w; H = h; Px = px; }

        /// <summary>Pixel using Minecraft/top-down coordinates (y = 0 is the top row).</summary>
        public Color32 GetTopDown(int x, int y) => Px[(H - 1 - y) * W + x];
        public void SetTopDown(int x, int y, Color32 c) { Px[(H - 1 - y) * W + x] = c; }

        public PixelImage Copy() { var c = new Color32[Px.Length]; Array.Copy(Px, c, Px.Length); return new PixelImage(W, H, c); }
    }

    /// <summary>Pixel helpers shared by textures, atlas, icons and fonts.</summary>
    internal static class Pixels
    {
        public static readonly Color32 Magenta = new Color32(0xF8, 0x00, 0xF8, 0xFF);
        public static readonly Color32 Black = new Color32(0, 0, 0, 0xFF);
        public static readonly Color32 Clear = new Color32(0, 0, 0, 0);

        /// <summary>Minecraft's "missingno" texture: 16x16, magenta/black 8x8 quadrants.</summary>
        public static PixelImage Missing()
        {
            var img = new PixelImage(16, 16);
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    img.SetTopDown(x, y, ((x < 8) ^ (y < 8)) ? Black : Magenta);
            return img;
        }

        public static Texture2D NewTexture(int w, int h, string name, FilterMode filter = FilterMode.Point)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
            {
                name = name,
                filterMode = filter,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0
            };
            return t;
        }

        public static Texture2D ToTexture(PixelImage img, string name, FilterMode filter = FilterMode.Point)
        {
            var t = NewTexture(img.W, img.H, name, filter);
            t.SetPixels32(img.Px);
            t.Apply(false, false);
            return t;
        }

        /// <summary>Parses "RRGGBB" (or "#RRGGBB"). Returns false on bad input.</summary>
        public static bool TryParseHex(string hex, out Color32 c)
        {
            c = new Color32(255, 255, 255, 255);
            if (string.IsNullOrEmpty(hex)) return false;
            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            c = new Color32((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF), 255);
            return true;
        }

        /// <summary>Multiplies RGB by the tint (alpha unchanged), in place.</summary>
        public static void Tint(PixelImage img, Color32 tint)
        {
            var p = img.Px;
            for (int i = 0; i < p.Length; i++)
            {
                var c = p[i];
                p[i] = new Color32((byte)(c.r * tint.r / 255), (byte)(c.g * tint.g / 255), (byte)(c.b * tint.b / 255), c.a);
            }
        }

        /// <summary>Alpha-composites <paramref name="over"/> onto <paramref name="dst"/> (same size), in place ("over" operator).</summary>
        public static void Over(PixelImage dst, PixelImage over)
        {
            var d = dst.Px; var o = over.Px;
            int n = Math.Min(d.Length, o.Length);
            for (int i = 0; i < n; i++)
            {
                var s = o[i];
                if (s.a == 0) continue;
                if (s.a == 255) { d[i] = s; continue; }
                var b = d[i];
                float sa = s.a / 255f, ba = b.a / 255f;
                float oa = sa + ba * (1f - sa);
                if (oa <= 0f) { d[i] = Clear; continue; }
                float r = (s.r * sa + b.r * ba * (1f - sa)) / oa;
                float g = (s.g * sa + b.g * ba * (1f - sa)) / oa;
                float bl = (s.b * sa + b.b * ba * (1f - sa)) / oa;
                d[i] = new Color32((byte)Mathf.Clamp(r + 0.5f, 0, 255), (byte)Mathf.Clamp(g + 0.5f, 0, 255), (byte)Mathf.Clamp(bl + 0.5f, 0, 255), (byte)Mathf.Clamp(oa * 255f + 0.5f, 0, 255));
            }
        }

        /// <summary>Nearest-neighbour resample (used to fit high-res resource textures into 16px atlas cells).</summary>
        public static PixelImage Resize(PixelImage src, int w, int h)
        {
            if (src.W == w && src.H == h) return src.Copy();
            var dst = new PixelImage(w, h);
            for (int y = 0; y < h; y++)
            {
                int sy = Math.Min(src.H - 1, (int)((y + 0.5f) * src.H / h));
                for (int x = 0; x < w; x++)
                {
                    int sx = Math.Min(src.W - 1, (int)((x + 0.5f) * src.W / w));
                    dst.Px[y * w + x] = src.Px[sy * src.W + sx];
                }
            }
            return dst;
        }

        /// <summary>Reads any texture (even non-readable GPU-only ones) into a new readable texture via a RenderTexture.</summary>
        public static Texture2D CopyReadable(Texture src, Rect srcRect, string name, FilterMode filter)
        {
            var prev = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                int w = Mathf.Max(1, Mathf.RoundToInt(srcRect.width)), h = Mathf.Max(1, Mathf.RoundToInt(srcRect.height));
                var t = NewTexture(w, h, name, filter);
                t.ReadPixels(new Rect(srcRect.x, srcRect.y, w, h), 0, 0, false);
                t.Apply(false, false);
                return t;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
