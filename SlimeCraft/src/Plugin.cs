using System;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft
{
    /// <summary>
    /// SlimeCraft – Minecraft inside Slime Rancher.
    /// Bootstraps every <see cref="IModule"/> found in this assembly (see SC.cs for the architecture).
    /// </summary>
    [BepInPlugin(SC.Guid, SC.Name, SC.Version)]
    public class Plugin : BaseUnityPlugin
    {
        private Harmony harmony;

        private void Awake()
        {
            SC.Log = Logger;
            SC.Config = Config;
            SC.PluginDir = Path.GetDirectoryName(Info.Location);
            SC.DataDir = Path.Combine(Paths.ConfigPath, "SlimeCraft");
            Directory.CreateDirectory(SC.DataDir);

            Logger.LogInfo($"{SC.Name} {SC.Version} starting (Unity {Application.unityVersion})");

            var root = new GameObject("SlimeCraft");
            DontDestroyOnLoad(root);
            root.hideFlags = HideFlags.HideAndDontSave;
            SC.Root = root;

            harmony = new Harmony(SC.Guid);
            foreach (var type in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
            {
                if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { harmony.CreateClassProcessor(type).Patch(); }
                catch (Exception e) { Logger.LogError($"Harmony patch {type.FullName} failed: {e}"); }
            }

            var modules = AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly())
                .Where(t => typeof(IModule).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
                .Select(CreateModule)
                .Where(m => m != null)
                .OrderBy(m => m.Order)
                .ToList();

            foreach (var m in modules)
            {
                try
                {
                    m.Init();
                    Logger.LogInfo($"Module {m.ModuleName} initialized");
                }
                catch (Exception e)
                {
                    Logger.LogError($"Module {m.ModuleName} failed to initialize: {e}");
                }
            }
            SC.RaiseAllModulesInitialized();
            Logger.LogInfo($"{SC.Name} ready with {modules.Count} modules");
        }

        private IModule CreateModule(Type t)
        {
            try
            {
                if (typeof(MonoBehaviour).IsAssignableFrom(t))
                    return (IModule)SC.Root.AddComponent(t);
                return (IModule)Activator.CreateInstance(t);
            }
            catch (Exception e)
            {
                Logger.LogError($"Could not create module {t.FullName}: {e}");
                return null;
            }
        }

        private void OnDestroy()
        {
            // BepInEx keeps plugins alive for the whole session; nothing to tear down.
        }
    }
}
