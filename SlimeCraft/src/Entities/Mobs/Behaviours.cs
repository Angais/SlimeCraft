using UnityEngine;

namespace SlimeCraft.Entities
{
    // Small, self-contained behaviour pieces owned by mobs. They hold state and make decisions; the owning mob turns
    // their answers into movement, sounds and attacks. All durations are in ticks (20 per second).

    // ======================================================================== kite and shoot

    /// <summary>Numbers that shape a <see cref="KiteAndShoot"/> archer.</summary>
    internal struct KiteTuning
    {
        /// <summary>Blocks; inside this distance (with the target settled in view) the archer circles instead of closing in.</summary>
        public float Range;
        /// <summary>Uninterrupted sight needed before circling starts.</summary>
        public float SettleTicks;
        /// <summary>Draw time before an arrow can leave.</summary>
        public float DrawTicks;
        /// <summary>Pause after a shot before the next draw.</summary>
        public float CooldownTicks;
        /// <summary>A target hidden for longer than this makes the archer lower its bow.</summary>
        public float ForgetTicks;
        /// <summary>How often the circling directions are reconsidered.</summary>
        public float RethinkTicks;
        /// <summary>Chance, at each reconsideration, to flip the sideways and the forward/back direction (each on its own).</summary>
        public float FlipChance;
        /// <summary>Share of Range squared below which the archer backs away.</summary>
        public float BackAwayBelow;
        /// <summary>Share of Range squared above which it stops backing away.</summary>
        public float StopBackingAbove;
        /// <summary>Walk speed factor while circling.</summary>
        public float CircleSpeed;
    }

    /// <summary>
    /// Generic "keep your distance and shoot" behaviour, assembled from three parts: a <see cref="SightMemory"/> of the
    /// target, a <see cref="BowCycle"/> that draws and looses on its own clock, and an <see cref="OrbitChoice"/> that
    /// says which way to walk around the target once it is near and has been in view for a moment (until then the
    /// archer simply closes in).
    /// </summary>
    internal sealed class KiteAndShoot
    {
        /// <summary>What the archer wants this step.</summary>
        public struct Plan
        {
            /// <summary>True: circle around the target using <see cref="Ahead"/>/<see cref="Aside"/>; false: walk at it.</summary>
            public bool Circle;
            /// <summary>Weight along the line to the target (negative backs away).</summary>
            public float Ahead;
            /// <summary>Weight to the right of the line to the target (negative goes left).</summary>
            public float Aside;
            /// <summary>Loose an arrow now.</summary>
            public bool Loose;
        }

        private readonly KiteTuning tune;
        private readonly BowCycle bow;
        private SightMemory sight;
        private OrbitChoice orbit;

        public KiteAndShoot(KiteTuning tune)
        {
            this.tune = tune;
            bow = new BowCycle(tune.DrawTicks, tune.CooldownTicks);
        }

        /// <summary>Lowers the bow and forgets the target (the circling directions are kept).</summary>
        public void Forget()
        {
            sight = default(SightMemory);
            orbit.Restart();
            bow.Lower();
        }

        /// <summary>Frame clock: a pulled string keeps being held.</summary>
        public void Tick(float ticks) => bow.Hold(ticks);

        /// <summary>One decision step covering <paramref name="stepTicks"/> ticks.</summary>
        public Plan Decide(bool targetVisible, float distanceSq, float stepTicks)
        {
            sight.Observe(targetVisible, stepTicks);

            var plan = new Plan();
            plan.Loose = bow.Step(stepTicks, targetVisible, sight.HiddenFor > tune.ForgetTicks);

            float rangeSq = tune.Range * tune.Range;
            plan.Circle = distanceSq <= rangeSq && sight.SeenFor >= tune.SettleTicks;
            if (!plan.Circle)
            {
                orbit.Restart();
                return plan;
            }

            orbit.Review(stepTicks, tune.RethinkTicks, tune.FlipChance);
            orbit.KeepBand(distanceSq, rangeSq * tune.BackAwayBelow, rangeSq * tune.StopBackingAbove);
            plan.Ahead = orbit.Ahead;
            plan.Aside = orbit.Aside;
            return plan;
        }
    }

    /// <summary>
    /// How long a target has been in view without a break, or out of view without a break. Seeing it clears the
    /// hidden time; losing it clears the seen time.
    /// </summary>
    internal struct SightMemory
    {
        public float SeenFor;
        public float HiddenFor;

