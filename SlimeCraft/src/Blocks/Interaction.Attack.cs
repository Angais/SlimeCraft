using System;
using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    /// <summary>
    /// Left mouse button with a Minecraft item (or an empty hand).
    ///
    /// The attack hand answers every cue it is told (see Interaction.cs) in two stages: <see cref="PlanAttack"/>
    /// reads the cue and the crosshair and picks one <see cref="AttackMove"/> without changing any state, then
    /// <see cref="Carry"/> performs that move. All the "what happens when" rules of the left button live in the planner;
    /// the effects live in the carrier and in the mining machine below.
    ///
    /// Melee: a strike's damage scales with how far the attack charge has recovered since it was last emptied
    /// (a mark on the hands' step clock). Swinging at nothing locks the button out for a while (a deadline on the
    /// same clock, survival only).
    ///
    /// Mining is a small state machine with a single entry point, <see cref="FeedMiner"/>, fed with three events:
    ///   Engage    - a press on a diggable target;
    ///   Work      - one step of holding the button on a diggable target;
    ///   Disengage - released, target lost, locked out, a hold-to-use started, controller stopped.
    /// The phase is never stored; <see cref="PhaseFor"/> reads it off the data whenever Work arrives:
    ///   Cooldown - recovery steps are left after a block gave way; Work spends one of them;
    ///   Digging  - the job matches this exact target and tool (survival); Work adds progress;
    ///   Idle     - anything else; Work engages the target (creative sweeps it instead).
    /// A job works on one of two surfaces: one of our voxel cells, or a spot on a Slime Rancher collider whose
    /// classification decides the Minecraft block it behaves like (SR geometry is never modified).
    /// </summary>
    internal sealed partial class InteractionController
    {
        // ------------------------------------------------------------------ tuning (durations in 20 Hz steps)
        private const float DefaultAttacksPerSecond = 4f;
        private const float ChargeGraceSteps = 0.5f;
        private const int PrimedChargeSteps = 100;      // charge age after a world (re)load: always full
        private const float FullStrengthAbove = 0.9f;
        private const float CriticalBonus = 1.5f;
        private const float CriticalFallSpeed = -0.1f;  // m/s, the player must be dropping faster than this
        private const float SRActorShove = 8f;          // m/s, only when the Entities module is missing
        private const int MissLockoutSteps = 10;
        private const int RecoverySteps = 5;            // steps sat out after a block gives way
        private const int CadenceSteps = 4;             // hit sound and arm swing rhythm while holding
        private const float SameSpotRadius = 0.6f;
        private const float HarvestBurstOffset = 0.25f;
        private const float HarvestBurstExtent = 0.6f;
        private const float TerrainCrackSize = 0.8f;
        private const float DigStartLogInterval = 0.25f;

        private enum DigSurface { None, Voxel, Terrain }
        private enum MinePhase { Idle, Digging, Cooldown }
        private enum MinerEvent { Engage, Work, Disengage }

        /// <summary>The one mining job: what is being worked, with which tool, and how far along it is.</summary>
        private sealed class MiningJob
        {
            public DigSurface On;
            public Vector3Int Cell;         // voxel jobs
            public int ColliderId;          // terrain jobs: collider, spot and its normal when the job began
            public Vector3 Anchor;
            public Vector3 AnchorNormal;
            public string Tool;
            public float Done;              // 0..1, the block gives way at 1
            public int Steps;               // steps worked so far
            public float BeganAt;

            public bool Open => On != DigSurface.None;
            public bool Finished => Done >= 1f;
            public int CrackStage => (int)(Done * 10f);
            /// <summary>Where the job's sounds come from: the cell centre, or the spot where a terrain job began.</summary>
            public Vector3 Site => On == DigSurface.Voxel ? BlockWorld.Center(Cell) : Anchor;

            /// <summary>
            /// Puts one more step of work into the job. Returns true when this step lands on the hit-sound beat:
            /// the very first step, then every <see cref="CadenceSteps"/>-th one after it.
            /// </summary>
            public bool Work(float rate)
            {
                int index = Steps;
                Steps = index + 1;
                Done += rate;
                return index % CadenceSteps == 0;
            }

            /// <summary>Closes a finished job and reports how long it ran.</summary>
            public void Close(out float seconds, out int steps)
            {
                seconds = Time.time - BeganAt;
                steps = Steps;
                Drop();
            }

            public void Begin(DigSurface on, string tool)
            {
                On = on;
                Tool = tool;
                Done = 0f;
                Steps = 0;
                BeganAt = Time.time;
            }

            public void Drop()
            {
                On = DigSurface.None;
                Tool = null;
                Done = 0f;
                Steps = 0;
            }
        }

        private readonly MiningJob job = new MiningJob();
        /// <summary>Work events still to sit out after a block gave way.</summary>
        private int recovery;
        /// <summary>Position in the swing rhythm while the arm is busy (a swing whenever it is 0).</summary>
        private int cadence;
        /// <summary>The button is down on a diggable target: FirstPerson gets a mining progress of at least 0.</summary>
        private bool miningEngaged;
        /// <summary>The latest classification of the Slime Rancher surface under the crosshair.</summary>
        private SurfaceKind lastSurface;
        /// <summary>Clock step at which the attack charge was last emptied.</summary>
        private long chargeEmptiedAt;
        /// <summary>Clock step until which the left button stays locked out after swinging at nothing.</summary>
        private long lockoutUntil;

        // ------------------------------------------------------------------ small helpers
        private static bool OnGround => SC.SR == null || SC.SR.PlayerGrounded;
        private static string HeldName => SelectedId ?? "hand";
        private static bool HoldingSword
        {
            get { var d = SelectedDef; return d != null && d.Tool == ToolType.Sword; }
        }
        private static bool TerrainHarvestOn => BlocksConfig.HarvestSRTerrain == null || BlocksConfig.HarvestSRTerrain.Value;

        private static void WearHeldTool(int amount)
        {
            if (amount > 0 && !Creative) Inv?.DamageSelected(amount);
        }

        private bool LockedOut => !clock.Reached(lockoutUntil);
        private void EmptyCharge() => chargeEmptiedAt = clock.Now;
        private void PrimeCharge() => chargeEmptiedAt = clock.Now - PrimedChargeSteps;

        /// <summary>0..1: how far the attack has recharged, given the held item's attacks per second.</summary>
        private float AttackCharge(ItemDef item)
        {
            float perSecond = item != null && item.AttackSpeed > 0f ? item.AttackSpeed : DefaultAttacksPerSecond;
            float stepsToFull = HandsClock.StepsPerSecond / perSecond;
            return Mathf.Clamp01((clock.Since(chargeEmptiedAt) + ChargeGraceSteps) / stepsToFull);
        }

        // ------------------------------------------------------------------ the attack hand: plan a move, then carry it out
        /// <summary>Everything the attack hand can do in answer to one cue.</summary>
        private enum AttackMove
        {
            Ignore,     // a click while the miss lockout runs: nothing happens at all, not even a swing
            Strike,     // a click on an entity
            Engage,     // a click on something to dig
            Whiff,      // a click on nothing
            Dig,        // held on something to dig
            Halt,       // held, but blocked or on nothing to dig
            LetGo,      // the button is up
        }

        /// <summary>One planned move and the surface it concerns (Engage and Dig only).</summary>
        private struct AttackPlan
        {
            public readonly AttackMove Move;
            public readonly DigSurface Surface;

            public AttackPlan(AttackMove move, DigSurface surface)
            {
                Move = move;
                Surface = surface;
            }

            public static AttackPlan Just(AttackMove move) => new AttackPlan(move, DigSurface.None);

            /// <summary>Every click that is not ignored swings the arm after it has acted.</summary>
            public bool SwingsAfter => Move == AttackMove.Strike || Move == AttackMove.Engage || Move == AttackMove.Whiff;
        }

        private void AttackHand(Cue cue) => Carry(PlanAttack(cue));

        /// <summary>Picks the move for a cue from the crosshair, the miss lockout and the use hand. Changes nothing.</summary>
        private AttackPlan PlanAttack(Cue cue)
        {
            switch (cue)
            {
                case Cue.Up:
                    return AttackPlan.Just(AttackMove.LetGo);

                case Cue.Click:
                    if (LockedOut) return AttackPlan.Just(AttackMove.Ignore);
                    if (kind == TargetKind.Entity) return AttackPlan.Just(AttackMove.Strike);
                    if (kind == TargetKind.Block) return new AttackPlan(AttackMove.Engage, DigSurface.Voxel);
                    // A click engages SR terrain even with harvesting switched off: the machine then finds nothing to
                    // dig, but it is still not a swing at thin air.
                    if (kind == TargetKind.World) return new AttackPlan(AttackMove.Engage, DigSurface.Terrain);
                    return AttackPlan.Just(AttackMove.Whiff);

                default:
                    // Held: a running hold-to-use or the miss lockout keeps the hand from digging.
                    if (holdMode != UseMode.None || LockedOut) return AttackPlan.Just(AttackMove.Halt);
                    if (kind == TargetKind.Block) return new AttackPlan(AttackMove.Dig, DigSurface.Voxel);
                    if (kind == TargetKind.World && TerrainHarvestOn) return new AttackPlan(AttackMove.Dig, DigSurface.Terrain);
                    return AttackPlan.Just(AttackMove.Halt);
            }
        }

        /// <summary>Performs a planned move.</summary>
        private void Carry(AttackPlan plan)
        {
            switch (plan.Move)
            {
                case AttackMove.Strike:
                    Strike(entityHit.Target);
                    break;

                case AttackMove.Engage:
                    miningEngaged = true;
                    FeedMiner(MinerEvent.Engage, plan.Surface);
                    break;

                case AttackMove.Whiff:
                    if (!Creative) lockoutUntil = clock.After(MissLockoutSteps);
                    EmptyCharge();
                    break;

                case AttackMove.Dig:
                    miningEngaged = true;
                    if (FeedMiner(MinerEvent.Work, plan.Surface)) ArmAtWork(plan.Surface);
                    break;

                case AttackMove.Halt:
                case AttackMove.LetGo:
                    miningEngaged = false;
                    FeedMiner(MinerEvent.Disengage, DigSurface.None);
                    // Letting go of the button forgives a miss at once.
                    if (plan.Move == AttackMove.LetGo) lockoutUntil = clock.Now;
                    break;
            }

            if (plan.SwingsAfter) Swing();
        }

        /// <summary>A busy dig step: a chip flies off the worked face, and the arm swings on the first beat of each cadence.</summary>
        private void ArmAtWork(DigSurface surface)
        {
            ShedChip(surface);
            if (cadence == 0) Swing();
            cadence = (cadence + 1) % CadenceSteps;
        }

        // ------------------------------------------------------------------ melee
        /// <summary>The numbers of one melee strike.</summary>
        private struct StrikeRoll
        {
            public float Charge;
            public float Damage;
            public bool Strong;
            public bool Critical;

            public string Sound => Critical ? "entity.player.attack.crit"
                                 : Strong ? "entity.player.attack.strong"
                                 : "entity.player.attack.weak";
        }

        /// <summary>Damage = base x (0.2 + 0.8 x charge^2), x1.5 for a full-strength strike while falling.</summary>
        private StrikeRoll RollStrike(ItemDef item, ISRBridge sr)
        {
            var roll = new StrikeRoll { Charge = AttackCharge(item) };
            float baseDamage = item != null ? Mathf.Max(0f, item.AttackDamage) : 1f;
            roll.Strong = roll.Charge > FullStrengthAbove;
            roll.Critical = roll.Strong && !sr.PlayerGrounded && sr.PlayerVelocity.y < CriticalFallSpeed;
            roll.Damage = baseDamage * (0.2f + 0.8f * roll.Charge * roll.Charge);
            if (roll.Critical) roll.Damage *= CriticalBonus;
            return roll;
        }

        private static Vector3 FlatLook(ISRBridge sr)
        {
            Vector3 flat = sr.LookDirection;
            flat.y = 0f;
            return flat.sqrMagnitude > 1e-4f ? flat.normalized : Vector3.forward;
        }

        private void Strike(GameObject target)
        {
            var sr = SR;
            if (target == null || sr == null) return;

            var item = SelectedDef;
            StrikeRoll roll = RollStrike(item, sr);
            Vector3 push = FlatLook(sr);
            string targetName = RateLog.Actions ? target.name : null; // read before the hit may destroy it

            try
            {
                var entities = SC.Entities;
                if (entities != null) entities.AttackEntity(target, roll.Damage, push);
                else sr.HitSRActor(target, roll.Damage, push * SRActorShove);
            }
            catch (Exception e) { RateLog.Error("AttackEntity", e); }

            SC.Audio?.Play(roll.Sound, sr.EyePosition);

            if (RateLog.Actions)
                RateLog.Action("attack", "attacked '" + targetName + "' with " + HeldName + ": damage " + RateLog.F(roll.Damage)
                    + " (strength " + RateLog.F(roll.Charge) + (roll.Critical ? ", critical" : "") + ")");

            WearHeldTool(Mining.AttackDamageCost(item));
            EmptyCharge();
        }

        // ------------------------------------------------------------------ the mining machine
        /// <summary>The machine's only entry point. Returns whether the arm is busy this step (chips and swings follow).</summary>
        private bool FeedMiner(MinerEvent ev, DigSurface surface)
        {
            switch (ev)
            {
                case MinerEvent.Engage:
                    return Engage(surface);
                case MinerEvent.Work:
                    // Moving between a voxel cell and SR terrain voids the job without touching the charge.
                    if (job.Open && job.On != surface) job.Drop();
                    switch (PhaseFor(surface))
                    {
                        case MinePhase.Cooldown:
                            recovery--;
                            return true;
                        case MinePhase.Digging:
                            return Advance(surface);
                        default:
                            return Creative ? CreativeSweep(surface) : Engage(surface);
                    }
                default:
                    // Abandoning an unfinished job drains the attack charge; recovery and lockout are left alone.
                    if (job.Open) EmptyCharge();
                    job.Drop();
                    cadence = 0;
                    return false;
            }
        }

        private MinePhase PhaseFor(DigSurface surface)
        {
            if (recovery > 0) return MinePhase.Cooldown;
            if (!Creative && JobMatches(surface)) return MinePhase.Digging;
            return MinePhase.Idle;
        }

        /// <summary>Same surface, same spot, same tool: anything else makes a job start over.</summary>
        private bool JobMatches(DigSurface surface)
        {
            if (job.On != surface || job.Tool != SelectedId) return false;
            if (surface == DigSurface.Voxel) return job.Cell == blockHit.Pos;

            if (lastSurface == null || !lastSurface.Harvestable) return false;
            var col = worldHit.collider;
            if (col == null || col.GetInstanceID() != job.ColliderId) return false;
            return (worldHit.point - job.Anchor).sqrMagnitude <= SameSpotRadius * SameSpotRadius;
        }

        /// <summary>Is there anything to dig at all? (A voxel needs a block, terrain needs harvesting on and a collider.)</summary>
        private bool SurfacePresent(DigSurface surface) =>
            surface == DigSurface.Voxel
                ? world.GetDef(blockHit.Pos) != null
                : TerrainHarvestOn && worldHit.collider != null;

        /// <summary>
        /// The block the target behaves like, or null when it yields nothing. For terrain this refreshes
        /// <see cref="lastSurface"/>.
        /// </summary>
        private BlockDef MaterialOf(DigSurface surface)
        {
            if (surface == DigSurface.Voxel) return world.GetDef(blockHit.Pos);
            lastSurface = TerrainHarvest.Classify(worldHit, world);
            return lastSurface.Harvestable ? lastSurface.ActsLike : null;
        }

        /// <summary>
        /// First contact with a target (a press, or a held button arriving on a new target). Returns false only when
        /// there is nothing to dig; terrain that yields nothing still keeps the arm busy.
        /// </summary>
        private bool Engage(DigSurface surface)
        {
            if (!SurfacePresent(surface)) return false;

            // Terrain is re-identified on every engagement, so it always starts from zero; a voxel engagement only
            // clears a terrain job (a job on another cell survives until a new job replaces it).
            if (surface == DigSurface.Terrain || job.On == DigSurface.Terrain) job.Drop();

            BlockDef material = MaterialOf(surface);
            if (material == null) return true;

            if (Creative)
            {
                // Creative breaks at once, except that swords never break blocks.
                if (!HoldingSword) GiveWay(surface, material, false, 0f, 0);
                recovery = RecoverySteps;
                return true;
            }

            // Pressing again on the block being mined keeps its progress.
            if (JobMatches(surface)) return true;

            float rate = Mining.ProgressPerTick(SelectedDef, material, OnGround);
            if (rate >= 1f)
            {
                // Fast enough to give way in one step: no recovery afterwards.
                GiveWay(surface, material, Mining.CanHarvest(SelectedDef, material), 0f, 0);
                return true;
            }

            job.Begin(surface, SelectedId);
            if (surface == DigSurface.Voxel)
            {
                job.Cell = blockHit.Pos;
            }
            else
            {
                job.ColliderId = worldHit.collider.GetInstanceID();
                job.Anchor = worldHit.point;
                job.AnchorNormal = worldHit.normal;
            }
            if (RateLog.Actions) LogJobStart(material, DescribeJobSite(), rate);
            return true;
        }

        private string DescribeJobSite() =>
            job.On == DigSurface.Voxel
                ? RateLog.P(job.Cell)
                : "SR terrain '" + lastSurface.Name + "' " + RateLog.P(job.Anchor);

        /// <summary>One step of survival digging on the matched job.</summary>
        private bool Advance(DigSurface surface)
        {
            BlockDef material = surface == DigSurface.Voxel ? world.GetDef(job.Cell) : lastSurface.ActsLike;
            if (material == null)
            {
                // The block vanished under us (explosion, another edit).
                job.Drop();
                return false;
            }

            if (job.Work(Mining.ProgressPerTick(SelectedDef, material, OnGround))) BlockSounds.Hit(material, job.Site);
            if (job.Finished) CompleteJob(surface, material);
            return true;
        }

        /// <summary>The worked block gives way: loot is decided by the tool, then the recovery steps begin.</summary>
        private void CompleteJob(DigSurface surface, BlockDef material)
        {
            bool drops = Mining.CanHarvest(SelectedDef, material);
            job.Close(out float seconds, out int steps);
            GiveWay(surface, material, drops, seconds, steps);
            recovery = RecoverySteps;
        }

        /// <summary>Creative with the button held: whatever is there gives way, then the recovery steps pass.</summary>
        private bool CreativeSweep(DigSurface surface)
        {
            recovery = RecoverySteps;
            BlockDef material = MaterialOf(surface);
            if (material != null && !HoldingSword) GiveWay(surface, material, false, 0f, 0);
            return true;
        }

        /// <summary>A small chip flies off the worked face on every busy step.</summary>
        private void ShedChip(DigSurface surface)
        {
            if (surface == DigSurface.Voxel)
            {
                var def = world.GetDef(blockHit.Pos);
                if (def != null) particles.SpawnHit(def, blockHit.Pos, Dirs.Dominant(blockHit.Normal));
            }
            else if (lastSurface != null && lastSurface.Harvestable)
                particles.SpawnHitAt(lastSurface.ActsLike, worldHit.point, worldHit.normal);
        }

        // ------------------------------------------------------------------ giving way
        private void GiveWay(DigSurface surface, BlockDef material, bool drops, float seconds, int steps)
        {
            if (surface == DigSurface.Voxel) BreakVoxel(blockHit.Pos, material, drops, seconds, steps);
            else HarvestTerrain(drops, seconds, steps);
        }

        private void BreakVoxel(Vector3Int cell, BlockDef def, bool drops, float seconds, int steps)
        {
            bool creative = Creative;
            var item = SelectedDef;
            string held = HeldName; // captured before the tool can wear out

            world.BreakBlock(cell, drops && !creative);
            world.RebuildAround(cell);
            if (!creative && def.Hardness != 0f) WearHeldTool(Mining.MineDamage(item));

            if (!RateLog.Actions) return;
            string how = creative ? " (creative, instant)"
                       : steps == 0 ? " instantly (instamine)"
                       : " in " + RateLog.F(seconds) + " s (" + steps + " ticks)";
            string loot = creative ? "none (creative)" : drops ? "yes" : "none (needs the right tool)";
            RateLog.Action("break", "block broken: " + def.Id + " at " + RateLog.P(cell) + " with " + held + how + ", drops: " + loot);
        }

        /// <summary>
        /// A harvest completed at the current crosshair hit: break sound, debris, Minecraft drops. The SR geometry
        /// itself is left exactly as it was.
        /// </summary>
        private void HarvestTerrain(bool drops, float seconds, int steps)
        {
            SurfaceKind surface = lastSurface;
            if (surface == null || !surface.Harvestable) return;

            bool creative = Creative;
            var item = SelectedDef;
            string held = HeldName;
            var block = surface.ActsLike;
            Vector3 point = worldHit.point;
            Vector3 normal = worldHit.normal;

            BlockSounds.Break(block, point);
            try { particles.SpawnBreakAt(block, point + normal * HarvestBurstOffset, HarvestBurstExtent); }
            catch (Exception e) { RateLog.Error("harvest particles", e); }

            string loot;
            if (creative) loot = "none (creative)";
            else if (!drops) loot = "none (needs the right tool)";
            else
            {
                try { loot = TerrainHarvest.RollDrops(surface, item, point, normal); }
                catch (Exception e) { RateLog.Error("harvest drops", e); loot = "none (error)"; }
            }

            if (!creative && block.Hardness != 0f) WearHeldTool(Mining.MineDamage(item));

            if (!RateLog.Actions) return;
            string how = creative ? " (creative, instant)"
                       : steps == 0 ? " instantly"
                       : " in " + RateLog.F(seconds) + " s (" + steps + " ticks)";
            RateLog.Action("harvest", "terrain harvested: '" + surface.Name + "' (acts like " + block.Id + ") from "
                + TerrainHarvest.Describe(worldHit) + " with " + held + how + " -> drops: " + loot);
        }

        // ------------------------------------------------------------------ outputs: crack and FirstPerson progress
        private void PublishMiningProgress()
        {
            bool cracking = job.Open && job.Done > 0f;
            if (!cracking) crack.Hide();
            else if (job.On == DigSurface.Voxel) crack.ShowBlock(job.Cell, job.CrackStage);
            else crack.ShowDecal(job.Anchor, job.AnchorNormal, job.CrackStage, TerrainCrackSize);

            // IFirstPerson contract: >= 0 for the whole mining action (also at 0 progress), so mining swings never
            // reset the attack-strength dip.
            float progress = cracking ? Mathf.Clamp01(job.Done) : job.Open || miningEngaged ? 0f : -1f;
            try { if (SC.FirstPerson != null) SC.FirstPerson.MiningProgress = progress; }
            catch (Exception e) { RateLog.Error("FirstPerson.MiningProgress", e); }
        }

        private void LogJobStart(BlockDef def, string where, float rate)
        {
            string eta;
            if (rate > 0f)
            {
                int steps = Mathf.CeilToInt(1f / rate);
                eta = steps + " ticks (" + RateLog.F(steps * HandsClock.StepSeconds) + " s)";
            }
            else eta = "never (unbreakable)";

            var sr = SR;
            string msg = "mining " + def.Id + " at " + where + " with " + HeldName + ": " + RateLog.F(rate, "0.0000")
                + "/tick -> " + eta + ", correct tool for drops: " + (Mining.CanHarvest(SelectedDef, def) ? "yes" : "no")
                + (sr != null && !sr.PlayerGrounded ? ", airborne (x0.2 speed)" : "");
            RateLog.Action("mine", msg, DigStartLogInterval);
        }
    }
}
