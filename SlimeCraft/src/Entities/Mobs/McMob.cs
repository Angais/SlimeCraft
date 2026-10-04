using System;
using System.Collections.Generic;
using SlimeCraft.Entities.Model;
using UnityEngine;

namespace SlimeCraft.Entities
{
    internal enum DamageKind { Generic, PlayerAttack, MobAttack, Projectile, Explosion, Fire, Fall, Void }

    /// <summary>A target a mob can chase/attack: the SR player or another Minecraft mob.</summary>
    internal struct MobTarget
    {
        public McMob Mob;
        public bool IsPlayer;
        public static MobTarget Player => new MobTarget { IsPlayer = true };
        public static MobTarget Of(McMob m) => new MobTarget { Mob = m };
        public static readonly MobTarget None = new MobTarget();
        public bool Valid => IsPlayer ? (SC.SR != null && SC.SR.InGame && SC.SR.Player != null) : (Mob != null && !Mob.Dead && !Mob.Removed);
        public Vector3 Feet => IsPlayer ? SC.SR.PlayerFeet : Mob.transform.position;
        public Vector3 Eye => IsPlayer ? SC.SR.EyePosition : Mob.EyePos;
        public Vector3 Center => IsPlayer ? SC.SR.PlayerFeet + Vector3.up * 0.9f : Mob.Center;
        public float Width => IsPlayer ? 0.6f : Mob.Width;
        public float Height => IsPlayer ? 1.8f : Mob.Height;
    }

    /// <summary>
    /// Base of all Minecraft mobs: Rigidbody movement tuned to feel like Minecraft's (gravity 0.08 b/t², drag 0.98,
    /// ground friction 0.546, jump 0.42 b/t), health/hurt/invulnerability/knockback/death (20 tick fall-over + poof),
    /// fire, water floating, ambient/step/hurt/death sounds, head look control, walk animation state, SR vacpack pull,
    /// LOD freezing. Subclasses provide the model, its per-frame animation and the AI.
    /// </summary>
    internal abstract partial class McMob : McEntity
    {
        public static readonly List<McMob> Mobs = new List<McMob>();

        public MobDef Def;
        public int Variant;
        public float Health;
        public bool Dead;
        public bool NaturalSpawn;
        public bool Frozen;
        private bool regionLoaded = true;
        private float nextRegionCheck;

        // ---------------- body / physics
        protected CapsuleCollider hitbox;
        protected bool grounded, wasGrounded;
        protected float fallStartY;
        protected bool inWater; protected float waterTopY;
        protected Vector3 moveDir;          // horizontal unit direction wanted by the AI
        protected float moveSpeed;          // m/s wanted by the AI
        protected bool jumpRequested;
        protected float jumpCooldown;
        protected float bodyYaw;
        protected bool cliffAhead, wallAhead;
        protected bool allowDrops;          // may walk off ledges (chasing)
        protected float stuckTimer;
        private Vector3 progressPos; private float progressTime;
        protected bool vacPulled;
        protected float maxDrop = 4f;

        // ---------------- look
        protected bool hasLook; protected Vector3 lookPos;
        protected float headYawRel, headPitch;   // degrees; pitch > 0 = looking down (Minecraft)
        protected float maxHeadYaw = 75f;

        // ---------------- animation / timers (Minecraft ticks)
        protected float ageTicks;
        protected Stride stride;
        protected float hurtTicks, invulTicks, deathTicks;
        protected float lastHurtAmount;
        protected float fireTicks;
        protected float swingProgress;       // 0..1 while a melee swing plays, 0 otherwise
        protected float swingClock = -1f;    // ticks into the current swing, -1 = no swing
        protected float ambientTime;
        protected float walkedSinceStep;
        private const float StrideBlocks = 1f / 0.6f; // one footstep sound per this many blocks walked
        private float thinkAcc;
        private float fireDamageAcc;
        private float envCheckAcc;
        private float lastX, lastZ;
        protected GameObject lastAttacker;
        protected bool killedByPlayer;

        // ---------------- visuals
        protected Transform visRoot, scaleRoot;
        protected RigInstance rig;
        /// <summary>Extra rigs that copy the main rig's pose (sheep wool, slime shell).</summary>
        protected readonly List<RigInstance> followers = new List<RigInstance>();
        protected GameObject fireVis;

        public float Width => Def.Width * SizeScale;
        public float Height => Def.Height * SizeScale;
        public float EyeHeight => Def.EyeHeight * SizeScale;
        public Vector3 EyePos => transform.position + Vector3.up * EyeHeight;
        public override float BBWidth => Width;
        public override float BBHeight => Height;
        protected virtual float SizeScale => 1f;
        public bool IsHostile => Def.Hostile;
        public bool OnFire => fireTicks > 0f;
        public float BodyYaw => bodyYaw;

