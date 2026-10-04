namespace SlimeCraft.Entities.Model
{
    /// <summary>
    /// Rig sheets (see <see cref="RigSheet"/> for the format) for every mob and for the arrow. The shapes and skin
    /// layouts match the Minecraft mobs so the player's own textures, loaded at runtime from their Minecraft install,
    /// map exactly. Pixels, +Y down, facing -Z; for living mobs rig y = 24 is the ground. Rest rotations are part of
    /// the bones because the pose code starts every frame from the rest pose.
    /// </summary>
    internal static class MobRigs
    {
        public static RigSpec Zombie() => RigSheet.Parse("zombie", ZombieSheet);
        public static RigSpec Skeleton() => RigSheet.Parse("skeleton", SkeletonSheet);
        public static RigSpec Enderman() => RigSheet.Parse("enderman", EndermanSheet);
        public static RigSpec Creeper() => RigSheet.Parse("creeper", CreeperSheet);
        public static RigSpec Pig() => RigSheet.Parse("pig", PigSheet);
        public static RigSpec Cow() => RigSheet.Parse("cow", CowSheet);
        public static RigSpec Sheep() => RigSheet.Parse("sheep", SheepSheet);
        public static RigSpec SheepFur() => RigSheet.Parse("sheep_fur", SheepFurSheet);
        public static RigSpec Chicken() => RigSheet.Parse("chicken", ChickenSheet);
        public static RigSpec SlimeInner() => RigSheet.Parse("slime_inner", SlimeInnerSheet);
        public static RigSpec SlimeOuter() => RigSheet.Parse("slime_outer", SlimeOuterSheet);
        public static RigSpec IronGolem() => RigSheet.Parse("iron_golem", IronGolemSheet);
        public static RigSpec Arrow() => RigSheet.Parse("arrow", ArrowSheet);

        // ------------------------------------------------------------------ two-legged mobs

        private const string ZombieSheet = @"
skin 64 64
bone head
  box  0  0  from -4 -8 -4  size 8 8 8
bone hat on head
  box 32  0  from -4 -8 -4  size 8 8 8   pad 0.5
bone body
  box 16 16  from -4 0 -2   size 8 12 4
bone right_arm at -5 2 0
  box 40 16  from -3 -2 -2  size 4 12 4
bone left_arm at 5 2 0
  box 40 16  from -1 -2 -2  size 4 12 4  flip
bone right_leg at -1.9 12 0
  box  0 16  from -2 0 -2   size 4 12 4
bone left_leg at 1.9 12 0
  box  0 16  from -2 0 -2   size 4 12 4  flip
";

        private const string SkeletonSheet = @"
skin 64 32
bone head
  box  0  0  from -4 -8 -4  size 8 8 8
bone hat on head
  box 32  0  from -4 -8 -4  size 8 8 8   pad 0.5
bone body
  box 16 16  from -4 0 -2   size 8 12 4
bone right_arm at -5 2 0
  box 40 16  from -1 -2 -1  size 2 12 2
bone left_arm at 5 2 0
  box 40 16  from -1 -2 -1  size 2 12 2  flip
bone right_leg at -2 12 0
  box  0 16  from -1 0 -1   size 2 12 2
bone left_leg at 2 12 0
  box  0 16  from -1 0 -1   size 2 12 2  flip
";

        // The 'hat' is a slightly shrunk inner head carrying the jaw texture. The angry face lifts the head and
        // lowers this layer, so it hangs under the head. The legs end one pixel below the ground (y = 25).
        private const string EndermanSheet = @"
skin 64 32
bone head at 0 -13 0
  box  0  0  from -4 -8 -4  size 8 8 8
bone hat on head
  box  0 16  from -4 -8 -4  size 8 8 8   pad -0.5
bone body at 0 -14 0
  box 32 16  from -4 0 -2   size 8 12 4
bone right_arm at -5 -12 0
  box 56  0  from -1 -2 -1  size 2 30 2
bone left_arm at 5 -12 0
  box 56  0  from -1 -2 -1  size 2 30 2  flip
bone right_leg at -2 -5 0
  box 56  0  from -1 0 -1   size 2 30 2
bone left_leg at 2 -5 0
  box 56  0  from -1 0 -1   size 2 30 2  flip
";

        // ------------------------------------------------------------------ creeper

        private const string CreeperSheet = @"
skin 64 32
bone head at 0 6 0
  box  0  0  from -4 -8 -4  size 8 8 8
bone body at 0 6 0
  box 16 16  from -4 0 -2   size 8 12 4
bone right_hind_leg at -2 18 4
  box  0 16  from -2 0 -2   size 4 6 4
bone left_hind_leg at 2 18 4
  box  0 16  from -2 0 -2   size 4 6 4
bone right_front_leg at -2 18 -4
  box  0 16  from -2 0 -2   size 4 6 4
bone left_front_leg at 2 18 -4
  box  0 16  from -2 0 -2   size 4 6 4
";

        // ------------------------------------------------------------------ farm animals

        private const string PigSheet = @"
skin 64 64
bone head at 0 12 -6
  box  0  0  from -4 -4 -8  size 8 8 8
  box 16 16  from -2 0 -9   size 4 3 1             # snout
bone body at 0 11 2 turn 90 0 0
  box 28  8  from -5 -10 -7 size 10 16 8
bone right_hind_leg at -3 18 7
  box  0 16  from -2 0 -2   size 4 6 4
bone left_hind_leg at 3 18 7
  box  0 16  from -2 0 -2   size 4 6 4   flip
bone right_front_leg at -3 18 -5
  box  0 16  from -2 0 -2   size 4 6 4
bone left_front_leg at 3 18 -5
  box  0 16  from -2 0 -2   size 4 6 4   flip
";

        private const string CowSheet = @"
skin 64 64
bone head at 0 4 -8
  box  0  0  from -4 -4 -6  size 8 8 6             # skull
  box  1 33  from -3 1 -7   size 6 3 1             # muzzle
  box 22  0  from -5 -5 -5  size 1 3 1             # right horn
  box 22  0  from 4 -5 -5   size 1 3 1             # left horn
bone body at 0 5 2 turn 90 0 0
  box 18  4  from -6 -10 -7 size 12 18 10          # trunk
  box 52  0  from -2 2 -8   size 4 6 1             # udder
bone right_hind_leg at -4 12 7
  box  0 16  from -2 0 -2   size 4 12 4
bone left_hind_leg at 4 12 7
  box  0 16  from -2 0 -2   size 4 12 4  flip
bone right_front_leg at -4 12 -5
  box  0 16  from -2 0 -2   size 4 12 4
bone left_front_leg at 4 12 -5
  box  0 16  from -2 0 -2   size 4 12 4  flip
";

        // Unlike the pig and the cow, the sheep mirrors its right legs.
        private const string SheepSheet = @"
skin 64 32
bone head at 0 6 -8
  box  0  0  from -3 -4 -6  size 6 6 8
bone body at 0 5 2 turn 90 0 0
  box 28  8  from -4 -10 -7 size 8 16 6
bone right_hind_leg at -3 12 7
  box  0 16  from -2 0 -2   size 4 12 4  flip
bone left_hind_leg at 3 12 7
  box  0 16  from -2 0 -2   size 4 12 4
bone right_front_leg at -3 12 -5
  box  0 16  from -2 0 -2   size 4 12 4  flip
bone left_front_leg at 3 12 -5
  box  0 16  from -2 0 -2   size 4 12 4
";

        // The wool coat follows the sheep's pose bone by bone, so the bone names must match the sheep's.
        // It covers only the upper half of each leg (6 of the 12 pixels).
        private const string SheepFurSheet = @"
skin 64 32
bone head at 0 6 -8
  box  0  0  from -3 -4 -4  size 6 6 6   pad 0.6
bone body at 0 5 2 turn 90 0 0
  box 28  8  from -4 -10 -7 size 8 16 6  pad 1.75
bone right_hind_leg at -3 12 7
  box  0 16  from -2 0 -2   size 4 6 4   pad 0.5
bone left_hind_leg at 3 12 7
  box  0 16  from -2 0 -2   size 4 6 4   pad 0.5
bone right_front_leg at -3 12 -5
  box  0 16  from -2 0 -2   size 4 6 4   pad 0.5
bone left_front_leg at 3 12 -5
  box  0 16  from -2 0 -2   size 4 6 4   pad 0.5
";

        // Beak and wattle hang under the head so they turn with it.
        private const string ChickenSheet = @"
skin 64 32
bone head at 0 15 -4
  box  0  0  from -2 -6 -2  size 4 6 3
bone beak on head
  box 14  0  from -2 -4 -4  size 4 2 2
bone wattle on head
  box 14  4  from -1 -2 -3  size 2 2 2
bone body at 0 16 0 turn 90 0 0
  box  0  9  from -3 -4 -3  size 6 8 6
bone right_leg at -2 19 1
  box 26  0  from -1 0 -3   size 3 5 3
bone left_leg at 1 19 1
  box 26  0  from -1 0 -3   size 3 5 3
bone right_wing at -4 13 0
  box 24 13  from 0 0 -3    size 1 4 6
bone left_wing at 4 13 0
  box 24 13  from -1 0 -3   size 1 4 6
";

        // ------------------------------------------------------------------ slime

        // Inner core, eyes and mouth: the boxes sit directly in rig space under bones without offset. The core
        // floats one pixel above the ground and the face details stick out half a pixel in front of it.
        private const string SlimeInnerSheet = @"
skin 64 32
bone cube
  box  0 16  from -3 17 -3        size 6 6 6
bone right_eye
  box 32  0  from -3.25 18 -3.5   size 2 2 2
bone left_eye
  box 32  4  from 1.25 18 -3.5    size 2 2 2
bone mouth
  box 32  8  from 0 21 -3.5       size 1 1 1
";

        // Translucent shell; its bone is called 'cube' as well so it can follow the inner rig by name.
        private const string SlimeOuterSheet = @"
skin 64 32
bone cube
  box  0  0  from -4 16 -4  size 8 8 8
";

        // ------------------------------------------------------------------ iron golem

        // Both arms pivot on the body's centre line at shoulder height, so a swing sweeps a wide arc. The arms use
        // different skin regions, as do the legs (the left leg is mirrored as well).
        private const string IronGolemSheet = @"
skin 128 128
bone head at 0 -7 -2
  box  0  0  from -4 -12 -5.5   size 8 10 8        # skull
  box 24  0  from -1 -5 -7.5    size 2 4 2         # nose
bone body at 0 -7 0
  box  0 40  from -9 -2 -6      size 18 12 11      # chest
  box  0 70  from -4.5 10 -3    size 9 5 6  pad 0.5  # waist
bone right_arm at 0 -7 0
  box 60 21  from -13 -2.5 -3   size 4 30 6
bone left_arm at 0 -7 0
  box 60 58  from 9 -2.5 -3     size 4 30 6
bone right_leg at -4 11 0
  box 37  0  from -3.5 -3 -3    size 6 16 5
bone left_leg at 5 11 0
  box 60  0  from -3.5 -3 -3    size 6 16 5  flip
";

        // ------------------------------------------------------------------ arrow

        // Static rig, flattened into one mesh. Tip towards +X, fletching towards -X. The end cap is a 5x5 card
        // turned 45 degrees about the shaft and shrunk to 0.8; two 16x4 cards at 45 and 135 degrees form the
        // X-shaped shaft and sample a 5-texel strip through the 0.8 vertical skin stretch. The whole rig is scaled
        // by 0.9 at its origin.
        private const string ArrowSheet = @"
skin 32 32
origin-scale 0.9
bone back at -11 0 0 turn 45 0 0 scale 0.8
  box 0 0  from 0 -2.5 -2.5  size 0 5 5
bone cross_1 turn 45 0 0
  box 0 0  from -12 -2 0     size 16 4 0  stretch-v 0.8
bone cross_2 turn 135 0 0
  box 0 0  from -12 -2 0     size 16 4 0  stretch-v 0.8
";
    }
}