        public void Observe(bool visible, float ticks)
        {
            if (visible)
            {
                SeenFor += ticks;
                HiddenFor = 0f;
            }
            else
            {
                HiddenFor += ticks;
                SeenFor = 0f;
            }
        }
    }

    /// <summary>
    /// Which way an archer walks around its target: to the right or the left of the line to it, and retreating or
    /// pressing in. Both choices may flip on a fixed review rhythm; a distance band then overrides the retreat choice
    /// at its edges (always retreat when too close, never when far enough).
    /// </summary>
    internal struct OrbitChoice
    {
        public bool Rightward;
        public bool Retreat;
        private float sinceReview;

        /// <summary>Weight along the line to the target.</summary>
        public float Ahead => Retreat ? -0.5f : 0.5f;
        /// <summary>Weight to the right of the line to the target.</summary>
        public float Aside => Rightward ? 0.5f : -0.5f;

        /// <summary>Starts the review rhythm over (the current directions stay).</summary>
        public void Restart() { sinceReview = 0f; }

        public void Review(float ticks, float every, float flipChance)
        {
            sinceReview += ticks;
            if (sinceReview < every) return;
            sinceReview = 0f;
            Rightward ^= Rng.F() < flipChance;
            Retreat ^= Rng.F() < flipChance;
        }

        public void KeepBand(float distanceSq, float innerSq, float outerSq)
        {
            if (distanceSq > outerSq) Retreat = false;
            else if (distanceSq < innerSq) Retreat = true;
        }
    }

    /// <summary>
    /// The bow of an archer as an explicit three-phase machine fed with one cue per decision step.
    /// <list type="bullet">
    /// <item><see cref="Phase.Idle"/>: lowered; raised as soon as the target is not lost.</item>
    /// <item><see cref="Phase.Drawing"/>: the string is pulled on the frame clock; losing the target lowers the bow,
    /// a full draw with the target in view looses the arrow.</item>
    /// <item><see cref="Phase.Cooldown"/>: waits on the decision clock; when the wait is over the bow is raised in the
    /// same step (or lowered if the target is lost).</item>
    /// </list>
    /// </summary>
    internal sealed class BowCycle
    {
        public enum Phase { Idle, Drawing, Cooldown }

        private enum Cue { None, Raise, Lower, Release }

        private readonly float drawTicks;
        private readonly float cooldownTicks;
        private float pulled;     // ticks the string has been held back (Drawing)
        private float waitLeft;   // ticks still to wait (Cooldown)

        public Phase Current { get; private set; }

        public BowCycle(float drawTicks, float cooldownTicks)
        {
            this.drawTicks = drawTicks;
            this.cooldownTicks = cooldownTicks;
        }

        public void Lower() { Enter(Phase.Idle); }

        public void Hold(float ticks)
        {
            if (Current == Phase.Drawing) pulled += ticks;
        }

        /// <summary>Runs one decision step; true on the step the arrow leaves.</summary>
        public bool Step(float ticks, bool targetInView, bool targetLost)
        {
            if (Current == Phase.Cooldown) waitLeft -= ticks;

            Cue cue = CueFor(targetInView, targetLost);
            switch (cue)
            {
                case Cue.Raise: Enter(Phase.Drawing); break;
                case Cue.Lower: Enter(Phase.Idle); break;
                case Cue.Release: Enter(Phase.Cooldown); break;
            }
            return cue == Cue.Release;
        }

        private Cue CueFor(bool targetInView, bool targetLost)
        {
            switch (Current)
            {
                case Phase.Drawing:
                    if (targetLost) return Cue.Lower;
                    return targetInView && pulled >= drawTicks ? Cue.Release : Cue.None;
                case Phase.Cooldown:
                    if (waitLeft > 0f) return Cue.None;
                    return targetLost ? Cue.Lower : Cue.Raise;
                default:
                    return targetLost ? Cue.None : Cue.Raise;
            }
        }

        private void Enter(Phase next)
        {
            Current = next;
            pulled = 0f;
            waitLeft = next == Phase.Cooldown ? cooldownTicks : 0f;
        }
    }

    // ======================================================================== fuse