        protected override void OnEnable() { base.OnEnable(); if (!Mobs.Contains(this)) Mobs.Add(this); }
        protected override void OnDestroy() { base.OnDestroy(); Mobs.Remove(this); }

        // ================================================================== setup

        public static McMob Create(MobDef def, Vector3 feet, float yaw, int variant)
        {
            var go = new GameObject("MC " + def.Id.Substring(def.Id.IndexOf(':') + 1));
            go.layer = ELayers.Mob;
            go.transform.position = feet;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            var mob = def.AddComponent(go);
            mob.Def = def;
            mob.bodyYaw = yaw;
            mob.Variant = variant;
            try { mob.Setup(); }
            catch
            {
                UnityEngine.Object.Destroy(go);
                throw;
            }
            return mob;
        }

        private void Setup()
        {
            OnPreSetup();
            Health = MaxHealth;
            ageTicks = Rng.F() * 1000f;
            ambientTime = -Def.AmbientInterval;
            SetupBody(Def.Mass * Mathf.Max(0.25f, SizeScale * SizeScale));
            hitbox = AddChildCollider<CapsuleCollider>("hitbox", ELayers.Mob);
            hitbox.sharedMaterial = Frictionless;
            ApplyHitboxSize();
            visRoot = new GameObject("vis").transform;
            visRoot.SetParent(transform, false);
            scaleRoot = new GameObject("scale").transform;
            scaleRoot.SetParent(visRoot, false);
            try { BuildModel(); }
            catch (Exception e) { ELog.Error("BuildModel " + Def.Id, e); }
            if (rig != null)
            {
                SetLayerRecursive(visRoot.gameObject, ELayers.Mob);
                rig.Rest();
                rig.Push();
                foreach (var f in followers)
                {
                    f.Follow(rig);
                    f.TakeLeaderPose();
                    f.Push();
                }
            }
            lastX = transform.position.x; lastZ = transform.position.z;
            progressPos = transform.position; progressTime = Time.time;
            OnSetup();
        }

        protected virtual float MaxHealth => Def.MaxHealth;
        protected virtual void OnPreSetup() { }
        protected virtual void OnSetup() { }

        protected void ApplyHitboxSize()
        {
            float w = Width, h = Height;
            hitbox.direction = 1;
            hitbox.radius = w * 0.5f;
            hitbox.height = Mathf.Max(h, w);
            hitbox.center = new Vector3(0f, Mathf.Max(h, w) * 0.5f, 0f);
        }

        protected static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            foreach (Transform t in go.transform) SetLayerRecursive(t.gameObject, layer);
        }

        /// <summary>Spawns the main rig with its lit material and the hurt/flash overlay.</summary>
        protected RigInstance BuildRig(string rigKey, Func<RigSpec> spec, string texturePath)
        {
            var baked = RigBaker.Get(rigKey, spec);
            var tex = EMat.Tex(texturePath);
            var instance = RigInstance.Spawn(baked, scaleRoot, EMat.Lit(texturePath, tex), ELayers.Mob);
            instance.EnableOverlay(tex);
            return instance;
        }

        /// <summary>A second rig (other geometry, same pose) such as sheep wool or the slime shell.</summary>
        protected RigInstance BuildFollowerRig(string rigKey, Func<RigSpec> spec, Material mat, Texture2D overlayTex)
        {
            var baked = RigBaker.Get(rigKey, spec);
            var instance = RigInstance.Spawn(baked, scaleRoot, mat, ELayers.Mob);
            if (overlayTex != null) instance.EnableOverlay(overlayTex);
            followers.Add(instance);
            return instance;
        }

        protected abstract void BuildModel();
        /// <summary>10 Hz AI decision step.</summary>
        protected abstract void Think();
        /// <summary>Poses the rig for this frame; every bone starts from its rest pose.</summary>
        protected abstract void Pose(PoseInput p);

        // ================================================================== frame loop

