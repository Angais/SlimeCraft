using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Renders Slime Rancher's real first-person vacpack model (the renderers under
    /// SimplePlayer/FPSCamera/VacuumTransform/Vacuum, prefab 'model_vac_v3_prefab': 'vac' body, 'Vac Display' gauge,
    /// 'mesh_glass' tube; Beatrix's 'arms'/'mesh_l_armextra' and inactive mochi/timer parts are skipped) into a
    /// Minecraft-style inventory icon:
    /// * temporary orthographic camera, culling mask = the model's layers (31 'Weapon'), 3/4 view from the left, above
    ///   and slightly behind, rolled so the nozzle points to the upper-left;
    /// * deterministic lighting: SR's lights that would reach the model are switched off for the two Render() calls and
    ///   one white key light from the viewer's upper-left is used, with a neutral flat ambient;
    /// * rendered twice into a 256² ARGB32 RenderTexture, over black and over white: alpha = 1 − (white − black) works
    ///   for every SR shader (refractive glass, emission) whatever they write to the alpha channel;
    /// * cropped to the model, box-filtered down to the icon size, alpha thresholded (crisp pixel edges) and given a
    ///   1px dark outline (darkened neighbour colour, like Minecraft item edges).
    /// Everything it changes (renderer enabled flags, lights, ambient, fog) is restored in the same call.
    /// </summary>
    internal static class VacpackIconRenderer
    {
        private const int RenderSize = 256;
        private const float NozzleAngle = 145f; // image-space direction of the nozzle (deg, 0 = right, 90 = up)

        internal sealed class Result
        {
            public PixelImage Icon;
            public PixelImage Full;
            public string Info;
        }

        public static Result Render(WeaponVacuum vac, Camera refCam, int iconSize, bool glass, bool outline)
        {
            if (vac == null) return null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var used = new List<Renderer>();
            var skipped = new StringBuilder();
            int mask = 0;
            foreach (var r in vac.GetComponentsInChildren<Renderer>(true))
            {
                string why = Exclude(r, glass);
                if (why != null) { skipped.Append(r.name).Append('(').Append(why).Append(") "); continue; }
                used.Add(r);
                mask |= 1 << r.gameObject.layer;
            }
            if (used.Count == 0) return null;

            // ---- view frame: F = nozzle direction, U = up, R = right of the vacpack
            Vector3 F = vac.vacOrigin != null ? vac.vacOrigin.transform.up : (refCam != null ? refCam.transform.forward : vac.transform.forward);
            Vector3 U = refCam != null ? refCam.transform.up : vac.transform.up;
            F.Normalize();
            U = U - F * Vector3.Dot(U, F);
            if (U.sqrMagnitude < 1e-6f) U = Mathf.Abs(F.y) < 0.9f ? Vector3.up : Vector3.forward;
            U.Normalize();
            Vector3 R = Vector3.Cross(U, F); // Unity: right = up x forward
            Vector3 toCam = (-R * 1.0f + U * 0.5f - F * 0.3f).normalized; // left side, from above, a bit from behind
            Vector3 V = -toCam;
            Vector3 n = F - V * Vector3.Dot(F, V);
            if (n.sqrMagnitude < 1e-6f) n = R;
            n.Normalize();
            Vector3 b = Vector3.Cross(V, n); // image "up" when n is image "right"
            float phi = NozzleAngle * Mathf.Deg2Rad;
            Vector3 camRight = Mathf.Cos(phi) * n - Mathf.Sin(phi) * b;
            Vector3 camUp = Mathf.Sin(phi) * n + Mathf.Cos(phi) * b;

            // ---- framing from the meshes' oriented bounds (independent of the renderers' enabled state:
            //      Renderer.bounds of a disabled renderer is not reliable, and the vacpack is hidden while a
            //      Minecraft item is selected). Fallback: the renderers' world AABBs once they are enabled.
            var pts = new List<Vector3>(used.Count * 8);
            foreach (var r in used)
            {
                if (r is SkinnedMeshRenderer smr)
                {
                    var space = smr.rootBone != null ? smr.rootBone : smr.transform;
                    AddCorners(pts, smr.localBounds, space.localToWorldMatrix);
                }
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf != null && mf.sharedMesh != null) AddCorners(pts, mf.sharedMesh.bounds, r.transform.localToWorldMatrix);
                    else AddCorners(pts, r.bounds, Matrix4x4.identity);
                }
            }
            var frame = Framing.From(pts, camRight, camUp, V);
            if (frame == null) return null;

            var prevActive = RenderTexture.active;
            var prevAmbient = RenderSettings.ambientLight;
            bool prevFog = RenderSettings.fog;
            var rendererStates = new List<KeyValuePair<Renderer, bool>>();
            var lightStates = new List<Light>();
            GameObject camGo = null, lightGo = null;
            RenderTexture rt = null;
            Texture2D read = null;
            Color32[] black = null, white = null;
            int zoomOuts = 0;
            bool aabbFallback = false;
            try
            {
                camGo = new GameObject("SlimeCraft_VacpackIconCamera") { hideFlags = HideFlags.HideAndDontSave };
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.aspect = 1f;
                cam.nearClipPlane = 0.01f;
                cam.cullingMask = mask;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.useOcclusionCulling = false;
                cam.renderingPath = RenderingPath.Forward;
                cam.depthTextureMode = DepthTextureMode.None;

                lightGo = new GameObject("SlimeCraft_VacpackIconLight") { hideFlags = HideFlags.HideAndDontSave };
                var key = lightGo.AddComponent<Light>();
                key.type = LightType.Directional;
                key.color = Color.white;
                key.intensity = 1.0f;
                key.shadows = LightShadows.None;
                key.cullingMask = mask;
                key.renderMode = LightRenderMode.ForcePixel;
                // light from the viewer's upper-left, slightly in front (Minecraft GUI items are lit from there)
                lightGo.transform.rotation = Quaternion.LookRotation((V * 1.0f - camUp * 0.9f + camRight * 0.6f).normalized, camUp);

                // deterministic lighting: SR's sun/fill/flashlight off for these renders
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                {
                    if (l == null || l == key || !l.enabled || (l.cullingMask & mask) == 0) continue;
                    l.enabled = false;
                    lightStates.Add(l);
                }
                RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.47f, 1f);
                RenderSettings.fog = false;

                // only the chosen renderers on those layers (the arms etc. off, hidden vacpack parts on)
                foreach (var r in vac.GetComponentsInChildren<Renderer>(true))
                {
                    bool want = used.Contains(r);
                    if (r.enabled == want) continue;
                    rendererStates.Add(new KeyValuePair<Renderer, bool>(r, r.enabled));
                    r.enabled = want;
                }

                rt = RenderTexture.GetTemporary(RenderSize, RenderSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default, 1);
                cam.targetTexture = rt;
                read = new Texture2D(RenderSize, RenderSize, TextureFormat.RGBA32, false, false) { hideFlags = HideFlags.HideAndDontSave };

                for (int attempt = 0; attempt < 4; attempt++)
                {
                    frame.Apply(cam);
                    cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
                    cam.Render();
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, RenderSize, RenderSize), 0, 0, false);
                    black = read.GetPixels32();

                    cam.backgroundColor = new Color(1f, 1f, 1f, 0f);
                    cam.Render();
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, RenderSize, RenderSize), 0, 0, false);
                    white = read.GetPixels32();

                    if (!AnyVisible(black, white))
                    {
                        // the oriented estimate missed the model: frame the (now enabled) renderers' world bounds
                        if (aabbFallback) break;
                        aabbFallback = true;
                        var wpts = new List<Vector3>(used.Count * 8);
                        foreach (var r in used) AddCorners(wpts, r.bounds, Matrix4x4.identity);
                        frame = Framing.From(wpts, camRight, camUp, V);
                        if (frame == null) break;
                        continue;
                    }
                    // estimated bounds too small (skinned bounds can be off): zoom out when the model touches the border
                    if (!TouchesBorder(black, white, RenderSize)) break;
                    frame.Half *= 1.5f;
                    zoomOuts++;
                }
                cam.targetTexture = null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                RenderSettings.ambientLight = prevAmbient;
                RenderSettings.fog = prevFog;
                foreach (var l in lightStates) if (l != null) l.enabled = true;
                foreach (var kv in rendererStates) if (kv.Key != null) kv.Key.enabled = kv.Value;
                // detach before releasing, also on the exception path (Unity errors on releasing a camera's target)
                var iconCam = camGo != null ? camGo.GetComponent<Camera>() : null;
                if (iconCam != null && iconCam.targetTexture == rt) iconCam.targetTexture = null;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (read != null) UnityEngine.Object.Destroy(read);
                // Destroy is deferred to the end of the frame: deactivate first so the key light cannot light the
                // first-person vacpack in this frame's normal render
                if (lightGo != null) { lightGo.SetActive(false); UnityEngine.Object.Destroy(lightGo); }
                if (camGo != null) { camGo.SetActive(false); UnityEngine.Object.Destroy(camGo); }
            }
            if (black == null || white == null || !AnyVisible(black, white)) return null;

            // ---- un-premultiply from the two backgrounds
            int N = RenderSize * RenderSize;
            var pr = new float[N]; var pg = new float[N]; var pb = new float[N]; var pa = new float[N];
            for (int i = 0; i < N; i++)
            {
                var k = black[i]; var w = white[i];
                float a = 1f - ((w.r - k.r) + (w.g - k.g) + (w.b - k.b)) / (3f * 255f);
                a = Mathf.Clamp01(a);
                pa[i] = a;
                pr[i] = Mathf.Min(k.r / 255f, a); pg[i] = Mathf.Min(k.g / 255f, a); pb[i] = Mathf.Min(k.b / 255f, a);
            }
            var icon = ToIcon(pr, pg, pb, pa, RenderSize, RenderSize, iconSize, outline, out string crop);
            if (icon == null) return null;
            var full = new PixelImage(RenderSize, RenderSize);
            for (int i = 0; i < N; i++) full.Px[i] = Straight(pr[i], pg[i], pb[i], pa[i]);

            var names = new StringBuilder();
            foreach (var r in used) names.Append(r.name).Append(", ");
            return new Result
            {
                Icon = icon,
                Full = full,
                Info = used.Count + " renderers [" + names.ToString().TrimEnd(' ', ',') + "], skipped [" + skipped.ToString().Trim() + "], layers 0x" +
                       mask.ToString("X8") + ", " + crop + (aabbFallback ? ", AABB framing" : "") + (zoomOuts > 0 ? ", zoomed out x" + zoomOuts : "") +
                       ", " + sw.ElapsedMilliseconds + " ms",
            };
        }

        /// <summary>Orthographic framing of a point set seen along V (camera right/up given).</summary>
        private sealed class Framing
        {
            public Vector3 Center, Right, Up, View;
            public float MinZ, Depth, Half;

            public static Framing From(List<Vector3> pts, Vector3 right, Vector3 up, Vector3 view)
            {
                if (pts == null || pts.Count == 0) return null;
                var world = new Bounds(pts[0], Vector3.zero);
                foreach (var p in pts) world.Encapsulate(p);
                Vector3 c = world.center;
                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var p in pts)
                {
                    var d = p - c;
                    float x = Vector3.Dot(d, right), y = Vector3.Dot(d, up), z = Vector3.Dot(d, view);
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                }
                float half = Mathf.Max(maxX - minX, maxY - minY) * 0.5f * 1.1f;
                if (!(half > 1e-4f) || half > 50f) return null;
                return new Framing
                {
                    Center = c + right * ((minX + maxX) * 0.5f) + up * ((minY + maxY) * 0.5f),
                    Right = right, Up = up, View = view,
                    MinZ = minZ,
                    Depth = Mathf.Max(maxZ - minZ, Mathf.Max(maxX - minX, maxY - minY)),
                    Half = half,
                };
            }

            public void Apply(Camera cam)
            {
                // camera well in front of the nearest point; generous far plane (ortho: distance does not scale)
                float back = Depth + 0.5f;
                cam.transform.position = Center + View * (MinZ - back);
                cam.transform.rotation = Quaternion.LookRotation(View, Up);
                cam.orthographicSize = Half;
                cam.farClipPlane = back + 2f * Depth + 1f;
            }
        }

        private static bool AnyVisible(Color32[] black, Color32[] white)
        {
            for (int i = 0; i < black.Length; i++) if (Visible(black, white, i)) return true;
            return false;
        }

        /// <summary>True when visible pixels (differing from the background) lie on the outermost rows/columns.</summary>
        private static bool TouchesBorder(Color32[] black, Color32[] white, int size)
        {
            for (int i = 0; i < size; i++)
            {
                if (Visible(black, white, i) || Visible(black, white, (size - 1) * size + i) ||
                    Visible(black, white, i * size) || Visible(black, white, i * size + size - 1)) return true;
            }
            return false;
        }

        private static bool Visible(Color32[] black, Color32[] white, int i)
        {
            var k = black[i]; var w = white[i];
            return (w.r - k.r) + (w.g - k.g) + (w.b - k.b) < 3 * 255 - 60; // alpha > ~0.08
        }

        /// <summary>Why a renderer is left out of the icon (null = used).</summary>
        private static string Exclude(Renderer r, bool glass)
        {
            if (r == null) return "null";
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) return r.GetType().Name;
            if (!r.gameObject.activeInHierarchy) return "inactive";
            if (r is SkinnedMeshRenderer smr && smr.sharedMesh == null) return "no mesh";
            if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) return "no mesh";
            }
            string n = r.name.ToLowerInvariant();
            if (n.Contains("arm") || n.Contains("hand") || n.Contains("finger")) return "arm";
            if (n.Contains("timer")) return "timer";
            bool isGlass = n.Contains("glass");
            foreach (var m in r.sharedMaterials)
            {
                if (m == null || m.shader == null) continue;
                string s = m.shader.name;
                if (s.IndexOf("Hands", StringComparison.OrdinalIgnoreCase) >= 0) return "arm";
                if (s.IndexOf("Vac Cone", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("Particles", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.StartsWith("SR/FX", StringComparison.OrdinalIgnoreCase)) return "fx";
                if (s.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0) isGlass = true;
            }
            if (isGlass && !glass) return "glass";
            return null;
        }

        private static void AddCorners(List<Vector3> pts, Bounds b, Matrix4x4 m)
        {
            var mn = b.min; var mx = b.max;
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3((i & 1) != 0 ? mx.x : mn.x, (i & 2) != 0 ? mx.y : mn.y, (i & 4) != 0 ? mx.z : mn.z);
                pts.Add(m.MultiplyPoint3x4(p));
            }
        }

        private static Color32 Straight(float r, float g, float b, float a)
        {
            if (a <= 0f) return Pixels.Clear;
            return new Color32((byte)Mathf.Clamp(r / a * 255f + 0.5f, 0, 255), (byte)Mathf.Clamp(g / a * 255f + 0.5f, 0, 255),
                               (byte)Mathf.Clamp(b / a * 255f + 0.5f, 0, 255), (byte)Mathf.Clamp(a * 255f + 0.5f, 0, 255));
        }

        /// <summary>Readable texture region (e.g. an SR sprite) to premultiplied float planes, then <see cref="ToIcon"/>.</summary>
        public static PixelImage IconFromPixels(Color32[] px, int w, int h, int iconSize, bool outline)
        {
            int N = w * h;
            var pr = new float[N]; var pg = new float[N]; var pb = new float[N]; var pa = new float[N];
            for (int i = 0; i < N; i++)
            {
                var c = px[i];
                float a = c.a / 255f;
                pa[i] = a; pr[i] = c.r / 255f * a; pg[i] = c.g / 255f * a; pb[i] = c.b / 255f * a;
            }
            return ToIcon(pr, pg, pb, pa, w, h, iconSize, outline, out _);
        }

        /// <summary>
        /// Crops to the visible pixels, box-filters into (size−2)² centred in a size² image (1px margin for the
        /// outline), thresholds alpha at 0.4 for crisp Minecraft-like edges and optionally adds a 1px dark outline.
        /// Input planes are premultiplied (colour·alpha), rows bottom-up.
        /// </summary>
        private static PixelImage ToIcon(float[] pr, float[] pg, float[] pb, float[] pa, int w, int h, int size, bool outline, out string crop)
        {
            crop = "";
            int minX = w, minY = h, maxX = -1, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (pa[y * w + x] > 0.1f)
                    {
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
            if (maxX < 0) return null;
            size = Mathf.Clamp(size, 8, 128);
            int inner = size - 2;
            float side = Mathf.Max(maxX - minX + 1, maxY - minY + 1);
            float s = side / inner;
            float x0 = (minX + maxX + 1) * 0.5f - inner * s * 0.5f;
            float y0 = (minY + maxY + 1) * 0.5f - inner * s * 0.5f;
            crop = "model " + (maxX - minX + 1) + "x" + (maxY - minY + 1) + "px -> " + size + "x" + size;

            var img = new PixelImage(size, size);
            var opaque = new bool[size * size];
            float area = s * s;
            for (int oy = 0; oy < inner; oy++)
            {
                float by0 = y0 + oy * s, by1 = by0 + s;
                int sy0 = Mathf.FloorToInt(by0), sy1 = Mathf.CeilToInt(by1);
                for (int ox = 0; ox < inner; ox++)
                {
                    float bx0 = x0 + ox * s, bx1 = bx0 + s;
                    int sx0 = Mathf.FloorToInt(bx0), sx1 = Mathf.CeilToInt(bx1);
                    float ar = 0f, ag = 0f, ab = 0f, aa = 0f;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        if (sy < 0 || sy >= h) continue;
                        float wy = Mathf.Min(sy + 1, by1) - Mathf.Max(sy, by0);
                        if (wy <= 0f) continue;
                        int row = sy * w;
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            if (sx < 0 || sx >= w) continue;
                            float wx = Mathf.Min(sx + 1, bx1) - Mathf.Max(sx, bx0);
                            if (wx <= 0f) continue;
                            float wgt = wx * wy;
                            int i = row + sx;
                            ar += pr[i] * wgt; ag += pg[i] * wgt; ab += pb[i] * wgt; aa += pa[i] * wgt;
                        }
                    }
                    float a = aa / area;
                    if (a < 0.4f || aa <= 0f) continue;
                    int o = (oy + 1) * size + (ox + 1);
                    img.Px[o] = new Color32((byte)Mathf.Clamp(ar / aa * 255f + 0.5f, 0, 255), (byte)Mathf.Clamp(ag / aa * 255f + 0.5f, 0, 255),
                                            (byte)Mathf.Clamp(ab / aa * 255f + 0.5f, 0, 255), 255);
                    opaque[o] = true;
                }
            }

            if (outline)
            {
                var src = (Color32[])img.Px.Clone();
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        int o = y * size + x;
                        if (opaque[o]) continue;
                        int r = 0, g = 0, bl = 0, cnt = 0;
                        if (x > 0 && opaque[o - 1]) { var c = src[o - 1]; r += c.r; g += c.g; bl += c.b; cnt++; }
                        if (x < size - 1 && opaque[o + 1]) { var c = src[o + 1]; r += c.r; g += c.g; bl += c.b; cnt++; }
                        if (y > 0 && opaque[o - size]) { var c = src[o - size]; r += c.r; g += c.g; bl += c.b; cnt++; }
                        if (y < size - 1 && opaque[o + size]) { var c = src[o + size]; r += c.r; g += c.g; bl += c.b; cnt++; }
                        if (cnt == 0) continue;
                        // darkened neighbour colour (keeps the hue, like the dark edge pixels of Minecraft items)
                        img.Px[o] = new Color32((byte)(r / cnt * 0.22f + 10), (byte)(g / cnt * 0.22f + 8), (byte)(bl / cnt * 0.22f + 12), 255);
                    }
            }
            return img;
        }
    }
}
