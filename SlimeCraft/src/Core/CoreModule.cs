using System;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Core module (Order 0): owns SC.Assets, SC.Audio, SC.SR, SC.Input, SC.Persistence, SC.ItemVisuals and
    /// SC.Commands. Lives on SC.Root (DontDestroyOnLoad) and drives the per-frame work of those services.
    /// </summary>
    public sealed class CoreModule : MonoBehaviour, IModule
    {
        public string ModuleName => "Core";
        public int Order => 0;

        private McAssets assets;
        private McAudio audio;
        private SRBridge bridge;
        private InputGate gate;
        private SavePersistence persistence;
        private ItemVisualsService visuals;
        private CommandRegistry commands;
        private SplashText splash;
        private bool initialized;

        public void Init()
        {
            CoreConfig.Bind();

            assets = new McAssets();
            assets.Load();
            SC.Assets = assets;

            bridge = new SRBridge();
            CoreRuntime.Bridge = bridge;
            SC.SR = bridge;

            gate = new InputGate(bridge);
            CoreRuntime.Gate = gate;
            SC.Input = gate;

            persistence = new SavePersistence(bridge);
            SC.Persistence = persistence;

            audio = new McAudio(assets, this);
            audio.Init();
            SC.Audio = audio;

            visuals = new ItemVisualsService(assets);
            SC.ItemVisuals = visuals;

            commands = new CommandRegistry();
            BuiltinCommands.Register(commands, bridge);
            SC.Commands = commands;

            splash = new SplashText(assets);

            bridge.AfterWorldLoaded += OnWorldLoadedLate;
            bridge.AfterWorldUnloading += OnWorldUnloaded;
            assets.WarmUpAsync();
            initialized = true;
        }

        private void OnWorldLoadedLate()
        {
            audio.PreloadCommon();
            visuals.OnWorldLoaded();
        }

        private void OnWorldUnloaded()
        {
            gate.Reset();
            audio.StopAllWorld();
        }

        private void Update()
        {
            if (!initialized) return;
            try { bridge.Tick(); } catch (Exception e) { CoreLog.Rate("core bridge", e); }
            try { gate.Tick(); } catch (Exception e) { CoreLog.Rate("core input", e); }
            try { audio.Tick(bridge.InGame && bridge.IsPaused); } catch (Exception e) { CoreLog.Rate("core audio", e); }
            try { splash.Tick(); } catch (Exception e) { CoreLog.Rate("core splash", e); }
            try { visuals.Tick(); } catch (Exception e) { CoreLog.Rate("core item visuals", e); }
        }

        private void OnGUI()
        {
            if (!initialized) return;
            splash.OnGUI();
        }

        private void OnApplicationQuit()
        {
            if (!initialized) return;
            try { persistence.OnApplicationQuit(); } catch (Exception e) { CoreLog.Error("quit save: " + e); }
        }
    }
}
