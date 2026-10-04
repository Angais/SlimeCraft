using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>
    /// Frame-side half of the hand: turns the ticked state into camera-space poses (Minecraft-style space: x right,
    /// y up, -z forward, every step applied in the current local frame). Steps that never change are composed once
    /// into fixed offsets below; only the animated steps are evaluated per frame.
    /// </summary>
    internal sealed partial class ItemInHand
    {
        private const float Pi = Mathf.PI;

        // Bare arm: the fixed chain that brings the shoulder of the arm model to the lower right of the view,
        // followed by the shoulder pivot (-5, 2, 0) px and a small constant roll of 0.1 rad.
        private static readonly McPose ArmMount = Compose(
            Step.Move(-1f, 3.6f, 3.5f),
            Step.Turn(Vector3.forward, 120f),
            Step.Turn(Vector3.right, 200f),
            Step.Turn(Vector3.up, -135f),
            Step.Move(5.6f, 0f, 0f),
            Step.Move(-5f / 16f, 2f / 16f, 0f),
            Step.Turn(Vector3.forward, 0.1f * Mathf.Rad2Deg));

        // Guarding pose for UseAnimation.Block (the classic sword-block look, no shield).
        private static readonly McPose GuardHold = Compose(
            Step.Move(-0.14142136f, 0.08f, 0.14142136f),
            Step.Turn(Vector3.right, -102.25f),
            Step.Turn(Vector3.up, 13.365f),
            Step.Turn(Vector3.forward, 78.05f));

        // Bow held up and tilted toward the centre of the screen while drawing.
        private static readonly McPose BowAim = Compose(
            Step.Move(-0.2785682f, 0.18344387f, 0.15731531f),
            Step.Turn(Vector3.right, -13.935f),
            Step.Turn(Vector3.up, 35.3f),
            Step.Turn(Vector3.forward, -9.785f));

        // ===================================================================================================
        //  Shared base: hurt tilt, walking bob, sway lag, mining shake
        // ===================================================================================================

        public McPose BasePose(float partial, bool viewBob, float hurtStrength, bool sway, float miningShake)
        {
            McPose p = McPose.Identity;
            if (hurtStrength > 0f) AddHurtTilt(ref p, partial, hurtStrength);
            if (viewBob) AddWalkBob(ref p, partial);
            if (sway) AddSwayLag(ref p, partial);
            if (miningShake > 0f && MiningProgress >= 0f) AddMiningShake(ref p, partial, miningShake);
            return p;
        }

        /// <summary>A quick roll (up to 14 degrees times the strength) about an axis pointing at the damage source.</summary>
        private void AddHurtTilt(ref McPose p, float partial, float strength)
        {
            float left = hurtLeft - partial;
            if (left < 0f) return;
            float x = left / HurtTicks;
            x *= x;
            float amount = Mathf.Sin(Pi * x * x);
            p.RotY(-hurtFrom);
            p.RotZ(-amount * 14f * strength);
            p.RotY(hurtFrom);
        }

        /// <summary>Side-to-side and up-down bob driven by the walked distance, faded in and out by the bob amount.</summary>
        private void AddWalkBob(ref McPose p, float partial)
        {
            float phase = -(walked + (walked - walkedBefore) * partial) * Pi;
            float amp = Mathf.LerpUnclamped(bobAmountBefore, bobAmount, partial);
            float side = Mathf.Sin(phase) * amp;
            p.Translate(side * 0.5f, -Mathf.Abs(Mathf.Cos(phase) * amp), 0f);
            p.RotZ(side * 3f);
            p.RotX(Mathf.Abs(Mathf.Cos(phase - 0.2f) * amp) * 5f);
        }

        /// <summary>The hand trails a tenth of the gap between the camera angles and their smoothed copies.</summary>
        private void AddSwayLag(ref McPose p, float partial)
        {
            p.RotX((XRot - Mathf.LerpUnclamped(lagPitchBefore, lagPitch, partial)) * 0.1f);
            p.RotY((YRot - Mathf.LerpUnclamped(lagYawBefore, lagYaw, partial)) * 0.1f);
        }

        /// <summary>SlimeCraft's own touch: a faint jitter while mining that grows as the block gets closer to breaking.</summary>
        private void AddMiningShake(ref McPose p, float partial, float strength)
        {
            float t = TickCount + partial;
            float amp = 0.0035f * strength * (0.35f + 0.65f * Mathf.Clamp01(MiningProgress));
            p.Translate(Mathf.Sin(2.7f * t) * amp, Mathf.Sin(3.9f * t + 1.3f) * amp, 0f);
        }

        // ===================================================================================================
        //  Bare arm (empty hand)
        // ===================================================================================================

        public McPose ArmPose(McPose p, float partial, float inverseArmHeight)
        {
            float swing = AttackAnim(partial);
            float sweep = Mathf.Sin(Pi * Mathf.Sqrt(swing));   // fast-in arc of the punch
            float lift = Mathf.Sin(2f * Pi * Mathf.Sqrt(swing));
            float jab = Mathf.Sin(Pi * swing);

            p.Translate(0.64f - 0.3f * sweep,
                        -0.6f + 0.4f * lift - 0.6f * inverseArmHeight,
                        -0.72f - 0.4f * jab);
            p.RotY(45f + 70f * sweep);
            p.RotZ(-20f * Mathf.Sin(Pi * swing * swing));
            Append(ref p, ArmMount);
            return p;
        }

        // ===================================================================================================
        //  Held item
        // ===================================================================================================

        /// <summary>
        /// Pose of the held item, split around one non-uniform scale so it stays exact as Unity transforms:
        /// world = <paramref name="a"/> * scale(<paramref name="pull"/>) * <paramref name="b"/> * scale(display) * mesh.
        /// </summary>
        public void ItemPoses(McPose basePose, float partial, float nowTicks, float inverseArmHeight, ItemDisplay display,
                              out McPose a, out Vector3 pull, out McPose b)
        {
            a = basePose;
            pull = Vector3.one;
            b = McPose.Identity;

            UseAnimation hold = Using ? UseAnim : UseAnimation.None;
            switch (hold)
            {
                case UseAnimation.Eat:
                    HoldToMouth(ref a, nowTicks, inverseArmHeight);
                    break;
                case UseAnimation.Block:
                    PlaceInHand(ref a, inverseArmHeight);
                    Append(ref a, GuardHold);
                    break;
                case UseAnimation.Bow:
                    DrawBow(ref a, ref pull, ref b, nowTicks, inverseArmHeight);
                    break;
                default:
                    SwingItem(ref a, partial, inverseArmHeight);
                    break;
            }

            // the item's own first-person display transform (translation in blocks, X-then-Y-then-Z rotation);
            // its scale is a separate node owned by the rig
            b.Translate(display.Translation.x, display.Translation.y, display.Translation.z);
            b.Rotate(McPose.EulerXYZ(display.Rotation));
        }

        /// <summary>Resting spot of a held item at the lower right, pushed down while the hand is lowered.</summary>
        private static void PlaceInHand(ref McPose p, float inverseArmHeight)
        {
            p.Translate(0.56f, -0.52f - 0.6f * inverseArmHeight, -0.72f);
        }

        private void SwingItem(ref McPose p, float partial, float inverseArmHeight)
        {
            float swing = AttackAnim(partial);
            float arc = Mathf.Sin(Pi * Mathf.Sqrt(swing));

            PlaceInHand(ref p, inverseArmHeight);
            p.Translate(-0.4f * arc, 0.2f * Mathf.Sin(2f * Pi * Mathf.Sqrt(swing)), -0.2f * Mathf.Sin(Pi * swing));
            p.RotY(45f - 20f * Mathf.Sin(Pi * swing * swing));
            p.RotZ(-20f * arc);
            p.RotX(-80f * arc);
            p.RotY(-45f);
        }

        /// <summary>
        /// Eating / drinking: the item swings to the mouth within a few ticks and bobs there (chewing) until the
        /// last fifth of the meal; past the end it simply stays at the mouth.
        /// </summary>
        private void HoldToMouth(ref McPose p, float nowTicks, float inverseArmHeight)
        {
            float left = Mathf.Max(0f, consumeTicks - UseTime(nowTicks));
            float fraction = left / consumeTicks;
            float toMouth = 1f - Mathf.Pow(fraction, 27f);

            if (fraction < 0.8f) p.Translate(0f, Mathf.Abs(Mathf.Cos(Pi * left / 4f) * 0.1f), 0f);
            p.Translate(0.6f * toMouth, -0.5f * toMouth, 0f);
            p.RotY(90f * toMouth);
            p.RotX(10f * toMouth);
            p.RotZ(30f * toMouth);
            PlaceInHand(ref p, inverseArmHeight);
        }

        /// <summary>Bow draw: the bow is raised, trembles under tension, comes closer and stretches along its depth.</summary>
        private void DrawBow(ref McPose a, ref Vector3 pull, ref McPose b, float nowTicks, float inverseArmHeight)
        {
            float t = UseTime(nowTicks);
            float secs = t / 20f;
            float power = Mathf.Min(1f, (secs * secs + 2f * secs) / 3f); // full draw after one second

            PlaceInHand(ref a, inverseArmHeight);
            Append(ref a, BowAim);
            if (power > 0.1f) a.Translate(0f, Mathf.Sin((t - 0.1f) * 1.3f) * (power - 0.1f) * 0.004f, 0f);
            a.Translate(0f, 0f, 0.04f * power);

            pull = new Vector3(1f, 1f, 1f + 0.2f * power);
            b.RotY(-45f);
        }

        // ===================================================================================================
        //  Fixed-offset helpers
        // ===================================================================================================

        /// <summary>One rigid step of a fixed chain: a translation or a rotation about a local axis.</summary>
        private struct Step
        {
            public Vector3 Offset;
            public Quaternion Rotation;

            public static Step Move(float x, float y, float z)
            {
                Step s; s.Offset = new Vector3(x, y, z); s.Rotation = Quaternion.identity; return s;
            }

            public static Step Turn(Vector3 axis, float degrees)
            {
                Step s; s.Offset = Vector3.zero; s.Rotation = Quaternion.AngleAxis(degrees, axis); return s;
            }
        }

        /// <summary>Composes a list of local steps into one rigid offset (done once, at type initialisation).</summary>
        private static McPose Compose(params Step[] steps)
        {
            McPose p = McPose.Identity;
            for (int i = 0; i < steps.Length; i++)
            {
                p.Translate(steps[i].Offset.x, steps[i].Offset.y, steps[i].Offset.z);
                p.Rotate(steps[i].Rotation);
            }
            return p;
        }

        /// <summary>Applies a pre-composed rigid offset in the current local frame of <paramref name="p"/>.</summary>
        private static void Append(ref McPose p, McPose local)
        {
            p.Translate(local.P.x, local.P.y, local.P.z);
            p.Rotate(local.Q);
        }
    }
}
