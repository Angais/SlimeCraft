using System;

namespace SlimeCraft.HudUI
{
    // Inventory UI model shared by the item pages.
    //
    // A panel (Panel.cs) is a window with a list of cells. A cell is a plain record: where its 16x16 square sits in
    // the window, which storage entry it shows, which role it plays and which stacks it lets in. Cells carry no
    // behaviour of their own; the click rule table (ClickRules.cs) decides what a gesture does and StackMoves
    // performs the actual stack arithmetic.

    /// <summary>Indexed stack storage a cell can show.</summary>
    internal interface IStackStore
    {
        /// <summary>The live stack object stored at <paramref name="entry"/> (never null, never a copy).</summary>
        ItemStack Peek(int entry);

        /// <summary>Stores a stack (empty stacks are normalised) and announces the change.</summary>
        void Store(int entry, ItemStack stack);

        /// <summary>Announces a change made directly to a live stack object.</summary>
        void Touch();
    }

    /// <summary>The player's 36-entry inventory (0..8 hotbar, 9..35 main rows) as cell storage.</summary>
    internal sealed class PlayerStore : IStackStore
    {
        private readonly Inventory inv;

        public PlayerStore(Inventory inventory)
        {
            inv = inventory;
        }

        public ItemStack Peek(int entry) => inv != null ? inv.Get(entry) : ItemStack.Empty;

        // Inventory.Set raises the inventory's own change event
        public void Store(int entry, ItemStack stack) => inv?.Set(entry, stack == null || stack.IsEmpty ? ItemStack.Empty : stack);

        public void Touch() => inv?.NotifyChanged();
    }

    /// <summary>
    /// A row of stacks owned by a page (crafting grid, crafting output, creative palette). Writing into
    /// <see cref="Raw"/> is silent; <see cref="Store"/> runs the change callback.
    /// </summary>
    internal sealed class LooseStore : IStackStore
    {
        public readonly ItemStack[] Raw;
        private readonly Action changed;

        public LooseStore(int length, Action onChange = null)
        {
            Raw = new ItemStack[Math.Max(0, length)];
            for (int i = 0; i < Raw.Length; i++) Raw[i] = ItemStack.Empty;
            changed = onChange;
        }

        public ItemStack Peek(int entry)
        {
            if ((uint)entry >= (uint)Raw.Length) return ItemStack.Empty;
            var st = Raw[entry];
            if (st == null) Raw[entry] = st = ItemStack.Empty;
            return st;
        }

        public void Store(int entry, ItemStack stack)
        {
            if ((uint)entry >= (uint)Raw.Length) return;
            Raw[entry] = stack == null || stack.IsEmpty ? ItemStack.Empty : stack;
            Touch();
        }

        public void Touch() => changed?.Invoke();
    }

    /// <summary>The special part a cell plays; the click rules are chosen per role.</summary>
    internal enum CellRole
    {
        /// <summary>Ordinary storage.</summary>
        Plain,
        /// <summary>Crafting output: lets nothing in, only gives its whole stack, and taking it runs a hook.</summary>
        Output,
        /// <summary>Exists only in the background art (armour, offhand): hidden, always empty, inert.</summary>
        Prop,
        /// <summary>Creative palette entry: an endless source of its item.</summary>
        Palette,
        /// <summary>Creative bin: always empty; what goes in is destroyed (never the vacpack).</summary>
        Bin,
    }

    /// <summary>One 16x16 cell of a panel: a position, a storage entry, a role and an entry filter.</summary>
    internal sealed class Cell
    {
        /// <summary>Edge length of the item square.</summary>
        public const int Span = 16;

        /// <summary>No cell ever holds more than this, whatever the item allows.</summary>
        private const int AbsoluteCeiling = 99;

        private static readonly Predicate<ItemStack> Nothing = _ => false;
        private static readonly Predicate<ItemStack> AnythingButVacpack = s => s == null || s.Id != Content.Vacpack;

        public readonly CellRole Role;
        /// <summary>Top-left of the item square, relative to the panel.</summary>
        public readonly int X, Y;
        /// <summary>Backing storage; null for cells that never hold anything (props, the bin).</summary>
        public readonly IStackStore Store;
        public readonly int Entry;
        /// <summary>Which stacks may be put in; null lets everything in.</summary>
        public readonly Predicate<ItemStack> Filter;
        /// <summary>Runs after items were taken out (output cells); others just announce a storage change.</summary>
        public readonly Action<ItemStack> AfterTake;

        /// <summary>Position in the owning panel's list (set by <see cref="Panel.Add"/>).</summary>
        public int Number = -1;

        private Cell(CellRole role, int x, int y, IStackStore store, int entry, Predicate<ItemStack> filter, Action<ItemStack> afterTake)
        {
            Role = role;
            X = x;
            Y = y;
            Store = store;
            Entry = entry;
            Filter = filter;
            AfterTake = afterTake;
        }

