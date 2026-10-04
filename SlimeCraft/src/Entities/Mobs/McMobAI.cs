using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>Shared mob behaviours: wandering, idle looking, panicking after a hit, and target selection.</summary>
    internal abstract partial class McMob
    {
        protected bool overrideFacing; protected float overrideYaw;

        // ---------------- wandering
        private bool hasWander; private Vector3 wanderPos; private float wanderGiveUp;

        /// <summary>Idle wandering: a 1/interval chance per tick to walk to a random spot within 10 blocks (7 vertical).</summary>
        protected void Wander(float speedModifier, int intervalTicks = 120, float range = 10f)
        {
            overrideFacing = false;
            if (!hasWander)
            {
                StopMoving();
                if (Rng.I(intervalTicks) < 2) // Think runs every 2 ticks
                {
                    if (FindRandomPos(range, 7f, out wanderPos)) { hasWander = true; wanderGiveUp = Time.time + 12f; }
                }
                return;
            }
            float d = MoveTowards(wanderPos, speedModifier);
            if (d < 0.8f || Time.time > wanderGiveUp || cliffAhead || stuckTimer >= 3f) { hasWander = false; StopMoving(); }
        }

        protected void CancelWander() { hasWander = false; }

        /// <summary>A random walkable ground position around the mob (up to 10 tries).</summary>
        protected bool FindRandomPos(float range, float vertical, out Vector3 pos)
        {
            pos = transform.position;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                var c = Rng.OnUnitCircle() * (Rng.F() * range);
                var probe = transform.position + new Vector3(c.x, vertical, c.y);
                if (!EPhys.RaycastWorld(probe, Vector3.down, vertical * 2f + 1f, out var hit)) continue;
                if (hit.normal.y < 0.6f) continue;
                if (Mathf.Abs(hit.point.y - transform.position.y) > vertical) continue;
                pos = hit.point;
                return true;
            }
            return false;
        }

        // ---------------- idle looking (at the player or around)
        private float lookUntil; private bool lookingAtPlayer; private Vector3 randomLook;

        protected void IdleLook(float lookDistance = 8f)
        {
            if (Time.time < lookUntil)
            {
                if (lookingAtPlayer && SC.SR.Player != null) LookAt(SC.SR.EyePosition);
                else LookAt(EyePos + randomLook);
                return;
            }
            ClearLook();
            // glance at a nearby player now and then (about 2 % per tick; Think runs every 2 ticks)
            if (Rng.Chance(0.04f) && (SC.SR.PlayerFeet - transform.position).sqrMagnitude < lookDistance * lookDistance)
            {
                lookingAtPlayer = true; lookUntil = Time.time + (40 + Rng.I(40)) / Mc.TPS;
            }
            else if (Rng.Chance(0.04f))
            {
                // otherwise look around in a random direction for a moment
                lookingAtPlayer = false;
                float a = Rng.F() * Mathf.PI * 2f;
                randomLook = Quaternion.Euler(0f, bodyYaw, 0f) * new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 3f;
                lookUntil = Time.time + (20 + Rng.I(20)) / Mc.TPS;
            }
        }

        // ---------------- panic
        protected float panicUntil;
        private Vector3 panicPos; private bool hasPanicPos;

        /// <summary>Panic: run to random positions within 5 blocks while recently hurt.</summary>
        protected bool Panic(float speedModifier)
        {
            if (Time.time >= panicUntil) { hasPanicPos = false; return false; }
            overrideFacing = false;
            ClearLook();
            if (!hasPanicPos || MoveTowards(panicPos, speedModifier) < 0.8f || stuckTimer > 1f)
            {
                if (FindRandomPos(5f, 4f, out panicPos)) hasPanicPos = true;
                // prefer running away from whoever hurt us
                if (lastAttacker != null)
                {
                    var away = Mc.Horizontal(transform.position - lastAttacker.transform.position).normalized;
                    var p2 = transform.position + away * 5f + new Vector3(Rng.Diff() * 2f, 0f, Rng.Diff() * 2f);
                    if (EPhys.RaycastWorld(p2 + Vector3.up * 4f, Vector3.down, 9f, out var hit) && hit.normal.y > 0.6f) { panicPos = hit.point; hasPanicPos = true; }
                }
                if (hasPanicPos) MoveTowards(panicPos, speedModifier);
            }
            return true;
        }

        // ---------------- targeting helpers

        /// <summary>Targets the player when in range and in sight.</summary>
        protected bool AcquirePlayer(float range)
        {
            if (!PlayerAvailable(range)) return false;
            if (!CanSee(MobTarget.Player)) return false;
            target = MobTarget.Player;
            return true;
        }

        /// <summary>Keeps a target while within range (follow range) - lost after 'range' or when the player goes creative.</summary>
        protected void ValidateTarget(float range)
        {
            if (!target.Valid) { target = MobTarget.None; return; }
            if (target.IsPlayer && !PlayerAvailable(range)) target = MobTarget.None;
            else if (!target.IsPlayer && (target.Mob.transform.position - transform.position).sqrMagnitude > range * range) target = MobTarget.None;
        }

        /// <summary>Turns the body to face a world position (used while strafing / attacking in place).</summary>
        protected void FaceTowards(Vector3 pos)
        {
            var d = Mc.Horizontal(pos - transform.position);
            if (d.sqrMagnitude < 1e-4f) return;
            overrideFacing = true;
            overrideYaw = Mc.YawOf(d);
        }

        /// <summary>Teleports onto the ground below/around <paramref name="wanted"/> if the spot is standable; returns success.</summary>
        protected bool TryTeleport(Vector3 wanted)
        {
            if (!EPhys.RaycastWorld(wanted + Vector3.up * 2f, Vector3.down, 40f, out var hit)) return false;
            if (hit.normal.y < 0.6f) return false;
            if (SC.SR != null && SC.SR.GetSRActorId(hit.collider.gameObject) != null) return false;
            var feet = hit.point + Vector3.up * 0.02f;
            // never into Minecraft blocks, water / slime sea, solid colliders or closed SR geometry (wanted may be inside a mountain)
            if (SpawnRules.CheckStand(feet, Width, Height, StandChecks.WorldOnly) != SpawnReject.None) return false;
            if (Body != null) { Body.position = feet; Body.velocity = Vector3.zero; }
            transform.position = feet;
            fallStartY = feet.y;
            return true;
        }
    }
}
