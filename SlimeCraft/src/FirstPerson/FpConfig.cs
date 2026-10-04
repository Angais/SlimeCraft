using System;
using BepInEx.Configuration;

namespace SlimeCraft.FP
{
    /// <summary>BepInEx config entries of the FirstPerson module (section "FirstPerson").</summary>
    internal sealed class FpConfig
    {
        private const string S = "FirstPerson";

        public readonly ConfigEntry<bool> Enabled;
        public readonly ConfigEntry<float> HandFov;
        public readonly ConfigEntry<bool> ViewBobbing;
        public readonly ConfigEntry<bool> RespectSRCameraBobOption;
        public readonly ConfigEntry<float> HurtTilt;
        public readonly ConfigEntry<bool> HandSway;
        public readonly ConfigEntry<float> MiningShake;
        public readonly ConfigEntry<string> SkinTexture;
        public readonly ConfigEntry<bool> ShowSleeve;
        public readonly ConfigEntry<bool> EatParticles;
        public readonly ConfigEntry<bool> EatSounds;
        public readonly ConfigEntry<bool> BowFovZoom;
        public readonly ConfigEntry<int> HandLayer;
        public readonly ConfigEntry<float> CameraDepthOverride;
        public readonly ConfigEntry<bool> AutoOrientItemMeshes;

        /// <summary>Raised when an entry that affects built meshes/materials changed (arm rebuild).</summary>
        public event Action ArmSettingsChanged;

        public FpConfig()
        {
            var c = SC.Config;
            Enabled = c.Bind(S, "Enabled", true, "Render Steve's first-person arm and the held Minecraft item.");
            HandFov = c.Bind(S, "HandFov", 70f, "Vertical field of view of the hand camera. Minecraft always renders the hand with 70 regardless of the FOV option.");
            ViewBobbing = c.Bind(S, "ViewBobbing", true, "Minecraft 'View Bobbing' applied to the hand/item while walking.");
            RespectSRCameraBobOption = c.Bind(S, "RespectSRCameraBobOption", false, "If true, hand bobbing is disabled whenever Slime Rancher's own 'disable camera bob' option is on.");
            HurtTilt = c.Bind(S, "HurtTilt", 1f, "Minecraft 'Damage Tilt' strength applied to the hand when the player takes damage (0 = off, 1 = same as Minecraft).");
            HandSway = c.Bind(S, "HandSway", true, "Hand lags slightly behind fast camera rotation, like in Minecraft.");
            MiningShake = c.Bind(S, "MiningShake", 1f, "Strength of the subtle hand shake while mining a block (0 = off). A SlimeCraft extra, not part of Minecraft.");
            SkinTexture = c.Bind(S, "SkinTexture", "entity/player/wide/steve", "Minecraft texture used for the arm (e.g. entity/player/wide/alex, entity/player/slim/alex). Paths containing '/slim/' use the 3px slim arm.");
            ShowSleeve = c.Bind(S, "ShowSleeve", true, "Render the sleeve overlay layer (Minecraft skin customization 'Right Sleeve').");
            EatParticles = c.Bind(S, "EatParticles", true, "Spawn Minecraft item-crumb particles while eating.");
            EatSounds = c.Bind(S, "EatSounds", false, "Play the Minecraft eating/drinking sounds from this module (keep off if the interaction module already plays them).");
            BowFovZoom = c.Bind(S, "BowFovZoom", true, "Zoom Slime Rancher's camera FOV while drawing a bow like Minecraft (up to -15%).");
            HandLayer = c.Bind(S, "HandLayer", -1, "Unity layer (8-31) used for the hand renderers. -1 = pick an unused layer automatically at runtime (logged).");
            CameraDepthOverride = c.Bind(S, "CameraDepthOverride", -1000f, "Depth of the hand camera. -1000 = automatic (just above Slime Rancher's main and weapon cameras, below any later UI camera).");
            AutoOrientItemMeshes = c.Bind(S, "AutoOrientItemMeshes", true, "Inspect item sprite meshes once and rotate them so the texture faces like Minecraft's (guards against mesh convention differences).");

            SkinTexture.SettingChanged += (s, e) => ArmSettingsChanged?.Invoke();
            ShowSleeve.SettingChanged += (s, e) => ArmSettingsChanged?.Invoke();
        }
    }
}
