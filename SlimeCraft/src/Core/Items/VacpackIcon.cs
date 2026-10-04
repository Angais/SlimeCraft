using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Icon for "slimecraft:vacpack" — ONE Texture2D object for the whole session whose pixels are replaced in place
    /// (the HUD draws it with full UVs every frame, so every cached reference picks the new art up immediately).
    /// Sources, best first:
    /// 1. the real SR vacpack model rendered by <see cref="VacpackIconRenderer"/> ~1 s after a world loads
    ///    (config Core.VacpackIconRender), saved to SC.DataDir/vacpack_icon.png (+ the 256² render as
    ///    vacpack_icon_render.png for inspection);
    /// 2. that cached PNG from an earlier session (used from the first frame, before any world is loaded);
    /// 3. SR's shop sprite 'iconShopTank01' (sharedassets2, loaded with the world), pixelated the same way;
    /// 4. the hand-drawn 16x16 pixel-art placeholder.
    /// </summary>
    internal sealed class VacpackIcon
    {
        private const int IconPixels = 32;
        private const int MaxRenderAttempts = 8;
        private const string FallbackSprite = "iconShopTank01";

        private Texture2D tex;
        private string source = "none";
        private bool pending;
        private int attempts;
        private float nextAttempt;
        private bool spriteTried;

        public string Source => source;

        private static string CachePath => string.IsNullOrEmpty(SC.DataDir) ? null : Path.Combine(SC.DataDir, "vacpack_icon.png");
        private static string RenderPath => string.IsNullOrEmpty(SC.DataDir) ? null : Path.Combine(SC.DataDir, "vacpack_icon_render.png");

        public Texture2D Texture
        {
            get
            {
                if (tex == null)
                {
                    tex = Pixels.ToTexture(Placeholder(), "icon:" + Content.Vacpack);
                    tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    source = "placeholder";
                    TryLoadCache();
                }
                return tex;
            }
        }

        /// <summary>World loaded: (re-)render the icon from the live model shortly after (the weapon gets posed first).</summary>
        public void OnWorldLoaded()
        {
            pending = true;
            attempts = 0;
            nextAttempt = Time.unscaledTime + 1f;
        }

        /// <summary>Per frame (cheap when nothing is pending). Returns true when the icon pixels changed.</summary>
        public bool Tick()
        {
            if (!pending) return false;
            var bridge = CoreRuntime.Bridge;
            if (bridge == null || !bridge.InGame) return false;
            if (Time.unscaledTime < nextAttempt) return false;
            var t = Texture; // make sure the texture object exists
            attempts++;

            bool renderOn = CoreConfig.VacpackIconRender == null || CoreConfig.VacpackIconRender.Value;
            if (renderOn)
            {
                try
                {
                    var vac = bridge.Vacpack.Vac;
                    if (vac == null && bridge.Player != null) vac = bridge.Player.GetComponentInChildren<WeaponVacuum>(true);
                    if (vac != null)
                    {
                        var res = VacpackIconRenderer.Render(vac, bridge.MainCamera, IconPixels,
                            CoreConfig.VacpackIconGlass == null || CoreConfig.VacpackIconGlass.Value,
                            CoreConfig.VacpackIconOutline == null || CoreConfig.VacpackIconOutline.Value);
                        if (res != null)
                        {
                            Set(res.Icon, "render");
                            pending = false;
                            CoreLog.Info("Vacpack icon rendered from the SR vacpack model: " + res.Info);
                            Save(res.Icon, CachePath);
                            Save(res.Full, RenderPath);
                            return true;
                        }
                    }
                }
                catch (Exception e) { CoreLog.Rate("vacpack icon render", e, 30f); }
            }

            if (renderOn && attempts < MaxRenderAttempts)
            {
                nextAttempt = Time.unscaledTime + 1f;
                return false;
            }
            pending = false;
            if (renderOn) CoreLog.Warn("Vacpack icon: could not render the SR vacpack model (" + attempts + " attempts); using the " +
                                       (source == "cache" ? "cached icon" : "fallback"));
            if (source == "placeholder" || (!renderOn && source != "sr-sprite")) return TrySprite();
            return false;
        }

        private void Set(PixelImage img, string src)
        {
            var t = Texture;
            if (t.width != img.W || t.height != img.H) t.Resize(img.W, img.H);
            t.SetPixels32(img.Px);
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            t.Apply(false, false);
            source = src;
        }

        private void TryLoadCache()
        {
            if (CoreConfig.VacpackIconRender != null && !CoreConfig.VacpackIconRender.Value) return;
            string path = CachePath;
            if (path == null || !File.Exists(path)) return;
            Texture2D tmp = null;
            try
            {
                tmp = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (!tmp.LoadImage(File.ReadAllBytes(path), false) || tmp.width < 8 || tmp.width > 256 || tmp.height < 8 || tmp.height > 256)
                {
                    CoreLog.Warn("Vacpack icon cache '" + path + "' is not a valid icon PNG; ignored");
                    return;
                }
                Set(new PixelImage(tmp.width, tmp.height, tmp.GetPixels32()), "cache");
                CoreLog.Info("Vacpack icon: using the cached render " + path + " (" + tmp.width + "x" + tmp.height + ") until the world loads");
            }
            catch (Exception e) { CoreLog.Warn("Vacpack icon cache unreadable: " + e.Message); }
            finally { if (tmp != null) UnityEngine.Object.Destroy(tmp); }
        }

        private static void Save(PixelImage img, string path)
        {
            if (img == null || path == null) return;
            Texture2D t = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                t = Pixels.ToTexture(img, "vacpack_icon_save");
                File.WriteAllBytes(path, t.EncodeToPNG());
            }
            catch (Exception e) { CoreLog.Warn("Could not save " + path + ": " + e.Message); }
            finally { if (t != null) UnityEngine.Object.Destroy(t); }
        }

        /// <summary>SR's 'iconShopTank01' sprite, pixelated like the rendered icon. Returns true when the pixels changed.</summary>
        private bool TrySprite()
        {
            if (spriteTried) return false;
            spriteTried = true;
            try
            {
                Sprite sprite = null;
                foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (s != null && s.texture != null && s.name == FallbackSprite) { sprite = s; break; }
                if (sprite == null) { CoreLog.Info("Vacpack icon: SR sprite '" + FallbackSprite + "' not loaded; keeping the pixel-art placeholder"); return false; }
                var copy = Pixels.CopyReadable(sprite.texture, sprite.textureRect, "vacpack_sr", FilterMode.Bilinear);
                PixelImage icon;
                try
                {
                    icon = VacpackIconRenderer.IconFromPixels(copy.GetPixels32(), copy.width, copy.height, IconPixels,
                        CoreConfig.VacpackIconOutline == null || CoreConfig.VacpackIconOutline.Value);
                }
                finally { UnityEngine.Object.Destroy(copy); }
                if (icon == null) return false;
                Set(icon, "sr-sprite");
                CoreLog.Info("Vacpack icon: using SR sprite '" + sprite.name + "' (pixelated to " + IconPixels + "x" + IconPixels + ")");
                return true;
            }
            catch (Exception e) { CoreLog.Rate("vacpack icon sprite", e, 60f); return false; }
        }

        /// <summary>16x16 pixel-art vacpack (tank + nozzle) in Minecraft item style.</summary>
        private static PixelImage Placeholder()
        {
            string[] art =
            {
                "................",
                "................",
                "..........KK....",
                ".........KccK...",
                "....KKKKKKccK...",
                "...KggggggKK....",
                "..KgGGGGGGgK....",
                "..KgGwwGGGgK....",
                "..KgGGGGGGgKKK..",
                "..KgGGGGGGgKbbK.",
                "..KgGGGGGGgKbbK.",
                "..KgggggggKKKK..",
                "...KKKKKKKK.....",
                "....KyK.KyK.....",
                "....KKK.KKK.....",
                "................",
            };
            var pal = new Dictionary<char, Color32>
            {
                { 'K', new Color32(0x2B, 0x23, 0x30, 255) },
                { 'g', new Color32(0x5D, 0x8A, 0xA8, 255) },
                { 'G', new Color32(0x8C, 0xC7, 0xE0, 255) },
                { 'w', new Color32(0xF2, 0xFB, 0xFF, 255) },
                { 'c', new Color32(0xB8, 0xB8, 0xC4, 255) },
                { 'b', new Color32(0xFF, 0x8F, 0xC8, 255) },
                { 'y', new Color32(0xF5, 0xC8, 0x42, 255) },
            };
            var img = new PixelImage(16, 16);
            for (int y = 0; y < 16; y++)
                for (int x = 0; x < 16; x++)
                    img.SetTopDown(x, y, pal.TryGetValue(art[y][x], out var c) ? c : Pixels.Clear);
            return img;
        }
    }
}
