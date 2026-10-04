using System.Collections.Generic;
using SlimeCraft.Entities.Model;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Passive farm animal: runs around in a panic for 5 seconds after being hurt, otherwise strolls and looks
    /// around. Natural spawning counts every subclass toward the passive-animal cap.
    /// </summary>
    internal abstract class AnimalMob : McMob
    {
        private const float PanicSeconds = 5f;

        /// <summary>Speed modifier used while fleeing.</summary>
        protected abstract float FleeSpeed { get; }

        /// <summary>
        /// Lets a species take over the decision step (the grazing sheep). Return true to skip the usual
        /// panic / stroll / look behaviour for this step.
        /// </summary>
        protected virtual bool OverridesRoutine() => false;

        protected override void Think()
        {
            if (OverridesRoutine()) return;
            if (Panic(FleeSpeed)) return;
            Wander(1f);
            IdleLook(6f);
        }

        protected override void OnHurt(DamageKind kind, GameObject attacker)
        {
            panicUntil = Time.time + PanicSeconds;
            CancelWander();
        }

        protected static string Cooked(bool onFire, string raw, string cooked) => onFire ? cooked : raw;
    }

    /// <summary>Pig, cow and sheep: a looking head and four legs trotting in diagonal pairs.</summary>
    internal abstract class FourLeggedAnimal : AnimalMob
    {
        protected Bone head;
        protected QuadLegs legs;

        protected void BindBody()
        {
            if (rig == null) return;
            head = rig.Find("head");
            legs = QuadLegs.Bind(rig);
        }

        protected override void Pose(PoseInput p)
        {
            PoseTerms.Look(head, p);
            legs.Trot(p, 0f);
        }
    }

    // ======================================================================== pig

    internal sealed class PigMob : FourLeggedAnimal
    {
        protected override float FleeSpeed => 1.25f;

        protected override void BuildModel()
        {
            rig = BuildRig("pig", MobRigs.Pig, "entity/pig/pig_temperate");
            BindBody();
        }

        protected override void DropLoot(List<ItemStack> drops, bool onFire, bool byPlayer)
        {
            AddDrop(drops, Cooked(onFire, "minecraft:porkchop", "minecraft:cooked_porkchop"), 1, 3);
        }
    }

    // ======================================================================== cow

    internal sealed class CowMob : FourLeggedAnimal
    {
        protected override float FleeSpeed => 2f;

        protected override void BuildModel()
        {
            rig = BuildRig("cow", MobRigs.Cow, "entity/cow/cow_temperate");
            BindBody();
        }

        protected override void DropLoot(List<ItemStack> drops, bool onFire, bool byPlayer)
        {
            AddDrop(drops, "minecraft:leather", 0, 2);
            AddDrop(drops, Cooked(onFire, "minecraft:beef", "minecraft:cooked_beef"), 1, 3);
        }
    }

    // ======================================================================== sheep

    /// <summary>
    /// Sheep in one of six wool colours (saved as the variant index). Now and then it lowers its head to graze for
    /// two seconds; grazing changes nothing in the world and there is no shearing.
    /// </summary>
    internal sealed class SheepMob : FourLeggedAnimal
    {
        /// <summary>Wool colour names by variant index. The order is part of the save format.</summary>
        internal static readonly string[] ColorNames = { "white", "light_gray", "gray", "black", "brown", "pink" };

        // Dye colours (RGB) in the same order; the wool tint is derived from them (white uses a fixed grey).
        private static readonly int[] DyeColours = { 0xF9FFFE, 0x9D9D97, 0x474F52, 0x1D1D21, 0x835432, 0xF38BAA };

        private const float GrazeLength = 40f;
        private const float GrazeEase = 4f;   // ticks to lower / raise the head

        private float grazeLeft;   // ticks of grazing remaining

        protected override float FleeSpeed => 1.25f;

        public string ColorName => ColorNames[Mathf.Clamp(Variant, 0, ColorNames.Length - 1)];

        /// <summary>
        /// Random colour with the natural-spawn odds (out of 100): black 5, gray 5, light gray 5, brown 3, white 82;
        /// a white result turns pink one time in 500.
        /// </summary>
        public static int RandomColor()
        {
            int roll = Rng.I(100);
            if (roll < 5) return 3;    // black
            if (roll < 10) return 2;   // gray
            if (roll < 15) return 1;   // light gray
            if (roll < 18) return 4;   // brown
            return Rng.I(500) == 0 ? 5 : 0;
        }

        private static Color32 WoolTint(int index)
        {
            if (index <= 0) return new Color32(0xE6, 0xE6, 0xE6, 0xFF);
            int rgb = DyeColours[Mathf.Clamp(index, 0, DyeColours.Length - 1)];
            return new Color32(Darken((rgb >> 16) & 0xFF), Darken((rgb >> 8) & 0xFF), Darken(rgb & 0xFF), 0xFF);
        }

        private static byte Darken(int channel) => (byte)Mathf.FloorToInt(channel * 0.75f);

        protected override void OnPreSetup()
        {
            if (Variant < 0 || Variant >= ColorNames.Length) Variant = RandomColor();
        }

        protected override void BuildModel()
        {
            rig = BuildRig("sheep", MobRigs.Sheep, "entity/sheep/sheep");
            var tint = WoolTint(Variant);
            string colour = ColorName;

            if (Variant != 0)
            {
                // Short tinted undercoat on the skin, drawn just after it.
                var undercoat = EMat.Tinted("entity/sheep/sheep_wool_undercoat", tint);
                rig.AddLayer(EMat.Lit("sheep_undercoat_" + colour, undercoat, true, 1));
            }

            // Wool coat: a second rig that follows the body pose every frame; the hurt flash shows on it too.
            var wool = EMat.Tinted("entity/sheep/sheep_wool", tint);
            BuildFollowerRig("sheep_fur", MobRigs.SheepFur, EMat.Lit("sheep_wool_" + colour, wool, true), wool);
            BindBody();
        }

        protected override bool OverridesRoutine()
        {
            if (grazeLeft <= 0f && Time.time >= panicUntil && grounded && Rng.I(1000) < 2)
            {
                grazeLeft = GrazeLength;
                StopMoving();
                CancelWander();
            }
            if (grazeLeft <= 0f) return false;
            StopMoving();
            ClearLook();
            return true;
        }

        protected override void TickTicks(float ticks)
        {
            if (grazeLeft > 0f) grazeLeft = Mathf.Max(0f, grazeLeft - ticks);
        }

        protected override void OnHurt(DamageKind kind, GameObject attacker)
        {
            grazeLeft = 0f;
            base.OnHurt(kind, attacker);
        }

        protected override void Pose(PoseInput p)
        {
            base.Pose(p);
            if (head == null || grazeLeft <= 0f) return;
            head.Offset.y += 9f * HeadDrop();
            head.Euler.x = GrazePitch();
        }

        /// <summary>0 = head up, 1 = fully down: down over the first 4 ticks of grazing, up over the last 4.</summary>
        private float HeadDrop()
        {
            float elapsed = GrazeLength - grazeLeft;
            return Mathf.Min(1f, Mathf.Min(grazeLeft, elapsed) / GrazeEase);
        }

        /// <summary>Head tipped down 36 degrees; a quick nibbling bob while the head is fully down.</summary>
        private float GrazePitch()
        {
            const float Tilt = Mathf.PI / 5f;
            bool nibbling = grazeLeft > GrazeEase && grazeLeft <= GrazeLength - GrazeEase;
            if (!nibbling) return Tilt;
            float t = (grazeLeft - GrazeEase) / (GrazeLength - 2f * GrazeEase);
            return Tilt + 0.07f * Mathf.PI * Mathf.Sin(t * 28.7f);
        }

        protected override void DropLoot(List<ItemStack> drops, bool onFire, bool byPlayer)
        {
            AddDrop(drops, "minecraft:" + ColorName + "_wool", 1, 1);
            AddDrop(drops, Cooked(onFire, "minecraft:mutton", "minecraft:cooked_mutton"), 1, 2);
        }
    }

    // ======================================================================== chicken

    /// <summary>
    /// Chicken: flutters its wings while airborne (<see cref="WingFlutter"/>) and glides down slowly, lays an egg
    /// every 5 to 10 minutes (<see cref="EggClock"/>). Fall damage immunity comes from its definition.
    /// </summary>
    internal sealed class ChickenMob : AnimalMob
    {
        private const string EggKey = "egg";

        private Bone head, rightLeg, leftLeg, rightWing, leftWing;
        private readonly WingFlutter wings = new WingFlutter();
        private EggClock eggs;

        protected override float FleeSpeed => 1.4f;

        protected override void BuildModel()
        {
            rig = BuildRig("chicken", MobRigs.Chicken, "entity/chicken/chicken_temperate");
            head = rig.Find("head");
            rightLeg = rig.Find("right_leg");
            leftLeg = rig.Find("left_leg");
            rightWing = rig.Find("right_wing");
            leftWing = rig.Find("left_wing");
            eggs.Wind();
        }

        protected override void TickTicks(float ticks)
        {
            wings.Step(!grounded, ticks);
            if (eggs.Run(ticks)) LayEgg();
        }

        private void LayEgg()
        {
            PlaySound("entity.chicken.egg", 1f, Rng.Pitch());
            var pop = new Vector3(Rng.Range(-0.1f, 0.1f), 0.2f, Rng.Range(-0.1f, 0.1f)) * Mc.BptToMs;
            EntitiesModule.SpawnItemStatic(new ItemStack("minecraft:egg", 1), transform.position + Vector3.up * 0.2f, pop, 0.5f);
        }

        protected override void ModifyFall(ref Vector3 v)
        {
            // Slow descent: vertical speed shrinks to 60 % per tick while falling.
            if (!grounded && v.y < 0f) v.y *= Mc.PerTick(0.6f, Time.fixedDeltaTime);
        }

        protected override void Pose(PoseInput p)
        {
            PoseTerms.Look(head, p);
            PoseTerms.Pitch(rightLeg, 1.4f * PoseTerms.Gait(p, 0f));
            PoseTerms.Pitch(leftLeg, 1.4f * PoseTerms.Gait(p, Mathf.PI));
            float lift = wings.Lift;
            if (rightWing != null) rightWing.Euler.z = lift;
            if (leftWing != null) leftWing.Euler.z = -lift;
        }

        public override void Save(JsonNode o)
        {
            base.Save(o);
            o[EggKey] = JsonNode.Of(eggs.Remaining);
        }

        public override void Load(JsonNode o)
        {
            base.Load(o);
            if (o.Has(EggKey)) eggs.Remaining = o[EggKey].AsFloat(eggs.Remaining);
        }

        protected override void DropLoot(List<ItemStack> drops, bool onFire, bool byPlayer)
        {
            AddDrop(drops, "minecraft:feather", 0, 2);
            AddDrop(drops, Cooked(onFire, "minecraft:chicken", "minecraft:cooked_chicken"), 1, 1);
        }
    }
}
