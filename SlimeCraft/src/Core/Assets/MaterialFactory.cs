using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Creates materials from shaders that exist in the Slime Rancher build (we cannot ship shaders).
    /// Lit cutout materials use SR's own "SR/Paintlight/Cutout" (so Minecraft blocks, mobs and the arm get the SR
    /// paint-light look). That shader only declares _PrimaryTex, _Depth and _Cutoff (verified in game and in the
    /// asset files); every SlimeCraft lit material gets a NEUTRAL mid-gray _Depth texture and all wind/sway props
    /// forced to 0. A loaded SR material is still used as template (keywords, render queue, stale props), but never
    /// a foliage one: e.g. 'objLeaves01' uses the Cutout shader with _SwayStrength 0.232 and its own leaf texture as
    /// _Depth. Materials created before a template was available are upgraded in place on <see cref="TryUpgrade"/>.
    /// </summary>
    internal static class MaterialFactory
    {
        public const string PaintCutout = "SR/Paintlight/Cutout";
        public const string PaintBasic = "SR/Paintlight/Basic";
        private static readonly string[] CutoutChain = { PaintCutout, "Unlit/Transparent Cutout", "Standard", "Legacy Shaders/Diffuse" };
        private static readonly string[] TranslucentChain = { "Unlit/Transparent", "Sprites/Default", "UI/Default" };
        private static readonly string[] UnlitCutoutChain = { "Unlit/Transparent Cutout", "Unlit/Texture", "Sprites/Default", "UI/Default" };
        private static readonly string[] ColorChain = { "Unlit/Color", "Sprites/Default", "UI/Default" };
        private static readonly string[] ColorAlphaChain = { "Sprites/Default", "UI/Default", "Unlit/Transparent" };

        /// <summary>Vertex-animation props of SR's foliage/paintlight shaders; always forced to 0 on our materials.</summary>
        private static readonly string[] WindProps =
        {
            "_SwayStrength", "_WindTurbulance", "_WindTurbulence", "_WindStrength", "_WindSpeed", "_Wind", "_Sway",
            "_SwayAmount", "_SwaySpeed", "_WaveStrength", "_WaveAmount", "_WaveSpeed", "_VertexOffset", "_YOffset",
        };

        /// <summary>Material names that are foliage-like (wind sway, leaf _Depth): never a template.</summary>
        private static readonly string[] FoliageWords =
        {
            "leaf", "leaves", "grass", "foliage", "petal", "flower", "plant", "bush", "fern", "vine", "weed", "reed",
            "kelp", "moss", "tree", "frond", "palm", "ivy", "shrub", "hedge", "sway", "wind",
        };

        private static readonly Dictionary<string, Shader> chosen = new Dictionary<string, Shader>();
        private static Material paintTemplate;
        private static bool templateSearched;
        private static readonly List<WeakReference> needsUpgrade = new List<WeakReference>();
        private static Texture2D white, neutralDepth;

        private static Shader Pick(string purpose, string[] chain)
        {
            if (chosen.TryGetValue(purpose, out var s) && s != null) return s;
            foreach (var name in chain)
            {
                s = Shader.Find(name);
                if (s != null && s.isSupported)
                {
                    chosen[purpose] = s;
                    CoreLog.Info("Shader for " + purpose + ": '" + name + "'");
                    return s;
                }
            }
            CoreLog.WarnOnce("noshader " + purpose, "No shader found for " + purpose + " (tried " + string.Join(", ", chain) + ")");
            return Shader.Find("Hidden/InternalErrorShader");
        }

        private static Texture2D White
        {
            get
            {
                if (white == null) white = SolidTexture("sc_white", new Color32(255, 255, 255, 255));
                return white;
            }
        }

        /// <summary>4x4 mid-gray (config Core.PaintDepthGray, default 128) used as _Depth of every Paintlight material.</summary>
        internal static Texture2D NeutralDepth
        {
            get
            {
                if (neutralDepth == null)
                {
                    byte g = (byte)Mathf.Clamp(CoreConfig.PaintDepthGray != null ? CoreConfig.PaintDepthGray.Value : 128, 0, 255);
                    neutralDepth = SolidTexture("SlimeCraft_NeutralDepth", new Color32(g, g, g, 255));
                    neutralDepth.wrapMode = TextureWrapMode.Repeat;
                }
                return neutralDepth;
            }
        }

        private static Texture2D SolidTexture(string name, Color32 c)
        {
            var t = Pixels.NewTexture(4, 4, name);
            var p = new Color32[16];
            for (int i = 0; i < p.Length; i++) p[i] = c;
            t.SetPixels32(p);
            t.Apply(false, false);
            t.hideFlags = HideFlags.DontUnloadUnusedAsset;
            return t;
        }

        private static bool IsFoliageName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            foreach (var w in FoliageWords) if (n.Contains(w)) return true;
            return false;
        }

        /// <summary>Float of a property the shader declares (Material.GetFloat on an undeclared one logs a Unity error).</summary>
        private static float SafeFloat(Material m, string prop, float def = 0f)
        {
            try { return m.HasProperty(prop) ? m.GetFloat(prop) : def; } catch { return def; }
        }

        private static string WindInfo(Material m)
        {
            var sb = new StringBuilder();
            foreach (var p in WindProps)
                if (m.HasProperty(p)) sb.Append(p).Append('=').Append(m.GetFloat(p).ToString("0.###")).Append(' ');
            return sb.Length > 0 ? sb.ToString() : "no wind props";
        }

        private static bool HasWind(Material m)
        {
            foreach (var p in WindProps) if (Mathf.Abs(SafeFloat(m, p)) > 1e-4f) return true;
            return false;
        }

        /// <summary>
        /// Looks for a loaded, non-foliage SR material using SR/Paintlight/Cutout (preferred) or SR/Paintlight/Basic.
        /// Logs every candidate once per search (cheap enough to call on WorldLoaded).
        /// </summary>
        public static void FindTemplate(bool force)
        {
            if (paintTemplate != null && !force) return;
            if (templateSearched && !force) return;
            templateSearched = true;
            try
            {
                Material best = null;
                int bestScore = int.MinValue;
                var log = new StringBuilder();
                int listed = 0, total = 0;
                foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (m == null || m.shader == null) continue;
                    string sn = m.shader.name;
                    bool cutout = sn == PaintCutout;
                    if (!cutout && sn != PaintBasic) continue;
                    if (m.name.StartsWith("SlimeCraft", StringComparison.Ordinal)) continue;
                    total++;
                    bool foliage = IsFoliageName(m.name);
                    bool wind = HasWind(m);
                    int score = (cutout ? 100 : 50) - (foliage ? 1000 : 0) - (wind ? 1000 : 0);
                    if (m.HasProperty("_Color") && m.GetColor("_Color") == Color.white) score += 5;
                    if (listed++ < 40)
                        log.Append("\n    ").Append(m.name).Append(" [").Append(sn).Append("] ").Append(WindInfo(m)).Append(" | ").Append(Describe(m))
                           .Append(foliage ? " (foliage name: skipped)" : "").Append(wind ? " (wind/sway: skipped)" : "");
                    if (foliage || wind) continue;
                    if (score > bestScore) { bestScore = score; best = m; }
                }
                if (total > 0) CoreLog.Info("Paintlight template candidates (" + total + "):" + log);
                if (best == null)
                {
                    CoreLog.Debug("No usable SR Paintlight template material loaded yet");
                    return;
                }
                if (best != paintTemplate)
                {
                    paintTemplate = best;
                    CoreLog.Info("Using SR material '" + best.name + "' [" + best.shader.name + "] as Paintlight template: " + Describe(best) +
                                 " keywords=[" + string.Join(" ", best.shaderKeywords ?? new string[0]) + "] queue=" + best.renderQueue +
                                 "; _Depth -> neutral gray " + (CoreConfig.PaintDepthGray != null ? CoreConfig.PaintDepthGray.Value : 128) + ", wind/sway -> 0");
                }
            }
            catch (Exception e) { CoreLog.Rate("template search", e); }
        }

        private static string Describe(Material m)
        {
            var sb = new StringBuilder();
            try
            {
                var sh = m.shader;
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    string pn = sh.GetPropertyName(i);
                    var t = sh.GetPropertyType(i);
                    sb.Append(pn).Append('=');
                    switch (t)
                    {
                        case ShaderPropertyType.Color: sb.Append(m.GetColor(pn)); break;
                        case ShaderPropertyType.Vector: sb.Append(m.GetVector(pn)); break;
                        case ShaderPropertyType.Float:
                        case ShaderPropertyType.Range: sb.Append(m.GetFloat(pn)); break;
                        case ShaderPropertyType.Texture: var tx = m.GetTexture(pn); sb.Append(tx ? tx.name : "null"); break;
                    }
                    sb.Append("; ");
                }
            }
            catch (Exception e) { sb.Append("(" + e.Message + ")"); }
            return sb.ToString();
        }

        /// <summary>Lit (SR paint-light) alpha-tested material. cutoff 0 = opaque.</summary>
        public static Material CreateLit(Texture tex, float cutoff, string name)
        {
            var shader = Pick("lit cutout", CutoutChain);
            bool paint = shader != null && shader.name == PaintCutout;
            if (paint && paintTemplate == null) FindTemplate(false);
            var m = new Material(shader);
            if (paint && paintTemplate != null) CopyTemplate(m, paintTemplate);
            m.name = "SlimeCraft_" + name;
            ApplyLit(m, tex, cutoff);
            if (paint && paintTemplate == null) lock (needsUpgrade) needsUpgrade.Add(new WeakReference(m));
            return m;
        }

        /// <summary>Template props/keywords/queue onto a material that keeps the Cutout shader (template may be Basic).</summary>
        private static void CopyTemplate(Material m, Material template)
        {
            var shader = m.shader;
            m.CopyPropertiesFromMaterial(template);
            if (m.shader != shader) m.shader = shader;
            try
            {
                m.shaderKeywords = template.shader == shader ? template.shaderKeywords : new string[0];
                m.DisableKeyword("_ENABLEDETAILTEX_ON"); // foliage detail-texture keyword (stale on SR's leaf materials)
            }
            catch (Exception e) { CoreLog.Rate("template keywords", e); }
            if (template.shader == shader) m.renderQueue = template.renderQueue;
        }

        private static void ApplyLit(Material m, Texture tex, float cutoff)
        {
            tex = tex ? tex : White;
            if (m.HasProperty("_PrimaryTex"))
            {
                m.SetTexture("_PrimaryTex", tex);
                m.SetTextureScale("_PrimaryTex", Vector2.one);
                m.SetTextureOffset("_PrimaryTex", Vector2.zero);
            }
            if (m.HasProperty("_MainTex"))
            {
                m.SetTexture("_MainTex", tex);
                m.SetTextureScale("_MainTex", Vector2.one);
                m.SetTextureOffset("_MainTex", Vector2.zero);
            }
            bool paint = m.shader != null && m.shader.name == PaintCutout;
            if (paint)
            {
                // clean paint lighting: neutral depth, no foliage vertex animation (SetFloat on an undeclared
                // property is harmless and still reaches a same-named shader uniform)
                m.SetTexture("_Depth", NeutralDepth);
                if (m.HasProperty("_Depth"))
                {
                    m.SetTextureScale("_Depth", Vector2.one);
                    m.SetTextureOffset("_Depth", Vector2.zero);
                }
                foreach (var p in WindProps) m.SetFloat(p, 0f);
            }
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", cutoff);
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            if (m.shader != null && m.shader.name == "Standard")
            {
                // Standard in cutout mode
                m.SetFloat("_Mode", 1f);
                m.SetOverrideTag("RenderType", "TransparentCutout");
                m.EnableKeyword("_ALPHATEST_ON");
                m.SetFloat("_Glossiness", 0f);
                m.renderQueue = (int)RenderQueue.AlphaTest;
            }
            else if (cutoff > 0f && m.renderQueue < (int)RenderQueue.AlphaTest) m.renderQueue = (int)RenderQueue.AlphaTest;
        }

        /// <summary>Called on WorldLoaded: if a Paintlight template became available, copy it into early materials.</summary>
        public static void TryUpgrade()
        {
            FindTemplate(true);
            if (paintTemplate == null) return;
            lock (needsUpgrade)
            {
                int n = 0;
                foreach (var w in needsUpgrade)
                {
                    var m = w.Target as Material;
                    if (m == null) continue;
                    try
                    {
                        var tex = m.HasProperty("_PrimaryTex") ? m.GetTexture("_PrimaryTex") : m.mainTexture;
                        float cut = m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : 0.5f;
                        CopyTemplate(m, paintTemplate);
                        ApplyLit(m, tex, cut);
                        n++;
                    }
                    catch (Exception e) { CoreLog.Rate("material upgrade", e); }
                }
                if (n > 0) CoreLog.Info("Upgraded " + n + " early materials with the SR Paintlight template '" + paintTemplate.name + "'");
                needsUpgrade.Clear();
            }
        }

        /// <summary>Alpha blended (translucent blocks / effects).</summary>
        public static Material CreateTranslucent(Texture tex, string name)
        {
            var m = new Material(Pick("translucent", TranslucentChain)) { name = "SlimeCraft_" + name };
            m.mainTexture = tex ? tex : White;
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            m.renderQueue = (int)RenderQueue.Transparent;
            return m;
        }

        /// <summary>
        /// Full-bright alpha-tested material ('Unlit/Transparent Cutout', _MainTex, _Cutoff 0.5, AlphaTest queue):
        /// light-emitting blocks keep their full texture brightness at night, like in Minecraft.
        /// </summary>
        public static Material CreateEmissive(Texture tex, string name)
        {
            var m = new Material(Pick("emissive (unlit cutout)", UnlitCutoutChain)) { name = "SlimeCraft_" + name };
            m.mainTexture = tex ? tex : White;
            m.mainTextureScale = Vector2.one;
            m.mainTextureOffset = Vector2.zero;
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            m.renderQueue = (int)RenderQueue.AlphaTest;
            return m;
        }

        public static Material CreateUnlit(Texture tex, bool transparent, string name)
        {
            if (transparent) return CreateTranslucent(tex, name);
            var m = new Material(Pick("unlit cutout", UnlitCutoutChain)) { name = "SlimeCraft_" + name };
            m.mainTexture = tex ? tex : White;
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
            return m;
        }

        public static Material CreateColor(Color c, string name)
        {
            bool alpha = c.a < 0.999f;
            var m = new Material(alpha ? Pick("color alpha", ColorAlphaChain) : Pick("color", ColorChain)) { name = "SlimeCraft_" + name };
            if (m.HasProperty("_MainTex") && m.shader.name != "Unlit/Color") m.mainTexture = White;
            m.color = c;
            if (alpha) m.renderQueue = (int)RenderQueue.Transparent;
            return m;
        }
    }
}