    /// <summary>
    /// A fuse that charges while burning and cools back down when not; it fires once it is fully charged.
    /// <see cref="Swell"/> reaches 1 two ticks before the end, so the swell peaks just before the blast.
    /// </summary>
    internal sealed class FuseTimer
    {
        private readonly float length;
        private float charge;

        /// <summary>True: the charge rises; false: it drains.</summary>
        public bool Burning;

        public FuseTimer(float lengthTicks) { length = lengthTicks; }

        public float Swell => Mathf.Clamp01(charge / (length - 2f));

        /// <summary>
        /// Advances the fuse. <paramref name="lit"/> is true when a burn starts from a cold fuse; the return value is
        /// true when the fuse has run out.
        /// </summary>
        public bool Advance(float ticks, out bool lit)
        {
            lit = Burning && charge <= 0f;
            charge = Mathf.Clamp(charge + (Burning ? ticks : -ticks), 0f, length);
            return charge >= length;
        }
    }

    // ======================================================================== hopping

    /// <summary>Jump-only travel: rests on the ground for a random 10..29 ticks (a third of that when hunting), then leaps.</summary>
    internal struct Hopper
    {
        /// <summary>Yaw (degrees) the next leap goes towards.</summary>
        public float Heading;
        private float restLeft;

        public void Rest(bool hunting)
        {
            restLeft = 10 + Rng.I(20);
            if (hunting) restLeft /= 3f;
        }

        /// <summary>Spends ground time; true once the rest is over (the caller then leaps and calls <see cref="Rest"/>).</summary>
        public bool Rested(float ticks)
        {
            restLeft -= ticks;
            return restLeft <= 0f;
        }

        public Vector3 Forward => Quaternion.Euler(0f, Heading, 0f) * Vector3.forward;
    }

    /// <summary>
    /// Squash and stretch of a bouncing body. A <see cref="Kick"/> (sent by the body's landing and take-off events)
    /// sets a goal that loses 40 % per tick; the value closes half of its remaining gap to that goal every tick.
    /// </summary>
    internal struct SquashSpring
    {
        private float kick;
        private float kickAge;

        /// <summary>Negative = flattened, positive = stretched.</summary>
        public float Value;

        /// <summary>Starts a new goal (landing: -0.5, take-off: 1).</summary>
        public void Kick(float amount)
        {
            kick = amount;
            kickAge = 0f;
        }

        /// <summary>Lets time pass: the value chases the current goal, and the goal ages.</summary>
        public void Advance(float ticks)
        {
            float goal = kick * Mathf.Pow(0.6f, kickAge);
            Value = Mathf.Lerp(goal, Value, Mathf.Pow(0.5f, ticks));
            kickAge += ticks;
        }
    }

    // ======================================================================== chicken parts

    /// <summary>
    /// Wing flutter of a bird: the wings open fast in the air (+1.2 per tick) and fold slowly on the ground
    /// (-0.3 per tick); beating restarts at full strength whenever airborne and fades by 10 % per tick afterwards.
    /// </summary>
    internal sealed class WingFlutter
    {
        private const float FullTurn = Mathf.PI * 2f;

        private float open;             // 0 folded .. 1 spread
        private float strength = 1f;    // beat strength; a new bird starts with a fading beat
        private float beat;             // radians

        public void Step(bool airborne, float ticks)
        {
            open = Mathf.MoveTowards(open, airborne ? 1f : 0f, (airborne ? 1.2f : 0.3f) * ticks);
            strength = (airborne ? 1f : strength) * Mathf.Pow(0.9f, ticks);
            beat = Mathf.Repeat(beat + 2f * strength * ticks, FullTurn);
        }

        /// <summary>Wing roll in radians (0..2), the right wing uses it as is, the left one negated.</summary>
        public float Lift => (Mathf.Sin(beat) + 1f) * open;
    }

    /// <summary>Counts down to the next egg: 6000..11999 ticks (5 to 10 minutes).</summary>
    internal struct EggClock
    {
        /// <summary>Ticks until the next egg (saved with the mob).</summary>
        public float Remaining;

        public void Wind() { Remaining = 6000 + Rng.I(6000); }

        /// <summary>True when an egg is due (the clock is wound again for the next one).</summary>
        public bool Run(float ticks)
        {
            Remaining -= ticks;
            if (Remaining > 0f) return false;
            Wind();
            return true;
        }
    }
}
