using BepInEx.Configuration;
using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    /// <summary>BepInEx config entries of the Blocks module (section "Blocks").</summary>
    internal static class BlocksConfig
    {
        public static ConfigEntry<int> MeshBudgetPerFrame;
        public static ConfigEntry<int> MaxLights;
        public static ConfigEntry<float> PointLightIntensity;
        public static ConfigEntry<float> PointLightRange;
        public static ConfigEntry<int> PixelLights;
        public static ConfigEntry<bool> LogActions;
        public static ConfigEntry<string> FaceShading;
        public static ConfigEntry<int> BreakParticles;
        public static ConfigEntry<bool> HarvestSRTerrain;
        public static ConfigEntry<bool> DebugHarvest;
        public static ConfigEntry<float> SlimeBounce;
        public static ConfigEntry<float> HoneySlowdown;
        public static ConfigEntry<float> OutlineWidthPx;
        public static ConfigEntry<bool> StepSounds;
        public static ConfigEntry<float> ReachSurvival;
        public static ConfigEntry<float> ReachCreative;
        public static ConfigEntry<float> EntityReachSurvival;
        public static ConfigEntry<float> EntityReachCreative;
        public static ConfigEntry<float> LeafAppleChance;
        public static ConfigEntry<string> DropKeyHandling;

        private static bool bound;

        public static void Bind()
        {
            if (bound) return;
            bound = true;
            var c = SC.Config;
            const string S = "Blocks";
            MeshBudgetPerFrame = c.Bind(S, "MeshBudgetPerFrame", 4, "Max dirty chunks (16x16x16) re-meshed per frame (the player's own edits are always rebuilt immediately).");
            MaxLights = c.Bind(S, "MaxLights", 24, "Max Unity point lights for light-emitting blocks (the nearest ones to the camera get a light, re-evaluated every 0.5 s).");
            // (replaces the old "LightIntensity" key so the retuned default applies to existing config files)
            PointLightIntensity = c.Bind(S, "PointLightIntensity", 1.2f, "Intensity of the warm point lights of light-emitting blocks (glowstone, sea lantern...). The blocks themselves are always drawn full-bright.");
            PointLightRange = c.Bind(S, "PointLightRange", 12f, "Point light range in meters (blocks) for a light level 15 block (scaled down for dimmer blocks).");
            PixelLights = c.Bind(S, "PixelLights", 8, "How many of the nearest block lights are forced per-pixel (always visible regardless of the quality setting's pixel light count).");
            LogActions = c.Bind(S, "LogActions", true, "Write a short (rate-limited) log line for player actions: mining started, block broken/placed, placement refused, SR terrain harvested, food eaten, TNT primed, spawn eggs, bow shots.");
            FaceShading = c.Bind(S, "FaceShading", "auto", "Minecraft directional face shading baked into vertex colors: auto (only if the block material's shader shows vertex colors), on, off.");
            BreakParticles = c.Bind(S, "BreakParticles", 24, "Number of particles when a block breaks (Minecraft uses 64).");
            HarvestSRTerrain = c.Bind(S, "HarvestSRTerrain", true, "Mining Slime Rancher rocks/ground/trees with Minecraft items yields cobblestone/dirt/logs... (SR geometry is never altered).");
            DebugHarvest = c.Bind(S, "DebugHarvest", false, "Log the names used to classify each Slime Rancher surface you mine (to tune harvesting).");
            SlimeBounce = c.Bind(S, "SlimeBounce", 0.95f, "Fraction of the landing speed given back when the player lands on a slime block.");
            HoneySlowdown = c.Bind(S, "HoneySlowdown", 0.2f, "Per-tick fraction of horizontal velocity pushed back while standing on a honey block (the SR controller keeps the push for ~20 frames, so 0.2 ends near Minecraft's 0.4 speed factor).");
            OutlineWidthPx = c.Bind(S, "OutlineWidthPx", 2f, "Block selection outline width in screen pixels (<=1 uses 1px GL lines).");
            StepSounds = c.Bind(S, "StepSounds", true, "Play Minecraft footstep/fall sounds when walking on placed blocks.");
            ReachSurvival = c.Bind(S, "ReachSurvival", 4.5f, "Block interaction range in survival (Minecraft 4.5).");
            ReachCreative = c.Bind(S, "ReachCreative", 5f, "Block interaction range in creative (Minecraft 5).");
            EntityReachSurvival = c.Bind(S, "EntityReachSurvival", 3f, "Entity attack range in survival (Minecraft 3).");
            EntityReachCreative = c.Bind(S, "EntityReachCreative", 5f, "Entity attack range in creative (Minecraft 5).");
            LeafAppleChance = c.Bind(S, "LeafAppleChance", 0.05f, "Chance of an extra apple when harvesting Slime Rancher tree leaves.");
            DropKeyHandling = c.Bind(S, "DropKeyHandling", "auto", "Who throws the selected item on the drop key: auto (this module, unless [Hud] HandleDropKey = true), on (always this module), off.");
        }
    }
}
