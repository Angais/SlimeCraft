using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// <see cref="IBlockAtlas"/>: one point-filtered RGBA atlas holding every block face spec of <see cref="Content.Blocks"/>.
    /// Tiles are 16x16 in 20x20 cells with a 2px edge-extruded border (no bleeding). The atlas is allocated with
    /// generous spare capacity (power of two, at least 512²) so on-demand additions never move existing tiles:
    /// UV rects handed out stay valid forever; additions are re-uploaded immediately.
    /// </summary>
    internal sealed class BlockAtlas : IBlockAtlas
    {
        private const int Tile = TextureSpec.Size;
        private const int Pad = 2;
        private const int Cell = Tile + 2 * Pad;

        private readonly McAssets assets;
        private readonly TextureSpec specs;
        private readonly Dictionary<string, Rect> uvs = new Dictionary<string, Rect>(StringComparer.Ordinal);
        private Texture2D texture;
        private Color32[] pixels;
        private int size, cols, used;
        private Material cutout, translucent, emissive;
        private Rect missingUv;

        public BlockAtlas(McAssets assets)
        {
            this.assets = assets;
            specs = new TextureSpec(assets);
        }

        internal TextureSpec Specs => specs;
        public Texture2D Texture => texture;

        public void Build()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var all = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in Content.Blocks)
                foreach (var f in b.Faces)
                    if (!string.IsNullOrEmpty(f) && seen.Add(f)) all.Add(f);
            // destroy-stage crack overlays are commonly needed by the block module; keep them in the atlas too
            for (int i = 0; i < 10; i++) { var f = "block/destroy_stage_" + i; if (seen.Add(f)) all.Add(f); }

            int needed = all.Count + 1; // + missing tile
            int capacity = Math.Max(needed * 2, needed + 256);
            size = 512;
            while ((size / Cell) * (size / Cell) < capacity && size < 4096) size *= 2;
            cols = size / Cell;
            pixels = new Color32[size * size];
            texture = Pixels.NewTexture(size, size, "SlimeCraft_BlockAtlas");

            missingUv = Place(Pixels.Missing());
            int missing = 0;
            foreach (var spec in all)
            {
                if (!specs.AllLayersExist(spec)) { missing++; CoreLog.Debug("atlas: missing texture for spec '" + spec + "'"); }
                uvs[spec] = Place(specs.Evaluate(spec));
            }
            Upload();
            CoreLog.Info("Block atlas " + size + "x" + size + ": " + all.Count + " tiles (" + missing + " missing) in " + sw.ElapsedMilliseconds + " ms");
        }

        public Rect GetUV(string textureSpec)
        {
            if (string.IsNullOrEmpty(textureSpec)) return missingUv;
            if (uvs.TryGetValue(textureSpec, out var r)) return r;
            if (used >= cols * cols)
            {
                CoreLog.Rate("atlas full", "Block atlas is full; '" + textureSpec + "' shows as missing", 60f);
                uvs[textureSpec] = missingUv;
                return missingUv;
            }
            r = Place(specs.Evaluate(textureSpec));
            uvs[textureSpec] = r;
            CoreLog.Debug("atlas: added '" + textureSpec + "' on demand");
            Upload(); // rare (all Content specs are pre-baked); upload now so the tile is valid this frame
            return r;
        }

        private void Upload()
        {
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        /// <summary>Copies a 16x16 tile into the next free cell with a 2px extruded border, returns the inset UV rect.</summary>
        private Rect Place(PixelImage img)
        {
            if (img.W != Tile || img.H != Tile) img = Pixels.Resize(img, Tile, Tile);
            int cx = (used % cols) * Cell, cy = (used / cols) * Cell;
            used++;
            for (int y = -Pad; y < Tile + Pad; y++)
            {
                int sy = Mathf.Clamp(y, 0, Tile - 1);
                int row = (cy + Pad + y) * size;
                for (int x = -Pad; x < Tile + Pad; x++)
                {
                    int sx = Mathf.Clamp(x, 0, Tile - 1);
                    pixels[row + cx + Pad + x] = img.Px[sy * Tile + sx];
                }
            }
            float inset = Mathf.Clamp(CoreConfig.AtlasUvInsetTexels?.Value ?? 0.02f, 0f, 0.5f);
            float x0 = (cx + Pad + inset) / size, y0 = (cy + Pad + inset) / size;
            float w = (Tile - 2f * inset) / size;
            return new Rect(x0, y0, w, w);
        }

        public Material Cutout
        {
            get
            {
                if (cutout == null) cutout = MaterialFactory.CreateLit(texture, 0.5f, "BlockAtlasCutout");
                return cutout;
            }
        }

        public Material Translucent
        {
            get
            {
                if (translucent == null) translucent = MaterialFactory.CreateTranslucent(texture, "BlockAtlasTranslucent");
                return translucent;
            }
        }

        /// <summary>Full-bright alpha-tested atlas material for light-emitting blocks (glowstone, sea lantern...).</summary>
        public Material Emissive
        {
            get
            {
                if (emissive == null) emissive = MaterialFactory.CreateEmissive(texture, "BlockAtlasEmissive");
                return emissive;
            }
        }
    }
}
