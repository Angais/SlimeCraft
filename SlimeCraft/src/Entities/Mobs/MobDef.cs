using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.Entities
{
    /// <summary>
    /// Fixed stats of a mob type: health, hitbox and eye height, movement speed, attack, follow range and sounds. The
    /// numbers match the stats the Minecraft mobs are documented with, so fights and chases feel the same.
    /// </summary>
    internal sealed class MobDef
    {
        public string Id;
        public bool Hostile;
        public float MaxHealth;
        public float Width, Height, EyeHeight;
        /// <summary>movement_speed attribute (ground speed = 44*(attr*modifier)^2 m/s, see Mc.WalkSpeed).</summary>
        public float SpeedAttr;
        public float Mass = 1f;
        public string AmbientSound, HurtSound, DeathSound, StepSound;
        public float StepVolume = 0.15f;
        public float SoundVolume = 1f;
        public int AmbientInterval = 80;
        public float FollowRange = 16f;
        public float AttackDamage;
        public float KnockbackResistance;
        public bool FallImmune;
        public bool BurnsInDaylight;
        /// <summary>Saved with the SR game (passive animals and golems; hostiles are not saved, like Minecraft despawning).</summary>
        public bool Persistent;
        public Func<GameObject, McMob> AddComponent;

        public static readonly Dictionary<string, MobDef> ById = new Dictionary<string, MobDef>();

        public static MobDef Get(string id)
        {
            if (id == null) return null;
            id = Content.Norm(id.Trim().ToLowerInvariant());
            return ById.TryGetValue(id, out var d) ? d : null;
        }

        private static void Add(MobDef d) { ById[d.Id] = d; }

        static MobDef()
        {
            Add(new MobDef
            {
                Id = MobIds.Zombie, Hostile = true, MaxHealth = 20, Width = 0.6f, Height = 1.95f, EyeHeight = 1.74f, SpeedAttr = 0.23f, Mass = 1.5f,
                AmbientSound = "entity.zombie.ambient", HurtSound = "entity.zombie.hurt", DeathSound = "entity.zombie.death", StepSound = "entity.zombie.step",
                FollowRange = 35f, AttackDamage = 3f, BurnsInDaylight = true, AddComponent = g => g.AddComponent<ZombieMob>()
            });
            Add(new MobDef
            {
                Id = MobIds.Skeleton, Hostile = true, MaxHealth = 20, Width = 0.6f, Height = 1.99f, EyeHeight = 1.74f, SpeedAttr = 0.25f, Mass = 1.2f,
                AmbientSound = "entity.skeleton.ambient", HurtSound = "entity.skeleton.hurt", DeathSound = "entity.skeleton.death", StepSound = "entity.skeleton.step",
                FollowRange = 16f, AttackDamage = 2f, BurnsInDaylight = true, AddComponent = g => g.AddComponent<SkeletonMob>()
            });
            Add(new MobDef
            {
                Id = MobIds.Creeper, Hostile = true, MaxHealth = 20, Width = 0.6f, Height = 1.7f, EyeHeight = 1.445f, SpeedAttr = 0.25f, Mass = 1.2f,
                AmbientSound = null, HurtSound = "entity.creeper.hurt", DeathSound = "entity.creeper.death", StepSound = null,
                FollowRange = 16f, AddComponent = g => g.AddComponent<CreeperMob>()
            });
            Add(new MobDef
            {
                Id = MobIds.Pig, MaxHealth = 10, Width = 0.9f, Height = 0.9f, EyeHeight = 0.765f, SpeedAttr = 0.25f, Mass = 1.0f,
                AmbientSound = "entity.pig.ambient", HurtSound = "entity.pig.hurt", DeathSound = "entity.pig.death", StepSound = "entity.pig.step",
                Persistent = true, AddComponent = g => g.AddComponent<PigMob>()
            });
            Add(new MobDef
            {
                Id = MobIds.Cow, MaxHealth = 10, Width = 0.9f, Height = 1.4f, EyeHeight = 1.3f, SpeedAttr = 0.2f, Mass = 2.0f,
                AmbientSound = "entity.cow.ambient", HurtSound = "entity.cow.hurt", DeathSound = "entity.cow.death", StepSound = "entity.cow.step",
                SoundVolume = 0.4f, Persistent = true, AddComponent = g => g.AddComponent<CowMob>()
            });
            Add(new MobDef
            {
                Id = MobIds.Sheep, MaxHealth = 8, Width = 0.9f, Height = 1.3f, EyeHeight = 1.235f, SpeedAttr = 0.23f, Mass = 1.2f,
                AmbientSound = "entity.sheep.ambient", HurtSound = "entity.sheep.hurt", DeathSound = "entity.sheep.death", StepSound = "entity.sheep.step",
                Persistent = true, AddComponent = g => g.AddComponent<SheepMob>()
            });
            Add(new MobDef
            {
                Id = MobIds.Chicken, MaxHealth = 4, Width = 0.4f, Height = 0.7f, EyeHeight = 0.644f, SpeedAttr = 0.25f, Mass = 0.3f,
                AmbientSound = "entity.chicken.ambient", HurtSound = "entity.chicken.hurt", DeathSound = "entity.chicken.death", StepSound = "entity.chicken.step",
                FallImmune = true, Persistent = true, AddComponent = g => g.AddComponent<ChickenMob>()
            });
            Add(new MobDef
            {
                Id = MobIds.Slime, Hostile = true, MaxHealth = 1, Width = 0.52f, Height = 0.52f, EyeHeight = 0.325f, SpeedAttr = 0.3f, Mass = 0.5f,
                AmbientSound = null, HurtSound = "entity.slime.hurt", DeathSound = "entity.slime.death", StepSound = null,
                FollowRange = 16f, AttackDamage = 1f, AddComponent = g => g.AddComponent<SlimeMob>()
            });
            Add(new MobDef
            {
                Id = MobIds.Enderman, Hostile = true, MaxHealth = 40, Width = 0.6f, Height = 2.9f, EyeHeight = 2.55f, SpeedAttr = 0.3f, Mass = 2.0f,
                AmbientSound = "entity.enderman.ambient", HurtSound = "entity.enderman.hurt", DeathSound = "entity.enderman.death", StepSound = null,
                FollowRange = 64f, AttackDamage = 7f, AddComponent = g => g.AddComponent<EndermanMob>()
            });
            Add(new MobDef
            {
                Id = MobIds.IronGolem, MaxHealth = 100, Width = 1.4f, Height = 2.7f, EyeHeight = 2.295f, SpeedAttr = 0.25f, Mass = 10f,
                AmbientSound = null, HurtSound = "entity.iron_golem.hurt", DeathSound = "entity.iron_golem.death", StepSound = "entity.iron_golem.step",
                StepVolume = 1f, FollowRange = 16f, AttackDamage = 15f, KnockbackResistance = 1f, FallImmune = true, Persistent = true,
                AddComponent = g => g.AddComponent<IronGolemMob>()
            });
        }
    }
}
