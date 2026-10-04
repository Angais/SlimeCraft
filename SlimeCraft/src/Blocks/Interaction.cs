using System;
using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    internal enum TargetKind { None, Block, Entity, World }
    internal enum UseMode { None, Eat, Bow }

    /// <summary>
    /// The player's Minecraft hands: targeting (blocks / entities / SR world within reach), block outline,
    /// left button (melee with a recharging attack, mining, harvesting SR terrain; Interaction.Attack.cs),
    /// right button (place, eat, bow, flint &amp; steel, spawn eggs, crafting table; Interaction.Use.cs) and the drop key.
    /// Runs only while in game, gameplay input is allowed and the vacpack is NOT the selected hotbar item.
    ///
    /// Every frame runs one fixed pipeline:
    ///   gate -> held item -> aim -> clicks -> 20 Hz steps -> drop key -> mining visuals.
    /// The two hands never poll the mouse themselves: the controller tells each <see cref="Hand"/> a <see cref="Cue"/>
    /// (Click within the frame of the press; Down or Up on every <see cref="HandsClock"/> step), in the order given by
    /// <see cref="ClickOrder"/> and <see cref="StepOrder"/>. The clock's step count is the time base for the attack
    /// charge, the miss lockout and the right-button repeat, which are kept as marks and deadlines on it rather than as
    /// counters ticking down.
    /// </summary>
    internal sealed partial class InteractionController
    {
        private readonly BlockWorld world;
        private readonly BlockParticles particles;
        private readonly CrackOverlay crack;
        private readonly BlockOutline outline;
        private readonly HandsClock clock = new HandsClock();

        private bool active;

        // ---- what the crosshair is on (refreshed every frame by Aim)
        private TargetKind kind;
        private BlockHit blockHit;
        private EntityHit entityHit;
        private RaycastHit worldHit;

        // ---- the held item as last seen (switching to another item empties the attack charge)
        private string watchedItem;
        private int watchedSlot = -1;

        // ---- right-button repeat: the clock step from which a held button may act again
        private long useRepeatAt;

        public InteractionController(BlockWorld world, BlockParticles particles, CrackOverlay crack, BlockOutline outline)
        {
            this.world = world;
            this.particles = particles;
            this.crack = crack;
            this.outline = outline;
        }

        // ------------------------------------------------------------------ shared accessors
        private static ISRBridge SR => SC.SR;
        private static IPlayerInventory Inv => SC.Inventory;
        private static bool Creative => SC.Inventory != null && SC.Inventory.Creative;

        private static ItemStack SelectedStack
        {
            get
            {
                var inv = SC.Inventory;
                if (inv == null) return ItemStack.Empty;
                return inv.Selected ?? ItemStack.Empty;
            }
        }

        private static ItemDef SelectedDef
        {
            get { var s = SelectedStack; return s.IsEmpty ? null : s.Def; }
        }

        private static string SelectedId
        {
            get { var s = SelectedStack; return s.IsEmpty ? null : s.Id; }
        }

        private static void Swing()
        {
            try { SC.FirstPerson?.Swing(); } catch (Exception e) { RateLog.Error("FirstPerson.Swing", e); }
        }

        /// <summary>A use consumed the held item (or succeeded in creative): the held item pops back up from below.</summary>
        private static void ItemUsed()
        {
            try { SC.FirstPerson?.ItemUsed(); } catch (Exception e) { RateLog.Error("FirstPerson.ItemUsed", e); }
        }

        // ------------------------------------------------------------------ step clock and input sampling
        /// <summary>
        /// Fixed 20 Hz step source for the hands. Frame time is banked and paid out in whole steps, at most
        /// <see cref="MaxStepsPerFrame"/> per frame; a backlog larger than that is forgotten so a hitch never replays
        /// seconds of held input. <see cref="Now"/> is the number of steps run so far.
        /// </summary>
        private sealed class HandsClock
        {
            public const float StepSeconds = 0.05f;
            public const float StepsPerSecond = 20f;
            private const int MaxStepsPerFrame = 5;

            private float banked;

            public long Now { get; private set; }

            /// <summary>Banks this frame's time and returns how many steps to run now.</summary>
            public int Withdraw(float frameSeconds)
            {
                banked += frameSeconds;
                int steps = 0;
                while (steps < MaxStepsPerFrame && banked >= StepSeconds)
                {
                    banked -= StepSeconds;
                    steps++;
                }
                if (banked > StepSeconds * MaxStepsPerFrame) banked = 0f;
                return steps;
            }

            /// <summary>Called at the very start of each step, so marks and deadlines move before anything reads them.</summary>
            public void Advance() { Now++; }

            public void ForgetBacklog() { banked = 0f; }

            public long After(int steps) => Now + steps;

            public bool Reached(long deadline) => Now >= deadline;

            public long Since(long mark) => Now - mark;
        }

        // ------------------------------------------------------------------ hands and cues
        /// <summary>The two hands: the one on the attack button and the one on the use button.</summary>
        private enum Hand { Attack, Use }

        /// <summary>
        /// What a hand is told. <c>Click</c>: its button went down in this frame. On every clock step each hand is told
        /// either <c>Down</c> or <c>Up</c>.
        /// </summary>
        private enum Cue { Click, Down, Up }

        /// <summary>
        /// The order in which the hands are told, kept as data. Clicks reach the attack hand first, so a use click that
        /// opens a screen cannot swallow an attack clicked in the same frame. On a step the use hand is told first, so
        /// a hold-to-use it starts already keeps the attack hand from digging on that very step.
        /// </summary>
        private static readonly Hand[] ClickOrder = { Hand.Attack, Hand.Use };
        private static readonly Hand[] StepOrder = { Hand.Use, Hand.Attack };

        /// <summary>
        /// Reads one button at the moment its hand is about to be told. The gameplay gate is checked on every read:
        /// a hand told earlier may have closed it in this same frame (the crafting table opens a screen), and from then
        /// on both buttons count as released.
        /// </summary>
        private static bool ButtonDown(IInputGate input, Hand hand, bool edge)
        {
            if (!input.GameplayInputAllowed) return false;
            if (hand == Hand.Attack) return edge ? input.AttackPressed : input.AttackHeld;
            return edge ? input.UsePressed : input.UseHeld;
        }

        private void Tell(Hand hand, Cue cue)
        {
            if (hand == Hand.Attack) AttackHand(cue);
            else UseHand(cue);
        }

        /// <summary>Clicks act within their frame, and are not heard at all while a hold-to-use is running.</summary>
        private void DeliverClicks(IInputGate input)
        {
            if (holdMode != UseMode.None) return;
            foreach (Hand hand in ClickOrder)
                if (ButtonDown(input, hand, true)) Tell(hand, Cue.Click);
        }

        /// <summary>One 20 Hz step: the clock moves first, so marks and deadlines are current, then each hand in turn.</summary>
        private void DeliverStep(IInputGate input)
        {
            clock.Advance();
            foreach (Hand hand in StepOrder)
                Tell(hand, ButtonDown(input, hand, false) ? Cue.Down : Cue.Up);
        }

        // ------------------------------------------------------------------ frame pipeline
        /// <summary>Called every frame by the module.</summary>
        public void Update()
        {
            ISRBridge sr = SR;
            IInputGate input = SC.Input;
            IPlayerInventory inv = Inv;
            if (!HandsMayAct(sr, input, inv))
            {
                if (active) Deactivate();
                return;
            }
            active = true;

            WatchHeldItem(inv);
            Aim(sr, inv);
            if (kind == TargetKind.Block) outline.Show(blockHit.Pos); else outline.Hide();

            DeliverClicks(input);

            int steps = clock.Withdraw(Time.deltaTime);
            for (int i = 0; i < steps; i++) DeliverStep(input);

            if (DropKeyHit(input)) DropSelected();
            PublishMiningProgress();
        }

        public void LateUpdate()
        {
            if (active) outline.LateUpdate();
        }

        private static bool HandsMayAct(ISRBridge sr, IInputGate input, IPlayerInventory inv) =>
            sr != null && sr.InGame && !sr.IsPaused && input != null && input.GameplayInputAllowed
            && inv != null && !inv.VacpackSelected;

        /// <summary>
        /// The use hand. A running hold-to-use (eat, drink, bow) owns the button: Down continues it, Up ends it.
        /// Otherwise a click acts at once, and a button kept down acts again whenever its repeat deadline has come.
        /// </summary>
        private void UseHand(Cue cue)
        {
            if (holdMode != UseMode.None)
            {
                if (cue == Cue.Down) OnHoldTick();
                else if (cue == Cue.Up) OnHoldReleased();
                return;
            }
            bool due = cue == Cue.Click || (cue == Cue.Down && clock.Reached(useRepeatAt));
            if (due) UseNow();
        }

        /// <summary>
        /// The right-button scheduler. Every use, clicked or repeated, books the next repeat
        /// <see cref="RepeatDelay"/> steps ahead before it acts: one short click does exactly one thing, and a held
        /// button acts again on that schedule.
        /// </summary>
        private void UseNow()
        {
            useRepeatAt = clock.After(RepeatDelay);
            ResolveUse();
        }

        // ------------------------------------------------------------------ lifecycle
        /// <summary>Everything stops when the controller is not allowed to run (vacpack selected, screen open...).</summary>
        private void Deactivate()
        {
            active = false;
            FeedMiner(MinerEvent.Disengage, DigSurface.None);
            if (holdMode != UseMode.None) EndHold();
            outline.Hide();
            crack.Hide();
            kind = TargetKind.None;
            miningEngaged = false;
            clock.ForgetBacklog();
            try { if (SC.FirstPerson != null) SC.FirstPerson.MiningProgress = -1f; } catch { }
        }

        /// <summary>World unload: forget all transient state.</summary>
        public void Reset()
        {
            Deactivate();
            watchedItem = null;
            watchedSlot = -1;
            PrimeCharge();
        }

        private void WatchHeldItem(IPlayerInventory inv)
        {
            string id = SelectedId;
            int slot = inv.SelectedSlot;
            bool otherItem = id != watchedItem;
            if (otherItem) EmptyCharge();
            if ((otherItem || slot != watchedSlot) && holdMode != UseMode.None) EndHold();
            watchedItem = id;
            watchedSlot = slot;
        }

        // ------------------------------------------------------------------ targeting
        private void Aim(ISRBridge sr, IPlayerInventory inv)
        {
            kind = TargetKind.None;
            Vector3 dir = sr.LookDirection;
            if (dir.sqrMagnitude < 1e-6f) return;
            var ray = new Ray(sr.EyePosition, dir.normalized);
            bool creative = inv.Creative;
            float reach = creative ? (BlocksConfig.ReachCreative?.Value ?? 5f) : (BlocksConfig.ReachSurvival?.Value ?? 4.5f);
            float eReach = creative ? (BlocksConfig.EntityReachCreative?.Value ?? 5f) : (BlocksConfig.EntityReachSurvival?.Value ?? 3f);
            float best = float.MaxValue;

            if (world.Raycast(ray, reach, out var bh))
            {
                kind = TargetKind.Block;
                blockHit = bh;
                best = bh.Distance;
            }

            if (sr.RaycastWorld(ray, reach, out var rh) && rh.collider != null && !world.IsBlockCollider(rh.collider) && rh.distance < best)
            {
                best = rh.distance;
                var actor = TerrainHarvest.SRActorOf(rh.collider);
                if (actor != null)
                {
                    if (rh.distance <= eReach)
                    {
                        kind = TargetKind.Entity;
                        entityHit = new EntityHit { Target = actor, Point = rh.point, Distance = rh.distance, IsMcMob = false };
                    }
                    else kind = TargetKind.None; // an actor beyond entity reach is a miss
                }
                else if (rh.collider.attachedRigidbody != null && !rh.collider.attachedRigidbody.isKinematic)
                {
                    kind = TargetKind.None; // a moving body (Minecraft mob, TNT, item...): resolved by RaycastEntity below if in reach
                }
                else
                {
                    kind = TargetKind.World;
                    worldHit = rh;
                }
            }

            var ents = SC.Entities;
            if (ents != null)
            {
                float maxE = Mathf.Min(eReach, best + 0.05f);
                try
                {
                    if (ents.RaycastEntity(ray, maxE, out var eh) && eh.Target != null && eh.Distance <= best + 0.05f)
                    {
                        kind = TargetKind.Entity;
                        entityHit = eh;
                    }
                }
                catch (Exception e) { RateLog.Error("RaycastEntity", e); }
            }
        }

        // ------------------------------------------------------------------ drop key
        /// <summary>Read after the steps, so a screen opened during them also swallows the drop key this frame.</summary>
        private static bool DropKeyHit(IInputGate input) =>
            input.DropKey != KeyCode.None && HandlesDropKey() && input.GameKeyDown(input.DropKey);

        /// <summary>The Hud module may also implement the drop key ([Hud] HandleDropKey): never drop twice.</summary>
        private static bool HandlesDropKey()
        {
            string mode = (BlocksConfig.DropKeyHandling?.Value ?? "auto").Trim().ToLowerInvariant();
            if (mode == "on" || mode == "true") return true;
            if (mode == "off" || mode == "false") return false;
            try
            {
                if (SC.Config != null && SC.Config.TryGetEntry<bool>("Hud", "HandleDropKey", out var hud) && hud.Value) return false;
            }
            catch { }
            return true;
        }

        private void DropSelected()
        {
            var inv = Inv;
            var sr = SR;
            if (inv == null || sr == null) return;
            var st = SelectedStack;
            if (st.IsEmpty || st.Id == Content.Vacpack) return;
            // Ctrl+drop throws the whole stack. Ctrl is our sneak key ([Core] SneakKey, Left Ctrl), read via SC.Input
            // so the automated test can drive it.
            bool all = SpecialBlocks.Sneaking;
            int n = all ? st.Count : 1;
            var dropped = st.WithCount(n);
            int slot = inv.SelectedSlot;
            int left = st.Count - n;

            // Thrown from just below the eyes: 0.3 blocks/tick along the view, a lift of 0.1 (+-0.1 random) and a
            // sideways nudge of at most 0.02 (blocks per tick, converted to m/s); picked up again after 2 s.
            Vector3 look = sr.LookDirection.normalized;
            Vector3 toss = look * 0.3f;
            toss.y += 0.1f + 0.1f * (Rng.F() - Rng.F());
            Vector2 nudge = UnityEngine.Random.insideUnitCircle * 0.02f;
            toss.x += nudge.x;
            toss.z += nudge.y;
            var pos = sr.EyePosition - new Vector3(0, 0.3f, 0) + look * 0.2f;
            GameObject e0 = null;
            try { e0 = SC.Entities?.SpawnItem(dropped, pos, toss * 20f, 2f); }
            catch (Exception e) { RateLog.Error("drop item", e); }
            if (e0 == null)
            {
                // never destroy the items when no entity could be spawned
                RateLog.Action("drop", "drop of " + n + "x " + dropped.Id + " failed: SC.Entities.SpawnItem returned null (kept in the inventory)", 0.5f);
                return;
            }
            inv.Inv.Set(slot, left > 0 ? st.WithCount(left) : ItemStack.Empty);
            Swing();
            RateLog.Action("drop", "dropped " + n + "x " + dropped.Id + (all ? " (whole stack)" : ""), 0.1f);
        }
    }
}
