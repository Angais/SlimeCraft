using System;
using UnityEngine;

namespace SlimeCraft.FP
{
    /// <summary>Everything the hand simulation needs to know about one 20 Hz game tick (filled in by FirstPersonModule).</summary>
    internal struct TickInput
    {
        /// <summary>Selected hotbar item id; null = empty hand; <see cref="Content.Vacpack"/> = SR's vacpack (draws nothing).</summary>
        public string SelectedId;
        /// <summary>Definition of the selected item, or null.</summary>
        public ItemDef SelectedDef;
        /// <summary>The player stands on the ground this tick.</summary>
        public bool Grounded;
        /// <summary>Horizontal distance walked during this tick, in blocks (0 for teleports).</summary>
        public float HorizontalMoved;
        /// <summary>Damage was taken since the previous tick.</summary>
        public bool Hurt;
        /// <summary>Direction of the damage source relative to the view in degrees (0 left, 90 front, 180 right, -90 behind).</summary>
        public float HurtDir;
    }

    /// <summary>
    /// Simulation state of the first-person right hand: swing, equip height and attack-cooldown dip, delayed sway,
    /// walking bob, hurt tilt, hold animations (eat / drink / bow / block), eating-effect timing and the bow zoom.
    /// The state advances at 20 ticks per second in <see cref="Tick"/>; the pose functions (ItemInHandPoses.cs)
    /// interpolate it with the partial tick so the hand moves with Minecraft's first-person feel at any frame rate.
    /// Pure math: no Unity scene access, no services.
    /// </summary>
    internal sealed partial class ItemInHand
    {
        // ---- timing facts -------------------------------------------------------------------------------
        private const int SwingTicks = 6;              // one full whack
        private const int SwingRestartStep = 3;        // a new request may interrupt from this step on
        private const float RaiseRatePerTick = 0.4f;   // max change of the equip height per tick
        private const float SwapVisibleBelow = 0.1f;   // the new item takes over once the hand is this low
        private const float DefaultAttackSpeed = 4f;   // attacks per second when the item doesn't say
        private const int HurtTicks = 10;
        private const int EatTicks = 32;               // 1.6 s
        private const int DrinkTicks = 40;             // 2.0 s
        private const float SwayFollow = 0.5f;         // fraction of the camera-angle gap closed per tick
        private const float BobFollow = 0.4f;
        private const float WalkWrap = 2000f;          // the bob repeats every 2 walk units, so wrapping is invisible
        private const string DrinkItem = "minecraft:honey_bottle";

        // ---- public state ---------------------------------------------------------------------------------
        /// <summary>Ticks processed so far; "now" in tick units is TickCount + partial.</summary>
        public int TickCount;
        /// <summary>Camera pitch in degrees, positive = looking down (written by the module every frame).</summary>
        public float XRot;
        /// <summary>Unwrapped camera yaw in degrees, positive = turning right (accumulated by the module every frame).</summary>
        public float YRot;
        /// <summary>Bow zoom FOV multiplier of this tick.</summary>
        public float FovMod = 1f;
        /// <summary>Bow zoom FOV multiplier of the previous tick (for interpolation).</summary>
        public float OFovMod = 1f;
        /// <summary>0..1 while something is being mined, -1 otherwise.</summary>
        public float MiningProgress = -1f;
        /// <summary>True only on ticks where eating crumbs and the eat sound should be produced.</summary>
        public bool EmitEatEffects;

        /// <summary>Item currently drawn in the hand (lags behind the selection during an equip). Empty = bare arm.</summary>
        public string VisibleId { get; private set; }
        /// <summary>A hold animation is running.</summary>
        public bool Using { get; private set; }
        /// <summary>The running hold animation (<see cref="UseAnimation.None"/> when idle).</summary>
        public UseAnimation UseAnim { get; private set; }
        /// <summary>The current (or last) use is a drink rather than food.</summary>
        public bool IsDrinking { get; private set; }

        /// <summary>Bow model to show while drawing: 0, then 1 after 13 use ticks, then 2 after 18 use ticks.</summary>
        public int BowPullStage
        {
            get
            {
                // equivalent to comparing useTicks * 0.05 against 0.65 and 0.9, without float rounding doubts
                if (useTicks >= 18) return 2;
                if (useTicks >= 13) return 1;
                return 0;
            }
        }

        // ---- swing ------------------------------------------------------------------------------------------
        private bool swingActive;
        private bool swingQueued;      // a (re)start was requested and begins on the next tick
        private int swingStep;         // tick index inside the current swing, 0..SwingTicks-1
        private float swingNow, swingBefore;

        // ---- equip height / cooldown dip ---------------------------------------------------------------------
        private int ticksSinceSwap;
        private bool attackDipQueued;
        private string selectedLastTick;
        private float raise, raiseBefore;  // 1 = fully raised, 0 = off screen