        private void Update()
        {
            if (Removed) return;
            try
            {
                float dt = Time.deltaTime;
                if (dt <= 0f || SC.SR == null || !SC.SR.InGame) return;
                float ticks = dt * Mc.TPS;

                // LOD freeze far away, or when SR unloaded the region we stand in (its terrain colliders are gone: we would fall through)
                float dist = Vector3.Distance(transform.position, SC.SR.PlayerFeet);
                bool freeze = dist > (EConfig.MobActiveDistance != null ? EConfig.MobActiveDistance.Value : 80f);
                if (!freeze && Time.time >= nextRegionCheck)
                {
                    nextRegionCheck = Time.time + 0.75f + Rng.F() * 0.5f;
                    regionLoaded = SRZones.RegionLoadedAt(transform.position + Vector3.up * 0.5f);
                }
                if (!regionLoaded) freeze = true;
                if (freeze != Frozen)
                {
                    Frozen = freeze;
                    if (Body != null) { Body.isKinematic = freeze; if (!freeze) Body.velocity = Vector3.zero; }
                }
                if (Frozen) return;

                ageTicks += ticks;
                if (hurtTicks > 0f) hurtTicks -= ticks;
                if (invulTicks > 0f) invulTicks -= ticks;
                if (jumpCooldown > 0f) jumpCooldown -= dt;
                if (swingClock >= 0f)
                {
                    swingClock += ticks;
                    swingProgress = swingClock / SwingDuration;
                    if (swingClock >= SwingDuration) { swingClock = -1f; swingProgress = 0f; }
                }

                if (Dead)
                {
                    deathTicks += ticks;
                    if (deathTicks >= 20f) { FinishDeath(); return; }
                }
                else
                {
                    TickFire(ticks);
                    TickSounds(ticks);
                    envCheckAcc += dt;
                    if (envCheckAcc >= 0.25f) { envCheckAcc = 0f; CheckEnvironment(); if (Removed) return; }
                    thinkAcc += dt;
                    if (thinkAcc >= 0.1f)
                    {
                        thinkAcc = 0f;
                        UpdateProbes();
                        if (target.Valid == false) target = MobTarget.None;
                        Think();
                        if (Removed || Dead) { if (Removed) return; }
                    }
                    TickTicks(ticks);
                }

                float blocksPerTick = !Dead && Body != null && !Body.isKinematic ? Mc.Horizontal(Body.velocity).magnitude / Mc.BptToMs : 0f;
                stride.Advance(blocksPerTick, ticks);
                UpdateLook(dt);
                UpdateVisuals(dt);
            }
            catch (Exception e) { ELog.Error(Def != null ? Def.Id + ".Update" : "Mob.Update", e); }
        }

        protected virtual float SwingDuration => 6f;
        /// <summary>Per-frame logic in Minecraft ticks (fractional).</summary>
        protected virtual void TickTicks(float ticks) { }

        protected MobTarget target;

        // ================================================================== physics

        private void FixedUpdate()
        {
            if (Removed || Body == null || Body.isKinematic) return;
            try
            {
                float dt = Time.fixedDeltaTime;
                var v = Body.velocity;
                wasGrounded = grounded;
                grounded = ProbeGround(Width * 0.5f, 0.08f, out var _);
                TrackGroundContact();

                Vector3 desired = Dead ? Vector3.zero : moveDir * moveSpeed;
                if (cliffAhead && !allowDrops) desired = Vector3.zero;
                var h = new Vector3(v.x, 0f, v.z);

                vacPulled = false;
                if (!Dead && SC.SR != null && SC.SR.InGame && SC.SR.VacActive && EConfig.VacpackPullsMobs.Value) vacPulled = VacPull(ref v, dt);

                if (!vacPulled)
                {
                    if (inWater)
                    {
                        // swimming: slow, water drag 0.8 per tick
                        var swim = desired * 0.45f;
                        h = swim + (h - swim) * Mc.PerTick(0.8f, dt);
                    }
                    else if (grounded)
                    {
                        float f = Mc.PerTick(0.546f, dt);
                        h = desired + (h - desired) * f;
                    }
                    else
                    {
                        h *= Mc.PerTick(0.91f, dt);
                        if (moveSpeed > 0f && !Dead)
                        {
                            float along = Vector3.Dot(h, moveDir);
                            if (along < moveSpeed) h += moveDir * (8f * Def.SpeedAttr * AirControlScale * dt);
                        }
                    }

                    if (inWater)
                    {
                        // water: drag 0.8, gravity 0.02, and mobs paddle upward (0.04 b/t) while submerged
                        float submerged = waterTopY - (transform.position.y + Height * 0.6f);
                        v.y -= 0.02f * Mc.Bpt2ToMs2 * dt;
                        if (submerged > 0f && FloatsInWater) v.y += 0.04f * Mc.Bpt2ToMs2 * 1.6f * dt;
                        v.y *= Mc.PerTick(0.8f, dt);
                    }
                    else
                    {
                        v.y -= Gravity * dt;
                        v.y *= Mc.PerTick(0.98f, dt);
                    }
                    ModifyFall(ref v);
                    v.x = h.x; v.z = h.z;

                    if (jumpRequested && !Dead && (grounded || inWater) && jumpCooldown <= 0f)
                    {
                        v.y = Mathf.Max(v.y, JumpVelocity);
                        jumpCooldown = 0.5f;
                        OnJump();
                    }
                }
                jumpRequested = false;
                Body.velocity = v;

                // body rotation: turn toward the walking direction (or follow a head turned far to the side)
                if (!Dead)
                {
                    float targetYaw = bodyYaw;
                    if (overrideFacing) targetYaw = overrideYaw;
                    else if (moveSpeed > 0.05f && moveDir.sqrMagnitude > 0.01f && FaceMovement) targetYaw = Mc.YawOf(moveDir);
                    else if (hasLook && Mathf.Abs(headYawRel) > maxHeadYaw * 0.8f) targetYaw = bodyYaw + headYawRel * 0.5f;
                    bodyYaw = Mc.RotLerp(bodyYaw, targetYaw, TurnSpeed * dt);
                    Body.MoveRotation(Quaternion.Euler(0f, bodyYaw, 0f));
                }
            }
            catch (Exception e) { ELog.Error(Def.Id + ".FixedUpdate", e); }
        }

