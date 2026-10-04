using System;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// The Minecraft player inventory (36 slots, 0-8 hotbar), selected hotbar slot and game mode.
    /// Persisted per SR save through SC.Persistence ("inventory" and "gamemode"). The Slime Rancher vacpack is
    /// an item ("slimecraft:vacpack") that always exists exactly once.
    /// </summary>
    internal sealed class PlayerInventory : IPlayerInventory
    {
        public Inventory Inv { get; } = new Inventory(36);
        public event Action Changed;

        /// <summary>Hotbar slot whose count just grew (item pickup) → "pop" animation like Minecraft.</summary>
        internal event Action<int> SlotPopped;
        internal event Action GameModeChanged;
        /// <summary>When true (a container screen holds carried/grid stacks) the vacpack guard is suspended.</summary>
        internal Func<bool> GuardSuspended;

        private int selected;
        private bool creative;
        private bool guarding;
        private bool loadedThisWorld;

        public PlayerInventory()
        {
            Inv.Changed += OnInvChanged;
        }

        private void OnInvChanged()
        {
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception e) { HudModule.LogLimited("inv.changed", "Inventory.Changed listener failed: " + e); }
        }

        // ------------------------------------------------------------------ IPlayerInventory
        public int SelectedSlot
        {
            get => selected;
            set
            {
                int v = Mathf.Clamp(value, 0, 8);
                if (v == selected) return;
                selected = v;
                RaiseChanged();
            }
        }

        public ItemStack Selected => Inv.Get(selected);

        public bool VacpackSelected { get { var s = Inv.Get(selected); return !s.IsEmpty && s.Id == Content.Vacpack; } }

        public bool Creative
        {
            get => creative;
            set
            {
                if (creative == value) return;
                creative = value;
                RaiseChanged();
                try { GameModeChanged?.Invoke(); } catch (Exception e) { HudModule.LogLimited("inv.mode", e.ToString()); }
            }
        }

        public int Give(ItemStack stack)
        {
            if (stack == null || stack.IsEmpty) return 0;
            if (Content.Item(stack.Id) == null) return stack.Count;
            if (stack.Id == Content.Vacpack && CountOf(Content.Vacpack) > 0) return stack.Count;
            var before = new int[9];
            for (int i = 0; i < 9; i++) { var s = Inv.Get(i); before[i] = s.IsEmpty ? 0 : (s.Id == stack.Id ? s.Count : -1); }
            int left = Inv.Add(stack.Copy());
            for (int i = 0; i < 9; i++)
            {
                var s = Inv.Get(i);
                if (!s.IsEmpty && s.Id == stack.Id && s.Count > Math.Max(0, before[i]))
                    try { SlotPopped?.Invoke(i); } catch { }
            }
            return left;
        }

        public void ConsumeSelected(int n = 1)
        {
            if (creative || n <= 0) return;
            var s = Inv.Get(selected);
            if (s.IsEmpty || s.Id == Content.Vacpack) return;
            s.Count -= n;
            if (s.Count <= 0) Inv.Set(selected, ItemStack.Empty);
            else Inv.NotifyChanged();
        }

        public void DamageSelected(int amount = 1)
        {
            if (creative || amount <= 0) return;
            var s = Inv.Get(selected);
            if (s.IsEmpty) return;
            var def = s.Def;
            if (def == null || def.MaxDamage <= 0) return;
            s.Damage += amount;
            if (s.Damage >= def.MaxDamage)
            {
                Inv.Set(selected, ItemStack.Empty);
                try { SC.Audio?.Play2D("entity.item.break", 0.8f, 0.8f + UnityEngine.Random.value * 0.4f); } catch { }
            }
            else Inv.NotifyChanged();
        }

        // ------------------------------------------------------------------ helpers
        public int CountOf(string id)
        {
            int n = 0;
            for (int i = 0; i < Inv.Size; i++) { var s = Inv.Get(i); if (!s.IsEmpty && s.Id == id) n += s.Count; }
            return n;
        }

        /// <summary>Guarantees exactly one vacpack stack (re-adds it to the hotbar if missing, removes duplicates).</summary>
        public void EnsureVacpack()
        {
            if (guarding) return;
            if (GuardSuspended != null && GuardSuspended()) return;
            guarding = true;
            try
            {
                int first = -1;
                for (int i = 0; i < Inv.Size; i++)
                {
                    var s = Inv.Get(i);
                    if (s.IsEmpty || s.Id != Content.Vacpack) continue;
                    if (first < 0)
                    {
                        first = i;
                        if (s.Count != 1 || s.Damage != 0) { s.Count = 1; s.Damage = 0; Inv.NotifyChanged(); }
                    }
                    else Inv.Set(i, ItemStack.Empty);
                }
                if (first >= 0) return;
                int target = -1;
                if (Inv.Get(0).IsEmpty) target = 0;
                for (int i = 0; i < Inv.Size && target < 0; i++) if (Inv.Get(i).IsEmpty) target = i;
                if (target < 0)
                {
                    // inventory completely full: make room in the last main-inventory slot
                    target = Inv.Size - 1;
                    var displaced = Inv.Get(target);
                    if (!displaced.IsEmpty) HudModule.Instance?.DropStack(displaced.Copy());
                }
                Inv.Set(target, new ItemStack(Content.Vacpack, 1));
            }
            finally { guarding = false; }
        }

        // ------------------------------------------------------------------ persistence
        public void RegisterPersistence()
        {
            var p = SC.Persistence;
            if (p == null) return;
            p.Register("inventory", SaveInventory, LoadInventory);
            p.Register("gamemode", () => creative ? "creative" : "survival", LoadGameMode);
        }

        /// <summary>Extra stacks (crafting grid / carried item of an open screen) are folded into the saved copy.</summary>
        internal Func<ItemStack[]> PendingStacks;

        private string SaveInventory()
        {
            try
            {
                string slots = Inv.Serialize();
                var extra = PendingStacks?.Invoke();
                if (extra != null && extra.Length > 0)
                {
                    var copy = new Inventory(Inv.Size);
                    copy.Deserialize(slots);
                    foreach (var st in extra) if (st != null && !st.IsEmpty && st.Id != Content.Vacpack) copy.Add(st.Copy());
                    slots = copy.Serialize();
                }
                var o = JsonNode.NewObject();
                o["slots"] = JsonNode.Of(slots);
                o["selected"] = JsonNode.Of(selected);
                return o.ToString();
            }
            catch (Exception e)
            {
                SC.Log?.LogError("[Hud] inventory save failed: " + e);
                return null;
            }
        }

        private void LoadInventory(string data)
        {
            loadedThisWorld = true;
            try
            {
                if (string.IsNullOrEmpty(data))
                {
                    Inv.Clear();
                    selected = 0;
                    if (HudConfig.GiveStarterKit) GiveStarterKit();
                }
                else
                {
                    var o = JsonNode.Parse(data);
                    if (o != null && o.IsObject)
                    {
                        Inv.Deserialize(o["slots"].AsString(""));
                        selected = Mathf.Clamp(o["selected"].AsInt(0), 0, 8);
                    }
                    else Inv.Deserialize(data); // raw serialized slots
                }
            }
            catch (Exception e)
            {
                SC.Log?.LogError("[Hud] inventory load failed: " + e);
            }
            EnsureVacpack();
            RaiseChanged();
        }

        private void LoadGameMode(string data)
        {
            Creative = data != null && data.Trim().Equals("creative", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Called on WorldLoaded: without a persistence service, start with the kit once.</summary>
        public void OnWorldLoaded()
        {
            if (!loadedThisWorld && SC.Persistence == null && Inv.CountOf(Content.Vacpack) == 0)
            {
                Inv.Clear();
                if (HudConfig.GiveStarterKit) GiveStarterKit();
            }
            EnsureVacpack();
        }

        public void OnWorldUnloading() { loadedThisWorld = false; }

        /// <summary>The SlimeCraft starter kit (fresh save).</summary>
        public void GiveStarterKit()
        {
            void Put(int slot, string id, int count)
            {
                if (Content.Item(id) == null) { SC.Log?.LogWarning("[Hud] starter kit: unknown item " + id); return; }
                int max = Content.Item(id).MaxStack;
                Inv.Set(slot, new ItemStack(Content.Norm(id), Math.Min(count, Math.Max(1, max))));
            }
            Put(0, Content.Vacpack, 1);
            Put(1, "minecraft:diamond_pickaxe", 1);
            Put(2, "minecraft:diamond_sword", 1);
            Put(3, "minecraft:grass_block", 64);
            Put(4, "minecraft:oak_planks", 64);
            Put(5, "minecraft:glass", 64);
            Put(6, "minecraft:tnt", 64);
            Put(7, "minecraft:flint_and_steel", 1);
            Put(8, "minecraft:cooked_beef", 16);
            int m = 9;
            foreach (var id in new[] { "stone_bricks", "oak_log", "glowstone", "slime_block", "sand", "white_wool" })
                Put(m++, "minecraft:" + id, 64);
            Put(m++, "minecraft:bow", 1);
            Put(m++, "minecraft:arrow", 64);
            foreach (var mob in new[] { "creeper", "zombie", "pig", "cow", "sheep", "chicken" })
                Put(m++, "minecraft:" + mob + "_spawn_egg", 16);
            if (HudConfig.KitCraftingTable) Put(m++, "minecraft:crafting_table", 64);
            selected = 0;
        }
    }
}
