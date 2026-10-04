using System;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SlimeCraft.FP
{
    /// <summary>
    /// The overlay camera + transform hierarchy that renders the hand on top of Slime Rancher's world.
    ///
    /// Slime Rancher (UFPS) renders the world with the main camera (vp_FPCamera/SRFPCamera, depth 0, culling mask without
    /// layer 31 "Weapon" and 8 "Player") and the vacpack on layer 31 through a separate weapon camera parented to the
    /// player rig. We do the same with our own camera:
    ///  * child of SR's main camera (inherits position, look, camera bob/shake, no frame lag);
    ///  * clearFlags = Depth so the hand never intersects world geometry, depth just above SR's player cameras (and
    ///    below any later UI camera), Forward path, no HDR target, no image effects: SR's post-processing
    ///    (bloom / SSAO / stylized fog via OnRenderImage on the main camera) has already been resolved into the frame
    ///    when this camera draws, so it is unaffected (the hand simply isn't bloomed, exactly like a weapon camera);
    ///  * renders only one otherwise unused layer chosen at runtime; that layer is stripped from every other camera's
    ///    culling mask each frame (SR's CameraDisabler restores cached masks) and added to the culling mask of scene
    ///    lights that light the world/vacpack so the hand is lit like the vacpack.
    /// Hierarchy: Camera → HandRoot → Arm (renderer)
    ///                               → ItemA → ItemPull (scale) → ItemB → ItemDisplay (scale) → ItemMesh (renderer)
    /// </summary>
    internal sealed class HandRig
    {
        public Camera Cam;
        public Camera Parent;
        public int Layer = -1;
        public Transform Root;
        public Transform Arm;
        public MeshFilter ArmFilter;
        public MeshRenderer ArmRenderer;
        public Transform ItemA, ItemPull, ItemB, ItemDisplay, ItemMesh;
        public MeshFilter ItemFilter;
        public MeshRenderer ItemRenderer;

        private int layerBit;
        private Camera[] camBuf = new Camera[32];
        private float nextDepthCheck;
        private float nextLightFix;
        private bool pendingCameraLog;

        public bool IsBuiltFor(Camera main)
        {
            return Cam != null && Parent != null && main != null && Parent == main && Cam.transform.parent == main.transform;
        }

        // ------------------------------------------------------------------ build / destroy
        public void Build(Camera main, int layer, float fov, float depthOverride)
        {
            Destroy();
            Parent = main;
            Layer = layer;
            layerBit = 1 << layer;

            var go = new GameObject("SlimeCraft_HandCamera");
            go.layer = layer;
            go.transform.SetParent(main.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;

            Cam = go.AddComponent<Camera>();
            Cam.clearFlags = CameraClearFlags.Depth;
            Cam.cullingMask = layerBit;
            Cam.fieldOfView = fov;
            Cam.nearClipPlane = 0.05f; // same near plane Minecraft uses for its hand
            Cam.farClipPlane = 20f;
            Cam.renderingPath = RenderingPath.Forward;
            Cam.allowHDR = false;
            Cam.allowMSAA = main.allowMSAA;
            Cam.useOcclusionCulling = false;
            Cam.depthTextureMode = DepthTextureMode.None;
            Cam.eventMask = 0;
            Cam.rect = main.rect;
            Cam.targetTexture = null; // always the screen; see Maintain
            Cam.targetDisplay = main.targetDisplay;
            Cam.depth = depthOverride > -999f ? depthOverride : ComputeDepth(main);

            Root = NewNode("HandRoot", go.transform);
            Arm = NewNode("Arm", Root);
            ArmFilter = Arm.gameObject.AddComponent<MeshFilter>();
            ArmRenderer = Arm.gameObject.AddComponent<MeshRenderer>();
            SetupRenderer(ArmRenderer);

            ItemA = NewNode("ItemA", Root);
            ItemPull = NewNode("ItemPull", ItemA);
            ItemB = NewNode("ItemB", ItemPull);
            ItemDisplay = NewNode("ItemDisplay", ItemB);
            ItemMesh = NewNode("ItemMesh", ItemDisplay);
            ItemFilter = ItemMesh.gameObject.AddComponent<MeshFilter>();
            ItemRenderer = ItemMesh.gameObject.AddComponent<MeshRenderer>();
            SetupRenderer(ItemRenderer);

            ArmRenderer.enabled = false;
            ItemRenderer.enabled = false;

            pendingCameraLog = true; // dumped after the first Maintain (once our layer was stripped from SR's cameras)
            FixLights();
            nextDepthCheck = Time.realtimeSinceStartup + 2f;
            nextLightFix = Time.realtimeSinceStartup + 10f;
        }

        private Transform NewNode(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.layer = Layer;
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        private static void SetupRenderer(MeshRenderer r)
        {
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = LightProbeUsage.BlendProbes; // ambient/probe light at the player's position (darker in caves)
            r.reflectionProbeUsage = ReflectionProbeUsage.Off;
            r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            r.allowOcclusionWhenDynamic = false;
        }

        public void Destroy()
        {
            if (Cam != null)
            {
                // detach any render target first: the main camera's RT may be released during scene teardown
                Cam.targetTexture = null;
                Cam.enabled = false;
                UnityEngine.Object.Destroy(Cam.gameObject);
            }
            Cam = null; Parent = null; Root = null; Arm = null; ArmFilter = null; ArmRenderer = null;
            ItemA = ItemPull = ItemB = ItemDisplay = ItemMesh = null; ItemFilter = null; ItemRenderer = null;
        }

        // ------------------------------------------------------------------ per frame
        /// <summary>Keeps the camera consistent with SR's main camera and our layer out of every other camera.</summary>
        public void Maintain(Camera main, float fov, float depthOverride)
        {
            if (Cam == null) return;
            if (Cam.fieldOfView != fov) Cam.fieldOfView = fov;
            // Slime Rancher only points its main camera at a texture for short-lived effects (e.g. the pause menu's
            // blurred backdrop) and frees that texture right after. Never follow it - Unity errors when a texture
            // still assigned to a camera is released - just skip drawing the hand while the main camera is offscreen.
            if (Cam.targetTexture != null) Cam.targetTexture = null;
            if (Cam.rect != main.rect) Cam.rect = main.rect;
            int wantMask = main.targetTexture != null ? 0 : layerBit;
            if (Cam.cullingMask != wantMask) Cam.cullingMask = wantMask;

            // strip our layer from all other cameras (no allocation)
            int n = Camera.allCamerasCount;
            if (camBuf.Length < n) camBuf = new Camera[n + 16];
            n = Camera.GetAllCameras(camBuf);
            for (int i = 0; i < n; i++)
            {
                var c = camBuf[i];
                if (c == null || c == Cam) continue;
                int m = c.cullingMask;
                if ((m & layerBit) != 0) c.cullingMask = m & ~layerBit;
            }
            Array.Clear(camBuf, 0, n);

            if (pendingCameraLog)
            {
                pendingCameraLog = false;
                LogCameras(main);
            }

            float now = Time.realtimeSinceStartup;
            if (now >= nextDepthCheck)
            {
                nextDepthCheck = now + 2f;
                float d = depthOverride > -999f ? depthOverride : ComputeDepth(main);
                if (Mathf.Abs(Cam.depth - d) > 1e-4f)
                {
                    FpLog.Info("Hand camera depth " + Cam.depth + " -> " + d);
                    Cam.depth = d;
                }
            }
            if (now >= nextLightFix)
            {
                nextLightFix = now + 10f;
                FixLights();
            }
        }

        public void SetCameraEnabled(bool on)
        {
            if (Cam != null && Cam.enabled != on) Cam.enabled = on;
        }

        // ------------------------------------------------------------------ helpers
        /// <summary>
        /// Just above the highest camera of the player rig (main + UFPS weapon camera) that draws to the same target,
        /// and below the next camera (e.g. a UI camera) if there is one.
        /// </summary>
        private float ComputeDepth(Camera main)
        {
            Transform root = main.transform.root;
            RenderTexture target = main.targetTexture;
            float maxRig = main.depth;
            int n = Camera.allCamerasCount;
            if (camBuf.Length < n) camBuf = new Camera[n + 16];
            n = Camera.GetAllCameras(camBuf);
            for (int i = 0; i < n; i++)
            {
                var c = camBuf[i];
                if (c == null || c == Cam || c.targetTexture != target) continue;
                if (c.transform.root == root && c.depth > maxRig) maxRig = c.depth;
            }
            float next = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                var c = camBuf[i];
                if (c == null || c == Cam || c.targetTexture != target) continue;
                if (c.depth > maxRig && c.depth < next) next = c.depth;
            }
            Array.Clear(camBuf, 0, n);
            return next == float.MaxValue ? maxRig + 1f : (maxRig + next) * 0.5f;
        }

        /// <summary>Lights that light the world (Default layer) or SR's vacpack (layer 31) also light our hand layer.</summary>
        private void FixLights()
        {
            try
            {
                var lights = UnityEngine.Object.FindObjectsOfType<Light>();
                int changed = 0;
                for (int i = 0; i < lights.Length; i++)
                {
                    var l = lights[i];
                    if (l == null) continue;
                    int m = l.cullingMask;
                    if ((m & layerBit) != 0) continue;
                    if ((m & 1) != 0 || (m & (1 << 31)) != 0)
                    {
                        l.cullingMask = m | layerBit;
                        changed++;
                    }
                }
                if (changed > 0) FpLog.Info("Added hand layer " + Layer + " to " + changed + " light culling masks");
            }
            catch (Exception e) { FpLog.Error("FixLights", e, 60f); }
        }

        /// <summary>
        /// Picks a layer for the hand: configured one, else an unnamed user layer that is not used by any renderer in the
        /// loaded scene, not part of SC.SR.WorldMask and not SC.SR.BlockLayer. Preference 30, 23, then 31..8.
        /// </summary>
        public static int ChooseLayer(int configured)
        {
            if (configured >= 8 && configured <= 31)
            {
                FpLog.Info("Hand layer " + configured + " (from config, name '" + LayerMask.LayerToName(configured) + "')");
                return configured;
            }
            int excluded = 0;
            try
            {
                var sr = SC.SR;
                if (sr != null)
                {
                    excluded |= sr.WorldMask;
                    int bl = sr.BlockLayer;
                    if (bl >= 0 && bl < 32) excluded |= 1 << bl;
                }
            }
            catch (Exception e) { FpLog.Error("ChooseLayer SR", e, 60f); }

            int used = 0;
            try
            {
                var rs = UnityEngine.Object.FindObjectsOfType<Renderer>();
                for (int i = 0; i < rs.Length; i++) if (rs[i] != null) used |= 1 << rs[i].gameObject.layer;
            }
            catch (Exception e) { FpLog.Error("ChooseLayer renderers", e, 60f); }

            int[] order = new int[26];
            int k = 0;
            order[k++] = 30; order[k++] = 23;
            for (int l = 31; l >= 8 && k < order.Length; l--) if (l != 30 && l != 23) order[k++] = l;
            for (int i = 0; i < k; i++)
            {
                int l = order[i];
                int bit = 1 << l;
                if ((excluded & bit) != 0 || (used & bit) != 0) continue;
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(l))) continue;
                FpLog.Info("Hand layer " + l + " chosen automatically (unnamed, no renderers, not in world mask)");
                return l;
            }
            for (int i = 0; i < k; i++)
            {
                int l = order[i];
                int bit = 1 << l;
                if ((excluded & bit) != 0 || (used & bit) != 0) continue;
                FpLog.Warn("No unnamed free layer; using layer " + l + " ('" + LayerMask.LayerToName(l) + "') for the hand");
                return l;
            }
            FpLog.Warn("No free layer found; falling back to layer 30 for the hand");
            return 30;
        }

        /// <summary>
        /// One-time diagnostic dump of every camera in render order (after our layer was stripped from the others), plus a
        /// verdict: only the hand camera draws the hand layer, and it draws after every camera that has image effects
        /// (SR's SSAOPro / Bloom run in OnRenderImage of FPSCamera and are resolved before the hand is drawn).
        /// </summary>
        private void LogCameras(Camera main)
        {
            try
            {
                var list = new System.Collections.Generic.List<Camera>(Camera.allCameras);
                if (!list.Contains(Cam)) list.Add(Cam); // ours may be disabled while nothing is held
                list.Sort((a, b) => a.depth.CompareTo(b.depth));
                var sb = new StringBuilder();
                sb.Append("Hand camera on layer ").Append(Layer).Append(" depth ").Append(Cam.depth)
                  .Append(" fov ").Append(Cam.fieldOfView.ToString("0.#"))
                  .Append(", parent '").Append(Path(main.transform)).Append("'. Render order:");
                bool othersDrawLayer = false;
                float lastFxDepth = float.NegativeInfinity;
                foreach (var c in list)
                {
                    bool mine = c == Cam;
                    bool drawsLayer = (c.cullingMask & layerBit) != 0;
                    if (!mine && drawsLayer && c.targetTexture == Cam.targetTexture) othersDrawLayer = true;
                    sb.Append("\n  ").Append(mine ? "* " : "  ").Append('\'').Append(Path(c.transform)).Append("' depth=").Append(c.depth)
                      .Append(" mask=0x").Append(c.cullingMask.ToString("X8"))
                      .Append(" handLayer=").Append(drawsLayer ? "yes" : "no")
                      .Append(" clear=").Append(c.clearFlags)
                      .Append(" fov=").Append(c.fieldOfView.ToString("0.#"))
                      .Append(" near=").Append(c.nearClipPlane.ToString("0.###"))
                      .Append(" path=").Append(c.actualRenderingPath)
                      .Append(" hdr=").Append(c.allowHDR)
                      .Append(" target=").Append(c.targetTexture != null ? c.targetTexture.name : "screen");
                    var fx = new StringBuilder();
                    foreach (var mb in c.GetComponents<MonoBehaviour>())
                    {
                        if (mb == null) continue;
                        var t = mb.GetType();
                        if (t.GetMethod("OnRenderImage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null)
                            fx.Append(fx.Length > 0 ? "," : "").Append(t.Name);
                    }
                    if (fx.Length > 0)
                    {
                        sb.Append(" imageEffects=[").Append(fx).Append(']');
                        if (!mine && c.targetTexture == Cam.targetTexture && c.depth > lastFxDepth) lastFxDepth = c.depth;
                    }
                }
                FpLog.Info(sb.ToString());
                if (othersDrawLayer || Cam.cullingMask != layerBit || lastFxDepth >= Cam.depth)
                    FpLog.Warn("Hand camera check FAILED: mask 0x" + Cam.cullingMask.ToString("X8") + " (want only layer " + Layer
                        + "), other camera draws the hand layer: " + othersDrawLayer + ", last image-effect camera depth " + lastFxDepth
                        + " vs hand depth " + Cam.depth);
                else
                    FpLog.Info("Hand camera check OK: only the hand camera draws layer " + Layer + "; it renders after every image-effect camera"
                        + (lastFxDepth > float.NegativeInfinity ? " (last at depth " + lastFxDepth + ")" : "") + ", so SSAO/bloom never touch the hand");
            }
            catch (Exception e) { FpLog.Error("LogCameras", e, 60f); }
        }

        private static string Path(Transform t)
        {
            string p = t.name;
            int guard = 0;
            while (t.parent != null && guard++ < 12) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}