        protected virtual float Gravity => 0.08f * Mc.Bpt2ToMs2;
        protected virtual float JumpVelocity => 0.42f * Mc.BptToMs;
        protected virtual float TurnSpeed => 540f;
        protected virtual bool FaceMovement => true;
        protected virtual bool FloatsInWater => true;
        protected virtual float AirControlScale => 1f;
        protected virtual void ModifyFall(ref Vector3 v) { }
        protected virtual void OnJump() { }
        /// <summary>Physics step in which the body stopped touching the ground (a jump, a ledge, a push).</summary>
        protected virtual void OnLeftGround() { }
        /// <summary>Physics step in which the body touched the ground again, with the height dropped since its highest point.</summary>
        protected virtual void OnLanded(float blocksFallen) { }

        /// <summary>
        /// Reacts to the ground contact of this physics step. In the air the highest point reached is remembered (water
        /// cushions: it restarts the measurement); on touchdown subclasses hear about it and fall damage applies: one
        /// point per block fallen beyond 3, rounded up. Contact edges are reported through <see cref="OnLeftGround"/>
        /// and <see cref="OnLanded"/>.
        /// </summary>
        private void TrackGroundContact()
        {
            float y = transform.position.y;
            if (!grounded)
            {
                if (wasGrounded) OnLeftGround();
                fallStartY = inWater ? y : Mathf.Max(fallStartY, y);
                return;
            }

            float dropped = fallStartY - y;
            fallStartY = y;
            if (wasGrounded) return;
            OnLanded(dropped);
            if (dropped > 3f && !Def.FallImmune && !Dead) Hurt(Mathf.Ceil(dropped - 3f), DamageKind.Fall, null, Vector3.zero, 0f);
        }

        /// <summary>SR vacpack suction: acceleration toward the nozzle scaled by 1/mass; light mobs fly in and hover at the nozzle.</summary>
        private bool VacPull(ref Vector3 v, float dt)
        {
            var ray = SC.SR.VacRay;
            var to = Center - ray.origin;
            float d = to.magnitude;
            if (d > 10f || d < 0.01f || Vector3.Angle(ray.direction, to) > 30f) return false;
            if (EPhys.WorldBlocked(ray.origin, Center)) return false;
            var dir = -to / d;
            float accel = 25f / Mathf.Max(0.1f, Body.mass);
            bool light = accel > Gravity;
            if (d < 1.6f)
            {
                if (!light) return false; // heavy mobs just get dragged along the ground
                v *= Mc.PerTick(0.5f, dt); // light mobs hover at the nozzle (too big to be vacuumed); no gravity
                return true;
            }
            v += dir * accel * dt;
            if (light)
            {
                // chickens & co. lift off and fly into the cone; mild sag + air drag
                v.y -= Gravity * 0.3f * dt;
                v *= Mc.PerTick(0.9f, dt);
            }
            else
            {
                v.y -= Gravity * dt;
                if (grounded) { var hh = Mc.Horizontal(v) * Mc.PerTick(0.8f, dt); v.x = hh.x; v.z = hh.z; }
            }
            return true;
        }

        /// <summary>Obstacle / cliff probes for the current move direction (step-up by jumping, no walking off &gt;4 block drops).</summary>
        private void UpdateProbes()
        {
            cliffAhead = false; wallAhead = false;
            if (moveSpeed <= 0.01f || moveDir.sqrMagnitude < 0.01f) { stuckTimer = 0f; progressPos = transform.position; progressTime = Time.time; return; }
            float r = Width * 0.5f;
            var feet = transform.position;
            int mask = ELayers.World;
            bool low = Physics.Raycast(feet + Vector3.up * 0.3f, moveDir, out var lh, r + 0.4f, mask, QueryTriggerInteraction.Ignore) && lh.normal.y < 0.7f;
            bool high = Physics.Raycast(feet + Vector3.up * Mathf.Min(1.3f, Height + 0.3f), moveDir, r + 0.5f, mask, QueryTriggerInteraction.Ignore);
            if (low && !high) jumpRequested = true;
            wallAhead = low && high;
            if (grounded)
            {
                var probe = feet + moveDir * (r + 0.6f) + Vector3.up * 0.5f;
                cliffAhead = !Physics.Raycast(probe, Vector3.down, maxDrop + 0.5f, mask | (1 << ELayers.Mob), QueryTriggerInteraction.Ignore);
                if (cliffAhead && inWater) cliffAhead = false;
            }
            // stuck detection: wanted to move for 1.5 s without real progress
            if (Time.time - progressTime > 1.5f)
            {
                float moved = Mc.Horizontal(transform.position - progressPos).magnitude;
                stuckTimer = moved < 0.5f ? stuckTimer + 1.5f : 0f;
                progressPos = transform.position; progressTime = Time.time;
            }
            if (inWater) jumpRequested = true;
        }

