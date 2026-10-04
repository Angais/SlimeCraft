using System;
using System.Collections.Generic;

namespace SlimeCraft.HudUI
{
    /// <summary>What the player did, independent of where. Flags, so one rule can list several gestures.</summary>
    [Flags]
    internal enum Gesture
    {
        None = 0,
        /// <summary>Left click.</summary>
        Primary = 1 << 0,
        /// <summary>Right click.</summary>
        Secondary = 1 << 1,
        /// <summary>Middle click in creative mode.</summary>
        Middle = 1 << 2,
        /// <summary>Any other button (middle outside creative): only some special cells react.</summary>
        OtherButton = 1 << 3,
        /// <summary>Shift + left click.</summary>
        QuickPrimary = 1 << 4,
        /// <summary>Shift + right click.</summary>
        QuickSecondary = 1 << 5,
        /// <summary>Shift + another button.</summary>
        QuickOther = 1 << 6,
        /// <summary>Double click: gather matching items into the hand.</summary>
        Gather = 1 << 7,
        /// <summary>Drop key over a cell.</summary>
        DropOne = 1 << 8,
        /// <summary>Ctrl + drop key over a cell.</summary>
        DropStack = 1 << 9,
        /// <summary>Number key over a cell (exchange with that hotbar entry).</summary>
        Hotbar = 1 << 10,

        Plain = Primary | Secondary,
        Quick = QuickPrimary | QuickSecondary,
        AnyQuick = Quick | QuickOther,
        Drop = DropOne | DropStack,
        Every = Primary | Secondary | Middle | OtherButton | AnyQuick | Gather | Drop | Hotbar,
    }

    /// <summary>Where the pointer was when a gesture happened.</summary>
    internal struct Hit
    {
        public Cell Cell;
        /// <summary>No cell, and beyond the window.</summary>
        public bool Outside;

        /// <summary>No cell, but inside the window: clicks there do nothing.</summary>
        public bool Dead => Cell == null && !Outside;

        public static Hit On(Cell c) => new Hit { Cell = c };
    }

    /// <summary>The effect a gesture resolves to; <see cref="PanelScreen"/> carries it out.</summary>
    internal enum Move
    {
        Nothing,
        // hand <-> cell
        TakeAll,
        TakeHalf,
        PutAll,
        PutOne,
        Exchange,
        /// <summary>The cell refuses the held item but holds more of it: pull what still fits into the hand.</summary>
        FillHand,
        QuickTransfer,
        Gather,
        CloneToHand,
        HotbarExchange,
        // throwing
        ThrowHand,
        ThrowHandOne,
        /// <summary>Throws from a cell, keeping crafted items flowing; anything that cannot be thrown goes back.</summary>
        ThrowFromCell,
        /// <summary>Throws from a cell in one go; failures go to the inventory.</summary>
        ThrowFromCellOnce,
        // creative palette
        GrabOne,
        GrabStack,
        AddOne,
        FillToMax,
        LoseOne,
        DiscardHand,
        PaletteToHotbar,
        PaletteDrop,
        // creative bin
        ClearInventory,
    }

    /// <summary>Three-valued condition of a rule.</summary>
    internal enum Need : byte { Any, Yes, No }

    /// <summary>
    /// One row of a click rule table: the gestures it answers, the conditions on the hand and the cell, and the
    /// resulting move. Rows are checked top to bottom; the first match wins.
    /// </summary>
    internal struct ClickRule
    {
        public Gesture On;
        public Need HandHeld;
        public Need CellFilled;
        /// <summary>The cell's filter lets the held stack in.</summary>
        public Need CellAllows;
        /// <summary>Hand and cell hold the same item.</summary>
        public Need SameItem;
        /// <summary>The held count fits under the cell limit.</summary>
        public Need HandFits;
        /// <summary>The hand holds the vacpack.</summary>
        public Need HandVacpack;
        public Move Then;

        public ClickRule(Gesture on, Move then, Need hand = Need.Any, Need cell = Need.Any, Need allows = Need.Any,
                         Need same = Need.Any, Need fits = Need.Any, Need vacpack = Need.Any)
        {
            On = on;
            Then = then;
            HandHeld = hand;
            CellFilled = cell;
            CellAllows = allows;
            SameItem = same;
            HandFits = fits;
            HandVacpack = vacpack;
        }
    }

    /// <summary>
    /// Maps (gesture, place, held stack) to a <see cref="Move"/> with per-role rule tables. Pages pick the resolver
    /// whose tables match their cells; the moves themselves are performed by <see cref="PanelScreen"/>.
    /// </summary>
    internal sealed class ClickResolver
    {
        private const Need Y = Need.Yes, N = Need.No;

        /// <summary>Plain storage and crafting output cells.</summary>
        private static readonly ClickRule[] StorageRules =
        {
            new ClickRule(Gesture.Primary, Move.TakeAll, hand: N, cell: Y),
            new ClickRule(Gesture.Secondary, Move.TakeHalf, hand: N, cell: Y),
            new ClickRule(Gesture.Primary, Move.PutAll, hand: Y, cell: N),
            new ClickRule(Gesture.Secondary, Move.PutOne, hand: Y, cell: N),
            new ClickRule(Gesture.Primary, Move.PutAll, hand: Y, cell: Y, allows: Y, same: Y),
            new ClickRule(Gesture.Secondary, Move.PutOne, hand: Y, cell: Y, allows: Y, same: Y),
            new ClickRule(Gesture.Plain, Move.Exchange, hand: Y, cell: Y, allows: Y, same: N, fits: Y),
            new ClickRule(Gesture.Plain, Move.FillHand, hand: Y, cell: Y, allows: N, same: Y),
            new ClickRule(Gesture.Quick, Move.QuickTransfer),
            new ClickRule(Gesture.Middle, Move.CloneToHand, hand: N, cell: Y),
            new ClickRule(Gesture.Drop, Move.ThrowFromCell, hand: N, cell: Y),
            new ClickRule(Gesture.Hotbar, Move.HotbarExchange),
            new ClickRule(Gesture.Gather, Move.Gather, hand: Y, cell: N),
        };

