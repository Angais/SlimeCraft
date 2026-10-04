using System;
using System.Collections.Generic;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Evaluates atlas texture specs (see <see cref="IBlockAtlas"/>):
    ///   "block/stone"                    plain texture
    ///   "block/oak_leaves@77AB2F"        RGB multiplied by the hex tint
    ///   "block/a|block/b@91BD59"         layers alpha-composited bottom→top, each with an optional tint
    /// Results are cached 16x16 (other sizes are resampled) CPU images shared by the atlas and the icon renderer.
    /// </summary>
    internal sealed class TextureSpec
    {
        public const int Size = 16;
        private readonly McAssets assets;
        private readonly Dictionary<string, PixelImage> cache = new Dictionary<string, PixelImage>(StringComparer.Ordinal);

        public TextureSpec(McAssets assets) { this.assets = assets; }

        /// <summary>Composited 16x16 pixels of a spec; never null (missing layers become missingno).</summary>
        public PixelImage Evaluate(string spec)
        {
            if (string.IsNullOrEmpty(spec)) spec = "missingno";
            if (cache.TryGetValue(spec, out var img)) return img;
            img = Compose(spec);
            cache[spec] = img;
            return img;
        }

        /// <summary>True if every layer texture of the spec exists in the jar.</summary>
        public bool AllLayersExist(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return false;
            foreach (var layer in spec.Split('|'))
            {
                ParseLayer(layer, out var path, out _, out _);
                if (!assets.TextureExists(path)) return false;
            }
            return true;
        }

        private PixelImage Compose(string spec)
        {
            PixelImage result = null;
            foreach (var layer in spec.Split('|'))
            {
                ParseLayer(layer, out var path, out var tint, out bool hasTint);
                var src = assets.Ready ? assets.GetPixels(path) : null;
                PixelImage px = src == null ? Pixels.Missing() : Pixels.Resize(src, Size, Size);
                if (src != null && hasTint) Pixels.Tint(px, tint);
                if (result == null) result = px;
                else Pixels.Over(result, px);
            }
            return result ?? Pixels.Missing();
        }

        private static void ParseLayer(string layer, out string path, out UnityEngine.Color32 tint, out bool hasTint)
        {
            layer = layer.Trim();
            int at = layer.IndexOf('@');
            hasTint = false;
            tint = new UnityEngine.Color32(255, 255, 255, 255);
            if (at >= 0)
            {
                hasTint = Pixels.TryParseHex(layer.Substring(at + 1), out tint);
                path = layer.Substring(0, at);
            }
            else path = layer;
        }
    }
}
