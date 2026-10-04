using System;
using System.Collections.Generic;
using SlimeCraft.Entities.Model;
using UnityEngine;

namespace SlimeCraft.Entities
{
    // ======================================================================== creeper

    /// <summary>
    /// Walks up to the player, then hisses, swells and flashes white and explodes when its 30-tick
    /// <see cref="FuseTimer"/> runs out. Flint and steel (<see cref="Ignite"/>) lights the fuse for good.
    /// </summary>
    internal sealed class CreeperMob : McMob
    {
        private const float FuseTicks = 30f;
        private const float StartWithinSq = 3f * 3f;   // the fuse catches inside this distance...
        private const float KeepWithinSq = 7f * 7f;    // ...and keeps burning up to this one

        /// <summary>Explosion power handed to the explosion system.</summary>
        public float ExplosionRadius = 3f;

        private readonly FuseTimer fuse = new FuseTimer(FuseTicks);
        private bool lit;             // set by flint and steel; not saved
        private Bone head;
        private QuadLegs legs;

        /// <summary>Lights the fuse: the creeper stops and explodes regardless of any target.</summary>
        public void Ignite() { lit = true; }

        protected override void BuildModel()
        {
            rig = BuildRig("creeper", MobRigs.Creeper, "entity/creeper/creeper");
            head = rig.Find("head");
            legs = QuadLegs.Bind(rig);
        }

        protected override void Think()
        {
            if (lit)
            {
                fuse.Burning = true;
                StopMoving();
                return;
            }

            ValidateTarget(Def.FollowRange);
            if (!target.Valid) AcquirePlayer(Def.FollowRange);
            if (!target.Valid)
            {
                fuse.Burning = false;
                Wander(0.8f);
                IdleLook(8f);
                return;
            }

            CancelWander();
            LookAt(target.Eye);
            float distSq = (target.Feet - transform.position).sqrMagnitude;
            bool inFuseRange = fuse.Burning ? distSq <= KeepWithinSq : distSq < StartWithinSq;
            fuse.Burning = inFuseRange && CanSee(target);

            if (fuse.Burning)
            {
                StopMoving();
                FaceTowards(target.Feet);
            }
            else
            {
                overrideFacing = false;
                MoveTowards(target.Feet, 1f, true);
            }
        }

        protected override void TickTicks(float ticks)
        {
            if (lit) fuse.Burning = true;
            bool spent = fuse.Advance(ticks, out bool sparked);
            if (sparked) PlaySound("entity.creeper.primed", 1f, 0.5f);
            if (spent) Explode();
        }

        private void Explode()
        {
            if (Removed) return;
            Vector3 at = transform.position;
            // Leave the world before the blast so the creeper is not caught by its own explosion (no death path, no loot).
            Dead = true;
            Discard();
            ELog.Event("creeper", "creeper exploded at " + ELog.V(at) + (lit ? " (ignited with flint and steel)" : " (target in range)"), 0.5f);
            try { ExplosionSystem.Boom(at, ExplosionRadius, gameObject, EConfig.MobGriefing.Value, "Creeper"); }
            catch (Exception e) { ELog.Error("Creeper explode", e); }
        }

        protected override void Pose(PoseInput p)
        {
            PoseTerms.Look(head, p);
            legs.Trot(p, Mathf.PI); // front-right moves with hind-left
        }

        protected override void ApplyRenderScale()
        {
            if (scaleRoot != null) scaleRoot.localScale = SwellShape(fuse.Swell);
        }

        protected override float WhiteOverlay() => FlashWeight(fuse.Swell);

        /// <summary>Body scale for a swell of 0..1: it widens by up to 40 % and grows 10 % taller, with a fast shiver.</summary>
        private static Vector3 SwellShape(float swell)
        {
            float s4 = swell * swell * swell * swell;
            float shiver = 1f + 0.01f * swell * Mathf.Sin(100f * swell);
            float wide = (1f + 0.4f * s4) * shiver;
            return new Vector3(wide, (1f + 0.1f * s4) / shiver, wide);
        }

        /// <summary>
        /// White flash weight: off in even tenths of the swell, on in odd ones; when on it follows the swell (at least
        /// half), in 15 steps, scaled to at most 75 % white.
        /// </summary>
        private static float FlashWeight(float swell)
        {
            int tenth = (int)(swell * 10f);
            if ((tenth & 1) == 0) return 0f;
            float steps = Mathf.Floor(Mathf.Clamp(swell, 0.5f, 1f) * 15f);
            return steps / 15f * 0.75f;
        }

        protected override void DropLoot(List<ItemStack> drops, bool onFire, bool byPlayer)
        {
            AddDrop(drops, "minecraft:gunpowder", 0, 2);
        }
    }