        // ================================================================== environment (water, slime sea, fire)

        private void CheckEnvironment()
        {
            inWater = false;
            int n = EPhys.Overlap(transform.position + Vector3.up * Mathf.Min(0.4f, Height * 0.5f), Mathf.Max(0.2f, Width * 0.4f), ~0, QueryTriggerInteraction.Collide, out var cols);
            for (int i = 0; i < n; i++)
            {
                var c = cols[i];
                if (c == null || !c.isTrigger) continue;
                if (c.GetComponent<KillOnTrigger>() != null) { KillVoid(); return; }
                var ls = c.GetComponent<LiquidSource>();
                if (ls != null && ls.waterTop != null) { inWater = true; waterTopY = ls.waterTop.position.y; }
            }
            if (inWater && fireTicks > 0f) fireTicks = 0f;
            var player = SC.SR.PlayerFeet;
            if (transform.position.y < player.y - 64f && transform.position.y < -50f) KillVoid();
        }

        protected void KillVoid()
        {
            if (Removed) return;
            ELog.Info(Def.Id + " fell out of the world / into the slime sea at " + transform.position);
            Discard();
        }

        protected override void OnSRKilled(DeathHandler.Source source) => KillVoid();

        private void TickFire(float ticks)
        {
            if (fireTicks <= 0f)
            {
                if (fireVis != null && fireVis.activeSelf) fireVis.SetActive(false);
                return;
            }
            fireTicks -= ticks;
            fireDamageAcc += ticks;
            if (fireDamageAcc >= 20f) { fireDamageAcc -= 20f; Hurt(1f, DamageKind.Fire, null, Vector3.zero, 0f); }
            if (fireVis == null) fireVis = FireFx.Create(transform, Width, Height);
            if (fireVis != null)
            {
                if (!fireVis.activeSelf) fireVis.SetActive(true);
                FireFx.FaceCamera(fireVis.transform);
            }
            if (Rng.Chance(0.02f * ticks)) Particles.Flame(transform.position + new Vector3(Rng.Diff() * Width, Rng.F() * Height, Rng.Diff() * Width), Vector3.zero);
        }

        public void SetOnFire(float seconds)
        {
            if (inWater) return;
            fireTicks = Mathf.Max(fireTicks, seconds * Mc.TPS);
        }

        /// <summary>Zombies/skeletons catch fire for 8 s in daylight under open sky (never in water).</summary>
        protected void CheckSunBurn()
        {
            if (!Def.BurnsInDaylight || Dead || inWater || SC.SR.IsNight || fireTicks > 20f) return;
            if (!SkyExposed()) return;
            // checked every 0.1 s (about 2 ticks): an 8 % chance per check in full daylight
            if (Rng.Chance(0.08f)) SetOnFire(8f);
        }

        private float skyCheckTime; private bool skyCache;
        protected bool SkyExposed()
        {
            if (Time.time - skyCheckTime < 1f) return skyCache;
            skyCheckTime = Time.time;
            skyCache = !EPhys.RaycastWorld(EyePos + Vector3.up * 0.2f, Vector3.up, 64f, out var _);
            return skyCache;
        }

        // ================================================================== sounds

        protected void PlaySound(string ev, float volume, float pitch) { if (ev != null) SC.Audio?.Play(ev, Center, volume, pitch); }
        protected virtual float VoicePitch => Rng.Pitch();
        protected virtual float SoundVolume => Def.SoundVolume;
        protected virtual string AmbientSound => Def.AmbientSound;
        protected virtual string HurtSound => Def.HurtSound;
        protected virtual string DeathSound => Def.DeathSound;

        private float ambientAcc;

