using System;
using UnityEngine;

namespace SlimeCraft.BlocksMod
{
    /// <summary>
    /// Blocks module (Order 200): owns SC.Blocks (the voxel block world) and the player's Minecraft interaction
    /// controller. Lives on SC.Root (persistent); world objects are created only while a save is loaded.
    /// </summary>
    public sealed class BlocksModule : MonoBehaviour, IModule
    {
        public string ModuleName => "Blocks";
        public int Order => 200;

        private BlockWorld world;
        private BlockLights lights;
        private BlockParticles particles;
        private CrackOverlay crack;
        private BlockOutline outline;
        private InteractionController controller;
        private SpecialBlocks special;
        private bool hooked, persistenceRegistered, preloaded;

        public void Init()
        {
            BlocksConfig.Bind();
            world = new BlockWorld();
            lights = new BlockLights { World = world };
            particles = new BlockParticles(world);
            world.Lights = lights;
            world.Particles = particles;
            crack = new CrackOverlay(world);
            outline = new BlockOutline(world);
            controller = new InteractionController(world, particles, crack, outline);
            special = new SpecialBlocks(world);
            SC.Blocks = world;

            TryHook();
            SC.AllModulesInitialized += TryHook;
        }

        /// <summary>Subscribes to SR events and registers persistence (retried once all modules are initialized).</summary>
        private void TryHook()
        {
            try
            {
                if (!hooked && SC.SR != null)
                {
                    SC.SR.WorldUnloading += OnWorldUnloading;
                    SC.SR.WorldLoaded += OnWorldLoaded;
                    hooked = true;
                }
                if (!persistenceRegistered && SC.Persistence != null)
                {
                    SC.Persistence.Register("blocks", SaveBlocks, LoadBlocks);
                    persistenceRegistered = true;
                }
            }
            catch (Exception e) { SC.Log?.LogError("[Blocks] hook failed: " + e); }
        }

        private string SaveBlocks()
        {
            try { return BlockPersistence.Save(world); }
            catch (Exception e) { SC.Log?.LogError("[Blocks] save failed: " + e); return null; }
        }

        private void LoadBlocks(string data)
        {
            try
            {
                controller.Reset();
                special.Reset();
                BlockPersistence.Load(world, data);
            }
            catch (Exception e) { SC.Log?.LogError("[Blocks] load failed: " + e); }
        }

        private void OnWorldUnloading()
        {
            try
            {
                SC.Log?.LogInfo("[Blocks] world unloading: clearing " + world.Count + " blocks in " + world.ChunkCount + " chunks, "
                    + lights.EmitterCount + " light emitters (chunk objects, colliders, lights and overlays are rebuilt on the next load)");
                controller.Reset();
                special.Reset();
                particles.Clear();
                crack.Hide();
                outline.Hide();
                world.Clear();
                TerrainHarvest.ClearCache();
            }
            catch (Exception e) { SC.Log?.LogError("[Blocks] unload failed: " + e); }
        }

        private void OnWorldLoaded()
        {
            try
            {
                TerrainHarvest.ClearCache();
                world.MarkAllDirty();
            }
            catch (Exception e) { SC.Log?.LogError("[Blocks] world loaded handler failed: " + e); }
        }

        private void Update()
        {
            var sr = SC.SR;
            bool inGame = false;
            try { inGame = sr != null && sr.InGame; } catch (Exception e) { RateLog.Error("SR.InGame", e); }
            if (!inGame)
            {
                try { controller?.Update(); } catch (Exception e) { RateLog.Error("controller (out of game)", e); }
                return;
            }
            if (!preloaded && SC.Audio != null && SC.Audio.Ready)
            {
                preloaded = true;
                try
                {
                    SC.Audio.Preload("entity.player.attack.strong", "entity.player.attack.weak", "entity.player.attack.crit",
                        "entity.generic.eat", "entity.player.burp", "entity.arrow.shoot", "item.flintandsteel.use",
                        "block.stone.hit", "block.stone.break", "block.stone.place", "block.stone.step",
                        "block.grass.hit", "block.grass.break", "block.gravel.hit", "block.wood.hit", "block.wood.break");
                }
                catch (Exception e) { RateLog.Error("audio preload", e); }
            }
            try { world.Update(); } catch (Exception e) { RateLog.Error("world update", e); }
            try { controller.Update(); } catch (Exception e) { RateLog.Error("interaction", e); }
            try { special.Update(); } catch (Exception e) { RateLog.Error("special blocks", e); }
            try { lights.Update(); } catch (Exception e) { RateLog.Error("block lights", e); }
        }

        private void LateUpdate()
        {
            try { controller?.LateUpdate(); } catch (Exception e) { RateLog.Error("outline", e); }
            try { particles?.LateUpdate(); } catch (Exception e) { RateLog.Error("particles", e); }
        }
    }
}
