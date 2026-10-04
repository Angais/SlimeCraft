using System;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// Global service locator. Every module talks to the others ONLY through these interfaces
    /// (modules never reference each other's concrete types). Services are assigned by their owning
    /// module during <see cref="IModule.Init"/>; consumers must tolerate a null service (module disabled
    /// or failed to start) and must not cache services before <see cref="SC.AllModulesInitialized"/> fired.
    /// </summary>
    public static class SC
    {
        public const string Guid = "com.angais.slimecraft";
        public const string Name = "SlimeCraft";
        public const string Version = "1.0.0";

        public static ManualLogSource Log;
        public static ConfigFile Config;
        /// <summary>Persistent (DontDestroyOnLoad) root object that hosts all module components.</summary>
        public static GameObject Root;
        /// <summary>Folder of the plugin dll (BepInEx/plugins/SlimeCraft).</summary>
        public static string PluginDir;
        /// <summary>Writable data folder (BepInEx/config/SlimeCraft) for caches and per-save data.</summary>
        public static string DataDir;

        // ---- Services (owner module in brackets) ----
        public static IMcAssets Assets;          // [Core]  Minecraft jar/asset-index access, textures, json, block atlas
        public static IMcAudio Audio;            // [Core]  Minecraft sound events
        public static ISRBridge SR;              // [Core]  Slime Rancher game access (player, camera, health, saves...)
        public static IInputGate Input;          // [Core]  routes input between SR and Minecraft features
        public static IPersistence Persistence;  // [Core]  per-SR-save data storage
        public static IItemVisuals ItemVisuals;  // [Core]  meshes/materials/icons for items & blocks
        public static ICommands Commands;        // [Core]  /commands registry
        public static IPlayerInventory Inventory;// [Hud]   Minecraft inventory/hotbar model + game mode
        public static IHud Hud;                  // [Hud]   Minecraft HUD, screens, chat
        public static IFirstPerson FirstPerson;  // [FirstPerson] Steve arm + held item rendering
        public static IBlockWorld Blocks;        // [Blocks] voxel block world
        public static IEntities Entities;        // [Entities] drops, TNT, mobs, projectiles
        public static IExplosions Explosions;    // [Entities] Minecraft+SR explosions

        /// <summary>Fired once after every module's Init ran (services are all assigned by now).</summary>
        public static event Action AllModulesInitialized;
        internal static void RaiseAllModulesInitialized()
        {
            try { AllModulesInitialized?.Invoke(); }
            catch (Exception e) { Log?.LogError("AllModulesInitialized handler failed: " + e); }
        }

        /// <summary>Safe invoke helper so one bad listener never breaks the caller.</summary>
        public static void Safe(Action a, string what)
        {
            try { a(); } catch (Exception e) { Log?.LogError(what + " failed: " + e); }
        }
    }

    /// <summary>
    /// A feature module. Implementations are discovered by reflection in Plugin.cs (any non-abstract class
    /// implementing IModule with a public parameterless ctor; if it is a MonoBehaviour it is added as a
    /// component on <see cref="SC.Root"/>). Init is called in ascending <see cref="Order"/>.
    /// Core = 0, Hud = 100, Blocks = 200, Entities = 300, FirstPerson = 400, Testing = 900.
    /// </summary>
    public interface IModule
    {
        string ModuleName { get; }
        int Order { get; }
        void Init();
    }
}
