using System;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Minecraft-style arrow: gravity 0.05 b/t², inertia 0.99 per tick, swept raycast collision every frame,
    /// damage = ceil(|v| (b/t) * baseDamage) (+ random(dmg/2+2) when critical), knockback 0.4 along the flight
    /// direction, sticks into terrain/blocks for 1200 ticks with a 7-tick wobble, player-shot arrows can be picked up.
    /// Model: two crossed 16x4 planes + a 5x5 back plate, textured with entity/projectiles/arrow.png from the jar.
    /// No Rigidbody: Minecraft arrows are pure raycast projectiles.
    /// </summary>
    internal sealed class FlyingArrow : McEntity
    {
        public Vector3 Velocity;          // m/s
        public GameObject Shooter;
        public bool ShotByPlayer;
        public float BaseDamage = 2f;
        public bool Critical;
        public bool InGround;
        private float groundTicks, lifeTicks, shake;
        private Transform vis;
        private Vector3 stuckDir;
        private float nextGroundCheck;
        private static Mesh arrowMesh;

        public override float BBWidth => 0.5f;
        public override float BBHeight => 0.5f;

        public static FlyingArrow Spawn(Vector3 from, Vector3 velocity, GameObject shooter, float baseDamage)
        {
            var go = new GameObject("MC Arrow");
            go.layer = ELayers.Item;
            go.transform.position = from;
            if (velocity.sqrMagnitude > 1e-6f) go.transform.rotation = Quaternion.LookRotation(velocity);
            var a = go.AddComponent<FlyingArrow>();
            a.Velocity = velocity;
            a.Shooter = shooter;
            var player = SC.SR?.Player;
            a.ShotByPlayer = shooter == null || (player != null && (shooter == player || shooter.transform.IsChildOf(player.transform)));
            a.BaseDamage = baseDamage > 0f ? baseDamage : 2f;
            // a fully drawn bow (power 1 -> 3 b/t) shoots a critical arrow
            a.Critical = a.ShotByPlayer && velocity.magnitude >= 2.9f * Mc.BptToMs;
            a.BuildVisual();
            return a;
        }

        private void BuildVisual()
        {
            if (arrowMesh == null)
            {
                // The arrow rig points its tip along +X; we want it along the flight direction, Unity +Z.
                // Swapping x and z does that (a reflection, so the triangle winding flips).
                var m = Matrix4x4.identity;
                m.m00 = 0; m.m02 = 1; m.m20 = 1; m.m22 = 0;
                arrowMesh = Model.RigBaker.BakeFlat("arrow", Model.MobRigs.Arrow(), m, true);
            }
            vis = new GameObject("vis").transform;
            vis.SetParent(transform, false);
            vis.gameObject.layer = gameObject.layer;
            vis.gameObject.AddComponent<MeshFilter>().sharedMesh = arrowMesh;
            var mr = vis.gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterial = EMat.Lit("entity/projectiles/arrow");
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
        }

        private void Update()
        {
            if (Removed) return;
            try
            {
                float dt = Time.deltaTime;
                if (dt <= 0f) return;
                float ticks = dt * Mc.TPS;
                if (InGround) UpdateInGround(dt, ticks);
                else Fly(dt, ticks);
                if (shake > 0f)
                {
                    shake -= ticks;
                    // decaying wobble: amplitude = remaining shake ticks (degrees), 3 rad of phase per tick
                    float left = Mathf.Max(0f, shake);
                    float wobbleDeg = left * Mathf.Sin(-3f * left);
                    vis.localRotation = Quaternion.AngleAxis(wobbleDeg, Vector3.right);
                }
                else vis.localRotation = Quaternion.identity;
            }
            catch (Exception e) { ELog.Error("Arrow.Update", e); }
        }

        private void Fly(float dt, float ticks)
        {
            lifeTicks += ticks;
            if (lifeTicks > 1200f) { Discard(); return; }
            var pos = transform.position;
            var step = Velocity * dt;
            float len = step.magnitude;
            if (len > 1e-5f && SweepHit(pos, step / len, len, out var hit))
            {
                OnHit(hit);
                if (Removed || InGround) return;
            }
            else transform.position = pos + step;
            // air drag first, then gravity (both per tick)
            Velocity *= Mc.PerTick(0.99f, dt);
            Velocity.y -= 0.05f * Mc.Bpt2ToMs2 * dt;
            if (Velocity.sqrMagnitude > 1e-4f) transform.rotation = Quaternion.LookRotation(Velocity);
            if (Critical) { critAcc += ticks; while (critAcc >= 0.25f) { critAcc -= 0.25f; Particles.Crit(transform.position - Velocity.normalized * Rng.F() * 0.4f, -Velocity / Mc.BptToMs * 0.1f); } }
        }
        private float critAcc;

        private bool SweepHit(Vector3 origin, Vector3 dir, float dist, out RaycastHit best)
        {
            best = default(RaycastHit);
            int mask = ELayers.Hittable;
            if (!ShotByPlayer) mask |= 1 << ELayers.SRPlayer;
            var hits = EPhys.HitBuffer;
            int n = Physics.RaycastNonAlloc(origin, dir, hits, dist, mask, QueryTriggerInteraction.Ignore);
            float bd = float.MaxValue; bool found = false;
            var shooterT = Shooter != null ? Shooter.transform : null;
            var playerT = SC.SR?.Player != null ? SC.SR.Player.transform : null;
            for (int i = 0; i < n; i++)
            {
                var h = hits[i];
                var c = h.collider;
                if (c == null || h.distance >= bd) continue;
                if (shooterT != null && c.transform.IsChildOf(shooterT) && lifeTicks < 10f) continue;
                if (ShotByPlayer && playerT != null && c.transform.IsChildOf(playerT)) continue;
                var ent = c.GetComponentInParent<McEntity>();
                if (ent != null && (!(ent is McMob) || ((McMob)ent).Dead)) continue;
                bd = h.distance; best = h; found = true;
            }
            return found;
        }

        private void OnHit(RaycastHit hit)
        {
            var c = hit.collider;
            var dir = Velocity.normalized;
            float speedBpt = Velocity.magnitude / Mc.BptToMs;
            int dmg = Mathf.CeilToInt(Mathf.Clamp(speedBpt * BaseDamage, 0f, 1e6f));
            if (Critical) dmg += Rng.I(dmg / 2 + 2);
            var horiz = Mc.Horizontal(dir).normalized;
            var playerT = SC.SR?.Player != null ? SC.SR.Player.transform : null;

            var mob = c.GetComponentInParent<McMob>();
            if (mob != null)
            {
                transform.position = hit.point;
                bool hurt = mob.HurtByProjectile(dmg, Shooter, horiz);
                SC.Audio?.Play("entity.arrow.hit", hit.point, 1f, 1.2f / (Rng.F() * 0.2f + 0.9f));
                if (hurt) { Discard(); return; }
                // deflected (enderman / invulnerable): bounce back like Minecraft
                Velocity *= -0.1f;
                transform.position = hit.point - dir * 0.1f;
                return;
            }
            if (playerT != null && c.transform.IsChildOf(playerT))
            {
                if (!ShotByPlayer && !(SC.Inventory != null && SC.Inventory.Creative))
                {
                    float scale = EConfig.PlayerDamageScale.Value;
                    SC.SR.DamagePlayer(Mathf.Max(1, Mathf.RoundToInt(dmg * scale)), Shooter != null ? Shooter : gameObject);
                    SC.SR.AddPlayerVelocity((horiz * 0.4f * Mc.BptToMs + Vector3.up * 3f) * EConfig.PlayerKnockbackScale.Value);
                    SC.Audio?.Play("entity.player.hurt", SC.SR.EyePosition, 1f, Rng.Pitch());
                }
                SC.Audio?.Play("entity.arrow.hit", hit.point, 1f, 1.2f / (Rng.F() * 0.2f + 0.9f));
                Discard();
                return;
            }
            string srId = SC.SR?.GetSRActorId(c.gameObject);
            if (srId != null)
            {
                try { SC.SR.HitSRActor(c.gameObject, dmg, horiz * 6f + Vector3.up * 2f + dir * 2f); } catch (Exception e) { ELog.Error("HitSRActor", e); }
                SC.Audio?.Play("entity.arrow.hit", hit.point, 1f, 1.2f / (Rng.F() * 0.2f + 0.9f));
                Discard();
                return;
            }
            // terrain / block: stick in it, pulled back 0.05 along the motion so the tip stays visible
            transform.position = hit.point - dir * 0.05f;
            transform.rotation = Quaternion.LookRotation(dir);
            stuckDir = dir;
            InGround = true;
            groundTicks = 0f;
            shake = 7f;
            Velocity = Vector3.zero;
            Critical = false;
            SC.Audio?.Play("entity.arrow.hit", hit.point, 1f, 1.2f / (Rng.F() * 0.2f + 0.9f));
        }

        private void UpdateInGround(float dt, float ticks)
        {
            groundTicks += ticks;
            if (groundTicks >= 1200f) { Discard(); return; }
            // the block we were stuck in vanished -> fall again
            if (Time.time >= nextGroundCheck)
            {
                nextGroundCheck = Time.time + 0.5f;
                if (!EPhys.RaycastWorld(transform.position - stuckDir * 0.05f, stuckDir, 0.4f, out var _))
                {
                    InGround = false;
                    Velocity = new Vector3(Rng.F() * 0.2f, Rng.F() * 0.2f, Rng.F() * 0.2f) * Mc.BptToMs * 0.1f;
                    lifeTicks = 0f;
                    return;
                }
            }
            TryPickup();
        }

        private float nextPickupTry;

        private void TryPickup()
        {
            if (!ShotByPlayer || SC.SR == null || !SC.SR.InGame || Time.time < nextPickupTry) return;
            var pb = SC.SR.PlayerBounds;
            pb.Expand(new Vector3(0.5f, 0.5f, 0.5f));
            if (!pb.Contains(transform.position)) return;
            bool creative = SC.Inventory != null && SC.Inventory.Creative;
            if (!creative)
            {
                if (SC.Inventory == null) return;
                int left = SC.Inventory.Give(new ItemStack("minecraft:arrow", 1));
                if (left > 0) { nextPickupTry = Time.time + 0.25f; return; }
                ELog.Event("pickup:minecraft:arrow", "item picked up: 1x minecraft:arrow (stuck arrow)", 0.5f);
            }
            SC.Audio?.Play("entity.item.pickup", transform.position, 0.2f, (Rng.Diff() * 0.7f + 1f) * 2f);
            Discard();
        }

        public override void OnExplosion(Vector3 knockbackMs, float damage, GameObject source)
        {
            if (!InGround) Velocity += knockbackMs;
        }
    }
}
