using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Snapshot of Slime Rancher's vacpack ammo for the Minecraft-style ammo strip, taken through SR's public
    /// API: the player's Ammo (usable slot count, selected index, per-slot id, count and capacity) plus icons and
    /// colours from the LookupDirector (slime icons come from their appearance). In the default ammo mode the
    /// fifth slot only accepts liquids, so it is drawn as the water slot.
    /// </summary>
    internal sealed class VacAmmo
    {
        public const int MaxSlots = 8;
        public int SlotCount;
        public int Selected = -1;
        public readonly Identifiable.Id[] Ids = new Identifiable.Id[MaxSlots];
        public readonly int[] Counts = new int[MaxSlots];
        public readonly int[] Max = new int[MaxSlots];
        public readonly bool[] Water = new bool[MaxSlots];

        private readonly Dictionary<Identifiable.Id, Sprite> icons = new Dictionary<Identifiable.Id, Sprite>();
        private readonly Dictionary<Identifiable.Id, uint> colors = new Dictionary<Identifiable.Id, uint>();
        private readonly Dictionary<Identifiable.Id, string> names = new Dictionary<Identifiable.Id, string>();
        private float nextCacheFlush;
        private Sprite vacpackIcon;
        private bool vacpackIconSearched;

        public void ClearCaches()
        {
            icons.Clear(); colors.Clear(); names.Clear();
            vacpackIcon = null; vacpackIconSearched = false;
        }

        /// <summary>Snapshot of the current ammo; false when the player state is unavailable.</summary>
        public bool Read()
        {
            SlotCount = 0;
            Selected = -1;
            var sc = SRSingleton<SceneContext>.Instance;
            if (sc == null) return false;
            var ps = sc.PlayerState;
            if (ps == null) return false;
            Ammo ammo = ps.Ammo;
            if (ammo == null) return false;
            if (Time.unscaledTime > nextCacheFlush) { nextCacheFlush = Time.unscaledTime + 10f; icons.Clear(); colors.Clear(); }
            bool defaultMode = ps.GetAmmoMode() == PlayerState.AmmoMode.DEFAULT;
            int n = Mathf.Clamp(ammo.GetUsableSlotCount(), 0, MaxSlots);
            for (int i = 0; i < n; i++)
            {
                var id = ammo.GetSlotName(i);
                Ids[i] = id;
                Counts[i] = id == Identifiable.Id.NONE ? 0 : ammo.GetSlotCount(i);
                Max[i] = ammo.GetSlotMaxCount(i);
                Water[i] = defaultMode && i == 4;
            }
            SlotCount = n;
            Selected = ammo.GetSelectedAmmoIdx();
            return true;
        }

        public Sprite Icon(Identifiable.Id id)
        {
            if (id == Identifiable.Id.NONE) return null;
            if (icons.TryGetValue(id, out var s)) return s;
            s = null;
            try { s = SRSingleton<GameContext>.Instance?.LookupDirector?.GetIcon(id); } catch { s = null; }
            icons[id] = s;
            return s;
        }

        public uint Color(Identifiable.Id id)
        {
            if (id == Identifiable.Id.NONE) return 0xFF808080;
            if (colors.TryGetValue(id, out var c)) return c;
            c = 0xFF55FF55;
            try
            {
                var lookup = SRSingleton<GameContext>.Instance?.LookupDirector;
                if (lookup != null)
                {
                    var col = lookup.GetColor(id);
                    if (col.a > 0f) { col.a = 1f; c = Argb.FromColor(col); }
                }
            }
            catch { }
            colors[id] = c;
            return c;
        }

        public string Name(Identifiable.Id id)
        {
            if (id == Identifiable.Id.NONE) return null;
            if (names.TryGetValue(id, out var n)) return n;
            try { n = Identifiable.GetName(id, false); } catch { n = null; }
            if (string.IsNullOrEmpty(n))
            {
                var raw = id.ToString().ToLowerInvariant().Split('_');
                for (int i = 0; i < raw.Length; i++) if (raw[i].Length > 0) raw[i] = char.ToUpperInvariant(raw[i][0]) + raw[i].Substring(1);
                n = string.Join(" ", raw);
            }
            names[id] = n;
            return n;
        }

        /// <summary>Slime Rancher's own vacpack picture (Slimepedia "Vacing" tutorial entry icon), used for the vacpack item.</summary>
        public Sprite VacpackIcon()
        {
            if (vacpackIconSearched) return vacpackIcon;
            var sc = SRSingleton<SceneContext>.Instance;
            if (sc == null || sc.PediaDirector == null) return null;
            vacpackIconSearched = true;
            try
            {
                // read the raw entry table (PediaDirector.Get returns the "locked" entry for locked pages)
                var entries = sc.PediaDirector.entries;
                if (entries != null)
                    foreach (var e in entries)
                        if (e != null && e.id == PediaDirector.Id.VACING) { vacpackIcon = e.icon; break; }
                if (vacpackIcon != null) SC.Log?.LogInfo("[Hud] vacpack icon: Slimepedia sprite '" + vacpackIcon.name + "'");
            }
            catch (Exception e) { SC.Log?.LogWarning("[Hud] vacpack icon lookup failed: " + e.Message); }
            return vacpackIcon;
        }
    }
}
