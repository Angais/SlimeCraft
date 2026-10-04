using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft.Core
{
    // All Core Harmony patches. Every body is wrapped in try/catch and
    // falls back to SR's original behaviour on error. Patches are applied by Plugin.cs before modules Init, so all
    // state they read lives in CoreRuntime (null-safe).

    /// <summary>Runtime state shared between the Core services and the static patch classes.</summary>
    internal static class CoreRuntime
    {
        public static SRBridge Bridge;
        public static InputGate Gate;

        /// <summary>Open Slime Rancher BaseUI windows (map, pedia, shops, ...).</summary>
        public static readonly HashSet<BaseUI> OpenUIs = new HashSet<BaseUI>();
        private static readonly List<BaseUI> scratch = new List<BaseUI>();

        public static bool AnySRWindowOpen()
        {
            if (OpenUIs.Count == 0) return false;
            bool any = false;
            scratch.Clear();
            foreach (var ui in OpenUIs)
            {
                if (ui == null) { scratch.Add(ui); continue; }
                if (ui.isActiveAndEnabled) any = true;
            }
            foreach (var dead in scratch) OpenUIs.Remove(dead);
            return any;
        }
    }

    /// <summary>WeaponVacuum.Update: skipped while a Minecraft item is selected.</summary>
    [HarmonyPatch(typeof(WeaponVacuum), nameof(WeaponVacuum.Update))]
    internal static class Patch_WeaponVacuum_Update
    {
        private static bool Prefix(WeaponVacuum __instance)
        {
            try
            {
                var vp = CoreRuntime.Bridge?.Vacpack;
                if (vp == null || !vp.Blocked) return true;
                vp.OnBlockedUpdate(__instance);
                return false;
            }
            catch (Exception e) { CoreLog.Rate("vac update patch", e); return true; }
        }
    }

    /// <summary>
    /// WeaponVacuum.UpdateSlotForInputs (private): SR's 1-5 slot keys are swallowed
    /// (the Minecraft hotbar uses 1-9); the mouse-wheel prev/next branch is left to SR.
    /// </summary>
    [HarmonyPatch(typeof(WeaponVacuum), "UpdateSlotForInputs")]
    internal static class Patch_WeaponVacuum_UpdateSlotForInputs
    {
        private static bool Prefix()
        {
            try
            {
                if (CoreConfig.SuppressSRSlotKeys == null || !CoreConfig.SuppressSRSlotKeys.Value) return true;
                var a = SRInput.Actions;
                if (a == null) return true;
                // skip the whole method on a frame where a slot key fired; at worst a wheel tick that
                // happened in that same frame is lost
                return !(a.slot1.WasPressed || a.slot2.WasPressed || a.slot3.WasPressed || a.slot4.WasPressed || a.slot5.WasPressed);
            }
            catch (Exception e) { CoreLog.Rate("slot keys patch", e); return true; }
        }
    }

    /// <summary>PauseMenu.Update: Escape closes our screens instead of opening SR's pause menu.</summary>
    [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.Update))]
    internal static class Patch_PauseMenu_Update
    {
        private static bool Prefix()
        {
            try
            {
                var g = CoreRuntime.Gate;
                if (g == null) return true;
                return !g.ShouldBlockPauseMenu();
            }
            catch (Exception e) { CoreLog.Rate("pause patch", e); return true; }
        }
    }

    /// <summary>AutoSaveDirector.SaveGame (private): every SR save path ends here.</summary>
    [HarmonyPatch(typeof(AutoSaveDirector), "SaveGame")]
    internal static class Patch_AutoSaveDirector_SaveGame
    {
        private static void Prefix(AutoSaveDirector __instance)
        {
            try
            {
                if (__instance != null && !__instance.IsLoadingGame()) CoreRuntime.Bridge?.RaiseSaving();
            }
            catch (Exception e) { CoreLog.Rate("save patch", e); }
        }
    }

    /// <summary>SceneContext.OnSessionEnded: quit to menu / game over / app quit, world still intact.</summary>
    [HarmonyPatch(typeof(SceneContext), nameof(SceneContext.OnSessionEnded))]
    internal static class Patch_SceneContext_OnSessionEnded
    {
        private static void Prefix()
        {
            try { CoreRuntime.Bridge?.RaiseWorldUnloading("session ended"); }
            catch (Exception e) { CoreLog.Rate("session end patch", e); }
        }
    }

    /// <summary>BaseUI.Awake: track open SR windows (subclasses call base.Awake()).</summary>
    [HarmonyPatch(typeof(BaseUI), nameof(BaseUI.Awake))]
    internal static class Patch_BaseUI_Awake
    {
        private static void Postfix(BaseUI __instance)
        {
            try { if (__instance != null) CoreRuntime.OpenUIs.Add(__instance); }
            catch (Exception e) { CoreLog.Rate("baseui awake patch", e); }
        }
    }

    /// <summary>BaseUI.OnDestroy: stop tracking the window.</summary>
    [HarmonyPatch(typeof(BaseUI), nameof(BaseUI.OnDestroy))]
    internal static class Patch_BaseUI_OnDestroy
    {
        private static void Postfix(BaseUI __instance)
        {
            try { CoreRuntime.OpenUIs.Remove(__instance); }
            catch (Exception e) { CoreLog.Rate("baseui destroy patch", e); }
        }
    }

    /// <summary>
    /// PlayerState.CanBeDamaged refuses damage unless SR's input mode is DEFAULT. Our Minecraft screens
    /// (inventory/chat) switch SR input to NONE, which would make the player invulnerable; like Minecraft you
    /// can still get hurt with the inventory open. We only flip the answer to true when our own screen holds the
    /// input mode and the game is otherwise live (not paused, not fast-forwarding, not locked by a death sequence).
    /// </summary>
    [HarmonyPatch(typeof(PlayerState), nameof(PlayerState.CanBeDamaged))]
    internal static class Patch_PlayerState_CanBeDamaged
    {
        private static void Postfix(ref bool __result)
        {
            try
            {
                if (__result) return;
                var g = CoreRuntime.Gate;
                if (g == null || !g.HoldsSRInputMode) return;
                if (SRInput.Instance.GetInputMode() != SRInput.InputMode.NONE) return;
                if (Time.timeScale <= 0f) return;
                var ctx = SRSingleton<SceneContext>.Instance;
                if (ctx == null || ctx.TimeDirector == null || ctx.TimeDirector.IsFastForwarding()) return;
                var lod = SRSingleton<LockOnDeath>.Instance;
                if (lod != null && lod.Locked()) return;
                __result = true;
            }
            catch (Exception e) { CoreLog.Rate("damage patch", e); }
        }
    }
}

namespace SlimeCraft.Core
{
    /// <summary>
    /// PlayerState.Damage(int, GameObject) is the single place where the player loses
    /// health (PlayerDamageable.Damage → PlayerState.Damage). Raises ISRBridge.PlayerDamaged with the health actually lost.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(PlayerState), nameof(PlayerState.Damage))]
    internal static class Patch_PlayerState_Damage
    {
        private static void Prefix(PlayerState __instance, out int __state)
        {
            __state = -1;
            try { if (__instance != null) __state = __instance.GetCurrHealth(); }
            catch { __state = -1; }
        }

        private static void Postfix(PlayerState __instance, GameObject source, int __state)
        {
            try
            {
                if (__state < 0 || __instance == null) return;
                int lost = __state - __instance.GetCurrHealth();
                if (lost > 0) CoreRuntime.Bridge?.RaisePlayerDamaged(lost, source);
            }
            catch (System.Exception e) { CoreLog.Rate("damage event patch", e); }
        }
    }
}
