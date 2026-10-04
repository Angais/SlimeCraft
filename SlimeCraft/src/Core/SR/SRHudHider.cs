using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SlimeCraft.Core
{
    /// <summary>
    /// Hides Slime Rancher's own HUD elements while the Minecraft HUD is active, without disabling them: a CanvasGroup
    /// (alpha 0, no raycasts) is put on each element so SR keeps updating everything and restoring is instant.
    ///
    /// SR's HUD (scene 'worldGenerated', HUD Root/HudUI/UIContainer) is FLAT: every element is a direct child of
    /// UIContainer — crossHair, RadarPanel, CurrentDay, CurrentTime, TimeIcon, MailIcon, PartnerArea, CurrencyIcon,
    /// Currency, KeysIcon, Keys, HealthIcon, Health Meter, EnergyIcon, Energy Meter, Ammo Slots, RadIcon, Rad Meter,
    /// Targeting, SaveIndicator, Debug (dumped from the game's level3 asset). So each element is targeted precisely:
    /// HudUI's own fields, the meter/crosshair/ammo components, and the icon siblings by name.
    ///
    /// Hidden: clock/day/weather icon, mail, partner level, keys, currency (+ coin), health/energy/rad meters (+ icons,
    /// labels), crosshair, ammo slots. Kept: Popups canvas (mail/pedia/achievement/tutorial popups), RadarPanel,
    /// Targeting (what you point at), SaveIndicator, Debug, DeathObscurer, the glitch-region exit HUD, and every SR
    /// window (map, pedia, shops...). The binding is verified once per second and re-done when SR recreated/replaced
    /// anything (new HudUI, destroyed element or CanvasGroup), then alpha is re-applied.
    /// </summary>
    internal sealed class SRHudHider
    {
        private sealed class Target
        {
            public GameObject Go;
            public CanvasGroup Group;
            public float OrigAlpha;
            public bool OrigBlocks;
            public bool Ammo;
        }

        /// <summary>UIContainer children hidden by name (the SR HUD prefab has no component on these icons).</summary>
        private static readonly string[] HiddenNames =
        {
            "HealthIcon", "EnergyIcon", "RadIcon", "CurrencyIcon", "TimeIcon", "MailIcon", "KeysIcon",
            "CurrentDay", "CurrentTime", "Currency", "Keys", "PartnerArea", "crossHair",
        };

        private readonly List<Target> targets = new List<Target>();
        private bool hudVisible = true;
        private bool ammoVisible = true;
        private int boundCtx;
        private global::HudUI boundHud; // global:: — the Hud module owns namespace SlimeCraft.HudUI
        private float nextCheck;
        private bool loggedOnce;

        public bool HudVisible => hudVisible;

        public void SetHudVisible(bool v) { if (hudVisible == v) return; hudVisible = v; Apply(); }
        public void SetAmmoVisible(bool v) { if (ammoVisible == v) return; ammoVisible = v; Apply(); }

        public void Unbind()
        {
            targets.Clear();
            boundCtx = 0;
            boundHud = null;
            nextCheck = 0f;
        }

        /// <summary>Per frame from SRBridge.Tick: once per second verify the binding (rebind if needed) and re-apply.</summary>
        public void Tick(int ctxId)
        {
            float now = Time.unscaledTime;
            if (now < nextCheck) return;
            nextCheck = now + 1f;
            Refresh(ctxId);
        }

        /// <summary>Re-resolves HUD objects when needed (new world, new HudUI, destroyed element) and re-applies.</summary>
        public void Refresh(int ctxId)
        {
            if (!BindingValid(ctxId)) Bind(ctxId);
            Apply();
        }

        private bool BindingValid(int ctxId)
        {
            if (boundCtx != ctxId || targets.Count == 0) return false;
            var hud = SRSingleton<global::HudUI>.Instance;
            if (hud == null || hud != boundHud) return false;
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t.Go == null || t.Group == null) return false;
            }
            return true;
        }

        private void Bind(int ctxId)
        {
            targets.Clear();
            boundHud = null;
            try
            {
                var hud = SRSingleton<global::HudUI>.Instance;
                if (hud == null) return;
                Transform container = hud.uiContainer != null ? hud.uiContainer.transform : hud.transform;

                // HudUI's own references (clock/day/weather, mail, currency, keys, partner level)
                Add(hud.dayText, false); Add(hud.timeText, false); Add(hud.timeIcon, false); Add(hud.mailIcon, false);
                Add(hud.currencyText, false); Add(hud.keysText, false); Add(hud.keysIcon, false);
                if (hud.partnerArea != null) Add(hud.partnerArea.transform, false);
                if (hud.energyMeter != null) Add(hud.energyMeter.transform, false);

                // meters + crosshair by component (each meter object holds its bar, frame and value label)
                foreach (var c in container.GetComponentsInChildren<HealthMeter>(true)) Add(c.transform, false);
                foreach (var c in container.GetComponentsInChildren<EnergyMeter>(true)) Add(c.transform, false);
                foreach (var c in container.GetComponentsInChildren<RadMeter>(true)) { Add(c.transform, false); if (c.icon != null) Add(c.icon.transform, false); }
                foreach (var c in container.GetComponentsInChildren<CrosshairUI>(true)) Add(c.transform, false);

                // the icon siblings (coin, heart, energy bolt, rad) and anything above that SR renamed fields for
                for (int i = 0; i < container.childCount; i++)
                {
                    var ch = container.GetChild(i);
                    if (Array.IndexOf(HiddenNames, ch.name) >= 0) Add(ch, false);
                }

                // ammo slots: hidden with the HUD, and also whenever the vacpack is not selected
                var ammo = SRSingleton<AmmoSlotUI>.Instance;
                if (ammo != null) Add(ammo.transform, true);
                else foreach (var c in container.GetComponentsInChildren<AmmoSlotUI>(true)) Add(c.transform, true);

                boundHud = hud;
                boundCtx = ctxId;
                if (!loggedOnce || CoreConfig.VerboseLog)
                {
                    loggedOnce = true;
                    var sb = new StringBuilder();
                    foreach (var t in targets) sb.Append(t.Go.name).Append(t.Ammo ? "(ammo)" : "").Append(", ");
                    CoreLog.Info("SR HUD hider bound " + targets.Count + " elements: " + sb.ToString().TrimEnd(' ', ','));
                }
            }
            catch (Exception e) { CoreLog.Rate("hud bind", e, 30f); }
        }

        private void Add(Component c, bool ammo) { if (c != null) Add(c.transform, ammo); }

        private void Add(Transform t, bool ammo)
        {
            if (t == null) return;
            foreach (var x in targets) if (x.Go == t.gameObject) return;
            var g = t.GetComponent<CanvasGroup>();
            if (g == null) g = t.gameObject.AddComponent<CanvasGroup>();
            // a group we left at alpha 0 (e.g. re-bind while hidden) must not be recorded as the original state
            float a = g.alpha;
            if (a <= 0f) a = 1f;
            targets.Add(new Target { Go = t.gameObject, Group = g, OrigAlpha = a, OrigBlocks = g.blocksRaycasts || g.alpha <= 0f, Ammo = ammo });
        }

        private void Apply()
        {
            for (int i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                if (t.Group == null) continue;
                bool visible = hudVisible && (!t.Ammo || ammoVisible);
                float a = visible ? t.OrigAlpha : 0f;
                bool blocks = visible && t.OrigBlocks;
                if (t.Group.alpha != a) t.Group.alpha = a;
                if (t.Group.blocksRaycasts != blocks) t.Group.blocksRaycasts = blocks;
            }
        }
    }
}
