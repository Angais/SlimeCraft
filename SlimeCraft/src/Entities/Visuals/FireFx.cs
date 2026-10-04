using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// The burning-mob overlay that matches Minecraft's look: a column of flat flame sheets wrapped around the mob,
    /// turned towards the viewer around the vertical axis only.
    ///
    /// Both fire textures of the player's jar (block/fire_0 and block/fire_1, vertical animation strips) are
    /// composed once into a single texture with one 16 px band per animation step, so one shared material animates
    /// every burning mob at the same time just by moving its texture offset. Flame meshes are cached per hitbox
    /// size. Deciding when something burns, fire damage and flame particles belong to McMob.
    /// </summary>
    internal static class FireFx
    {
        private const int FramePx = 16;
        private const int StepCount = 32;           // animation steps in the composed texture (1 per tick, 1.6 s loop)
        private const int MaxSheets = 32;
        private const float SheetHeight = 1.4f;     // flame units
        private const float SheetRise = 0.45f;      // each sheet starts this much higher than the previous one
        private const float SheetRecede = 0.03f;    // and sits this much further from the viewer
        private const float SheetNarrowing = 0.9f;  // and is this much narrower

        private static Material sharedMaterial;
        private static bool unavailable;
        private static int displayedStep = -1;
        private static readonly Dictionary<long, Mesh> meshesBySize = new Dictionary<long, Mesh>();

        // ------------------------------------------------------------------ public API

        /// <summary>Moves the shared material to the animation step for the current game time (once per frame, no allocation).</summary>
        public static void Animate()
        {
            if (sharedMaterial == null) return;
            int step = (int)Mathf.Repeat(Mathf.Floor(Time.time * Mc.TPS), StepCount);
            if (step == displayedStep) return;
            displayedStep = step;
            sharedMaterial.mainTextureOffset = new Vector2(0f, step / (float)StepCount);
        }

        /// <summary>
        /// New inactive flame object under <paramref name="parent"/> (whose origin is the bottom centre of the box)
        /// sized for a <paramref name="bbWidth"/> x <paramref name="bbHeight"/> hitbox. Null while the fire resources
        /// cannot be built (assets not ready yet, or permanently unavailable).
        /// </summary>
        public static GameObject Create(Transform parent, float bbWidth, float bbHeight)
        {
            if (unavailable) return null;
            if (sharedMaterial == null && !TryBuildResources()) return null;

            var go = new GameObject("fire");
            if (parent != null)
            {
                go.layer = parent.gameObject.layer;
                go.transform.SetParent(parent, false);
            }
            go.AddComponent<MeshFilter>().sharedMesh = FlameMesh(bbWidth, bbHeight);
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = sharedMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            go.SetActive(false);
            return go;
        }

        /// <summary>Turns the flames (world rotation, yaw only) so their front side faces the game camera.</summary>
        public static void FaceCamera(Transform fire)
        {
            if (fire == null || SC.SR == null) return;
            var cam = SC.SR.MainCamera;
            if (cam == null) return;
            Vector3 toCamera = cam.transform.position - fire.position;
            toCamera.y = 0f;
            if (toCamera.sqrMagnitude < 1e-4f) return;   // camera (almost) straight above or below
            fire.rotation = Quaternion.LookRotation(toCamera, Vector3.up);
        }

        // ------------------------------------------------------------------ shared texture and material

        private static bool TryBuildResources()
        {
            if (!EMat.AssetsReady) return false;   // try again later
            Texture2D left = null, right = null;
            try
            {
                left = EMat.LoadFullJarTexture("block/fire_0");
                right = EMat.LoadFullJarTexture("block/fire_1");
                int leftFrames = UsableFrames(left), rightFrames = UsableFrames(right);
                if (leftFrames == 0 || rightFrames == 0) { unavailable = true; return false; }

                int[] leftOrder = FrameOrder("block/fire_0", leftFrames);
                int[] rightOrder = FrameOrder("block/fire_1", rightFrames);

                const int stripW = FramePx * 2, stripH = FramePx * StepCount;
                var pixels = new Color32[stripW * stripH];
                var leftPixels = left.GetPixels32();
                var rightPixels = right.GetPixels32();
                for (int step = 0; step < StepCount; step++)
                {
                    CopyFrame(leftPixels, left.width, left.height, leftOrder[step % leftOrder.Length], pixels, stripW, 0, step * FramePx);
                    CopyFrame(rightPixels, right.width, right.height, rightOrder[step % rightOrder.Length], pixels, stripW, FramePx, step * FramePx);
                }

                var shader = EMat.Find("Unlit/Transparent Cutout", "Unlit/Transparent");
                if (shader == null) { unavailable = true; return false; }

                var strip = new Texture2D(stripW, stripH, TextureFormat.RGBA32, false)
                {
                    name = "SC_FireStrip",
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Repeat,
                };
                strip.SetPixels32(pixels);
                strip.Apply(false, false);

                var mat = new Material(shader) { name = "SC_Fire", mainTexture = strip, renderQueue = 2460 };
                if (mat.HasProperty("_Cutoff")) mat.SetFloat("_Cutoff", 0.1f);
                mat.mainTextureScale = new Vector2(1f, 1f / StepCount);   // one quad shows exactly one band
                sharedMaterial = mat;
                displayedStep = -1;
                Animate();
                return true;
            }
            catch (Exception e)
            {
                ELog.Error("FireFx init", e);
                unavailable = true;
                return false;
            }
            finally
            {
                if (left != null) UnityEngine.Object.Destroy(left);
                if (right != null) UnityEngine.Object.Destroy(right);
            }
        }

        /// <summary>Number of whole 16 px frames stacked in a strip (0 when the texture is missing or too narrow).</summary>
        private static int UsableFrames(Texture2D tex)
        {
            if (tex == null || tex.width < FramePx) return 0;
            return tex.height / FramePx;
        }

        /// <summary>
        /// Frame shown at each animation step, read from the texture's .mcmeta "animation.frames" list (numbers or
        /// objects with an "index"); natural order when the list is missing or empty. Indices wrap into [0, frames).
        /// </summary>
        private static int[] FrameOrder(string texturePath, int frames)
        {
            var order = new List<int>();
            try
            {
                string metaPath = "assets/minecraft/textures/" + texturePath + ".png.mcmeta";
                var assets = SC.Assets;
                if (assets != null && assets.JarExists(metaPath))
                {
                    var meta = assets.ReadJarJson(metaPath);
                    var list = meta != null ? meta["animation"]["frames"] : JsonNode.Missing;
                    if (list.IsArray)
                    {
                        foreach (var entry in list.Items)
                        {
                            if (entry.IsNumber) order.Add(Wrap(entry.AsInt(), frames));
                            else if (entry.IsObject && entry["index"].IsNumber) order.Add(Wrap(entry["index"].AsInt(), frames));
                        }
                    }
                }
            }
            catch (Exception e) { ELog.Error("FireFx mcmeta " + texturePath, e); order.Clear(); }

            if (order.Count == 0)
                for (int i = 0; i < frames; i++) order.Add(i);
            return order.ToArray();
        }

        private static int Wrap(int index, int count)
        {
            int m = index % count;
            return m < 0 ? m + count : m;
        }

        /// <summary>Copies frame <paramref name="frame"/> (counted from the top of the PNG) into a 16x16 slot, upright.</summary>
        private static void CopyFrame(Color32[] src, int srcW, int srcH, int frame, Color32[] dst, int dstW, int dstX, int dstY)
        {
            int srcY = srcH - FramePx * (frame + 1);   // Unity pixel rows start at the bottom
            for (int y = 0; y < FramePx; y++)
            {
                int from = (srcY + y) * srcW;
                int to = (dstY + y) * dstW + dstX;
                for (int x = 0; x < FramePx; x++) dst[to + x] = src[from + x];
            }
        }

        // ------------------------------------------------------------------ flame mesh

        private static Mesh FlameMesh(float bbWidth, float bbHeight)
        {
            long key = ((long)Mathf.RoundToInt(bbWidth * 100f) << 32) ^ (uint)Mathf.RoundToInt(bbHeight * 100f);
            if (meshesBySize.TryGetValue(key, out var cached) && cached != null) return cached;
            var built = BuildFlameMesh(bbWidth, bbHeight);
            meshesBySize[key] = built;
            return built;
        }

        /// <summary>
        /// Stack of sheets in "flame units" scaled by 1.4 x box width. Local +Z points at the viewer (FaceCamera aims
        /// it there), so the viewer's right is local -X. Each sheet is drawn from both sides.
        /// </summary>
        private static Mesh BuildFlameMesh(float bbWidth, float bbHeight)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();

            float unit = 1.4f * bbWidth;
            float relHeight = (bbWidth > 0f && bbHeight > 0f) ? bbHeight / unit : 0f;   // NaN-safe: comparisons fail
            int sheets = 0;
            if (relHeight > 0f)
                while (sheets < MaxSheets && relHeight - SheetRise * sheets > 0f) sheets++;
            float nearest = 0.3f - 0.02f * Mathf.Floor(relHeight);

            for (int i = 0; i < sheets; i++)
            {
                float halfWidth = 0.5f * Mathf.Pow(SheetNarrowing, i) * unit;
                float bottom = SheetRise * i * unit;
                float top = (SheetHeight + SheetRise * i) * unit;
                float toward = (nearest - SheetRecede * i) * unit;

                // Alternate the two fire textures, and mirror every other pair of sheets.
                float columnStart = (i % 2 == 0) ? 0f : 0.5f;
                bool flipped = (i / 2) % 2 == 1;
                float uViewerLeft = flipped ? columnStart + 0.5f : columnStart;
                float uViewerRight = flipped ? columnStart : columnStart + 0.5f;

                int first = verts.Count;
                // viewer-left is local +X, viewer-right is local -X
                verts.Add(new Vector3(halfWidth, bottom, toward)); uvs.Add(new Vector2(uViewerLeft, 0f));
                verts.Add(new Vector3(halfWidth, top, toward)); uvs.Add(new Vector2(uViewerLeft, 1f));
                verts.Add(new Vector3(-halfWidth, top, toward)); uvs.Add(new Vector2(uViewerRight, 1f));
                verts.Add(new Vector3(-halfWidth, bottom, toward)); uvs.Add(new Vector2(uViewerRight, 0f));

                tris.Add(first); tris.Add(first + 1); tris.Add(first + 2);
                tris.Add(first); tris.Add(first + 2); tris.Add(first + 3);
                tris.Add(first); tris.Add(first + 2); tris.Add(first + 1);
                tris.Add(first); tris.Add(first + 3); tris.Add(first + 2);
            }
            return Model.RigBaker.MakeMesh("fire", verts, uvs, tris);
        }
    }
}