        public static Cell Plain(IStackStore store, int entry, int x, int y) => new Cell(CellRole.Plain, x, y, store, entry, null, null);

        public static Cell Output(IStackStore store, int x, int y, Action<ItemStack> taken) =>
            new Cell(CellRole.Output, x, y, store, 0, Nothing, taken);

        public static Cell Prop(int x, int y) => new Cell(CellRole.Prop, x, y, null, 0, Nothing, null);

        public static Cell PaletteEntry(IStackStore store, int entry, int x, int y) => new Cell(CellRole.Palette, x, y, store, entry, null, null);

        public static Cell Bin(int x, int y) => new Cell(CellRole.Bin, x, y, null, 0, AnythingButVacpack, null);

        /// <summary>Drawn, hoverable and clickable (everything except props).</summary>
        public bool Shown => Role != CellRole.Prop;

        /// <summary>The live stack in the cell (an empty stack for cells without storage).</summary>
        public ItemStack Contents => Store != null ? Store.Peek(Entry) : ItemStack.Empty;

        public bool Holding => !Contents.IsEmpty;

        public bool Allows(ItemStack s) => Filter == null || Filter(s);

        /// <summary>Takes part in drag-spreading and double-click gathering (plain storage only).</summary>
        public bool InGestures => Role == CellRole.Plain;

        public bool SharesStore(Cell other) => other != null && ReferenceEquals(Store, other.Store);

        /// <summary>Largest count a cell holds of this stack's item.</summary>
        public static int Limit(ItemStack s) => Math.Min(AbsoluteCeiling, s != null ? s.MaxStack : 64);
    }

    /// <summary>Stack arithmetic between cells and loose stacks. Every write goes through the cell's storage.</summary>
    internal static class StackMoves
    {
        /// <summary>Both stacks hold the same item (same id and damage, neither empty).</summary>
        public static bool Alike(ItemStack a, ItemStack b) =>
            a != null && b != null && !a.IsEmpty && !b.IsEmpty && a.Id == b.Id && a.Damage == b.Damage;

        /// <summary>Removes <paramref name="n"/> items from <paramref name="s"/> in place and returns them as a new stack.</summary>
        public static ItemStack Peel(ItemStack s, int n)
        {
            if (s == null || s.IsEmpty || n <= 0) return ItemStack.Empty;
            int k = Math.Min(n, s.Count);
            s.Count -= k;
            return new ItemStack(s.Id, k, s.Damage);
        }

        public static void Write(Cell c, ItemStack s) => c.Store?.Store(c.Entry, s ?? ItemStack.Empty);

        public static void Touch(Cell c) => c.Store?.Touch();

        /// <summary>Runs the cell's after-take behaviour for items that just left it.</summary>
        public static void Taken(Cell c, ItemStack taken)
        {
            if (c.AfterTake != null) c.AfterTake(taken);
            else Touch(c);
        }

        /// <summary>
        /// Takes up to <paramref name="want"/> items (never more than <paramref name="ceiling"/>) out of a cell, without the
        /// after-take behaviour. Output cells hand over their whole stack or nothing. Null when nothing came out.
        /// </summary>
        public static ItemStack Pull(Cell c, int want, int ceiling)
        {
            var inside = c.Contents;
            if (inside.IsEmpty) return null;
            int n = Math.Min(want, ceiling);
            if (n <= 0) return null;
            if (c.Role == CellRole.Output)
            {
                if (ceiling < inside.Count) return null;
                Write(c, ItemStack.Empty);
                return inside;
            }
            var part = Peel(inside, n);
            if (inside.Count > 0) Touch(c);
            else Write(c, ItemStack.Empty);
            return part;
        }

        /// <summary><see cref="Pull"/> followed by the after-take behaviour; an empty stack instead of null.</summary>
        public static ItemStack PullAndNotify(Cell c, int want, int ceiling)
        {
            var part = Pull(c, want, ceiling);
            if (part == null) return ItemStack.Empty;
            Taken(c, part);
            return part;
        }

        /// <summary>
        /// Moves up to <paramref name="amount"/> items of <paramref name="stack"/> into a cell (only onto the same item, never
        /// past the cell limit) and returns what is left: the same object, or an empty stack.
        /// </summary>
        public static ItemStack Push(Cell c, ItemStack stack, int amount)
        {
            if (stack == null) return ItemStack.Empty;
            if (stack.IsEmpty || !c.Allows(stack)) return stack;
            var inside = c.Contents;
            bool vacant = inside.IsEmpty;
            if (!vacant && !Alike(inside, stack)) return stack;
            int room = Cell.Limit(stack) - (vacant ? 0 : inside.Count);
            int moving = Math.Min(room, Math.Min(amount, stack.Count));
            if (moving <= 0) return stack;
            if (vacant)
            {
                Write(c, Peel(stack, moving));
            }
            else
            {
                inside.Count += moving;
                stack.Count -= moving;
                Write(c, inside);
            }
            return stack.Count > 0 ? stack : ItemStack.Empty;
        }
    }
}
