using System;
using System.Collections.Generic;
using System.Text;

namespace SlimeCraft
{
    /// <summary>A stack of items. Count 0 / null Id means empty. Damage = tool durability used.</summary>
    public sealed class ItemStack
    {
        public string Id;
        public int Count;
        public int Damage;

        public ItemStack(string id, int count = 1, int damage = 0) { Id = id; Count = count; Damage = damage; }

        public static ItemStack Empty => new ItemStack(null, 0);
        public bool IsEmpty => string.IsNullOrEmpty(Id) || Count <= 0;
        public ItemDef Def => IsEmpty ? null : Content.Item(Id);
        public int MaxStack => Def?.MaxStack ?? 64;
        public ItemStack Copy() => new ItemStack(Id, Count, Damage);
        public ItemStack WithCount(int c) => new ItemStack(Id, c, Damage);
        public bool CanMergeWith(ItemStack o) => o != null && !IsEmpty && !o.IsEmpty && o.Id == Id && Damage == 0 && o.Damage == 0;
        public override string ToString() => IsEmpty ? "empty" : (Count + "x " + Id + (Damage > 0 ? " dmg" + Damage : ""));
    }

    /// <summary>
    /// Plain container model (no Unity). Slots never hold null (use ItemStack.Empty semantics via IsEmpty).
    /// Player inventory: 36 slots, 0..8 = hotbar.
    /// </summary>
    public sealed class Inventory
    {
        private readonly ItemStack[] slots;
        public event Action Changed;

        public Inventory(int size)
        {
            slots = new ItemStack[size];
            for (int i = 0; i < size; i++) slots[i] = ItemStack.Empty;
        }

        public int Size => slots.Length;

        public ItemStack Get(int i) => (i >= 0 && i < slots.Length) ? slots[i] : ItemStack.Empty;

        public void Set(int i, ItemStack s)
        {
            if (i < 0 || i >= slots.Length) return;
            slots[i] = (s == null || s.IsEmpty) ? ItemStack.Empty : s;
            Changed?.Invoke();
        }

        public void Clear()
        {
            for (int i = 0; i < slots.Length; i++) slots[i] = ItemStack.Empty;
            Changed?.Invoke();
        }

        public void NotifyChanged() => Changed?.Invoke();

        /// <summary>Adds the stack: first merges into existing stacks (hotbar first), then empty slots. Returns leftover count.</summary>
        public int Add(ItemStack stack)
        {
            if (stack == null || stack.IsEmpty) return 0;
            int left = stack.Count;
            int max = stack.MaxStack;
            if (max > 1 && stack.Damage == 0)
            {
                for (int i = 0; i < slots.Length && left > 0; i++)
                {
                    var s = slots[i];
                    if (!s.IsEmpty && s.Id == stack.Id && s.Damage == 0 && s.Count < max)
                    {
                        int add = Math.Min(left, max - s.Count);
                        s.Count += add; left -= add;
                    }
                }
            }
            for (int i = 0; i < slots.Length && left > 0; i++)
            {
                if (slots[i].IsEmpty)
                {
                    int add = Math.Min(left, max);
                    slots[i] = new ItemStack(stack.Id, add, stack.Damage);
                    left -= add;
                }
            }
            if (left != stack.Count) Changed?.Invoke();
            return left;
        }

        public int CountOf(string id)
        {
            int n = 0;
            foreach (var s in slots) if (!s.IsEmpty && s.Id == id) n += s.Count;
            return n;
        }

        /// <summary>Removes up to n items of id. Returns removed amount.</summary>
        public int Remove(string id, int n)
        {
            int removed = 0;
            for (int i = slots.Length - 1; i >= 0 && removed < n; i--)
            {
                var s = slots[i];
                if (s.IsEmpty || s.Id != id) continue;
                int take = Math.Min(n - removed, s.Count);
                s.Count -= take; removed += take;
                if (s.Count <= 0) slots[i] = ItemStack.Empty;
            }
            if (removed > 0) Changed?.Invoke();
            return removed;
        }

        /// <summary>Compact text form: "id*count*damage;..." (empty slots as "").</summary>
        public string Serialize()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < slots.Length; i++)
            {
                if (i > 0) sb.Append(';');
                var s = slots[i];
                if (!s.IsEmpty) sb.Append(s.Id).Append('*').Append(s.Count).Append('*').Append(s.Damage);
            }
            return sb.ToString();
        }

        public void Deserialize(string data)
        {
            for (int i = 0; i < slots.Length; i++) slots[i] = ItemStack.Empty;
            if (!string.IsNullOrEmpty(data))
            {
                var parts = data.Split(';');
                for (int i = 0; i < parts.Length && i < slots.Length; i++)
                {
                    if (string.IsNullOrEmpty(parts[i])) continue;
                    var f = parts[i].Split('*');
                    if (f.Length < 2 || Content.Item(f[0]) == null) continue;
                    int.TryParse(f[1], out int c);
                    int d = 0; if (f.Length > 2) int.TryParse(f[2], out d);
                    if (c > 0) slots[i] = new ItemStack(f[0], c, d);
                }
            }
            Changed?.Invoke();
        }
    }
}
