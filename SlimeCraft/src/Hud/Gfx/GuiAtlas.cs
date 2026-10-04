using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// A rectangle of pixels that can be drawn by <see cref="Gui"/>: either packed into the shared GUI atlas
    /// (so most of the HUD renders in a single batch) or a standalone texture (non-readable sources, SR sprites).
    /// Also carries the sprite's "gui.scaling" metadata from its .png.mcmeta (stretch / tile / nine_slice).
    /// </summary>
    internal sealed class GuiRegion
    {
        public Texture Tex;
        /// <summary>Pixel rect inside <see cref="Tex"/>; Y is measured from the bottom (Unity texture space).</summary>
        public int X, Y, W, H;
        public int TexW, TexH;
        public bool InAtlas;

        public const int ScaleStretch = 0, ScaleTile = 1, ScaleNineSlice = 2;
        public int Scaling;
        /// <summary>Declared logical size of the sprite (mcmeta width/height; defaults to the pixel size).</summary>
        public int SW, SH;
        public int BL, BT, BR, BB;
        public bool StretchInner;

        /// <summary>
        /// Normalized UVs of a sub-rectangle given in top-down pixels of a sprite whose logical size is
        /// spaceW x spaceH (that space is laid over the whole region). vTop &gt; vBottom (Unity UVs grow upwards).
        /// </summary>
        public void UV(float sx, float sy, float sw, float sh, float spaceW, float spaceH,
            out float u0, out float vTop, out float u1, out float vBottom)
        {
            float fx0 = sx / spaceW, fx1 = (sx + sw) / spaceW;
            float fy0 = sy / spaceH, fy1 = (sy + sh) / spaceH;
            u0 = (X + fx0 * W) / TexW;
            u1 = (X + fx1 * W) / TexW;
            vTop = (Y + (1f - fy0) * H) / TexH;
            vBottom = (Y + (1f - fy1) * H) / TexH;
        }
    }

    /// <summary>
    /// Runtime texture atlas for every Minecraft GUI sprite/texture/font page the HUD uses (shelf packed,
    /// 1px padding, point filtered). Sprites are loaded on demand from the jar through SC.Assets.
    /// </summary>
    internal sealed class GuiAtlas
    {
        public const int Size = 2048;

        public Texture2D Texture { get; private set; }
        public GuiRegion White { get; private set; }

        private readonly Dictionary<string, GuiRegion> regions = new Dictionary<string, GuiRegion>();
        private int shelfX, shelfY, shelfH;
        private bool dirty;
        private bool full;

        public bool Created => Texture != null;

        public void Create()
        {
            if (Texture != null) return;
            Texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "SlimeCraft.GuiAtlas",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
                hideFlags = HideFlags.HideAndDontSave
            };
            // clear once so padding texels are transparent (rotated / scaled text may sample them)
            var clear = new Color32[Size * Size];
            Texture.SetPixels32(clear);
            // 4x4 opaque white block for fills; sampled at its center
            var white = new Color32[16];
            for (int i = 0; i < 16; i++) white[i] = new Color32(255, 255, 255, 255);
            White = AddPixels("slimecraft:white", 4, 4, white);
            Texture.Apply(false, false);
        }

        /// <summary>Region for "assets/minecraft/textures/{path}.png"; null while assets are not ready.</summary>
        public GuiRegion Get(string path)
        {
            if (path == null) return null;
            if (regions.TryGetValue(path, out var r)) return r;
            if (SC.Assets == null || !SC.Assets.Ready || Texture == null) return null;
            try
            {
                var src = SC.Assets.GetTexture(path);
                if (src == null) return null;
                r = AddTexture(path, src);
                ReadGuiScaling(path, r);
            }
            catch (Exception e)
            {
                SC.Log?.LogWarning("[Hud] atlas: cannot load " + path + ": " + e.Message);
                r = null;
            }
            regions[path] = r;
            return r;
        }

        public bool TryGetCached(string key, out GuiRegion r) => regions.TryGetValue(key, out r);

        /// <summary>Adds a texture under a key (copied into the atlas when readable).</summary>
        public GuiRegion AddTexture(string key, Texture2D src)
        {
            Color32[] px = null;
            try { px = src.GetPixels32(); } catch { px = null; }
            GuiRegion r = null;
            if (px != null) r = AddPixels(key, src.width, src.height, px);
            if (r == null)
            {
                r = new GuiRegion { Tex = src, X = 0, Y = 0, W = src.width, H = src.height, TexW = src.width, TexH = src.height };
            }
            r.SW = r.W; r.SH = r.H;
            regions[key] = r;
            return r;
        }

        /// <summary>Copies raw pixels (bottom-up rows, Unity order) into the atlas. Null if it does not fit.</summary>
        public GuiRegion AddPixels(string key, int w, int h, Color32[] px)
        {
            if (Texture == null || full || w <= 0 || h <= 0) return null;
            if (!Allocate(w, h, out int x, out int y)) return null;
            Texture.SetPixels32(x, y, w, h, px);
            dirty = true;
            var r = new GuiRegion { Tex = Texture, X = x, Y = y, W = w, H = h, TexW = Size, TexH = Size, InAtlas = true, SW = w, SH = h };
            regions[key] = r;
            return r;
        }

        private bool Allocate(int w, int h, out int x, out int y)
        {
            x = y = 0;
            int pw = w + 2, ph = h + 2;
            if (pw > Size || ph > Size) return false;
            if (shelfX + pw > Size) { shelfY += shelfH; shelfX = 0; shelfH = 0; }
            if (shelfY + ph > Size)
            {
                if (!full) SC.Log?.LogWarning("[Hud] GUI atlas is full; further sprites use their own textures");
                full = true;
                return false;
            }
            x = shelfX + 1; y = shelfY + 1;
            shelfX += pw;
            if (ph > shelfH) shelfH = ph;
            return true;
        }

        public void ApplyIfDirty()
        {
            if (!dirty || Texture == null) return;
            dirty = false;
            Texture.Apply(false, false);
        }

        /// <summary>Reads {"gui":{"scaling":{...}}} from the sprite's .png.mcmeta (nine_slice / tile / stretch).</summary>
        private static void ReadGuiScaling(string path, GuiRegion r)
        {
            if (r == null || !path.StartsWith("gui/sprites/", StringComparison.Ordinal)) return;
            string meta = "assets/minecraft/textures/" + path + ".png.mcmeta";
            if (!SC.Assets.JarExists(meta)) return;
            var json = SC.Assets.ReadJarJson(meta);
            if (json == null) return;
            var sc = json["gui"]["scaling"];
            if (!sc.IsObject) return;
            string type = sc["type"].AsString("stretch");
            r.SW = sc["width"].AsInt(r.W);
            r.SH = sc["height"].AsInt(r.H);
            if (type == "nine_slice")
            {
                r.Scaling = GuiRegion.ScaleNineSlice;
                var b = sc["border"];
                if (b.IsObject)
                {
                    r.BL = b["left"].AsInt(); r.BT = b["top"].AsInt(); r.BR = b["right"].AsInt(); r.BB = b["bottom"].AsInt();
                }
                else
                {
                    r.BL = r.BT = r.BR = r.BB = b.AsInt();
                }
                r.StretchInner = sc["stretch_inner"].AsBool(false);
            }
            else if (type == "tile")
            {
                r.Scaling = GuiRegion.ScaleTile;
            }
        }
    }
}