        private void TickSounds(float ticks)
        {
            // Ambient voice: after a voice the mob stays quiet for AmbientInterval ticks; from then on the chance of
            // speaking grows by 0.1 % with every tick until it does.
            ambientAcc += ticks;
            while (ambientAcc >= 1f)
            {
                ambientAcc -= 1f;
                float chance = ambientTime / 1000f;
                ambientTime += 1f;
                if (chance <= 0f || Rng.F() >= chance) continue;
                ambientTime = -Def.AmbientInterval;
                if (AmbientSound != null) PlaySound(AmbientSound, SoundVolume, VoicePitch);
            }
            // footsteps while walking on the ground
            float dx = transform.position.x - lastX, dz = transform.position.z - lastZ;
            lastX = transform.position.x; lastZ = transform.position.z;
            if (grounded)
            {
                walkedSinceStep += Mathf.Sqrt(dx * dx + dz * dz);
                if (walkedSinceStep >= StrideBlocks) { walkedSinceStep = 0f; PlayStep(); }
            }
        }

        protected virtual void PlayStep()
        {
            if (Def.StepSound != null) { PlaySound(Def.StepSound, Def.StepVolume, 1f); return; }
            // default: the block's step sound (Minecraft blocks below, otherwise grass on SR terrain)
            string group = "grass";
            try
            {
                var id = SC.Blocks?.GetBlock(Vector3Int.FloorToInt(transform.position + Vector3.down * 0.2f));
                if (id != null) { var b = Content.Block(id); if (b != null) group = b.Sound; }
            }
            catch { }
            SC.Audio?.Play("block." + group + ".step", transform.position, 0.15f, 1f);
        }

        // ================================================================== damage

        /// <summary>
        /// Damages the mob the way Minecraft mobs take hits: a fresh hit tints the mob red for 10 ticks and opens a
        /// 20 tick protection window. While the first half of that window lasts, only a hit stronger than the one
        /// that opened it gets through, and only for the difference (no new knockback or hurt sound). Knockback
        /// pushes along 'pushDir'; the mob dies at 0 health. Returns true if any damage was applied.
        /// </summary>
        public virtual bool Hurt(float amount, DamageKind kind, GameObject attacker, Vector3 pushDir, float knockback)
        {
            if (Dead || Removed || amount <= 0f) return false;

            bool shielded = invulTicks > 10f;
            float applied = shielded ? amount - lastHurtAmount : amount;
            if (applied <= 0f) return false;
            lastHurtAmount = amount;
            bool freshHit = !shielded;
            if (freshHit)
            {
                invulTicks = 20f;
                hurtTicks = 10f;
            }

            if (!OnBeforeHurt(ref applied, kind, attacker)) return false;
            Health -= applied;
            if (attacker != null) lastAttacker = attacker;
            bool byPlayer = kind == DamageKind.PlayerAttack || (kind == DamageKind.Projectile && IsPlayerObject(attacker));
            if (byPlayer) killedByPlayer = true;
            if (freshHit && knockback > 0f && pushDir.sqrMagnitude > 1e-6f) Knockback(knockback, pushDir);
            if (Health <= 0f) Die(kind, attacker);
            else if (freshHit) PlaySound(HurtSound, SoundVolume, VoicePitch);
            OnHurt(kind, attacker);
            return true;
        }

        /// <summary>Arrow hit (enderman overrides to dodge). Returns false if the arrow should bounce off.</summary>
        public virtual bool HurtByProjectile(float damage, GameObject shooter, Vector3 dir)
            => Hurt(damage, DamageKind.Projectile, shooter, dir, 0.4f);

        protected virtual bool OnBeforeHurt(ref float amount, DamageKind kind, GameObject attacker) => true;
        protected virtual void OnHurt(DamageKind kind, GameObject attacker) { }

        public static bool IsPlayerObject(GameObject go)
        {
            if (go == null || SC.SR == null || SC.SR.Player == null) return false;
            return go == SC.SR.Player || go.transform.IsChildOf(SC.SR.Player.transform);
        }

        /// <summary>
        /// Minecraft-style knockback (power in blocks per tick): half of the current horizontal speed is kept and the
        /// push is added along 'pushDir'; a mob on the ground is also lifted, by at most 0.4 b/t. Knockback resistance
        /// scales the push down.
        /// </summary>
        public void Knockback(float power, Vector3 pushDir)
        {
            float push = power * (1f - Def.KnockbackResistance);
            if (push <= 0f || Body == null || Body.isKinematic) return;
            Vector3 dir = Mc.Horizontal(pushDir);
            if (dir.sqrMagnitude < 1e-6f) dir = new Vector3(Rng.Diff(), 0f, Rng.Diff());
            dir.Normalize();

            Vector3 current = Body.velocity / Mc.BptToMs;
            Vector3 result = Mc.Horizontal(current) * 0.5f + dir * push;
            result.y = grounded ? Mathf.Min(0.4f, current.y * 0.5f + push) : current.y;
            Body.velocity = result * Mc.BptToMs;
            grounded = false;
        }

        public void AddVelocity(Vector3 ms) { if (Body != null && !Body.isKinematic) Body.velocity += ms; }

