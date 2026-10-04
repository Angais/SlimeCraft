using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Entities.Model
{
    // Data side of the blocky mob models ("rigs"). A rig is a flat array of bones; each bone points at its parent by
    // array index (always an earlier entry, or -1 for the rig origin), has a rest pose and owns a few textured boxes.
    // Rigs are written as text sheets (see RigSheet) and turned into meshes by RigBaker.
    //
    // Units and axes used by everything under Model/:
    //  * lengths are skin pixels: 16 px = one block = one Unity unit,
    //  * rig space has +Y pointing down and the creature facing -Z,
    //  * rotations are radians, applied about the bone pivot around X first, then Y, then Z,
    //  * skin coordinates are texels counted from the top-left corner of the image.

    /// <summary>Placement of a bone inside its parent: pivot offset (pixels), Euler angles (radians) and scale.</summary>
    internal struct BonePose
    {
        public Vector3 Offset;
        public Vector3 Euler;
        public Vector3 Scale;

        /// <summary>No offset, no rotation, unit scale.</summary>
        public static readonly BonePose Identity = new BonePose(Vector3.zero, Vector3.zero, 1f);

        public BonePose(Vector3 offset, Vector3 euler, float uniformScale)
        {
            Offset = offset;
            Euler = euler;
            Scale = new Vector3(uniformScale, uniformScale, uniformScale);
        }
    }

    /// <summary>One textured cuboid owned by a bone. Immutable.</summary>
    internal sealed class RigBox
    {
        /// <summary>Corner with the smallest coordinates, in bone space (pixels).</summary>
        public readonly Vector3 Min;
        /// <summary>Edge lengths in pixels; a zero edge gives a flat, two-sided card.</summary>
        public readonly Vector3 Size;
        /// <summary>Grows the geometry by this many pixels on every side; the skin mapping does not change.</summary>
        public readonly float Pad;
        /// <summary>Top-left texel of the unfolded box on the skin.</summary>
        public readonly int SkinU, SkinV;
        /// <summary>Textures the box as its own mirror image across X (left limbs reuse right-limb texels).</summary>
        public readonly bool Flip;
        /// <summary>Factor applied to the skin height when this box's UVs are normalised.</summary>
        public readonly float SkinStretchV;

        public RigBox(int skinU, int skinV, Vector3 min, Vector3 size, float pad, bool flip, float skinStretchV)
        {
            SkinU = skinU;
            SkinV = skinV;
            Min = min;
            Size = size;
            Pad = pad;
            Flip = flip;
            SkinStretchV = skinStretchV;
        }
    }

    /// <summary>One bone of a rig.</summary>
    internal sealed class BoneSpec
    {
        public readonly string Name;
        /// <summary>Index of the parent bone in <see cref="RigSpec.Bones"/>, or -1 when it hangs from the rig origin.</summary>
        public readonly int Parent;
        public readonly BonePose Rest;
        public readonly RigBox[] Boxes;

        public BoneSpec(string name, int parent, BonePose rest, RigBox[] boxes)
        {
            Name = name;
            Parent = parent;
            Rest = rest;
            Boxes = boxes ?? new RigBox[0];
        }
    }

    /// <summary>A complete rig: the skin size its texture layout assumes and its bones, parents first.</summary>
    internal sealed class RigSpec
    {
        public readonly string Name;
        public readonly int SkinWidth, SkinHeight;
        /// <summary>Transform of the rig origin. Only used when the whole rig is flattened into one mesh.</summary>
        public readonly BonePose Origin;
        public readonly BoneSpec[] Bones;

        public RigSpec(string name, int skinWidth, int skinHeight, BonePose origin, List<BoneSpec> bones)
        {
            Name = name;
            SkinWidth = skinWidth;
            SkinHeight = skinHeight;
            Origin = origin;
            Bones = bones.ToArray();
        }

        /// <summary>Index of the first bone with this exact name, or -1.</summary>
        public int IndexOf(string boneName)
        {
            for (int i = 0; i < Bones.Length; i++)
                if (string.Equals(Bones[i].Name, boneName, System.StringComparison.Ordinal)) return i;
            return -1;
        }
    }
}
