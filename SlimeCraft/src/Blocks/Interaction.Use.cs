using System;
using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    /// <summary>
    /// Right mouse button with a Minecraft item: the crafting table, placing blocks (against our blocks or Slime
    /// Rancher terrain, with occupancy checks), flint and steel, spawn eggs, and the hold-to-use items (eating,
    /// drinking, drawing a bow). Interaction.cs decides when these entry points run.
    /// </summary>
    internal sealed partial class InteractionController
    {
        // ------------------------------------------------------------------ identifiers and timing (20 Hz ticks)
        private const string FlintMarker = "flint_and_steel";
        private const string BowMarker = "bow";
        private const string ArrowItem = "minecraft:arrow";
        private const string CraftingTableBlock = "minecraft:crafting_table";
        private const string GoldenAppleItem = "minecraft:golden_apple";
        private const string HoneyBottleItem = "minecraft:honey_bottle";
        private const string OpenCraftingCommand = "/screen crafting";

        private const int RepeatDelay = 4;
        private const int MealTicks = 32;
        private const int DrinkTicks = 40;
        private const int BiteSpacing = 4;
        private const int CrumbsWhenFinished = 16;

        private const float PlaceBoxSide = 0.998f;
        private const float CapsuleTolerance = 0.02f;
        private const float BoundsShrink = 0.04f;
        private const float BodyBoxScale = 0.98f;
        private const float TinyBodyVolume = 0.03f;

        private const float ArrowBaseDamage = 2f;
        private const float ArrowSpread = 0.0075f;
        private const float ArrowMinPower = 0.1f;

        private static readonly Collider[] placeOverlap = new Collider[32];

        // ------------------------------------------------------------------ state
        private UseMode holdMode;
        private int holdTicks;
        private string holdItemId;
        private int holdSlot = -1;
        private int mealLength;
        private int nextBiteTick;

        private CharacterController playerCapsule;
        private int playerCapsuleOwner;


        // ------------------------------------------------------------------ small helpers
        private static bool IsFlint(ItemDef item) => item != null && item.Special == FlintMarker;
        private static bool IsBow(ItemDef item) => item != null && item.Special == BowMarker;
        private static bool IsDrink(ItemDef item) => item != null && item.Id == HoneyBottleItem;

        private static void UseUpDurability(int amount)
        {
            if (amount > 0 && !Creative) Inv?.DamageSelected(amount);
        }

        private static int ArrowsCarried()
        {
            var inv = Inv;
            return inv != null && inv.Inv != null ? inv.Inv.CountOf(ArrowItem) : 0;
        }

        private static void FlintSound(Vector3 at) =>
            SC.Audio?.Play("item.flintandsteel.use", at, 1f, Rng.Range(0.8f, 1.2f));

        // ------------------------------------------------------------------ one use (a click or a scheduled repeat; see UseNow)
        private void ResolveUse()
        {
            try
            {
                var item = SelectedDef;
                bool handled;
                switch (kind)
                {
                    case TargetKind.Block:
                        handled = UseOnOurBlock(item);
                        break;
                    case TargetKind.World:
                        handled = item != null && worldHit.collider != null && UseOnTerrain(item);
                        break;
                    case TargetKind.Entity:
                        handled = item != null && entityHit.Target != null && UseOnEntity(item);
                        break;
                    default:
                        handled = false;
                        break;
                }
                if (!handled) UseHeldItem(item);
            }
            catch (Exception e) { RateLog.Error("use item", e); }
        }

        private bool UseOnOurBlock(ItemDef item)
        {
            Vector3Int cell = blockHit.Pos;
            var clicked = world.GetDef(cell);
            if (clicked == null) return false;

            // Sneaking with something in hand lets the player build against the table instead of opening it.
            if (clicked.Id == CraftingTableBlock && (item == null || !SpecialBlocks.Sneaking))
            {
                try { SC.Commands?.Execute(OpenCraftingCommand); }
                catch (Exception e) { RateLog.Error("open crafting", e); }
                RateLog.Action("crafting", "crafting table used at " + RateLog.P(cell), 0.5f);
                Swing();
                return true;
            }
            if (item == null) return false;

            if (IsFlint(item))
            {
                if (clicked.IsTnt) LightTnt(cell);
                else
                {
                    FlintSound(blockHit.Point);
                    RateLog.Action("flint", "flint and steel used on " + clicked.Id + " at " + RateLog.P(cell)
                        + ": no effect: fire is not implemented", 0.5f);
                }
                UseUpDurability(1);
                Swing();
                return true;
            }

            Vector3Int inFront = cell + blockHit.Normal;
            if (item.IsBlock)
            {
                TryPlace(item, inFront, Dirs.Dominant(blockHit.Normal), "against " + clicked.Id + " " + RateLog.P(cell));
                return true;
            }
            if (item.Kind == ItemKind.SpawnEgg)
            {
                HatchEgg(item, new Vector3(inFront.x + 0.5f, inFront.y, inFront.z + 0.5f));
                return true;
            }
            return false;
        }

        private bool UseOnTerrain(ItemDef item)
        {
            RaycastHit hit = worldHit;
            if (IsFlint(item))
            {
                SetAlight(hit.collider.gameObject);
                FlintSound(hit.point);
                UseUpDurability(1);
                Swing();
                return true;
            }
            if (item.IsBlock)
            {
                Vector3Int cell = Vector3Int.FloorToInt(hit.point + hit.normal * 0.5f);
                TryPlace(item, cell, Dirs.Dominant(hit.normal),
                    "on SR terrain '" + hit.collider.name + "' " + RateLog.P(hit.point));
                return true;
            }
            if (item.Kind == ItemKind.SpawnEgg)
            {
                // Floors take the mob where they were clicked; walls and ceilings push it off by half a block.
                Vector3 feet = hit.normal.y > 0.5f ? hit.point : hit.point + hit.normal * 0.5f;
                HatchEgg(item, feet);
                return true;
            }
            return false;
        }

        private bool UseOnEntity(ItemDef item)
        {
            if (!IsFlint(item)) return false;
            SetAlight(entityHit.Target);
            FlintSound(entityHit.Point);
            UseUpDurability(1);
            Swing();
            return true;
        }

        /// <summary>Using the item itself (nothing targeted, or the target did not react).</summary>
        private void UseHeldItem(ItemDef item)
        {
            if (item == null) return;

            if (item.IsFood)
            {
                if (MayEat(item))
                {
                    BeginHold(UseMode.Eat, UseAnimation.Eat);
                }
                else if (RateLog.Actions)
                {
                    var sr = SR;
                    string health = sr != null ? sr.Health + "/" + sr.MaxHealth : "?";
                    string energy = sr != null ? sr.Energy + "/" + sr.MaxEnergy : "?";
                    RateLog.Action("eat-refused", "can't eat " + item.Id + ": SR health " + health + " and energy " + energy
                        + " are full (survival; creative always eats)", 1f);
                }
                return;
            }

            if (IsBow(item))
            {
                if (Creative || ArrowsCarried() >= 1) BeginHold(UseMode.Bow, UseAnimation.Bow);
                else RateLog.Action("bow-refused", "can't draw the bow: no " + ArrowItem + " in the inventory (survival)", 1f);
            }
        }

        /// <summary>Food also heals in SlimeCraft, so being hurt is reason enough to eat.</summary>
        private static bool MayEat(ItemDef item)
        {
            var sr = SR;
            if (Creative || sr == null || item.Id == GoldenAppleItem) return true;
            return sr.Energy < sr.MaxEnergy || sr.Health < sr.MaxHealth;
        }

        // ------------------------------------------------------------------ placing blocks
        private void TryPlace(ItemDef item, Vector3Int cell, int clickedFace, string where)
        {
            var def = Content.Block(item.BlockId ?? item.Id);
            var sr = SR;
            if (def == null || sr == null) return;

            if (world.IsSolid(cell))
            {
                RefusePlacement(def, cell, where, "cell already holds " + world.GetBlock(cell));
                return;
            }

            var box = new Bounds(new Vector3(cell.x + 0.5f, cell.y + 0.5f, cell.z + 0.5f),
                new Vector3(PlaceBoxSide, PlaceBoxSide, PlaceBoxSide));
            if (PlayerOverlaps(sr, box))
            {
                RefusePlacement(def, cell, where, "inside the player (feet " + RateLog.P(sr.PlayerFeet) + ")");
                return;
            }
            var blocker = BodyInside(sr, box);
            if (blocker != null)
            {
                RefusePlacement(def, cell, where, "occupied by '" + blocker.name + "'");
                return;
            }

            BlockFacing facing = Dirs.ToFacing(ChooseFacing(def, clickedFace, sr));
            if (!world.SetBlock(cell, def.Id, facing, true))
            {
                RefusePlacement(def, cell, where, "SetBlock failed");
                return;
            }

            world.RebuildAround(cell);
            Inv?.ConsumeSelected(1);
            ItemUsed();
            Swing();
            if (RateLog.Actions)
                RateLog.Action("place", "block placed: " + def.Id + " at " + RateLog.P(cell) + " facing " + facing + " " + where
                    + (Creative ? " (creative)" : ""));
        }

        private static void RefusePlacement(BlockDef def, Vector3Int cell, string where, string reason)
        {
            if (RateLog.Actions)
                RateLog.Action("place-refused", "placement refused: " + def.Id + " at " + RateLog.P(cell) + " " + where + ": " + reason, 0.5f);
        }

        /// <summary>Front faces the player for orientable blocks; logs follow the axis of the clicked face.</summary>
        private static int ChooseFacing(BlockDef def, int clickedFace, ISRBridge sr)
        {
            switch (def.Rotation)
            {
                case BlockRotation.Horizontal:
                    return Dirs.Opposite[Dirs.DominantHorizontal(sr.LookDirection)];
                case BlockRotation.Axis:
                    if (clickedFace == Dirs.Up || clickedFace == Dirs.Down) return Dirs.Up;
                    if (clickedFace == Dirs.North || clickedFace == Dirs.South) return Dirs.North;
                    return Dirs.East;
                default:
                    return Dirs.North;
            }
        }

        /// <summary>
        /// Does the cell box touch the player? Uses the real vertical capsule of SR's character controller (its
        /// bounding box would wrongly reject diagonal cells), with a small tolerance so the player can pillar up and
        /// build right next to themselves. Falls back to the bridge's player bounds.
        /// </summary>
        private bool PlayerOverlaps(ISRBridge sr, Bounds box)
        {
            var capsule = FindPlayerCapsule(sr);
            if (capsule != null && capsule.enabled)
            {
                Transform t = capsule.transform;
                Vector3 centre = t.TransformPoint(capsule.center);
                Vector3 scale = t.lossyScale;
                float radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
                float spine = Mathf.Max(capsule.height * Mathf.Abs(scale.y) * 0.5f - radius, 0f);
                float bottom = centre.y - spine, top = centre.y + spine;

                Vector3 min = box.min, max = box.max;
                float gx = centre.x < min.x ? min.x - centre.x : centre.x > max.x ? centre.x - max.x : 0f;
                float gz = centre.z < min.z ? min.z - centre.z : centre.z > max.z ? centre.z - max.z : 0f;
                float gy = top < min.y ? min.y - top : bottom > max.y ? bottom - max.y : 0f;

                float reach = radius - CapsuleTolerance;
                return gx * gx + gy * gy + gz * gz < reach * reach;
            }

            Bounds body = sr.PlayerBounds;
            if (body.size == Vector3.zero) return false;
            body.Expand(-BoundsShrink);
            return body.Intersects(box);
        }

        private CharacterController FindPlayerCapsule(ISRBridge sr)
        {
            var player = sr.Player;
            if (player == null) return null;
            int owner = player.GetInstanceID();
            if (owner != playerCapsuleOwner || playerCapsule == null)
            {
                playerCapsuleOwner = owner;
                var found = player.GetComponent<CharacterController>();
                if (found == null) found = player.GetComponentInChildren<CharacterController>(true);
                playerCapsule = found;
            }
            return playerCapsule;
        }

        /// <summary>
        /// A physics body that sits in the cell and would end up inside the new block: moving bodies bigger than a
        /// dropped item, or kinematic ones that are creatures. Static SR geometry may overlap our blocks.
        /// </summary>
        private GameObject BodyInside(ISRBridge sr, Bounds box)
        {
            int n = Physics.OverlapBoxNonAlloc(box.center, box.extents * BodyBoxScale, placeOverlap, Quaternion.identity,
                ~0, QueryTriggerInteraction.Ignore);
            var player = sr.Player;
            Transform playerRoot = player != null ? player.transform : null;

            for (int i = 0; i < n; i++)
            {
                var col = placeOverlap[i];
                if (col == null || world.IsBlockCollider(col)) continue;
                var body = col.attachedRigidbody;
                if (body == null) continue;
                if (playerRoot != null && (col.transform.IsChildOf(playerRoot) || body.transform.IsChildOf(playerRoot))) continue;
                Vector3 size = col.bounds.size;
                if (size.x * size.y * size.z < TinyBodyVolume) continue;
                if (body.isKinematic && !IsCreature(col.gameObject)) continue;
                return body.gameObject;
            }
            return null;
        }

        /// <summary>An SR actor (slime, animal, food...) or one of our Minecraft mobs, which may be kinematic while frozen.</summary>
        private static bool IsCreature(GameObject go)
        {
            if (go.GetComponentInParent<Identifiable>() != null) return true;
            var entities = SC.Entities;
            if (entities == null) return false;
            try
            {
                Transform t = go.transform;
                foreach (var mob in entities.LiveMobs)
                    if (mob != null && t.IsChildOf(mob.transform)) return true;
            }
            catch (Exception e) { RateLog.Error("LiveMobs", e); }
            return false;
        }

        // ------------------------------------------------------------------ flint and steel / spawn eggs
        private void LightTnt(Vector3Int pos)
        {
            var entities = SC.Entities;
            if (entities == null) return;

            // The block goes away even if the primed entity fails to appear.
            world.SetBlock(pos, null);
            world.RebuildAround(pos);
            GameObject lit = null;
            try { lit = entities.SpawnPrimedTnt(BlockWorld.Center(pos)); }
            catch (Exception e) { RateLog.Error("SpawnPrimedTnt", e); }
            RateLog.Action("tnt", "flint and steel primed TNT at " + RateLog.P(pos)
                + (lit != null ? " (80 tick fuse)" : " -> SpawnPrimedTnt returned null"), 0.2f);
        }

        /// <summary>
        /// Minecraft entities react first (a creeper starts its fuse). Otherwise Slime Rancher's own fire is used:
        /// every ignitable part of an SR actor, or only the clicked object's own components for scenery.
        /// </summary>
        private static void SetAlight(GameObject target)
        {
            var sr = SR;
            if (sr == null || target == null) return;
            var igniter = sr.Player;
            if (igniter == null) return;

            var entities = SC.Entities;
            if (entities != null)
            {
                try
                {
                    if (entities.Ignite(target))
                    {
                        RateLog.Action("flint", "flint and steel ignited Minecraft entity '" + target.name + "'", 0.5f);
                        return;
                    }
                }
                catch (Exception e) { RateLog.Error("Entities.Ignite", e); }
            }

            GameObject owner = target;
            Identifiable actor = null;
            try { actor = target.GetComponentInParent<Identifiable>(); } catch { }

            int burned = 0;
            try
            {
                Ignitable[] parts;
                if (actor != null)
                {
                    owner = actor.gameObject;
                    parts = owner.GetComponentsInChildren<Ignitable>();
                }
                else
                {
                    // Scenery: never walk a big hierarchy, only the object that was clicked.
                    parts = target.GetComponents<Ignitable>();
                }
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] == null) continue;
                    parts[i].Ignite(igniter);
                    burned++;
                }
            }
            catch (Exception e) { RateLog.Error("ignite", e); }

            RateLog.Action("flint", "flint and steel used on '" + owner.name + "': "
                + (burned > 0 ? burned + " SR Ignitable(s) ignited" : "nothing ignitable"), 0.5f);
        }

        private void HatchEgg(ItemDef item, Vector3 feet)
        {
            var entities = SC.Entities;
            if (entities == null || string.IsNullOrEmpty(item.SpawnsMob)) return;

            GameObject mob = null;
            try { mob = entities.SpawnMob(item.SpawnsMob, feet); }
            catch (Exception e) { RateLog.Error("SpawnMob", e); }
            if (mob != null)
            {
                Inv?.ConsumeSelected(1);
                ItemUsed();
            }
            Swing();
            RateLog.Action("egg", "spawn egg " + item.Id + " at " + RateLog.P(feet)
                + (mob != null ? " -> spawned '" + mob.name + "'" : " -> SpawnMob failed"), 0.2f);
        }

        // ------------------------------------------------------------------ hold-to-use (eat, drink, bow)
        private void BeginHold(UseMode mode, UseAnimation animation)
        {
            holdMode = mode;
            holdTicks = 0;
            holdItemId = SelectedId;
            holdSlot = Inv != null ? Inv.SelectedSlot : -1;

            if (mode == UseMode.Eat)
            {
                mealLength = IsDrink(SelectedDef) ? DrinkTicks : MealTicks;
                // The first ~22 % of the meal is quiet; after that a bite lands every 4 ticks, lined up so the last
                // one comes exactly 4 ticks before the meal ends.
                int quiet = mealLength * 7 / 32;
                nextBiteTick = quiet + 1;
                while ((mealLength - nextBiteTick) % BiteSpacing != 0) nextBiteTick++;
            }

            FeedMiner(MinerEvent.Disengage, DigSurface.None);
            try { SC.FirstPerson?.StartUsing(animation); }
            catch (Exception e) { RateLog.Error("FirstPerson.StartUsing", e); }
        }

        private void EndHold()
        {
            holdMode = UseMode.None;
            holdTicks = 0;
            try { SC.FirstPerson?.StopUsing(); }
            catch (Exception e) { RateLog.Error("FirstPerson.StopUsing", e); }
        }

        private void OnHoldTick()
        {
            var inv = Inv;
            if (inv == null || inv.SelectedSlot != holdSlot || SelectedId != holdItemId)
            {
                EndHold();
                return;
            }

            holdTicks++;
            // The bow only counts; FirstPerson draws the pull and the zoom.
            if (holdMode != UseMode.Eat) return;

            if (holdTicks >= mealLength)
            {
                FinishMeal();
                return;
            }
            if (holdTicks == nextBiteTick)
            {
                // FirstPerson shows the crumb puff on these same ticks; this side adds the sound.
                PlayBiteSound(SelectedDef);
                nextBiteTick += BiteSpacing;
            }
        }

        private static void PlayBiteSound(ItemDef item)
        {
            var sr = SR;
            if (sr == null || item == null) return;
            Vector3 mouth = sr.EyePosition;
            if (IsDrink(item))
                SC.Audio?.Play("item.honey_bottle.drink", mouth, 0.5f, Rng.Range(0.9f, 1f));
            else
                SC.Audio?.Play("entity.generic.eat", mouth, Rng.R.Next(2) == 0 ? 0.5f : 1f, Rng.Triangle(1f, 0.2f));
        }

        private void FinishMeal()
        {
            var item = SelectedDef;
            var sr = SR;
            if (item == null || sr == null)
            {
                EndHold();
                return;
            }

            bool drink = IsDrink(item);
            Vector3 mouth = sr.EyePosition;
            PlayBiteSound(item);
            if (!drink)
            {
                try { particles.SpawnItemCrumbs(item, CrumbsWhenFinished); }
                catch (Exception e) { RateLog.Error("eat particles", e); }
                SC.Audio?.Play("entity.generic.eat", mouth, 1f, Rng.Triangle(1f, 0.4f));
            }
            SC.Audio?.Play("entity.player.burp", mouth, 0.5f, Rng.Range(0.9f, 1f));

            int healthBefore = sr.Health, energyBefore = sr.Energy;
            try
            {
                if (item.SRHeal > 0) sr.HealPlayer(item.SRHeal);
                if (item.SREnergy > 0) sr.AddEnergy(item.SREnergy);
            }
            catch (Exception e) { RateLog.Error("food effects", e); }
            Inv?.ConsumeSelected(1);

            if (RateLog.Actions)
                RateLog.Action("eat", "food eaten: " + item.Id + " after " + holdTicks + " ticks -> SR health "
                    + healthBefore + "->" + sr.Health + "/" + sr.MaxHealth + " (+" + item.SRHeal + "), energy "
                    + energyBefore + "->" + sr.Energy + "/" + sr.MaxEnergy + " (+" + item.SREnergy + ")"
                    + (Creative ? " (creative: not consumed)" : ""));
            EndHold();
        }

        private void OnHoldReleased()
        {
            // Letting go early while eating simply cancels; letting go of a drawn bow shoots.
            if (holdMode == UseMode.Bow) Shoot(holdTicks);
            EndHold();
        }

        private void Shoot(int ticksDrawn)
        {
            var sr = SR;
            var inv = Inv;
            if (sr == null || inv == null) return;

            // Draw strength grows quickly at first and is full after one second.
            float seconds = ticksDrawn / 20f;
            float power = Mathf.Min(1f, seconds * (seconds + 2f) / 3f);
            if (power < ArrowMinPower)
            {
                RateLog.Action("bow", "bow released after " + ticksDrawn + " ticks: too weak to shoot (power "
                    + RateLog.F(power) + " < 0.1)", 0.2f);
                return;
            }

            bool creative = Creative;
            if (!creative && ArrowsCarried() <= 0) return;

            Vector3 aim = sr.LookDirection.normalized;
            aim += new Vector3(Rng.Gaussian(), Rng.Gaussian(), Rng.Gaussian()) * ArrowSpread;
            aim.Normalize();
            Vector3 velocity = aim * (power * 3f * 20f);
            Vector3 muzzle = sr.EyePosition + Vector3.down * 0.1f + aim * 0.3f;

            GameObject arrow = null;
            try { arrow = SC.Entities?.ShootArrow(muzzle, velocity, sr.Player, ArrowBaseDamage); }
            catch (Exception e) { RateLog.Error("ShootArrow", e); }

            bool noArrow = arrow == null;
            if (noArrow)
                SC.Audio?.Play("entity.arrow.shoot", sr.EyePosition, 1f, 1f / (Rng.F() * 0.4f + 1.2f) + power * 0.5f);

            if (!creative)
            {
                inv.DamageSelected(1);
                inv.Inv?.Remove(ArrowItem, 1);
            }

            RateLog.Action("bow", "bow fired after " + ticksDrawn + " ticks: power " + RateLog.F(power) + ", "
                + RateLog.F(velocity.magnitude, "0.0") + " m/s" + (noArrow ? " (ShootArrow returned null)" : ""), 0.2f);
        }
    }
}