        public override void OnExplosion(Vector3 knockbackMs, float damage, GameObject source)
        {
            if (damage > 0f) Hurt(damage, DamageKind.Explosion, source, Vector3.zero, 0f);
            AddVelocity(knockbackMs);
        }

        public void Kill() { if (!Dead) { Health = 0f; Die(DamageKind.Generic, null); } }

        protected virtual void Die(DamageKind kind, GameObject attacker)
        {
            if (Dead) return;
            Dead = true;
            Health = 0f;
            deathTicks = 0f;
            moveSpeed = 0f;
            PlaySound(DeathSound, SoundVolume, VoicePitch);
            var drops = new List<ItemStack>();
            try
            {
                DropLoot(drops, OnFire, killedByPlayer);
                foreach (var s in drops)
                    if (s != null && !s.IsEmpty)
                        EntitiesModule.SpawnItemStatic(s, transform.position + Vector3.up * 0.1f,
                            new Vector3(Rng.Range(-0.1f, 0.1f), 0.2f, Rng.Range(-0.1f, 0.1f)) * Mc.BptToMs, 0.5f);
            }
            catch (Exception e) { ELog.Error("DropLoot " + Def.Id, e); }
            ELog.Event("kill:" + Def.Id, "mob killed: " + Def.Id + (Variant > 0 && this is SlimeMob ? " (size " + Variant + ")" : "") + " at " + ELog.V(transform.position)
                + " by " + kind + (killedByPlayer ? " (player kill)" : "") + (OnFire ? " (on fire)" : "") + ", drops: " + ELog.Stacks(drops), 1f, 10);
            OnDied(kind, attacker);
        }

        protected virtual void OnDied(DamageKind kind, GameObject attacker) { }
        protected virtual void DropLoot(List<ItemStack> drops, bool onFire, bool byPlayer) { }

        protected static void AddDrop(List<ItemStack> drops, string id, int min, int max)
        {
            int n = Rng.Range(min, max);
            if (n > 0 && Content.Item(id) != null) drops.Add(new ItemStack(id, n));
        }

        private void FinishDeath()
        {
            Particles.DeathPoof(transform.position, Width, Height);
            OnRemovedAfterDeath();
            Discard();
        }

        protected virtual void OnRemovedAfterDeath() { }

        // ================================================================== melee helpers

        /// <summary>Melee reach: the attacker's box, grown horizontally by sqrt(2.04) - 0.6 blocks, must touch the target's box.</summary>
        protected bool WithinMeleeRange(MobTarget t, float extra = 0f)
        {
            float reach = Mathf.Sqrt(2.04f) - 0.6f + extra;
            var a = transform.position; var b = t.Feet;
            float hx = Mathf.Abs(a.x - b.x) - (Width + t.Width) * 0.5f;
            float hz = Mathf.Abs(a.z - b.z) - (Width + t.Width) * 0.5f;
            bool vert = b.y < a.y + Height && b.y + t.Height > a.y;
            return hx <= reach && hz <= reach && vert;
        }

        /// <summary>Applies a Minecraft melee hit to the target (player: SR damage * PlayerDamageScale + knockback).</summary>
        protected bool HitTarget(MobTarget t, float damage, float knockback = 0.4f, float extraUp = 0f)
        {
            if (!t.Valid) return false;
            swingClock = 0f;
            var push = Mc.Horizontal(t.Feet - transform.position).normalized;
            if (t.IsPlayer)
            {
                if (SC.Inventory != null && SC.Inventory.Creative) return false;
                int dmg = Mathf.Max(1, Mathf.RoundToInt(damage * EConfig.PlayerDamageScale.Value));
                SC.SR.DamagePlayer(dmg, gameObject);
                var kb = push * knockback * Mc.BptToMs + Vector3.up * (Mathf.Min(0.4f, knockback) * Mc.BptToMs * 0.5f + extraUp);
                SC.SR.AddPlayerVelocity(kb * EConfig.PlayerKnockbackScale.Value);
                SC.Audio?.Play("entity.player.hurt", SC.SR.EyePosition, 1f, Rng.Pitch());
                return true;
            }
            bool ok = t.Mob.Hurt(damage, DamageKind.MobAttack, gameObject, push, knockback);
            if (ok && extraUp > 0f) t.Mob.AddVelocity(Vector3.up * extraUp);
            return ok;
        }

        protected bool CanSee(MobTarget t) => t.Valid && !EPhys.WorldBlocked(EyePos, t.Eye);

        protected bool PlayerAvailable(float range)
        {
            if (SC.SR == null || !SC.SR.InGame || SC.SR.Player == null) return false;
            if (SC.Inventory != null && SC.Inventory.Creative) return false;
            return (SC.SR.PlayerFeet - transform.position).sqrMagnitude < range * range;
        }