    // ======================================================================== slime

    /// <summary>
    /// Hopping cube in sizes 1, 2 and 4: travels only by jumping (<see cref="Hopper"/>), squashes on landing and
    /// stretches on take-off (<see cref="SquashSpring"/>), hurts on contact (sizes above 1) and splits into smaller
    /// slimes when it dies.
    /// </summary>
    internal sealed class SlimeMob : McMob
    {
        private Hopper hopper;
        private SquashSpring squash;
        private float roamClock;          // ticks until a new random heading while nothing is hunted
        private float contactCooldown;

        /// <summary>
        /// Where the children of a split land, as corners of a square around the parent's feet; a split uses the first
        /// two to four entries. Scaled by an eighth of the parent's size, so the square is a quarter of it across.
        /// </summary>
        private static readonly Vector2[] SplitCorners =
        {
            new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(-1f, 1f), new Vector2(1f, 1f),
        };

        public int Size => Mathf.Clamp(Variant, 1, 8);

        private bool Small => Size <= 1;
        private float SpeedAttribute => 0.2f + 0.1f * Size;
        private float HopSpeed => 6f * SpeedAttribute;

        protected override float SizeScale => Size;
        protected override float MaxHealth => Size * Size;
        protected override float SoundVolume => 0.4f * Size;
        protected override string HurtSound => Small ? "entity.slime.hurt_small" : "entity.slime.hurt";
        protected override string DeathSound => Small ? "entity.slime.death_small" : "entity.slime.death";
        protected override float AirControlScale => 12f;   // it travels while airborne, so it steers hard in the air
        protected override bool FaceMovement => false;

        protected override void OnPreSetup()
        {
            if (Variant == 1 || Variant == 2 || Variant == 4) return;
            int pick = Rng.I(3);
            Variant = pick == 0 ? 1 : pick == 1 ? 2 : 4;
        }

        protected override void BuildModel()
        {
            rig = BuildRig("slime_inner", MobRigs.SlimeInner, "entity/slime/slime");
            var shellTex = EMat.Tex("entity/slime/slime");
            BuildFollowerRig("slime_outer", MobRigs.SlimeOuter, EMat.Unlit("entity/slime/slime_outer", shellTex, true), shellTex);
            hopper.Heading = bodyYaw;
            hopper.Rest(false);
        }

        protected override void Think()
        {
            ValidateTarget(Def.FollowRange);
            bool hunting = target.Valid || AcquirePlayer(Def.FollowRange);
            if (hunting)
            {
                Vector3 flat = Mc.Horizontal(target.Feet - transform.position);
                if (flat.sqrMagnitude > 1e-4f) hopper.Heading = Mc.YawOf(flat);
                LookAt(target.Eye);
            }
            else
            {
                roamClock -= 2f;
                if (roamClock <= 0f)
                {
                    roamClock = 40 + Rng.I(60);
                    hopper.Heading = Rng.F() * 360f;
                }
                ClearLook();
            }
            // The body always faces where the next leap goes.
            overrideFacing = true;
            overrideYaw = hopper.Heading;
        }

        protected override void TickTicks(float ticks)
        {
            if (contactCooldown > 0f) contactCooldown -= ticks;
            squash.Advance(ticks);
            Travel(ticks);
            TouchTarget();
        }

        // The squash spring is kicked by the body's own ground contact events (raised by the physics step).
        protected override void OnLeftGround()
        {
            if (!Dead) squash.Kick(1f);
        }

        /// <summary>Landing: a ring of puffs around the feet, the squish sound and a flattening kick.</summary>
        protected override void OnLanded(float blocksFallen)
        {
            if (Dead) return;
            int puffs = Mathf.Min(48, 8 * Size);
            float rim = Width * 0.5f;
            Vector3 feet = transform.position;
            for (int i = 0; i < puffs; i++)
            {
                Vector2 around = Rng.OnUnitCircle() * (rim * Rng.Range(0.5f, 1f));
                Particles.Poof(feet + new Vector3(around.x, 0.05f, around.y), new Vector3(0f, 0.02f, 0f));
            }
            PlaySound(Small ? "entity.slime.squish_small" : "entity.slime.squish", SoundVolume, Rng.Pitch() / 0.8f);
            squash.Kick(-0.5f);
        }

        /// <summary>Keeps sailing forward while airborne; on the ground waits for the hopper, then leaps.</summary>
        private void Travel(float ticks)
        {
            Vector3 forward = hopper.Forward;
            if (!grounded)
            {
                moveDir = forward;
                moveSpeed = HopSpeed;
                return;
            }

            StopMoving();
            if (!hopper.Rested(ticks)) return;
            hopper.Rest(target.Valid);
            Leap(forward);
        }

