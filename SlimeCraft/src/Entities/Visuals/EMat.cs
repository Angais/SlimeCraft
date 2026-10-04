using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Materials and textures for entities. Prefers SC.ItemVisuals (Slime Rancher paint-light look), falls back to
    /// shaders that are always present in the SR build. All results are cached; caches are reset on world unload only
    /// for per-world objects (materials stay valid across worlds).
    /// </summary>
    internal static class EMat
    {
        private static readonly Dictionary<string, Material> litCache = new Dictionary<string, Material>();
        private static readonly Dictionary<string, Material> unlitCache = new Dictionary<string, Material>();
        private static readonly Dictionary<Texture2D, Texture2D> maskCache = new Dictionary<Texture2D, Texture2D>();
        private static readonly Dictionary<string, Texture2D> tintCache = new Dictionary<string, Texture2D>();
        private static Shader spriteShader;
        private static bool spriteLooked;
        private static Texture2D whiteMask;

        public static bool AssetsReady => SC.Assets != null && SC.Assets.Ready;

        /// <summary>Entity texture "entity/creeper/creeper" (never null; checker if missing).</summary>
        public static Texture2D Tex(string path)
        {
            Texture2D t = null;
            try { t = SC.Assets?.GetTexture(path); } catch (Exception e) { ELog.Error("GetTexture " + path, e); }
            if (t == null) t = Checker();
            return t;
        }

        private static Texture2D checker;
        private static Texture2D Checker()
        {
            if (checker != null) return checker;
            checker = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Repeat };
            checker.SetPixels32(new[] { new Color32(255, 0, 255, 255), new Color32(0, 0, 0, 255), new Color32(0, 0, 0, 255), new Color32(255, 0, 255, 255) });
            checker.Apply();
            return checker;
        }

        /// <summary>Lit (SR look) alpha-tested material for an entity texture path; cached per key.</summary>
        public static Material Lit(string key, Texture2D tex, bool cutout = true, int queueOffset = 0)
        {
            string k = key + "|" + cutout + "|" + queueOffset;
            if (litCache.TryGetValue(k, out var m) && m != null) return m;
            m = CreateLit(tex, cutout);
            if (m != null)
            {
                if (queueOffset != 0) m.renderQueue = (m.renderQueue > 0 ? m.renderQueue : 2450) + queueOffset;
                m.name = "SC_Ent_" + key;
                if (AssetsReady) litCache[k] = m;
            }
            return m;
        }

        public static Material Lit(string texPath, bool cutout = true, int queueOffset = 0) => Lit(texPath, Tex(texPath), cutout, queueOffset);

        public static Material CreateLit(Texture2D tex, bool cutout)
        {
            Material m = null;
            try { m = SC.ItemVisuals?.CreateLitMaterial(tex, cutout); } catch (Exception e) { ELog.Error("CreateLitMaterial", e); }
            if (m != null) return m;
            Shader s = Find("SR/Paintlight/Cutout", "Legacy Shaders/Transparent/Cutout/Diffuse", "Unlit/Transparent Cutout", "Standard");
            if (s == null) return null;
            m = new Material(s);
            SetTex(m, tex);
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            return m;
        }

        /// <summary>Unlit, alpha blended (slime outer layer, enderman eyes).</summary>
        public static Material Unlit(string key, Texture2D tex, bool transparent = true)
        {
            string k = key + "|" + transparent;
            if (unlitCache.TryGetValue(k, out var m) && m != null) return m;
            try { m = SC.ItemVisuals?.CreateUnlitMaterial(tex, transparent); } catch (Exception e) { ELog.Error("CreateUnlitMaterial", e); }
            if (m == null)
            {
                Shader s = transparent ? Find("Unlit/Transparent", "Sprites/Default", "UI/Default") : Find("Unlit/Transparent Cutout", "Unlit/Texture", "Sprites/Default");
                if (s != null) { m = new Material(s); SetTex(m, tex); }
            }
            if (m != null) { m.name = "SC_EntU_" + key; if (AssetsReady) unlitCache[k] = m; }
            return m;
        }

        public static void SetTex(Material m, Texture tex)
        {
            if (m == null) return;
            if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);
            if (m.HasProperty("_PrimaryTex")) m.SetTexture("_PrimaryTex", tex);
        }

        public static Shader Find(params string[] names)
        {
            foreach (var n in names)
            {
                var s = Shader.Find(n);
                if (s != null) return s;
            }
            ELog.WarnOnce("shader:" + names[0], "None of the shaders [" + string.Join(", ", names) + "] were found");
            return null;
        }

        public static Shader SpriteShader
        {
            get
            {
                if (!spriteLooked || spriteShader == null)
                {
                    spriteLooked = true;
                    spriteShader = Find("Sprites/Default", "UI/Default");
                }
                return spriteShader;
            }
        }

        /// <summary>
        /// Overlay material (Sprites/Default, alpha blended, vertex color * _Color) using a white mask with the
        /// texture's (cut) alpha. Drawn with the same mesh on top of the entity to give Minecraft's hurt and flash
        /// tints (red hurt tint 30%, white TNT/creeper flash). Colors are set per renderer via MaterialPropertyBlock.
        /// </summary>
        public static Material Overlay(Texture2D baseTex)
        {
            var sh = SpriteShader;
            if (sh == null) return null;
            var mask = baseTex != null ? Mask(baseTex) : WhiteMask();
            var key = "overlay|" + (baseTex != null ? baseTex.GetInstanceID().ToString() : "white");
            if (unlitCache.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(sh) { name = "SC_EntOverlay" };
            m.mainTexture = mask;
            PrepareSpriteMaterial(m);
            m.renderQueue = 3001;
            unlitCache[key] = m;
            return m;
        }

        /// <summary>Sprites/Default on a MeshRenderer: make sure the per-renderer sprite color / flip are neutral.</summary>
        public static void PrepareSpriteMaterial(Material m)
        {
            if (m == null) return;
            if (m.HasProperty("_RendererColor")) m.SetColor("_RendererColor", Color.white);
            if (m.HasProperty("_Flip")) m.SetVector("_Flip", new Vector4(1f, 1f, 0f, 0f));
        }

        public static Texture2D WhiteMask()
        {
            if (whiteMask != null) return whiteMask;
            whiteMask = new Texture2D(1, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            whiteMask.SetPixel(0, 0, Color.white); whiteMask.Apply();
            return whiteMask;
        }

        /// <summary>White texture with the source alpha thresholded at 0.5 (matches alpha-test cutout).</summary>
        public static Texture2D Mask(Texture2D src)
        {
            if (src == null) return WhiteMask();
            if (maskCache.TryGetValue(src, out var m) && m != null) return m;
            try
            {
                var px = src.GetPixels32();
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, (byte)(px[i].a >= 128 ? 255 : 0));
                m = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = src.name + "_mask" };
                m.SetPixels32(px); m.Apply(false, false);
            }
            catch (Exception e) { ELog.Error("Mask texture (texture not readable?)", e); m = WhiteMask(); }
            maskCache[src] = m;
            return m;
        }

        /// <summary>Texture multiplied by an RGB tint (sheep wool colors). Cached.</summary>
        public static Texture2D Tinted(string path, Color32 tint)
        {
            string key = path + "#" + tint.r + "," + tint.g + "," + tint.b;
            if (tintCache.TryGetValue(key, out var t) && t != null) return t;
            var src = Tex(path);
            try
            {
                var px = src.GetPixels32();
                for (int i = 0; i < px.Length; i++)
                {
                    var p = px[i];
                    px[i] = new Color32((byte)(p.r * tint.r / 255), (byte)(p.g * tint.g / 255), (byte)(p.b * tint.b / 255), p.a);
                }
                t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = key };
                t.SetPixels32(px); t.Apply(false, false);
            }
            catch (Exception e) { ELog.Error("Tinted texture " + path, e); t = src; }
            if (AssetsReady) tintCache[key] = t;
            return t;
        }

        /// <summary>Loads a full PNG from the jar (animated strips that GetTexture would cut to one frame).</summary>
        public static Texture2D LoadFullJarTexture(string path)
        {
            try
            {
                var bytes = SC.Assets?.ReadJarBytes("assets/minecraft/textures/" + path + ".png");
                if (bytes == null) return null;
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                if (!t.LoadImage(bytes, false)) return null;
                t.filterMode = FilterMode.Point; t.wrapMode = TextureWrapMode.Clamp;
                return t;
            }
            catch (Exception e) { ELog.Error("LoadFullJarTexture " + path, e); return null; }
        }

        private static readonly Dictionary<int, Mesh> whiteColored = new Dictionary<int, Mesh>();

        /// <summary>Copy of a mesh with white vertex colors (for the Sprites/Default overlay pass). Cached.</summary>
        public static Mesh WhiteColored(Mesh src)
        {
            if (src == null) return null;
            int id = src.GetInstanceID();
            if (whiteColored.TryGetValue(id, out var m) && m != null) return m;
            try
            {
                if (!src.isReadable) { whiteColored[id] = src; return src; }
                m = new Mesh { name = src.name + "_white" };
                m.vertices = src.vertices;
                m.normals = src.normals;
                m.uv = src.uv;
                m.subMeshCount = src.subMeshCount;
                for (int s = 0; s < src.subMeshCount; s++) m.SetTriangles(src.GetTriangles(s), s);
                var cols = new Color32[src.vertexCount];
                for (int i = 0; i < cols.Length; i++) cols[i] = new Color32(255, 255, 255, 255);
                m.colors32 = cols;
                m.RecalculateBounds();
            }
            catch (Exception e) { ELog.Error("WhiteColored", e); m = src; }
            whiteColored[id] = m;
            return m;
        }

        /// <summary>Property block forcing Sprites/Default's per-renderer color to white (non-sprite renderers).</summary>
        public static MaterialPropertyBlock SpriteBlock(Color color)
        {
            var b = new MaterialPropertyBlock();
            b.SetColor("_Color", color);
            b.SetColor("_RendererColor", Color.white);
            b.SetVector("_Flip", new Vector4(1f, 1f, 0f, 0f));
            return b;
        }

        /// <summary>Solid cube mesh (fallback visuals when SC.ItemVisuals is unavailable).</summary>
        public static Mesh UnitCube()
        {
            if (unitCube != null) return unitCube;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            unitCube = go.GetComponent<MeshFilter>().sharedMesh;
            UnityEngine.Object.Destroy(go);
            return unitCube;
        }
        private static Mesh unitCube;
    }
}