        /// <summary>Steers toward a position at a Minecraft speed modifier (returns horizontal distance).</summary>
        protected float MoveTowards(Vector3 pos, float speedModifier, bool mayDrop = false)
        {
            var d = Mc.Horizontal(pos - transform.position);
            float dist = d.magnitude;
            allowDrops = mayDrop;
            if (dist < 0.3f) { StopMoving(); return dist; }
            moveDir = d / dist;
            moveSpeed = Mc.WalkSpeed(Def.SpeedAttr, speedModifier);
            // simple obstacle avoidance when stuck against a wall: slide sideways for a moment
            if (stuckTimer > 0f && Time.time < sidestepUntil) moveDir = sidestepDir;
            else if (stuckTimer >= 1.5f)
            {
                sidestepDir = Quaternion.Euler(0f, Rng.Chance(0.5f) ? 90f : -90f, 0f) * moveDir;
                sidestepUntil = Time.time + 1f;
                stuckTimer = 0f;
                jumpRequested = true;
            }
            return dist;
        }
        private Vector3 sidestepDir; private float sidestepUntil;

        protected void StopMoving() { moveSpeed = 0f; moveDir = Vector3.zero; }

        // ================================================================== look control

        protected void LookAt(Vector3 pos) { hasLook = true; lookPos = pos; }
        protected void ClearLook() { hasLook = false; }

        private void UpdateLook(float dt)
        {
            float wantYaw = 0f, wantPitch = 0f;
            if (hasLook && !Dead)
            {
                var d = lookPos - EyePos;
                float hd = Mc.Horizontal(d).magnitude;
                wantYaw = Mathf.Clamp(Mc.WrapDeg(Mc.YawOf(d) - bodyYaw), -maxHeadYaw, maxHeadYaw);
                wantPitch = Mathf.Clamp(-Mathf.Atan2(d.y, hd) * Mathf.Rad2Deg, -40f, 40f);
            }
            // the head turns gradually toward the look target
            headYawRel = Mathf.MoveTowards(headYawRel, wantYaw, 200f * dt);
            headPitch = Mathf.MoveTowards(headPitch, wantPitch, 400f * dt);
        }

        // ================================================================== animation / visuals

        /// <summary>Snapshot of the animation inputs for this frame.</summary>
        protected PoseInput CurrentPoseInput()
        {
            return new PoseInput
            {
                WalkCycle = stride.Phase,
                WalkStrength = stride.Amount,
                LookYaw = headYawRel * Mathf.Deg2Rad,
                LookPitch = headPitch * Mathf.Deg2Rad,
                Swing = swingProgress,
                Age = ageTicks,
            };
        }

        protected virtual void UpdateVisuals(float dt)
        {
            if (rig == null) return;
            rig.Rest();
            Pose(CurrentPoseInput());
            rig.Push();
            for (int i = 0; i < followers.Count; i++)
            {
                followers[i].TakeLeaderPose();
                followers[i].Push();
            }

            // a dying mob tips over sideways: fast at first, easing out, lying flat (90°) about 13.5 ticks in
            float rotZ = 0f;
            if (Dead)
            {
                float tipped = Mathf.Clamp01((deathTicks - 1f) * 0.08f);
                rotZ = 90f * Mathf.Sqrt(tipped);
            }
            visRoot.localRotation = Quaternion.AngleAxis(rotZ, Vector3.forward) * ExtraBodyRotation();
            ApplyRenderScale();

            // overlay: red while hurt or dying, otherwise subclass white flash
            if (hurtTicks > 0f || Dead) SetOverlay(new Color(1f, 0f, 0f), 0.3f);
            else
            {
                float w = WhiteOverlay();
                SetOverlay(Color.white, w);
            }
        }

        protected virtual Quaternion ExtraBodyRotation() => Quaternion.identity;
        protected virtual void ApplyRenderScale() { }
        /// <summary>Weight (0..1) of the white flash overlay, e.g. a creeper about to explode.</summary>
        protected virtual float WhiteOverlay() => 0f;

        private void SetOverlay(Color c, float w)
        {
            rig?.SetOverlay(c, w);
            for (int i = 0; i < followers.Count; i++) followers[i].SetOverlay(c, w);
        }

        // ================================================================== persistence

        public virtual void Save(JsonNode o)
        {
            o["id"] = JsonNode.Of(Def.Id);
            o["x"] = JsonNode.Of(transform.position.x); o["y"] = JsonNode.Of(transform.position.y); o["z"] = JsonNode.Of(transform.position.z);
            o["yaw"] = JsonNode.Of(bodyYaw);
            o["hp"] = JsonNode.Of(Health);
            o["v"] = JsonNode.Of(Variant);
        }

        public virtual void Load(JsonNode o)
        {
            Health = Mathf.Clamp(o["hp"].AsFloat(MaxHealth), 1f, MaxHealth);
        }
    }
}