        private void Leap(Vector3 forward)
        {
            moveDir = forward;
            moveSpeed = HopSpeed;
            jumpRequested = true;
            if (Body != null && !Body.isKinematic)
            {
                Vector3 v = Body.velocity;
                v.x = forward.x * HopSpeed;
                v.z = forward.z * HopSpeed;
                Body.velocity = v;
            }
            PlaySound(Small ? "entity.slime.jump_small" : "entity.slime.jump", SoundVolume, Rng.Pitch() * 0.8f);
        }

        /// <summary>Contact damage (sizes above 1): touching, in reach and in sight, at most every 10 ticks.</summary>
        private void TouchTarget()
        {
            if (Size <= 1 || !target.Valid || contactCooldown > 0f) return;
            float touch = 0.6f * Size;
            bool touching = (Center - target.Center).sqrMagnitude < touch * touch + 0.5f
                            && WithinMeleeRange(target) && CanSee(target);
            if (!touching) return;
            contactCooldown = 10f;
            if (HitTarget(target, Size, 0.4f)) PlaySound("entity.slime.attack", 1f, Rng.Pitch());
        }

        protected override void Pose(PoseInput p) { }

        protected override void ApplyRenderScale()
        {
            if (scaleRoot == null) return;
            float stretch = 1f + squash.Value / (Size * 0.5f + 1f);   // bigger slimes deform less
            scaleRoot.localPosition = new Vector3(0f, 0.001f, 0f);   // keeps the bottom face off the ground
            scaleRoot.localScale = new Vector3(1f / stretch, stretch, 1f / stretch) * (Size * 0.999f);
        }

        /// <summary>A slime bigger than size 1 breaks into two to four slimes of half its size.</summary>
        protected override void OnRemovedAfterDeath()
        {
            if (Small) return;
            int litter = 2 + Rng.I(3);
            float cornerScale = Size / 8f;
            for (int k = 0; k < litter; k++)
            {
                Vector2 flat = SplitCorners[k] * cornerScale;
                SpawnSplitChild(transform.position + new Vector3(flat.x, 0.5f, flat.y));
            }
        }

        private void SpawnSplitChild(Vector3 feet)
        {
            var child = EntitiesModule.SpawnMobStatic(MobIds.Slime, feet, Rng.F() * 360f, Size / 2);
            if (child != null) child.NaturalSpawn = NaturalSpawn;
        }

        protected override void DropLoot(List<ItemStack> drops, bool onFire, bool byPlayer)
        {
            if (Size == 1) AddDrop(drops, "minecraft:slime_ball", 0, 2);
        }
    }

    // ======================================================================== iron golem

    /// <summary>
    /// Heavy neutral guardian: hunts hostile mobs (never creepers), flings what it hits upward, shows cracks as its
    /// health drops, rocks while walking and only fights the player after being hit by them.
    /// </summary>
    internal sealed class IronGolemMob : McMob
    {
        private const float SwingTicks = 10f;
        private const float CooldownTicks = 20f;
        private const float ScanInterval = 0.5f;
        private const float GaitPeriod = 13f;   // ticks of walk cycle per triangle-wave period

        private Bone head, rightArm, leftArm, rightLeg, leftLeg;
        private float swingLeft;
        private float cooldown;
        private float sinceScan;
        private bool provoked;

        private List<MeshRenderer> cracksLow, cracksMedium, cracksHigh;
        private int crackStage = -1;   // -1 = not evaluated yet

        protected override void BuildModel()
        {
            rig = BuildRig("iron_golem", MobRigs.IronGolem, "entity/iron_golem/iron_golem");
            head = rig.Find("head");
            rightArm = rig.Find("right_arm");
            leftArm = rig.Find("left_arm");
            rightLeg = rig.Find("right_leg");
            leftLeg = rig.Find("left_leg");
            cracksLow = rig.AddLayer(EMat.Lit("entity/iron_golem/iron_golem_crackiness_low", true, 1));
            cracksMedium = rig.AddLayer(EMat.Lit("entity/iron_golem/iron_golem_crackiness_medium", true, 1));
            cracksHigh = rig.AddLayer(EMat.Lit("entity/iron_golem/iron_golem_crackiness_high", true, 1));
            RefreshCracks();
        }

        /// <summary>Shows the crack layer that matches the remaining health; a clank plays when it gets worse.</summary>
        private void RefreshCracks()
        {
            float max = MaxHealth;
            float fraction = max > 0f ? Health / max : 1f;
            int stage = fraction >= 0.75f ? 0 : fraction >= 0.5f ? 1 : fraction >= 0.25f ? 2 : 3;
            if (stage == crackStage) return;
            bool worse = crackStage >= 0 && stage > crackStage;
            crackStage = stage;
            ShowLayer(cracksLow, stage == 1);
            ShowLayer(cracksMedium, stage == 2);
            ShowLayer(cracksHigh, stage == 3);
            if (worse) PlaySound("entity.iron_golem.damage", 1f, 1f);
        }