        // ---- sway / bob / hurt -----------------------------------------------------------------------------
        private float lagPitch, lagPitchBefore, lagYaw, lagYawBefore;
        private float bobAmount, bobAmountBefore;
        private float walked, walkedBefore;
        private int hurtLeft;
        private float hurtFrom;

        // ---- hold animations --------------------------------------------------------------------------------
        private float useStartedAt;
        private int useTicks;          // the start tick counts as one
        private int consumeTicks = EatTicks;
        private string useItem;

        public ItemInHand()
        {
            Reset(null);
        }

        private static bool SameId(string a, string b) { return string.Equals(a, b, StringComparison.Ordinal); }

        // ===================================================================================================
        //  Commands from the module
        // ===================================================================================================

        /// <summary>Fresh world: hand lowered (it rises like on spawn), every animation idle, sway snapped to the camera.</summary>
        public void Reset(string selectedId)
        {
            VisibleId = selectedId;
            selectedLastTick = selectedId;
            raise = raiseBefore = 0f;
            ticksSinceSwap = 0;
            attackDipQueued = false;

            swingActive = false;
            swingQueued = false;
            swingStep = 0;
            swingNow = swingBefore = 0f;

            lagPitch = lagPitchBefore = XRot;
            lagYaw = lagYawBefore = YRot;
            bobAmount = bobAmountBefore = 0f;
            walked = walkedBefore = 0f;
            hurtLeft = 0;
            hurtFrom = 0f;

            Using = false;
            UseAnim = UseAnimation.None;
            useTicks = 0;
            useItem = null;
            IsDrinking = false;
            consumeTicks = EatTicks;

            FovMod = OFovMod = 1f;
            MiningProgress = -1f;
            EmitEatEffects = false;
        }

        /// <summary>Shifts the unwrapped yaw (and its lagged copies) by a whole number of turns; the sway is unaffected.</summary>
        public void RebaseYaw(float delta)
        {
            YRot -= delta;
            lagYaw -= delta;
            lagYawBefore -= delta;
        }

        /// <summary>
        /// Requests an arm swing. It (re)starts on the next tick unless the current swing is still in its first half,
        /// so holding the button while mining gives a continuous whack. An attack swing also queues the cooldown dip.
        /// </summary>
        public void Swing(bool attack)
        {
            if (!swingActive || swingQueued || swingStep >= SwingRestartStep)
            {
                swingActive = true;
                swingQueued = true;
            }
            if (attack) attackDipQueued = true;
        }

        /// <summary>The held item was consumed / placed: it drops out of view at once and pops back up.</summary>
        public void ItemUsed()
        {
            raise = 0f; // raiseBefore stays, so the drop is interpolated within the current tick
        }

        /// <summary>Begins a hold animation. <see cref="UseAnimation.None"/> stops instead.</summary>
        public void StartUsing(UseAnimation anim, string itemId, float nowTicks)
        {
            if (anim == UseAnimation.None)
            {
                StopUsing();
                return;
            }
            Using = true;
            UseAnim = anim;
            useStartedAt = nowTicks;
            useTicks = 1;
            useItem = itemId;
            IsDrinking = SameId(itemId, DrinkItem);
            consumeTicks = IsDrinking ? DrinkTicks : EatTicks;
            ItemUsed();
        }

        /// <summary>Ends the hold animation (only ever called from outside; uses never end on their own here).</summary>
        public void StopUsing()
        {
            Using = false;
            UseAnim = UseAnimation.None;
            useItem = null;
            useTicks = 0;
        }

        // ===================================================================================================
        //  20 Hz simulation
        // ===================================================================================================

        public void Tick(ref TickInput input)
        {
            EmitEatEffects = false;

            AdvanceHurt(input.Hurt, input.HurtDir);
            AdvanceSway();
            AdvanceBob(input.Grounded, input.HorizontalMoved);
            AdvanceSwing();
            float cooldown = AdvanceCooldown(input.SelectedId, input.SelectedDef);
            AdvanceEquip(input.SelectedId, cooldown);
            AdvanceUse();
            AdvanceZoom();

            TickCount++;
        }

        private void AdvanceHurt(bool hurt, float dir)
        {
            if (hurtLeft > 0) hurtLeft--;
            if (hurt)
            {
                hurtLeft = HurtTicks;
                hurtFrom = dir;
            }
        }

        private void AdvanceSway()
        {
            lagPitchBefore = lagPitch;
            lagYawBefore = lagYaw;
            lagPitch += (XRot - lagPitch) * SwayFollow;
            lagYaw += (YRot - lagYaw) * SwayFollow;
        }

