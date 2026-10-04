using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Entities.Model
{
    /// <summary>
    /// Live, animatable bone of a spawned rig. Pose code writes <see cref="Offset"/> (pixels), <see cref="Euler"/>
    /// (radians) and <see cref="Scale"/> in rig space every frame, starting from <see cref="RestPose"/>;
    /// <see cref="RigInstance.Push"/> then moves the Unity transforms.
    /// </summary>
    internal sealed class Bone
    {
        public readonly string Name;
        public readonly BonePose RestPose;
        public Vector3 Offset;
        public Vector3 Euler;
        public Vector3 Scale;
        public bool Shown = true;

        internal readonly Transform Node;
        internal readonly Mesh Mesh;
        internal MeshRenderer Renderer;
        private bool nodeActive = true;

        internal Bone(string name, BonePose rest, Transform node, Mesh mesh)
        {
            Name = name;
            RestPose = rest;
            Node = node;
            Mesh = mesh;
        }

        /// <summary>Writes the pose to the transform; rig space maps to Unity space through a point reflection.</summary>
        internal void WriteNode()
        {
            if (Shown != nodeActive)
            {
                nodeActive = Shown;
                Node.gameObject.SetActive(Shown);
            }
            if (!Shown) return;
            Node.localPosition = Offset * (-1f / 16f);
            Node.localRotation = RigBaker.EulerToRotation(Euler);
            Node.localScale = Scale;
        }
    }

    /// <summary>
    /// A spawned rig: one GameObject per bone under an origin transform, sharing the baked meshes and material.
    /// A rig can follow another one (sheep wool, slime shell): it then copies the leader's pose bone by bone.
    /// </summary>
    internal sealed class RigInstance
    {
        /// <summary>Height of the rig origin above the entity's feet, so that rig y = 24 px lands on the ground.</summary>
        public const float OriginHeight = 1.501f;

        public readonly Transform Origin;
        private readonly Bone[] bones;
        private readonly Dictionary<string, Bone> byName = new Dictionary<string, Bone>();

        private RigInstance leader;
        private int[] leaderSlots;   // for each of our bones: index of the leader bone with the same name, or -1

        private readonly List<MeshRenderer> tintRenderers = new List<MeshRenderer>();
        private MaterialPropertyBlock tintBlock;
        private Color appliedTint = new Color(0f, 0f, 0f, -1f);

        private RigInstance(Transform origin, int boneCount)
        {
            Origin = origin;
            bones = new Bone[boneCount];
        }

        public static RigInstance Spawn(BakedRig baked, Transform parent, Material mat, int layer, float originHeight = OriginHeight)
        {
            var originGo = new GameObject("model") { layer = layer };
            originGo.transform.SetParent(parent, false);
            originGo.transform.localPosition = new Vector3(0f, originHeight, 0f);
            originGo.transform.localRotation = Quaternion.identity;

            var rig = new RigInstance(originGo.transform, baked.Bones.Length);
            for (int i = 0; i < baked.Bones.Length; i++)
            {
                BakedBone source = baked.Bones[i];
                var go = new GameObject(source.Name) { layer = layer };
                Transform hangFrom = source.Parent < 0 ? rig.Origin : rig.bones[source.Parent].Node;
                go.transform.SetParent(hangFrom, false);

                var bone = new Bone(source.Name, source.Rest, go.transform, source.Mesh);
                if (source.Mesh != null)
                {
                    go.AddComponent<MeshFilter>().sharedMesh = source.Mesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    mr.receiveShadows = true;
                    bone.Renderer = mr;
                }
                rig.bones[i] = bone;
                if (!rig.byName.ContainsKey(source.Name)) rig.byName[source.Name] = bone;
            }
            rig.Rest();
            return rig;
        }

        /// <summary>The first bone with this name, or null.</summary>
        public Bone Find(string name) => byName.TryGetValue(name, out var b) ? b : null;

        /// <summary>Puts every bone back into its rest pose and makes it visible.</summary>
        public void Rest()
        {
            for (int i = 0; i < bones.Length; i++)
            {
                Bone b = bones[i];
                b.Offset = b.RestPose.Offset;
                b.Euler = b.RestPose.Euler;
                b.Scale = b.RestPose.Scale;
                b.Shown = true;
            }
        }

        /// <summary>Moves the Unity transforms to the current bone poses.</summary>
        public void Push()
        {
            for (int i = 0; i < bones.Length; i++) bones[i].WriteNode();
        }

        /// <summary>Makes this rig copy <paramref name="other"/>'s pose by bone name whenever <see cref="TakeLeaderPose"/> runs.</summary>
        public void Follow(RigInstance other)
        {
            leader = other;
            leaderSlots = new int[bones.Length];
            for (int i = 0; i < bones.Length; i++)
            {
                leaderSlots[i] = -1;
                if (other == null) continue;
                for (int j = 0; j < other.bones.Length; j++)
                {
                    if (other.bones[j].Name != bones[i].Name) continue;
                    leaderSlots[i] = j;
                    break;
                }
            }
        }

        /// <summary>Copies the current pose of the followed rig onto the bones that share a name with it.</summary>
        public void TakeLeaderPose()
        {
            if (leader == null) return;
            for (int i = 0; i < bones.Length; i++)
            {
                int slot = leaderSlots[i];
                if (slot < 0) continue;
                Bone from = leader.bones[slot], to = bones[i];
                to.Offset = from.Offset;
                to.Euler = from.Euler;
                to.Scale = from.Scale;
                to.Shown = from.Shown;
            }
        }

        /// <summary>
        /// Adds an extra renderer (same mesh, another material) to every meshed bone accepted by the filter, for
        /// layers that reuse the geometry (enderman eyes, golem cracks, wool undercoat).
        /// </summary>
        public List<MeshRenderer> AddLayer(Material mat, System.Func<string, bool> boneFilter = null)
        {
            var added = new List<MeshRenderer>();
            if (mat == null) return added;
            for (int i = 0; i < bones.Length; i++)
            {
                Bone b = bones[i];
                if (b.Mesh == null || b.Renderer == null) continue;
                if (boneFilter != null && !boneFilter(b.Name)) continue;
                var go = new GameObject("layer") { layer = b.Renderer.gameObject.layer };
                go.transform.SetParent(b.Renderer.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = b.Mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                added.Add(mr);
            }
            return added;
        }

        /// <summary>Creates the (disabled) tint renderers used for the hurt tint and the white flash.</summary>
        public void EnableOverlay(Texture2D baseTex)
        {
            var mat = EMat.Overlay(baseTex);
            if (mat == null) return;
            foreach (var r in AddLayer(mat))
            {
                r.receiveShadows = false;
                r.enabled = false;
                tintRenderers.Add(r);
            }
        }

        /// <summary>
        /// Hurt / flash tint: weight = how much of the colour replaces the texture (hurt: red 0.3, TNT / creeper
        /// flash: white up to 0.75). A weight of zero or less hides the tint.
        /// </summary>
        public void SetOverlay(Color c, float weight)
        {
            Color wanted = weight <= 0.001f ? new Color(0f, 0f, 0f, 0f) : new Color(c.r, c.g, c.b, Mathf.Clamp01(weight));
            if (wanted == appliedTint) return;
            appliedTint = wanted;
            bool visible = wanted.a > 0f;
            if (tintBlock == null) tintBlock = new MaterialPropertyBlock();
            tintBlock.SetColor("_Color", wanted);
            tintBlock.SetColor("_RendererColor", Color.white);
            tintBlock.SetVector("_Flip", new Vector4(1f, 1f, 0f, 0f));
            for (int i = 0; i < tintRenderers.Count; i++)
            {
                var r = tintRenderers[i];
                if (r == null) continue;
                r.enabled = visible;
                if (visible) r.SetPropertyBlock(tintBlock);
            }
        }
    }
}
