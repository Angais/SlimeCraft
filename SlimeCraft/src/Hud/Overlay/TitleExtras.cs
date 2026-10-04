using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Title screen extras on Slime Rancher's main menu (MainMenuUI is active only while the main menu panel is
    /// shown): a yellow splash tilted by 20° that gently pulses and shrinks for long lines, matching the look of
    /// Minecraft's title splash, anchored to the RIGHT end of the lower edge of SR's logo (where Minecraft puts it
    /// relative to its own logo), and a version line in the bottom-left corner.
    /// <para>Logo placement: Minecraft draws its 256x44 logo at (width/2 - 128, 30) and the splash at
    /// (width/2 + 123, 69), i.e. at 251/256 of the logo width and 39/44 of its height. The SR logo graphic
    /// (the main-menu Image whose sprite is "logoTitle", or a graphic named "TitleImage"/"*logo*") is measured from
    /// its RectTransform world corners (respecting preserveAspect), trimmed to the opaque pixels of its sprite
    /// (one GPU read-back per sprite), and the same fractions are applied to that rectangle.</para>
    /// </summary>
    internal sealed class TitleExtras
    {
        private MainMenuUI menu, lastMenu;
        private float nextScan, nextLogoScan;
        private Graphic logo;
        private string splash;
        private List<string> splashes;
        private bool loggedRect;

        // opaque part of a logo texture region, as fractions of the drawn rect (top-down)
        private static readonly Dictionary<int, Rect> opaqueCache = new Dictionary<int, Rect>();

        private static readonly string[] OwnSplashes =
        {
            "Now with blocks!", "Plort-tastic!", "Slimes not included!", "Also try Minecraft!", "Vacpack compatible!",
            "Largo-sized!", "Tarr-free!", "Mind the Tarr!", "Punch trees, vac slimes!", "Creeper + slime = ?",
            "Beatrix approved!", "The Far, Far Range!", "100% pure plorts!", "Rancher by day, miner by night!"
        };

        public bool Active { get; private set; }

        public void Update()
        {
            if (!HudConfig.TitleExtras) { GiveBackCoreSplash(); Active = false; return; }
            if (SC.SR != null && SC.SR.InGame) { Active = false; menu = null; logo = null; return; }
            if (Time.unscaledTime >= nextScan)
            {
                nextScan = Time.unscaledTime + 0.5f;
                TakeOverCoreSplash(); // no-op while held; re-takes it if TitleScreenExtras was switched back on
                MainMenuUI found = null;
                try { found = UnityEngine.Object.FindObjectOfType<MainMenuUI>(); } catch { found = null; }
                if (found != menu)
                {
                    menu = found;
                    logo = null; loggedRect = false; nextLogoScan = 0f;
                    // a new splash per title screen visit; coming back from Options/Load (SR only deactivates the
                    // same MainMenuUI meanwhile) keeps the current one, as Minecraft does
                    if (menu != null && (menu != lastMenu || splash == null)) { lastMenu = menu; PickSplash(); }
                }
            }
            Active = menu != null && menu.isActiveAndEnabled && !HudModule.SRLoading();
            if (Active && (logo == null || !logo.isActiveAndEnabled) && Time.unscaledTime >= nextLogoScan)
            {
                nextLogoScan = Time.unscaledTime + 1f;
                FindLogo();
            }
        }

        private void PickSplash()
        {
            if (splashes == null || (splashes.Count == 0 && SC.Assets != null && SC.Assets.Ready))
            {
                splashes = new List<string>();
                try
                {
                    string txt = SC.Assets != null && SC.Assets.Ready ? SC.Assets.ReadJarText("assets/minecraft/texts/splashes.txt") : null;
                    if (txt != null)
                        foreach (var l in txt.Replace("\r", "").Split('\n'))
                        {
                            var t = l.Trim();
                            if (t.Length > 0 && !t.Contains("§")) splashes.Add(t);
                        }
                }
                catch (Exception e) { SC.Log?.LogWarning("[Hud] splashes.txt: " + e.Message); }
            }
            // SlimeCraft's own holiday lines (original text, not Minecraft's)
            var now = DateTime.Now;
            if (now.Month == 12 && now.Day >= 24 && now.Day <= 26) { splash = "Wiggly Wonderland is here!"; return; }
            if (now.Month == 1 && now.Day == 1) { splash = "A new year on the Far, Far Range!"; return; }
            if (now.Month == 10 && now.Day == 31) { splash = "The Tarr come out tonight!"; return; }
            var rnd = new System.Random();
            if (splashes.Count == 0 || rnd.Next(4) == 0) splash = OwnSplashes[rnd.Next(OwnSplashes.Length)];
            else splash = splashes[rnd.Next(splashes.Count)];
        }

        // ------------------------------------------------------------------ logo
        /// <summary>
        /// Finds SR's logo: best score wins among the active Images/RawImages that are not part of a button:
        /// sprite "logoTitle" (100) &gt; sprite "*logo*" (80) &gt; object "TitleImage" (70) &gt; object "*logo*" (60) &gt;
        /// object "*title*" with a picture (20).
        /// </summary>
        private void FindLogo()
        {
            try
            {
                Graphic best = null;
                int bestScore = 0;
                foreach (var gr in UnityEngine.Object.FindObjectsOfType<Graphic>())
                {
                    if (gr == null || !gr.isActiveAndEnabled) continue;
                    var img = gr as Image;
                    var raw = gr as RawImage;
                    if (img == null && raw == null) continue;
                    if (gr.GetComponentInParent<Button>() != null) continue;
                    string n = gr.gameObject.name.ToLowerInvariant();
                    string pic = null;
                    if (img != null && img.sprite != null) pic = img.sprite.name.ToLowerInvariant();
                    else if (raw != null && raw.texture != null) pic = raw.texture.name.ToLowerInvariant();
                    if (pic != null && pic.Contains("monomi")) continue; // studio logo, not the game logo
                    int score = 0;
                    if (pic == "logotitle") score = 100;
                    else if (pic != null && pic.Contains("logo")) score = 80;
                    else if (n == "titleimage") score = 70;
                    else if (n.Contains("logo")) score = 60;
                    else if (n.Contains("title") && pic != null) score = 20;
                    if (score > bestScore) { bestScore = score; best = gr; }
                }
                if (best != logo)
                {
                    logo = best;
                    loggedRect = false;
                    if (best != null)
                    {
                        var img = best as Image;
                        string pic = img != null && img.sprite != null ? img.sprite.name : (best as RawImage)?.texture?.name;
                        SC.Log?.LogInfo("[Hud] title splash anchored to logo graphic '" + Path(best.transform) + "' (picture '" + (pic ?? "none") + "', score " + bestScore + ")");
                    }
                    else SC.Log?.LogInfo("[Hud] no logo graphic found on the main menu yet; splash uses Minecraft's default position");
                }
            }
            catch (Exception e) { SC.Log?.LogWarning("[Hud] logo search failed: " + e.Message); }
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            for (var x = t.parent; x != null; x = x.parent) p = x.name + "/" + p;
            return p;
        }

        private static readonly Vector3[] corners = new Vector3[4];

        /// <summary>The visible (opaque) logo rectangle in GUI pixels (origin top-left, y down).</summary>
        private bool TryLogoRect(int scale, out Rect gui)
        {
            gui = default(Rect);
            if (logo == null || !logo.isActiveAndEnabled) return false;
            var rt = logo.rectTransform;
            rt.GetWorldCorners(corners); // 0 bottom-left, 1 top-left, 2 top-right, 3 bottom-right
            Canvas root = logo.canvas != null ? logo.canvas.rootCanvas : null;
            Camera cam = root != null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 4; i++)
            {
                Vector2 p = RectTransformUtility.WorldToScreenPoint(cam, corners[i]);
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
            }
            float w = x1 - x0, h = y1 - y0;
            if (w < 8f || h < 4f) return false;

            // Image.preserveAspect shrinks the drawn quad around the pivot (Image.PreserveSpriteAspectRatio)
            Texture tex = null;
            Rect uv = new Rect(0f, 0f, 1f, 1f);
            var img = logo as Image;
            var raw = logo as RawImage;
            if (img != null && img.sprite != null)
            {
                var sp = img.sprite;
                if (img.preserveAspect && img.type == Image.Type.Simple && sp.rect.height > 0f)
                {
                    float spriteAspect = sp.rect.width / sp.rect.height;
                    if (w / h > spriteAspect)
                    {
                        float nw = h * spriteAspect;
                        x0 += (w - nw) * rt.pivot.x; w = nw;
                    }
                    else
                    {
                        float nh = w / spriteAspect;
                        y0 += (h - nh) * rt.pivot.y; h = nh;
                    }
                    x1 = x0 + w; y1 = y0 + h;
                }
                if (img.type == Image.Type.Simple)
                {
                    tex = sp.texture;
                    try
                    {
                        Vector4 o = UnityEngine.Sprites.DataUtility.GetOuterUV(sp);
                        uv = Rect.MinMaxRect(o.x, o.y, o.z, o.w);
                    }
                    catch { tex = null; }
                }
            }
            else if (raw != null && raw.texture != null)
            {
                tex = raw.texture;
                uv = raw.uvRect;
            }

            // trim to the opaque pixels of the logo picture (transparent margins are common in logo textures)
            Rect frac = new Rect(0f, 0f, 1f, 1f);
            if (tex != null) frac = OpaqueFraction(tex, uv);

            float vx0 = x0 + frac.xMin * w, vx1 = x0 + frac.xMax * w;
            float vTop = y1 - frac.yMin * h, vBottom = y1 - frac.yMax * h; // screen y up
            float sh = UnityEngine.Screen.height;
            gui = Rect.MinMaxRect(vx0 / scale, (sh - vTop) / scale, vx1 / scale, (sh - vBottom) / scale);
            if (!loggedRect)
            {
                loggedRect = true;
                SC.Log?.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[Hud] title logo on screen: rect ({0:0},{1:0})-({2:0},{3:0}) px, opaque part ({4:0},{5:0})-({6:0},{7:0}) px (top-left origin)",
                    x0, sh - y1, x1, sh - y0, vx0, sh - vTop, vx1, sh - vBottom));
            }
            return gui.width > 8f && gui.height > 4f;
        }

        /// <summary>
        /// Opaque bounds (alpha &gt; ~0.15) of a texture region as fractions of the region, top-down. The texture is
        /// usually not CPU-readable, so it is blitted to a temporary RenderTexture and read back once (cached).
        /// </summary>
        private static Rect OpaqueFraction(Texture tex, Rect uv)
        {
            int key = tex.GetInstanceID() * 31 + uv.GetHashCode();
            if (opaqueCache.TryGetValue(key, out var cached)) return cached;
            Rect result = new Rect(0f, 0f, 1f, 1f);
            RenderTexture rt = null;
            Texture2D tmp = null;
            RenderTexture prev = RenderTexture.active;
            try
            {
                int tw = tex.width, th = tex.height;
                int px0 = Mathf.Clamp(Mathf.RoundToInt(uv.xMin * tw), 0, tw), px1 = Mathf.Clamp(Mathf.RoundToInt(uv.xMax * tw), 0, tw);
                int py0 = Mathf.Clamp(Mathf.RoundToInt(uv.yMin * th), 0, th), py1 = Mathf.Clamp(Mathf.RoundToInt(uv.yMax * th), 0, th);
                int w = px1 - px0, h = py1 - py0;
                if (w > 0 && h > 0 && w <= 8192 && h <= 8192)
                {
                    rt = RenderTexture.GetTemporary(tw, th, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    Graphics.Blit(tex, rt);
                    RenderTexture.active = rt;
                    tmp = new Texture2D(w, h, TextureFormat.RGBA32, false);
                    tmp.ReadPixels(new Rect(px0, py0, w, h), 0, 0, false);
                    var px = tmp.GetPixels32();
                    int minX = w, minY = h, maxX = -1, maxY = -1;
                    for (int y = 0; y < h; y++)
                    {
                        int row = y * w;
                        for (int x = 0; x < w; x++)
                        {
                            if (px[row + x].a <= 40) continue;
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                    // Unity pixel rows are bottom-up → convert to top-down fractions
                    if (maxX >= 0)
                        result = Rect.MinMaxRect(minX / (float)w, 1f - (maxY + 1) / (float)h, (maxX + 1) / (float)w, 1f - minY / (float)h);
                    SC.Log?.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "[Hud] title logo '{0}' {1}x{2}: opaque part x {3:0.000}-{4:0.000}, y {5:0.000}-{6:0.000} (top-down fractions)",
                        tex.name, w, h, result.xMin, result.xMax, result.yMin, result.yMax));
                }
            }
            catch (Exception e)
            {
                SC.Log?.LogWarning("[Hud] logo alpha bounds unavailable (" + e.Message + "); using the full logo rect");
                result = new Rect(0f, 0f, 1f, 1f);
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (tmp != null) UnityEngine.Object.Destroy(tmp);
            }
            opaqueCache[key] = result;
            return result;
        }

        // ------------------------------------------------------------------ Core splash hand-over
        private static ConfigEntry<bool> coreSplashEntry; // non-null while the Hud holds Core's splash off
        private static bool hooksInstalled, ownChange, configSavedSinceTakeover, takeoverLogged;

        /// <summary>
        /// The Core module also renders a (fixed-position) title splash, config [Core] ShowSplash. Only one splash
        /// must be visible and the Hud's one follows the SR logo, so while [Hud] TitleScreenExtras is on the Core
        /// entry is switched off in memory (looked up by key, no type reference). The config FILE keeps the user's
        /// value: SaveOnConfigSet is suspended for the change, and if another setting saved the file meanwhile, the
        /// original value is written back when the Hud gives the splash back (TitleScreenExtras turned off, or quit).
        /// </summary>
        public static void TakeOverCoreSplash()
        {
            if (!HudConfig.TitleExtras || coreSplashEntry != null) return;
            try
            {
                var cfg = SC.Config;
                if (cfg == null || !cfg.TryGetEntry<bool>("Core", "ShowSplash", out var e) || e == null || !e.Value) return;
                if (!hooksInstalled)
                {
                    hooksInstalled = true;
                    cfg.SettingChanged += (s, a) => { if (!ownChange && coreSplashEntry != null && cfg.SaveOnConfigSet) configSavedSinceTakeover = true; };
                    Application.quitting += () => GiveBackCoreSplash();
                }
                SetWithoutSaving(cfg, e, false);
                coreSplashEntry = e;
                if (!takeoverLogged)
                {
                    takeoverLogged = true;
                    SC.Log?.LogInfo("[Hud] the logo-anchored Hud splash replaces Core's fixed-position title splash ([Core] ShowSplash is switched off in memory only; the config file keeps its value)");
                }
            }
            catch (Exception ex) { SC.Log?.LogWarning("[Hud] could not take over the Core splash: " + ex.Message); }
        }

        /// <summary>Restores [Core] ShowSplash = true (and re-saves the file if another setting saved it meanwhile).</summary>
        public static void GiveBackCoreSplash()
        {
            var e = coreSplashEntry;
            var cfg = SC.Config;
            if (e == null || cfg == null) return;
            coreSplashEntry = null;
            try
            {
                SetWithoutSaving(cfg, e, true);
                if (configSavedSinceTakeover && cfg.SaveOnConfigSet) cfg.Save();
                configSavedSinceTakeover = false;
            }
            catch { }
        }

        private static void SetWithoutSaving(ConfigFile cfg, ConfigEntry<bool> e, bool value)
        {
            bool save = cfg.SaveOnConfigSet;
            ownChange = true;
            cfg.SaveOnConfigSet = false;
            try { e.Value = value; }
            finally { cfg.SaveOnConfigSet = save; ownChange = false; }
        }

        /// <summary>True while Core still draws its own splash (Hud TitleScreenExtras off, or the hand-over failed).</summary>
        private static bool CoreDrawsSplash()
        {
            try
            {
                return SC.Config != null && SC.Config.TryGetEntry<bool>("Core", "ShowSplash", out var e) && e != null && e.Value;
            }
            catch { return false; }
        }

        // ------------------------------------------------------------------ render
        public void Render(Gui g, int scale)
        {
            if (!Active || g.Font == null || !g.Font.Loaded) return;

            if (!string.IsNullOrEmpty(splash) && !CoreDrawsSplash())
            {
                // Minecraft: logo 256x44 at (w/2 - 128, 30) → splash pivot at (w/2 + 123, 69)
                float cx = g.Width / 2f + 123f, cy = 69f;
                if (TryLogoRect(scale, out Rect r))
                {
                    cx = r.xMin + r.width * (251f / 256f);
                    cy = r.yMin + r.height * (39f / 44f);
                }
                int textWidth = g.Font.Width(splash);
                // two gentle pulses per second between 1.7x and 1.8x, normalised so every line has a similar length
                float cycle = DateTime.Now.Millisecond / 1000f;
                float pulse = 1.8f - 0.1f * Mathf.Abs(Mathf.Sin(cycle * 2f * Mathf.PI));
                float textScale = pulse * 100f / (textWidth + 32);
                // keep the whole splash on screen (the SR logo reaches further right than Minecraft's)
                float halfW = textWidth * textScale * 0.5f;
                float maxX = g.Width - 2f - halfW * Mathf.Cos(Mathf.PI / 9f);
                if (cx > maxX) cx = maxX;
                float minY = 2f + halfW * Mathf.Sin(Mathf.PI / 9f) + 8f * textScale;
                if (cy < minY) cy = minY;
                g.LayerBreak();
                g.Push();
                g.Translate(cx, cy);
                g.Rotate(-Mathf.PI / 9f);
                g.Scale(textScale, textScale);
                g.Text(splash, -textWidth / 2, -8, 0xFFFFFF00, true);
                g.Pop();
            }
            string version = "SlimeCraft " + SC.Version.Substring(0, SC.Version.LastIndexOf('.') > 0 ? SC.Version.LastIndexOf('.') : SC.Version.Length)
                             + " - Minecraft " + (SC.Assets?.McVersion ?? "?") + " inside Slime Rancher";
            g.Text(version, 2, g.Height - 10, Argb.White, true);
        }
    }
}
