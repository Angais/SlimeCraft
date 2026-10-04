using BepInEx.Configuration;

namespace SlimeCraft.Entities
{
    /// <summary>BepInEx config entries of the Entities module (section "Entities").</summary>
    internal static class EConfig
    {
        public static ConfigEntry<bool> NaturalSpawning;
        public static ConfigEntry<int> HostileCap;
        public static ConfigEntry<int> PassiveCap;
        public static ConfigEntry<bool> SpawnOnRanch;
        public static ConfigEntry<string> PassiveZones;
        public static ConfigEntry<float> PlayerDamageScale;
        public static ConfigEntry<float> PlayerKnockbackScale;
        public static ConfigEntry<float> SRExplosionPowerPerMcPower;
        public static ConfigEntry<bool> ExplosionsBreakBlocks;
        public static ConfigEntry<bool> MobGriefing;
        public static ConfigEntry<bool> ExplosionsDestroyItems;
        public static ConfigEntry<bool> SRFoodBridge;
        public static ConfigEntry<bool> VacpackPullsMobs;
        public static ConfigEntry<bool> ScreenShake;
        public static ConfigEntry<int> MaxParticles;
        public static ConfigEntry<float> MobActiveDistance;
        public static ConfigEntry<bool> SpawnDebugSummary;

        public static void Bind()
        {
            var c = SC.Config;
            const string S = "Entities";
            NaturalSpawning = c.Bind(S, "NaturalSpawning", true, "Minecraft mobs spawn naturally (hostiles at Slime Rancher night, animals by day). In game: /mobspawning on|off.");
            HostileCap = c.Bind(S, "HostileCap", 10, "Maximum naturally spawned hostile mobs near the player.");
            PassiveCap = c.Bind(S, "PassiveCap", 8, "Maximum passive animals (pig/cow/sheep/chicken) near the player.");
            SpawnOnRanch = c.Bind(S, "SpawnOnRanch", false, "Allow HOSTILE mobs to spawn naturally on The Ranch (off: none while the player is on the ranch and never at spots inside the ranch). In game: /mobspawning ranch on|off.");
            PassiveZones = c.Bind(S, "PassiveZones", "Ranch,Moss", "Comma separated substrings of SR zone names where animals spawn by day.");
            PlayerDamageScale = c.Bind(S, "PlayerDamageScale", 5f, "Minecraft damage (half hearts, player has 20) is multiplied by this to get Slime Rancher damage (player has 100+).");
            PlayerKnockbackScale = c.Bind(S, "PlayerKnockbackScale", 0.5f, "Multiplier of Minecraft knockback (explosions, mob hits, arrows) applied to the SR player.");
            SRExplosionPowerPerMcPower = c.Bind(S, "SRExplosionPowerPerMcPower", 200f, "Slime Rancher PhysicsUtil.Explode power per Minecraft explosion power (Boom slime = 600; TNT power 4 -> 800).");
            ExplosionsBreakBlocks = c.Bind(S, "ExplosionsBreakBlocks", true, "Explosions destroy Minecraft blocks (never Slime Rancher terrain).");
            MobGriefing = c.Bind(S, "MobGriefing", true, "Creeper explosions destroy blocks.");
            ExplosionsDestroyItems = c.Bind(S, "ExplosionsDestroyItems", true, "Dropped items caught in an explosion are destroyed (as in Minecraft).");
            SRFoodBridge = c.Bind(S, "SRFoodBridge", true, "Dropped Minecraft food touching a Slime Rancher slime turns into its SR equivalent so the slime eats it.");
            VacpackPullsMobs = c.Bind(S, "VacpackPullsMobs", true, "The SR vacpack pulls Minecraft mobs (light ones fly to the nozzle).");
            ScreenShake = c.Bind(S, "ScreenShake", true, "Nearby explosions shake the SR camera.");
            MaxParticles = c.Bind(S, "MaxParticles", 1536, "Maximum simultaneous Minecraft particles (explosions, smoke, poof...).");
            MobActiveDistance = c.Bind(S, "MobActiveDistance", 80f, "Mobs farther than this from the player freeze (AI + physics) to save CPU.");
            SpawnDebugSummary = c.Bind(S, "SpawnDebugSummary", true, "Every 30 s, log a summary of natural spawn attempts and why spots were rejected (BepInEx log, [spawn-debug]).");
        }
    }
}
