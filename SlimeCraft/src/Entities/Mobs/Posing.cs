using SlimeCraft.Entities.Model;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>Everything a pose function may read, sampled once per frame before the rig is posed.</summary>
    internal struct PoseInput
    {
        /// <summary>Walk cycle position; it grows with the distance walked (see <see cref="Stride"/>).</summary>
        public float WalkCycle;
        /// <summary>0..1, how strongly the limbs swing.</summary>
        public float WalkStrength;
        /// <summary>Head yaw relative to the body, radians.</summary>
        public float LookYaw;
        /// <summary>Head pitch, radians; positive looks down.</summary>
        public float LookPitch;
        /// <summary>0..1 progress of the melee swing in flight (0 = none).</summary>
        public float Swing;
        /// <summary>Age in ticks (drives idle motions).</summary>
        public float Age;
    }

    /// <summary>
    /// Distance-driven limb cycle of a walking mob: <see cref="Amount"/> eases towards four times the distance moved
    /// per tick (capped at 1), keeping 60 % of the gap each tick, and <see cref="Phase"/> advances by Amount per tick.
    /// </summary>
    internal struct Stride
    {
        public float Phase;
        public float Amount;

        public void Advance(float blocksPerTick, float ticks)
        {
            float goal = Mathf.Min(4f * blocksPerTick, 1f);
            Amount = Mathf.Lerp(goal, Amount, Mathf.Pow(0.6f, ticks));
            Phase += Amount * ticks;
        }
    }

    /// <summary>Small motion terms shared by several pose functions.</summary>
    internal static class PoseTerms
    {
        /// <summary>Radians of walk cycle per unit of <see cref="PoseInput.WalkCycle"/> for the 0.6662-rate gaits.</summary>
        public const float GaitRate = 0.6662f;

        /// <summary>Basic limb swing: cosine of the walk cycle times the stride. Limbs half a cycle apart use phase 0 and pi.</summary>
        public static float Gait(PoseInput p, float phase) => Mathf.Cos(p.WalkCycle * GaitRate + phase) * p.WalkStrength;

        /// <summary>Points a head bone where the mob looks.</summary>
        public static void Look(Bone head, PoseInput p)
        {
            if (head == null) return;
            head.Euler.x = p.LookPitch;
            head.Euler.y = p.LookYaw;
        }

        /// <summary>Sets a bone's pitch; null bones are ignored.</summary>
        public static void Pitch(Bone bone, float radians)
        {
            if (bone != null) bone.Euler.x = radians;
        }
    }

    /// <summary>The four legs of a quadruped rig, swung in diagonal pairs.</summary>
    internal struct QuadLegs
    {
        public Bone HindRight, HindLeft, FrontRight, FrontLeft;

        public static QuadLegs Bind(RigInstance rig)
        {
            var legs = new QuadLegs();
            if (rig == null) return legs;
            legs.HindRight = rig.Find("right_hind_leg");
            legs.HindLeft = rig.Find("left_hind_leg");
            legs.FrontRight = rig.Find("right_front_leg");
            legs.FrontLeft = rig.Find("left_front_leg");
            return legs;
        }

        /// <summary>
        /// Trot: hind-right and front-left share one swing, the other diagonal swings half a cycle later.
        /// <paramref name="phase"/> shifts the whole pattern (pi swaps which diagonal leads).
        /// </summary>
        public void Trot(PoseInput p, float phase)
        {
            float lead = 1.4f * PoseTerms.Gait(p, phase);
            float trail = 1.4f * PoseTerms.Gait(p, phase + Mathf.PI);
            PoseTerms.Pitch(HindRight, lead);
            PoseTerms.Pitch(FrontLeft, lead);
            PoseTerms.Pitch(HindLeft, trail);
            PoseTerms.Pitch(FrontRight, trail);
        }
    }

    /// <summary>How a two-legged mob carries its arms.</summary>
    internal enum ArmStyle
    {
        /// <summary>Arms hang and swing with the walk.</summary>
        Hanging,
        /// <summary>Right arm half raised holding an item.</summary>
        CarryingItem,
        /// <summary>Both arms raised along the look direction, drawing a bow.</summary>
        AimingBow,
        /// <summary>Both arms stretched forward (zombie), with their own chop.</summary>
        Reaching,
    }

    /// <summary>Bones of the zombie, skeleton and enderman rigs.</summary>
    internal sealed class BipedBones
    {
        public Bone Head, Hat, Body, RightArm, LeftArm, RightLeg, LeftLeg;

        public static BipedBones Bind(RigInstance rig)
        {
            var b = new BipedBones();
            if (rig == null) return b;
            b.Head = rig.Find("head");
            b.Hat = rig.Find("hat");
            b.Body = rig.Find("body");
            b.RightArm = rig.Find("right_arm");
            b.LeftArm = rig.Find("left_arm");
            b.RightLeg = rig.Find("right_leg");
            b.LeftLeg = rig.Find("left_leg");
            return b;
        }
    }

    /// <summary>
    /// Pose function of the two-legged mobs, written bone by bone: every bone's final angles are put together from
    /// the gait, the arm style, the melee chop and a slow breathing sway.
    /// </summary>
    internal static class BipedPose
    {
        private const float ShoulderSpan = 5f;    // pixels from the body's centre line to each shoulder pivot
        private const float LegSplay = 0.005f;    // tiny outward yaw/roll of the legs
        private const float LegSwingScale = 1.4f;

        /// <param name="eager">For <see cref="ArmStyle.Reaching"/>: arms held higher (chasing).</param>
        public static void Apply(BipedBones b, PoseInput p, ArmStyle style, bool eager = false)
        {
            float rightLegStep = PoseTerms.Gait(p, 0f);         // also drives the left arm
            float leftLegStep = PoseTerms.Gait(p, Mathf.PI);    // also drives the right arm
            float twist = ChopTwist(p.Swing);

            PoseTerms.Look(b.Head, p);
            if (b.Body != null) b.Body.Euler.y = twist;
            SetLeg(b.RightLeg, LegSwingScale * rightLegStep, LegSplay);
            SetLeg(b.LeftLeg, LegSwingScale * leftLegStep, -LegSplay);

            Vector3 right, left;
            if (style == ArmStyle.Reaching)
            {
                ReachingArms(p.Swing, eager, out right, out left);
            }
            else
            {
                ArmPosture(p, style, leftLegStep, rightLegStep, out right, out left);
                right += RightChop(p.Swing, p.LookPitch, twist);
                left += new Vector3(twist, twist, 0f);
            }

            Vector3 breath = Breath(p.Age);
            SetArm(b.RightArm, right + breath, -1f, twist);
            SetArm(b.LeftArm, left - breath, 1f, twist);
        }

        /// <summary>Short, stiff steps: halves every limb pitch and keeps it within +-0.4 rad (enderman).</summary>
        public static void Stiffen(BipedBones b)
        {
            StiffenOne(b.RightArm);
            StiffenOne(b.LeftArm);
            StiffenOne(b.RightLeg);
            StiffenOne(b.LeftLeg);
        }

        // ------------------------------------------------------------------ terms

        /// <summary>Body yaw during a melee swing; also turns the shoulder line.</summary>
        private static float ChopTwist(float swing) => 0.2f * Mathf.Sin(Mathf.Sqrt(swing) * 2f * Mathf.PI);

        /// <summary>Right-arm chop of a melee swing: a fast lift that eases out, plus a roll; zero when not swinging.</summary>
        private static Vector3 RightChop(float swing, float lookPitch, float twist)
        {
            float remaining = 1f - swing;
            float eased = 1f - remaining * remaining * remaining * remaining;
            float arc = Mathf.Sin(swing * Mathf.PI);
            float lift = 1.2f * Mathf.Sin(eased * Mathf.PI) + 0.75f * arc * (0.7f - lookPitch);
            return new Vector3(-lift, 3f * twist, -0.4f * arc);
        }

        /// <summary>Breathing sway of the right arm (pitch in x, roll in z); the left arm uses the negation.</summary>
        private static Vector3 Breath(float age)
            => new Vector3(0.05f * Mathf.Sin(age * 0.067f), 0f, 0.05f * Mathf.Cos(age * 0.09f) + 0.05f);

        private static void ArmPosture(PoseInput p, ArmStyle style, float rightArmStep, float leftArmStep,
                                       out Vector3 right, out Vector3 left)
        {
            switch (style)
            {
                case ArmStyle.AimingBow:
                    right = new Vector3(p.LookPitch - Mathf.PI / 2f, p.LookYaw - 0.1f, 0f);
                    left = new Vector3(p.LookPitch - Mathf.PI / 2f, p.LookYaw + 0.5f, 0f);
                    break;
                case ArmStyle.CarryingItem:
                    right = new Vector3(rightArmStep * 0.5f - Mathf.PI / 10f, 0f, 0f);
                    left = new Vector3(leftArmStep, 0f, 0f);
                    break;
                default:
                    right = new Vector3(rightArmStep, 0f, 0f);
                    left = new Vector3(leftArmStep, 0f, 0f);
                    break;
            }
        }

        private static void ReachingArms(float swing, bool eager, out Vector3 right, out Vector3 left)
        {
            float chop = Mathf.Sin(swing * Mathf.PI);
            float remaining = 1f - swing;
            float settle = Mathf.Sin((1f - remaining * remaining) * Mathf.PI);
            float pitch = (eager ? -Mathf.PI / 1.5f : -Mathf.PI / 2.25f) + 1.2f * chop - 0.4f * settle;
            float spread = 0.6f * chop - 0.1f;
            right = new Vector3(pitch, spread, 0f);
            left = new Vector3(pitch, -spread, 0f);
        }

        private static void SetLeg(Bone leg, float pitch, float splay)
        {
            if (leg == null) return;
            leg.Euler = new Vector3(pitch, splay, splay);
        }

        /// <param name="side">-1 for the right arm, +1 for the left arm.</param>
        private static void SetArm(Bone arm, Vector3 euler, float side, float twist)
        {
            if (arm == null) return;
            arm.Euler = euler;
            arm.Offset.x = side * ShoulderSpan * Mathf.Cos(twist);
            arm.Offset.z = -side * ShoulderSpan * Mathf.Sin(twist);
        }

        private static void StiffenOne(Bone limb)
        {
            if (limb != null) limb.Euler.x = Mathf.Clamp(limb.Euler.x * 0.5f, -0.4f, 0.4f);
        }
    }
}