        private static void ShowLayer(List<MeshRenderer> layer, bool on)
        {
            if (layer == null) return;
            for (int i = 0; i < layer.Count; i++)
                if (layer[i] != null) layer[i].enabled = on;
        }

        protected override void Think()
        {
            RefreshCracks();
            sinceScan += 0.1f;

            if (provoked && !PlayerAvailable(Def.FollowRange)) provoked = false;
            ValidateTarget(Def.FollowRange);
            if (provoked) target = MobTarget.Player;
            else if (target.IsPlayer) target = MobTarget.None;   // never goes after the player unprovoked

            if (!target.Valid && sinceScan >= ScanInterval)
            {
                sinceScan = 0f;
                target = NearestHostile();
            }

            if (!target.Valid)
            {
                Wander(0.6f);
                IdleLook(6f);
                return;
            }

            CancelWander();
            LookAt(target.Eye);
            bool inReach = WithinMeleeRange(target, 0.7f);
            if (inReach)
            {
                StopMoving();
                FaceTowards(target.Feet);
            }
            else
            {
                overrideFacing = false;
                MoveTowards(target.Feet, 1f, true);
            }

            if (inReach && cooldown <= 0f && CanSee(target))
            {
                cooldown = CooldownTicks;
                swingLeft = SwingTicks;
                float damage = Def.AttackDamage / 2f + Rng.I((int)Def.AttackDamage);
                HitTarget(target, damage, 0.4f, 0.4f * Mc.BptToMs);
                PlaySound("entity.iron_golem.attack", 1f, 1f);
            }
        }

        private MobTarget NearestHostile()
        {
            McMob best = null;
            float bestSq = Def.FollowRange * Def.FollowRange;
            Vector3 feet = transform.position;
            foreach (var m in Mobs)
            {
                if (m == null || m == this || m.Dead || !m.IsHostile || m is CreeperMob) continue;
                float sq = (m.transform.position - feet).sqrMagnitude;
                if (sq >= bestSq) continue;
                if (!CanSee(MobTarget.Of(m))) continue;
                best = m;
                bestSq = sq;
            }
            return best != null ? MobTarget.Of(best) : MobTarget.None;
        }

        protected override void OnHurt(DamageKind kind, GameObject attacker)
        {
            if (IsPlayerObject(attacker))
            {
                provoked = true;
                target = MobTarget.Player;
            }
            else if (attacker != null)
            {
                var other = attacker.GetComponentInParent<McMob>();
                if (other != null && other != this && !other.Dead) target = MobTarget.Of(other);
            }
            RefreshCracks();
        }

        protected override void TickTicks(float ticks)
        {
            if (swingLeft > 0f) swingLeft -= ticks;
            if (cooldown > 0f) cooldown -= ticks;
        }

        protected override Quaternion ExtraBodyRotation()
        {
            if (stride.Amount < 0.01f) return Quaternion.identity;
            // Side-to-side rocking about the forward axis while walking.
            return Quaternion.AngleAxis(6.5f * Mc.TriangleWave(stride.Phase + 6f, GaitPeriod), Vector3.forward);
        }

        protected override void Pose(PoseInput p)
        {
            float wave = Mc.TriangleWave(p.WalkCycle, GaitPeriod);
            PoseTerms.Look(head, p);
            PoseTerms.Pitch(rightLeg, -1.5f * wave * p.WalkStrength);
            PoseTerms.Pitch(leftLeg, 1.5f * wave * p.WalkStrength);

            if (swingLeft > 0f)
            {
                // Both arms snap up together and come down over the swing.
                float raised = -2f + 1.5f * Mc.TriangleWave(swingLeft, 10f);
                PoseTerms.Pitch(rightArm, raised);
                PoseTerms.Pitch(leftArm, raised);
            }
            else
            {
                PoseTerms.Pitch(rightArm, (-0.2f + 1.5f * wave) * p.WalkStrength);
                PoseTerms.Pitch(leftArm, (-0.2f - 1.5f * wave) * p.WalkStrength);
            }
        }

        protected override void DropLoot(List<ItemStack> drops, bool onFire, bool byPlayer)
        {
            AddDrop(drops, "minecraft:iron_ingot", 3, 5);
        }

        public override void Load(JsonNode o)
        {
            base.Load(o);
            RefreshCracks();
        }
    }
}
