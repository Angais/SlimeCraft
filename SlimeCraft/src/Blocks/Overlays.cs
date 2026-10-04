using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    /// <summary>
    /// Minecraft block-breaking cracks (block/destroy_stage_0..9): a cube inflated by 0.001 around one of our
    /// blocks, or a small decal quad on Slime Rancher terrain being "harvested".
    /// MC draws cracks with a multiplicative blend (dst*2*src); we bake that into alpha-blended textures:
    /// dark pixels → black with alpha 1-2g, light pixels → white with a faint alpha.
    /// </summary>
    internal sealed class CrackOverlay
    {
        private readonly BlockWorld world;
        private Material[] mats;
        private bool matsTried;
        private GameObject cubeGo, decalGo;
        private MeshRenderer cubeMr, decalMr;

        public CrackOverlay(BlockWorld world) { this.world = world; }

        private bool EnsureMaterials()
        {
            if (mats != null) return true;
            if (matsTried) return false;
            var assets = SC.Assets;
            if (assets == null || !assets.Ready) return false;
            matsTried = true;
            try
            {
                mats = new Material[10];
                for (int s = 0; s < 10; s++)
                {
                    var src = assets.GetTexture("block/destroy_stage_" + s);
                    var tex = Bake(src);
                    Material m = SC.ItemVisuals != null ? SC.ItemVisuals.CreateUnlitMaterial(tex, true) : null;
                    if (m == null)
                    {
                        var sh = Shader.Find("Unlit/Transparent") ?? Shader.Find("Sprites/Default");
                        m = new Material(sh) { mainTexture = tex };
                    }
                    m.renderQueue = 3001;
                    mats[s] = m;
                }
                return true;
            }
            catch (System.Exception e)
            {
                mats = null;
                RateLog.Error("crack materials", e, 60f);
                return false;
            }
        }

        private static Texture2D Bake(Texture2D src)
        {
            int w = src != null ? src.width : 16, h = src != null ? src.height : 16;
            var dst = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "SC_crack" };
            Color32[] px;
            try { px = src.GetPixels32(); }
            catch { px = new Color32[w * h]; }
            var outPx = new Color32[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                if (c.a < 26) { outPx[i] = new Color32(0, 0, 0, 0); continue; } // pixels with alpha below 0.1 count as fully transparent
                float g = (c.r + c.g + c.b) / (3f * 255f);
                float m = 2f * g; // multiplicative factor dst*2*src
                if (m < 1f) outPx[i] = new Color32(0, 0, 0, (byte)Mathf.RoundToInt((1f - m) * 255f));
                else outPx[i] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01((m - 1f) * 0.5f) * 255f));
            }
            dst.SetPixels32(outPx);
            dst.Apply(false, false);
            return dst;
        }

        private void EnsureObjects()
        {
            if (cubeGo == null)
            {
                cubeGo = new GameObject("SlimeCraft_Crack");
                Object.DontDestroyOnLoad(cubeGo);
                cubeGo.layer = world.RenderLayer;
                cubeGo.AddComponent<MeshFilter>().sharedMesh = BuildCube();
                cubeMr = cubeGo.AddComponent<MeshRenderer>();
                cubeMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                cubeMr.receiveShadows = false;
                cubeGo.SetActive(false);
            }
            if (decalGo == null)
            {
                decalGo = new GameObject("SlimeCraft_CrackDecal");
                Object.DontDestroyOnLoad(decalGo);
                decalGo.layer = world.RenderLayer;
                decalGo.AddComponent<MeshFilter>().sharedMesh = BuildQuad();
                decalMr = decalGo.AddComponent<MeshRenderer>();
                decalMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                decalMr.receiveShadows = false;
                decalGo.SetActive(false);
            }
        }

        private static Mesh BuildCube()
        {
            var v = new Vector3[24]; var uv = new Vector2[24]; var n = new Vector3[24]; var t = new int[36];
            for (int d = 0; d < 6; d++)
            {
                var cs = Dirs.Corners(d, Dirs.DefaultUp(d));
                for (int k = 0; k < 4; k++)
                {
                    v[d * 4 + k] = (cs[k] - new Vector3(0.5f, 0.5f, 0.5f)) * 1.002f + new Vector3(0.5f, 0.5f, 0.5f); // inflate 0.001
                    n[d * 4 + k] = Dirs.VecF[d];
                }
                uv[d * 4] = new Vector2(0, 1); uv[d * 4 + 1] = new Vector2(0, 0); uv[d * 4 + 2] = new Vector2(1, 0); uv[d * 4 + 3] = new Vector2(1, 1);
                int b = d * 4, ti = d * 6;
                t[ti] = b; t[ti + 1] = b + 3; t[ti + 2] = b + 2; t[ti + 3] = b; t[ti + 4] = b + 2; t[ti + 5] = b + 1;
            }
            var m = new Mesh { name = "SC_crack_cube", vertices = v, uv = uv, normals = n, triangles = t };
            return m;
        }

        private static Mesh BuildQuad()
        {
            // Front face points to local -Z (TL,BL,BR,TR with triangles 0,3,2 / 0,2,1).
            var v = new[] { new Vector3(-0.5f, 0.5f, 0), new Vector3(-0.5f, -0.5f, 0), new Vector3(0.5f, -0.5f, 0), new Vector3(0.5f, 0.5f, 0) };
            var uv = new[] { new Vector2(0, 1), new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1) };
            var n = new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back };
            return new Mesh { name = "SC_crack_quad", vertices = v, uv = uv, normals = n, triangles = new[] { 0, 3, 2, 0, 2, 1 } };
        }

        public void ShowBlock(Vector3Int pos, int stage)
        {
            if (stage < 0 || !EnsureMaterials()) { Hide(); return; }
            EnsureObjects();
            if (decalGo.activeSelf) decalGo.SetActive(false);
            cubeGo.transform.position = new Vector3(pos.x, pos.y, pos.z);
            cubeMr.sharedMaterial = mats[Mathf.Clamp(stage, 0, 9)];
            if (!cubeGo.activeSelf) cubeGo.SetActive(true);
        }

        public void ShowDecal(Vector3 point, Vector3 normal, int stage, float size)
        {
            if (stage < 0 || !EnsureMaterials()) { Hide(); return; }
            EnsureObjects();
            if (cubeGo.activeSelf) cubeGo.SetActive(false);
            if (normal.sqrMagnitude < 1e-4f) normal = Vector3.up;
            decalGo.transform.position = point + normal * 0.02f;
            decalGo.transform.rotation = Quaternion.LookRotation(-normal, Mathf.Abs(normal.y) > 0.9f ? Vector3.forward : Vector3.up);
            decalGo.transform.localScale = new Vector3(size, size, 1f);
            decalMr.sharedMaterial = mats[Mathf.Clamp(stage, 0, 9)];
            if (!decalGo.activeSelf) decalGo.SetActive(true);
        }

        public void Hide()
        {
            if (cubeGo != null && cubeGo.activeSelf) cubeGo.SetActive(false);
            if (decalGo != null && decalGo.activeSelf) decalGo.SetActive(false);
        }
    }

    /// <summary>
    /// Minecraft's black selection box (alpha 0.4) around the targeted block, inflated by 0.002.
    /// Edges are camera-facing ribbons with a constant pixel width (MC renders its lines as quads too);
    /// OutlineWidthPx &lt;= 1 falls back to 1px GL lines.
    /// </summary>
    internal sealed class BlockOutline
    {
        private static readonly int[,] edges =
        {
            {0,1},{1,3},{3,2},{2,0}, {4,5},{5,7},{7,6},{6,4}, {0,4},{1,5},{2,6},{3,7}
        };
        private readonly BlockWorld world;
        private GameObject go;
        private MeshRenderer mr;
        private Mesh mesh;
        private readonly Vector3[] v = new Vector3[48];
        private readonly int[] tris = new int[72];
        private readonly Vector3[] lineV = new Vector3[8];
        private readonly Vector3[] box = new Vector3[8];
        private readonly Color[] white = new Color[48];
        private readonly Color[] white8 = new Color[8];
        private bool visible, useLines;
        private Vector3Int pos;

        public BlockOutline(BlockWorld world) { this.world = world; }

        public void Show(Vector3Int p) { visible = true; pos = p; }
        public void Hide()
        {
            visible = false;
            if (go != null && go.activeSelf) go.SetActive(false);
        }

        private bool Ensure()
        {
            if (go != null) return true;
            Material mat = null;
            var black = new Color(0f, 0f, 0f, 0.4f);
            try { mat = SC.ItemVisuals?.CreateColorMaterial(black); } catch (System.Exception e) { RateLog.Error("outline material", e, 60f); }
            if (mat == null)
            {
                var sh = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                if (sh == null) return false;
                mat = new Material(sh) { color = black };
            }
            mat.renderQueue = 3002;
            go = new GameObject("SlimeCraft_BlockOutline");
            Object.DontDestroyOnLoad(go);
            go.layer = world.RenderLayer;
            mesh = new Mesh { name = "SC_outline" };
            mesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            // The color comes from the material (_Color); vertex colors stay white so alpha is not applied twice.
            for (int i = 0; i < white.Length; i++) white[i] = Color.white;
            mesh.vertices = v;
            mesh.colors = white;
            return true;
        }

        public void LateUpdate()
        {
            if (!visible) return;
            var cam = CamUtil.Main;
            if (cam == null || !Ensure()) return;
            if (!go.activeSelf) go.SetActive(true);

            const float e = 0.002f;
            Vector3 mn = new Vector3(pos.x - e, pos.y - e, pos.z - e), mx = new Vector3(pos.x + 1 + e, pos.y + 1 + e, pos.z + 1 + e);
            for (int i = 0; i < 8; i++)
                box[i] = new Vector3((i & 1) != 0 ? mx.x : mn.x, (i & 2) != 0 ? mx.y : mn.y, (i & 4) != 0 ? mx.z : mn.z);

            Vector3 cp = cam.transform.position;
            float px = BlocksConfig.OutlineWidthPx?.Value ?? 2f;
            bool lines = px <= 1f;
            if (lines)
            {
                if (!useLines)
                {
                    mesh.Clear();
                    var idx = new int[24];
                    for (int k = 0; k < 12; k++) { idx[k * 2] = edges[k, 0]; idx[k * 2 + 1] = edges[k, 1]; }
                    for (int i = 0; i < 8; i++) white8[i] = Color.white;
                    mesh.vertices = box;
                    mesh.colors = white8;
                    mesh.SetIndices(idx, MeshTopology.Lines, 0);
                    useLines = true;
                }
                mesh.vertices = box;
                mesh.bounds = new Bounds(BlockWorld.Center(pos), Vector3.one * 2f);
                return;
            }
            if (useLines) { mesh.Clear(); mesh.vertices = v; mesh.colors = white; useLines = false; }

            float k2 = 2f * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, Screen.height);
            for (int k = 0; k < 12; k++)
            {
                Vector3 a = box[edges[k, 0]], b = box[edges[k, 1]];
                Vector3 dir = (b - a).normalized;
                Vector3 mid = (a + b) * 0.5f;
                Vector3 side = Vector3.Cross(dir, cp - mid);
                if (side.sqrMagnitude < 1e-8f) side = Vector3.Cross(dir, Vector3.up);
                side.Normalize();
                float da = (a - cp).magnitude, db = (b - cp).magnitude;
                Vector3 sa = side * (px * 0.5f * da * k2), sb = side * (px * 0.5f * db * k2);
                // pull towards the camera along the view ray so the lines win the depth test against the faces
                Vector3 pa = a + (cp - a) * 0.01f, pb = b + (cp - b) * 0.01f;
                int o = k * 4;
                v[o] = pa - sa; v[o + 1] = pa + sa; v[o + 2] = pb + sb; v[o + 3] = pb - sb;
                int t = k * 6;
                Vector3 nrm = Vector3.Cross(v[o + 1] - v[o], v[o + 2] - v[o]);
                if (Vector3.Dot(nrm, cp - v[o]) >= 0f)
                { tris[t] = o; tris[t + 1] = o + 1; tris[t + 2] = o + 2; tris[t + 3] = o; tris[t + 4] = o + 2; tris[t + 5] = o + 3; }
                else
                { tris[t] = o; tris[t + 1] = o + 2; tris[t + 2] = o + 1; tris[t + 3] = o; tris[t + 4] = o + 3; tris[t + 5] = o + 2; }
            }
            mesh.vertices = v;
            mesh.triangles = tris;
            mesh.bounds = new Bounds(BlockWorld.Center(pos), Vector3.one * 2f);
        }
    }
}