        private void AdvanceBob(bool grounded, float moved)
        {
            bobAmountBefore = bobAmount;
            float goal = grounded ? Mathf.Min(0.1f, moved) : 0f;
            bobAmount += (goal - bobAmount) * BobFollow;

            walkedBefore = walked;
            walked += moved * 0.6f;
            if (walked > WalkWrap)
            {
                walked -= WalkWrap;
                walkedBefore -= WalkWrap;
            }
        }

        private void AdvanceSwing()
        {
            swingBefore = swingNow;
            if (swingQueued)
            {
                swingQueued = false;
                swingStep = 0;
            }
            else if (swingActive && ++swingStep >= SwingTicks)
            {
                swingActive = false;
                swingStep = 0;
            }
            swingNow = swingActive ? swingStep / (float)SwingTicks : 0f;
        }

        /// <summary>
        /// Updates the swap/attack counter and returns the cooldown fraction (0 just after a swap or attack, 1 when
        /// recovered). Recovery takes 20 / attackSpeed ticks: 5 for most items, 12.5 for swords.
        /// </summary>
        private float AdvanceCooldown(string selected, ItemDef def)
        {
            ticksSinceSwap++;
            if (!SameId(selected, selectedLastTick))
            {
                ticksSinceSwap = 0;
                selectedLastTick = selected;
                if (Using && !SameId(useItem, selected)) StopUsing();
            }
            if (attackDipQueued)
            {
                // swings that belong to block mining never dip the hand
                if (MiningProgress < 0f) ticksSinceSwap = 0;
                attackDipQueued = false;
            }
            float speed = (def != null && def.AttackSpeed > 0f) ? def.AttackSpeed : DefaultAttackSpeed;
            return Mathf.Clamp01((ticksSinceSwap + 1) / (20f / speed));
        }

        /// <summary>Lowers the hand on an item change, swaps the drawn item near the bottom and raises it again.</summary>
        private void AdvanceEquip(string selected, float cooldown)
        {
            if (SameId(VisibleId, selected) || !ItemDisplayResolver.Get(selected).HandAnimationOnSwap)
                VisibleId = selected;

            bool shownEmpty = string.IsNullOrEmpty(VisibleId);
            bool wantedEmpty = string.IsNullOrEmpty(selected);
            bool needsReequip = shownEmpty != wantedEmpty || (!shownEmpty && !SameId(VisibleId, selected));
            if (!needsReequip && !SameId(VisibleId, selected))
                VisibleId = selected; // e.g. null vs "" : both empty, nothing to animate

            float goal = needsReequip ? 0f : cooldown * cooldown * cooldown;
            raiseBefore = raise;
            raise += Mathf.Clamp(goal - raise, -RaiseRatePerTick, RaiseRatePerTick);
            if (raise < SwapVisibleBelow) VisibleId = selected;
        }

        private void AdvanceUse()
        {
            if (!Using) return;
            useTicks++;
            if (UseAnim != UseAnimation.Eat) return;

            // crumbs + sound every 4 ticks once the first ~22 % of the meal have passed, never on the final tick
            int elapsed = useTicks - 1;
            int remaining = consumeTicks - elapsed;
            int quietStart = Mathf.FloorToInt(consumeTicks * 0.21875f);
            if (elapsed > quietStart && remaining > 0 && remaining % 4 == 0)
                EmitEatEffects = true;
        }

        private void AdvanceZoom()
        {
            OFovMod = FovMod;
            float goal = 1f;
            if (Using && UseAnim == UseAnimation.Bow)
            {
                float draw = Mathf.Min(useTicks / 20f, 1f);
                goal = 1f - draw * draw * 0.15f;
            }
            FovMod += (goal - FovMod) * 0.5f;
        }

        // ===================================================================================================
        //  Interpolated values
        // ===================================================================================================

        /// <summary>
        /// Swing progress 0..1 at the given partial tick. A drop between ticks is treated as a wrap past 1, so the
        /// last segment of a swing (and an interrupted swing) completes its sweep instead of jumping back.
        /// </summary>
        public float AttackAnim(float partial)
        {
            float step = swingNow - swingBefore;
            if (step < 0f) step += 1f;
            return swingBefore + step * partial;
        }

        /// <summary>How far the hand is pushed down (0 = fully raised), scaled by the item's swap animation scale.</summary>
        public float InverseArmHeight(float partial, float swapAnimationScale)
        {
            return swapAnimationScale * (1f - Mathf.LerpUnclamped(raiseBefore, raise, partial));
        }

        /// <summary>Continuous ticks since the current hold animation began (never negative).</summary>
        public float UseTime(float nowTicks)
        {
            return Mathf.Max(0f, nowTicks - useStartedAt);
        }
    }
}
