using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Shows/hides Slime Rancher's first-person vacpack (WeaponVacuum renderers) and blocks its Update while a
    /// Minecraft item is selected (see SRPatches: WeaponVacuum.Update prefix). On deactivation the vac is
    /// cleanly released (held object dropped via ClearVac, gadget mode exited, targeting cleared).
    /// </summary>
    internal sealed class SRVacpack
    {
        private static readonly System.Reflection.FieldInfo VacModeField = AccessTools.Field(typeof(WeaponVacuum), "vacMode");
        private static readonly System.Reflection.MethodInfo ClearVacMethod = AccessTools.Method(typeof(WeaponVacuum), "ClearVac");

        private WeaponVacuum vac;
        private Renderer[] renderers = new Renderer[0];
        private float nextRendererScan;
        private readonly List<Renderer> hidden = new List<Renderer>();
        private bool wantActive = true;
        private bool suppress;
        private bool released; // ClearVac already run for the current blocked period

        public bool Active => wantActive;
        public WeaponVacuum Vac => vac;

        /// <summary>True when WeaponVacuum.Update must not run.</summary>
        public bool Blocked => !wantActive || suppress;

        public void Bind(WeaponVacuum v)
        {
            vac = v;
            hidden.Clear();
            released = false;
            try
            {
                string path = v.name;
                for (var t = v.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
                CoreLog.Info("Vacpack: WeaponVacuum at '" + path + "' with " + v.GetComponentsInChildren<Renderer>(true).Length + " renderers");
            }
            catch { }
            Apply();
        }

        public void Unbind()
        {
            vac = null;
            hidden.Clear();
            renderers = new Renderer[0];
        }

        /// <summary>
        /// Per frame while hidden: re-disables renderers SR (animator/FX) may have re-enabled. The renderer list is
        /// rescanned every 2 s so newly spawned children are caught; otherwise this does not allocate.
        /// </summary>
        public void Enforce()
        {
            if (vac == null || wantActive) return;
            try
            {
                if (Time.unscaledTime >= nextRendererScan)
                {
                    nextRendererScan = Time.unscaledTime + 2f;
                    renderers = vac.GetComponentsInChildren<Renderer>(true);
                }
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null || !r.enabled) continue;
                    r.enabled = false;
                    if (!hidden.Contains(r)) hidden.Add(r);
                }
            }
            catch (Exception e) { CoreLog.Rate("vacpack enforce", e); }
        }

        public void SetActive(bool active)
        {
            if (wantActive == active) return;
            wantActive = active;
            Apply();
        }

        public void SetSuppressed(bool s)
        {
            if (suppress == s) return;
            suppress = s;
            if (!s) released = false;
        }

        private void Apply()
        {
            if (vac == null) return;
            try
            {
                if (!wantActive)
                {
                    Release();
                    renderers = vac.GetComponentsInChildren<Renderer>(true);
                    nextRendererScan = Time.unscaledTime + 2f;
                    foreach (var r in renderers)
                    {
                        if (r == null || !r.enabled) continue;
                        r.enabled = false;
                        if (!hidden.Contains(r)) hidden.Add(r);
                    }
                }
                else
                {
                    foreach (var r in hidden) if (r != null) r.enabled = true;
                    hidden.Clear();
                    released = false;
                }
            }
            catch (Exception e) { CoreLog.Rate("vacpack apply", e); }
        }

        /// <summary>Called from the WeaponVacuum.Update prefix while blocked: releases the vac once.</summary>
        public void OnBlockedUpdate(WeaponVacuum v)
        {
            if (released) return;
            released = true;
            Release(v);
        }

        private void Release(WeaponVacuum v = null)
        {
            v = v ?? vac;
            if (v == null) return;
            try
            {
                if (v.InGadgetMode())
                {
                    SRSingleton<Overlay>.Instance?.SetEnableGadgetMode(false);
                    var ps = SRSingleton<SceneContext>.Instance?.PlayerState;
                    if (ps != null) ps.InGadgetMode = false;
                }
                if (VacModeField != null) VacModeField.SetValue(v, Enum.ToObject(VacModeField.FieldType, 0)); // VacMode.NONE
                ClearVacMethod?.Invoke(v, null); // drops held object, stops vac audio/fx, releases joints
                var player = SRSingleton<SceneContext>.Instance?.PlayerState;
                if (player != null)
                {
                    player.PointedAtVaccable = false;
                    player.Targeting = null;
                }
            }
            catch (Exception e) { CoreLog.Rate("vacpack release", e); }
        }

        public bool InVacMode => vac != null && wantActive && !suppress && vac.InVacMode();

        public Ray VacRay
        {
            get
            {
                if (vac == null || vac.vacOrigin == null) return new Ray(Vector3.zero, Vector3.forward);
                var t = vac.vacOrigin.transform;
                return new Ray(t.position, t.up);
            }
        }
    }
}
