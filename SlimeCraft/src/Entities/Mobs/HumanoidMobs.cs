using System;
using System.Collections.Generic;
using SlimeCraft.Entities.Model;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Shared base of the zombie, skeleton and enderman: bone binding, melee helpers and a helper that puts an item
    /// mesh into the right hand. Their poses come from <see cref="BipedPose"/>.
    /// </summary>
    internal abstract class BipedMob : McMob
    {
        protected BipedBones bones = new BipedBones();
        /// <summary>Melee cooldown in ticks, counted down every frame.</summary>
        protected float hitCooldown;

        protected void BindBones() { bones = BipedBones.Bind(rig); }

        protected override void TickTicks(float ticks)
        {
            if (hitCooldown > 0f) hitCooldown -= ticks;
        }

        /// <summary>Melee step shared by the zombie and the angry enderman: hit when ready, in reach and visible.</summary>
        protected void TryMeleeHit()
        {
            if (hitCooldown > 0f || !target.Valid) return;
            if (!WithinMeleeRange(target) || !CanSee(target)) return;
            HitTarget(target, Def.AttackDamage);
            hitCooldown = 20f;
        }

        /// <summary>
        /// Walks to the target, or stops and turns to face it when already in reach. <paramref name="speedBonus"/>
        /// is added to the movement speed attribute while walking.
        /// </summary>
        protected void ChaseTarget(float speedBonus = 0f)
        {
            if (WithinMeleeRange(target))
            {
                StopMoving();
                FaceTowards(target.Feet);
                return;
            }
            overrideFacing = false;
            MoveTowards(target.Feet, 1f, true);
            if (speedBonus > 0f && moveSpeed > 0f) moveSpeed = Mc.WalkSpeed(Def.SpeedAttr + speedBonus);
        }

        /// <summary>
        /// Puts the shared item mesh of <paramref name="itemId"/> into the right hand. The placement was tuned by eye
        /// so the item lies along the arm; it follows every arm motion because it hangs under the arm bone.
        /// </summary>
        protected void AttachHandItem(string itemId)
        {
            try
            {
                var visuals = SC.ItemVisuals;
                if (visuals == null || bones.RightArm == null || bones.RightArm.Node == null) return;
                var mesh = visuals.GetItemMesh(itemId);
                var mat = visuals.GetItemMaterial(itemId);
                if (mesh == null || mat == null) return;

                var holder = new GameObject("held_" + itemId) { layer = ELayers.Mob };
                holder.transform.SetParent(bones.RightArm.Node, false);
                holder.transform.localPosition = new Vector3(0f, -0.62f, -0.06f);
                holder.transform.localRotation = Quaternion.AngleAxis(90f, Vector3.right)
                                                 * Quaternion.AngleAxis(90f, Vector3.up)
                                                 * Quaternion.AngleAxis(45f, Vector3.forward);
                holder.transform.localScale = Vector3.one * 0.85f;
                holder.AddComponent<MeshFilter>().sharedMesh = mesh;
                holder.AddComponent<MeshRenderer>().sharedMaterial = mat;
            }
            catch (Exception e) { ELog.Error("Held item " + itemId, e); }
        }
    }

    // ======================================================================== zombie

    /// <summary>Melee chaser that also goes after iron golems; arms stretched forward, higher while chasing.</summary>
    internal sealed class ZombieMob : BipedMob
    {
        private const float GolemSearchRange = 16f;

        private bool chasing;

        protected override void BuildModel()
        {
            rig = BuildRig("zombie", MobRigs.Zombie, "entity/zombie/zombie");
            BindBones();
        }

        protected override void Think()
        {
            CheckSunBurn();

            ValidateTarget(Def.FollowRange);
            if (!target.Valid && !AcquirePlayer(Def.FollowRange)) target = NearestVisibleGolem();

            chasing = target.Valid;
            if (!chasing)
            {
                Wander(1f);
                IdleLook(8f);
                return;
            }
            CancelWander();
            overrideFacing = false;
            LookAt(target.Eye);
            ChaseTarget();
            TryMeleeHit();
        }

        private MobTarget NearestVisibleGolem()
        {
            McMob best = null;
            float bestSq = GolemSearchRange * GolemSearchRange;
            var feet = transform.position;
            foreach (var m in Mobs)
            {
                if (!(m is IronGolemMob) || m.Dead || m.Removed) continue;
                float sq = (m.transform.position - feet).sqrMagnitude;
                if (sq > bestSq) continue;
                if (!CanSee(MobTarget.Of(m))) continue;
                best = m;
                bestSq = sq;
            }
            return best != null ? MobTarget.Of(best) : MobTarget.None;
        }

        protected override void OnHurt(DamageKind kind, GameObject attacker)
        {
            // Being hit by the player always provokes the zombie, even without line of sight.
            if (IsPlayerObject(attacker) && PlayerAvailable(Def.FollowRange)) target = MobTarget.Player;
        }

        protected override void Pose(PoseInput p) => BipedPose.Apply(bones, p, ArmStyle.Reaching, chasing);

        protected override void DropLoot(List<ItemStack> drops, bool onFire, bool byPlayer)
        {
            AddDrop(drops, "minecraft:rotten_flesh", 0, 2);
            if (!byPlayer || !Rng.Chance(0.025f)) return;
            switch (Rng.I(3))
            {
                case 0: AddDrop(drops, "minecraft:iron_ingot", 1, 1); break;
                case 1: AddDrop(drops, "minecraft:carrot", 1, 1); break;
                default: AddDrop(drops, onFire ? "minecraft:baked_potato" : "minecraft:potato", 1, 1); break;
            }
        }
    }

    // ======================================================================== skeleton

    /// <summary>Archer built on <see cref="KiteAndShoot"/>: keeps its distance, circles the target and shoots after a full draw.</summary>
    internal sealed class SkeletonMob : BipedMob
    {
        private static readonly KiteTuning Archery = new KiteTuning
        {
            Range = 15f,
            SettleTicks = 20f,
            DrawTicks = 20f,
            CooldownTicks = 40f,
            ForgetTicks = 60f,
            RethinkTicks = 20f,
            FlipChance = 0.3f,
            BackAwayBelow = 0.25f,
            StopBackingAbove = 0.75f,
            CircleSpeed = 0.7f,
        };

        private const float ArrowSpeedBpt = 1.6f;
        private const float AimSpread = 0.0172275f * 6f; // Normal difficulty

        private readonly KiteAndShoot archery = new KiteAndShoot(Archery);
        private ArmStyle arms = ArmStyle.CarryingItem;

        protected override void BuildModel()
        {
            rig = BuildRig("skeleton", MobRigs.Skeleton, "entity/skeleton/skeleton");
            BindBones();
            AttachHandItem("minecraft:bow");
            arms = ArmStyle.CarryingItem;
        }

        protected override void TickTicks(float ticks)
        {
            base.TickTicks(ticks);
            archery.Tick(ticks);
        }

        protected override void Think()
        {
            CheckSunBurn();
            ValidateTarget(Def.FollowRange);
            if (!target.Valid) AcquirePlayer(Def.FollowRange);

            if (!target.Valid)
            {
                archery.Forget();
                arms = ArmStyle.CarryingItem;
                overrideFacing = false;
                Wander(1f);
                IdleLook(8f);
                return;
            }

            arms = ArmStyle.AimingBow;
            CancelWander();
            LookAt(target.Eye);

            float distanceSq = (target.Feet - transform.position).sqrMagnitude;
            var plan = archery.Decide(CanSee(target), distanceSq, 2f);
            if (plan.Circle)
            {
                CircleTarget(plan.Ahead, plan.Aside);
            }
            else
            {
                MoveTowards(target.Feet, 1f, true);
                overrideFacing = false;
            }
            if (plan.Loose) LooseArrow();
        }

        /// <summary>Walks a mix of "towards the target" and "to its right" while keeping the body turned to it.</summary>
        private void CircleTarget(float ahead, float aside)
        {
            Vector3 toward = Mc.Horizontal(target.Feet - transform.position);
            if (toward.sqrMagnitude < 1e-6f) toward = transform.forward;
            toward.Normalize();
            Vector3 right = new Vector3(toward.z, 0f, -toward.x);
            moveDir = (toward * ahead + right * aside).normalized;
            moveSpeed = Mc.WalkSpeed(Def.SpeedAttr) * Archery.CircleSpeed;
            allowDrops = false;
            FaceTowards(target.Feet);
        }

        private void LooseArrow()
        {
            if (!target.Valid) return;
            Vector3 facing = Quaternion.Euler(0f, bodyYaw, 0f) * Vector3.forward;
            Vector3 muzzle = EyePos + facing * 0.3f + Vector3.down * 0.1f;
            Vector3 flight = target.Feet + Vector3.up * (target.Height / 3f) - muzzle;
            flight.y += 0.2f * Mc.Horizontal(flight).magnitude; // aim a little high to make up for the drop
            Vector3 heading = flight.normalized + Rng.Scatter(AimSpread);

            float damage = 2f + Rng.Triangle(0.22f, 0.57425f);
            EntitiesModule.ShootArrowStatic(muzzle, heading * (ArrowSpeedBpt * Mc.BptToMs), gameObject, damage);
            PlaySound("entity.skeleton.shoot", 1f, 1f / (Rng.F() * 0.4f + 0.8f));
        }

        protected override void OnHurt(DamageKind kind, GameObject attacker)
        {
            if (IsPlayerObject(attacker) && PlayerAvailable(Def.FollowRange * 2f)) target = MobTarget.Player;
        }

        protected override void Pose(PoseInput p) => BipedPose.Apply(bones, p, arms);

        protected override void DropLoot(List<ItemStack> drops, bool onFire, bool byPlayer)
        {
            AddDrop(drops, "minecraft:arrow", 0, 2);
            AddDrop(drops, "minecraft:bone", 0, 2);
        }
    }

    // ======================================================================== enderman

    /// <summary>
    /// Tall neutral mob with two moods. Calm: strolls, blinks away in daylight and turns hostile when the player
    /// stares at its eyes. Hostile: hunts the player, blinks closer when far behind, shakes and opens its jaw, and
    /// calms down after losing the player for ten seconds. Water, most damage and every arrow make it blink away.
    /// </summary>
    internal sealed class EndermanMob : BipedMob
    {
        private enum Mood { Calm, Hostile }

        private const float AwarenessRange = 64f;
        private const float StareTicksToAnger = 5f;
        private const float StareTolerance = 0.025f;      // allowed (1 - cos) of the look direction, times the distance
        private const float CalmDownSeconds = 10f;
        private const float BlinkCloserEvery = 30f;       // ticks
        private const float BlinkCloserBeyondSq = 16f * 16f;
        private const float SoakTicksPerHurt = 10f;
        private const float TargetEyeHeight = 1.62f;
        private const int TrailSpecks = 128;

        private Mood mood = Mood.Calm;
        private float stareTicks;
        private float calmSeconds;
        private float blinkCloserClock;
        private float soakTicks;

        private bool Hostile => mood == Mood.Hostile;

        protected override string AmbientSound => Hostile ? "entity.enderman.scream" : base.AmbientSound;

        protected override void BuildModel()
        {
            rig = BuildRig("enderman", MobRigs.Enderman, "entity/enderman/enderman");
            var eyes = EMat.Unlit("entity/enderman/enderman_eyes", EMat.Tex("entity/enderman/enderman_eyes"), true);
            if (eyes != null) rig.AddLayer(eyes, IsFaceBone);
            BindBones();
        }

        private static bool IsFaceBone(string boneName) => boneName == "head" || boneName == "hat";

        private void Provoke(bool byStare)
        {
            if (Hostile) return;
            mood = Mood.Hostile;
            target = MobTarget.Player;
            PlaySound("entity.enderman.scream", SoundVolume, VoicePitch);
            if (byStare) SC.Audio?.Play("entity.enderman.stare", Center, 2.5f, 1f);
        }

        protected override void Think()
        {
            Soak();
            if (Dead || Removed) return;
            if (Hostile) Hunt();
            else Idle();
        }

        /// <summary>Water hurts: 1 damage and a blink away for every 10 ticks spent in it.</summary>
        private void Soak()
        {
            if (!inWater) return;
            soakTicks += 2f;
            if (soakTicks < SoakTicksPerHurt) return;
            soakTicks -= SoakTicksPerHurt;
            Hurt(1f, DamageKind.Generic, null, Vector3.zero, 0f);
            BlinkAnywhere();
        }

        private void Idle()
        {
            Wander(1f);
            IdleLook(8f);

            bool watched = PlayerAvailable(AwarenessRange) && WatchedByPlayer();
            stareTicks = watched ? stareTicks + 2f : 0f;
            if (watched)
            {
                LookAt(SC.SR.EyePosition);
                if (stareTicks >= StareTicksToAnger) Provoke(true);
            }

            // Blinks around in daylight under open sky (about 8 % per decision step).
            if (!Hostile && SC.SR != null && !SC.SR.IsNight && SkyExposed() && Rng.F() * 30f < 2.4f) BlinkAnywhere();
        }

        private void Hunt()
        {
            CancelWander();
            if (!PlayerAvailable(AwarenessRange))
            {
                StopMoving();
                calmSeconds += 0.1f;
                if (calmSeconds <= CalmDownSeconds) return;
                mood = Mood.Calm;
                calmSeconds = 0f;
                target = MobTarget.None;
                return;
            }

            calmSeconds = 0f;
            target = MobTarget.Player;
            LookAt(target.Eye);

            blinkCloserClock += 2f;
            bool farBehind = (target.Feet - transform.position).sqrMagnitude > BlinkCloserBeyondSq;
            if (farBehind && blinkCloserClock >= BlinkCloserEvery && BlinkToward(target)) blinkCloserClock = 0f;

            ChaseTarget(0.15f);
            TryMeleeHit();
        }

        /// <summary>
        /// True when the player looks right at our eyes with nothing solid in between. The tolerated deviation
        /// shrinks with distance, so far away the look has to be almost exact.
        /// </summary>
        private bool WatchedByPlayer()
        {
            if (SC.SR == null) return false;
            Vector3 playerEye = SC.SR.EyePosition;
            Vector3 toUs = EyePos - playerEye;
            float dist = toUs.magnitude;
            if (dist < 0.01f) return false;
            float offAxis = 1f - Vector3.Dot(SC.SR.LookDirection.normalized, toUs / dist);
            if (offAxis * dist >= StareTolerance) return false;
            return !EPhys.WorldBlocked(playerEye, EyePos);
        }

        protected override void TickTicks(float ticks)
        {
            base.TickTicks(ticks);
            if (visRoot == null) return;
            visRoot.localPosition = Hostile ? new Vector3(Rng.Gaussian() * 0.02f, 0f, Rng.Gaussian() * 0.02f) : Vector3.zero;
        }

        protected override void OnHurt(DamageKind kind, GameObject attacker)
        {
            if (IsPlayerObject(attacker)) Provoke(false);
            bool meleeHit = kind == DamageKind.PlayerAttack || kind == DamageKind.MobAttack;
            if (!meleeHit && !Dead && Rng.I(10) != 0) BlinkAnywhere();
        }

        public override bool HurtByProjectile(float damage, GameObject shooter, Vector3 dir)
        {
            if (IsPlayerObject(shooter)) Provoke(false);
            for (int attempt = 0; attempt < 64; attempt++)
                if (BlinkAnywhere()) return false; // dodged: the arrow bounces off
            return base.HurtByProjectile(damage, shooter, dir);
        }

        // ------------------------------------------------------------------ blinking

        /// <summary>Somewhere within 32 blocks on each axis.</summary>
        private bool BlinkAnywhere()
        {
            var jump = new Vector3(Rng.Range(-32f, 32f), Rng.Range(-32, 31), Rng.Range(-32f, 32f));
            return BlinkTo(transform.position + jump);
        }

        /// <summary>16 blocks along the line towards the target's eyes, give or take a few.</summary>
        private bool BlinkToward(MobTarget t)
        {
            Vector3 here = transform.position, feet = t.Feet;
            Vector3 toward = new Vector3(feet.x - here.x,
                                         (feet.y + TargetEyeHeight) - (here.y + Height * 0.5f),
                                         feet.z - here.z).normalized;
            var jitter = new Vector3(Rng.Range(-4f, 4f), Rng.Range(-8, 7), Rng.Range(-4f, 4f));
            return BlinkTo(here + toward * 16f + jitter);
        }

        private bool BlinkTo(Vector3 wanted)
        {
            Vector3 from = transform.position;
            if (!TryTeleport(wanted)) return false;
            Vector3 to = transform.position;
            PortalTrail(from, to);
            SC.Audio?.Play("entity.enderman.teleport", from + Vector3.up, 1f, 1f);
            SC.Audio?.Play("entity.enderman.teleport", to + Vector3.up, 1f, 1f);
            CancelWander();
            return true;
        }

        /// <summary>Evenly spaced portal specks along the jump, scattered over the body's volume, drifting slowly.</summary>
        private void PortalTrail(Vector3 from, Vector3 to)
        {
            Vector3 step = (to - from) / (TrailSpecks - 1);
            Vector3 body = new Vector3(Width, Height, Width);
            Vector3 cursor = from;
            for (int i = 0; i < TrailSpecks; i++, cursor += step)
            {
                Vector3 scatter = Vector3.Scale(new Vector3(Rng.Range(-1f, 1f), Rng.F(), Rng.Range(-1f, 1f)), body);
                Vector3 drift = new Vector3(Rng.Range(-0.1f, 0.1f), Rng.Range(-0.1f, 0.1f), Rng.Range(-0.1f, 0.1f));
                Particles.Portal(cursor + scatter, drift);
            }
        }

        // ------------------------------------------------------------------ pose

        protected override void Pose(PoseInput p)
        {
            BipedPose.Apply(bones, p, ArmStyle.Hanging);
            BipedPose.Stiffen(bones);
            if (!Hostile) return;
            // Scream face: the head rises while the jaw layer stays where it was.
            if (bones.Head != null) bones.Head.Offset.y -= 5f;
            if (bones.Hat != null) bones.Hat.Offset.y += 5f;
        }

        protected override void DropLoot(List<ItemStack> drops, bool onFire, bool byPlayer)
        {
            AddDrop(drops, "minecraft:ender_pearl", 0, 1);
        }
    }
}