        /// <summary>Storage cells of the creative inventory tab: the drop key throws in one go, even with a full hand.</summary>
        private static readonly ClickRule[] CreativeStorageRules =
            Prepend(new ClickRule(Gesture.Drop, Move.ThrowFromCellOnce, cell: Y), StorageRules);

        /// <summary>Creative palette entries: taking costs nothing and the palette never changes.</summary>
        private static readonly ClickRule[] PaletteRules =
        {
            new ClickRule(Gesture.Hotbar, Move.PaletteToHotbar, cell: Y),
            new ClickRule(Gesture.Middle, Move.CloneToHand, hand: N, cell: Y),
            new ClickRule(Gesture.Drop, Move.PaletteDrop, cell: Y),
            new ClickRule(Gesture.Plain, Move.GrabOne, hand: N, cell: Y),
            new ClickRule(Gesture.Quick, Move.GrabStack, hand: N, cell: Y),
            new ClickRule(Gesture.Primary, Move.AddOne, hand: Y, same: Y),
            new ClickRule(Gesture.QuickPrimary, Move.FillToMax, hand: Y, same: Y),
            new ClickRule(Gesture.Secondary | Gesture.QuickSecondary, Move.LoseOne, hand: Y, same: Y),
            new ClickRule(Gesture.Primary | Gesture.QuickPrimary, Move.DiscardHand, hand: Y, same: N),
            new ClickRule(Gesture.Secondary | Gesture.QuickSecondary, Move.LoseOne, hand: Y, same: N, vacpack: N),
        };

        /// <summary>Creative bin: shift empties the inventory (except the vacpack), anything else destroys the held stack.</summary>
        private static readonly ClickRule[] BinRules =
        {
            new ClickRule(Gesture.AnyQuick, Move.ClearInventory),
            new ClickRule(Gesture.Every & ~Gesture.AnyQuick, Move.DiscardHand, hand: Y, vacpack: N),
        };

        /// <summary>Clicks beyond the window: left throws the held stack, right throws one item of it.</summary>
        private static readonly ClickRule[] OutsideRules =
        {
            new ClickRule(Gesture.Primary, Move.ThrowHand, hand: Y),
            new ClickRule(Gesture.Secondary, Move.ThrowHandOne, hand: Y),
        };

        /// <summary>Creative inventory tab: both buttons throw the whole held stack.</summary>
        private static readonly ClickRule[] OutsideWholeRules =
        {
            new ClickRule(Gesture.Plain, Move.ThrowHand, hand: Y),
        };

        /// <summary>Survival inventory and crafting table.</summary>
        public static readonly ClickResolver Standard = new ClickResolver(StorageRules, OutsideRules, null, null);
        /// <summary>Creative category and search tabs (palette above the hotbar).</summary>
        public static readonly ClickResolver CreativePalette = new ClickResolver(StorageRules, OutsideRules, PaletteRules, null);
        /// <summary>Creative inventory tab (player rows and the bin).</summary>
        public static readonly ClickResolver CreativeInventory = new ClickResolver(CreativeStorageRules, OutsideWholeRules, null, BinRules);

        private readonly ClickRule[] storage, outside, palette, bin;

        private ClickResolver(ClickRule[] storage, ClickRule[] outside, ClickRule[] palette, ClickRule[] bin)
        {
            this.storage = storage;
            this.outside = outside;
            this.palette = palette;
            this.bin = bin;
        }

        public Move Resolve(Gesture gesture, Hit hit, ItemStack hand)
        {
            var table = TableFor(hit);
            if (table == null || gesture == Gesture.None) return Move.Nothing;
            var cellStack = hit.Cell != null ? hit.Cell.Contents : ItemStack.Empty;
            foreach (var rule in table)
            {
                if ((rule.On & gesture) == 0) continue;
                if (Matches(rule, hit.Cell, cellStack, hand)) return rule.Then;
            }
            return Move.Nothing;
        }

        private ClickRule[] TableFor(Hit hit)
        {
            if (hit.Cell == null) return hit.Outside ? outside : null;
            switch (hit.Cell.Role)
            {
                case CellRole.Plain:
                case CellRole.Output: return storage;
                case CellRole.Palette: return palette;
                case CellRole.Bin: return bin;
                default: return null;
            }
        }

        private static bool Matches(ClickRule r, Cell cell, ItemStack cellStack, ItemStack hand)
        {
            bool held = !hand.IsEmpty;
            if (!Check(r.HandHeld, held)) return false;
            if (!Check(r.CellFilled, !cellStack.IsEmpty)) return false;
            if (!Check(r.HandVacpack, held && hand.Id == Content.Vacpack)) return false;
            if (r.CellAllows != Need.Any && !Check(r.CellAllows, cell != null && cell.Allows(hand))) return false;
            if (!Check(r.SameItem, StackMoves.Alike(cellStack, hand))) return false;
            if (r.HandFits != Need.Any && !Check(r.HandFits, hand.Count <= Cell.Limit(hand))) return false;
            return true;
        }

        private static bool Check(Need need, bool value) => need == Need.Any || (need == Need.Yes) == value;

        private static ClickRule[] Prepend(ClickRule first, ClickRule[] rest)
        {
            var all = new List<ClickRule>(rest.Length + 1) { first };
            all.AddRange(rest);
            return all.ToArray();
        }
    }
}
