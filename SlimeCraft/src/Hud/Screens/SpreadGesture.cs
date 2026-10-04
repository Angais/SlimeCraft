using System;
using System.Collections.Generic;

namespace SlimeCraft.HudUI
{
    /// <summary>How a dragged stack is shared out over the cells the pointer crossed.</summary>
    internal enum SpreadKind
    {
        /// <summary>Left button: equal shares, the remainder stays in the hand.</summary>
        Even,
        /// <summary>Right button: one item per cell.</summary>
        One,
        /// <summary>Middle button (creative): a full stack per cell.</summary>
        Full,
    }

    /// <summary>
    /// The drag-to-distribute gesture: the cells a held stack was dragged over (in the order they were reached) and
    /// the arithmetic that decides how much each of them gets. Knows nothing about input or drawing.
    /// </summary>
    internal sealed class SpreadGesture
    {
        public readonly List<Cell> Cells = new List<Cell>();
        public int Button { get; private set; }
        public SpreadKind Kind { get; private set; }

        public int Count => Cells.Count;

        public void Start(int button)
        {
            Button = button;
            Kind = button == 0 ? SpreadKind.Even : (button == 1 ? SpreadKind.One : SpreadKind.Full);
            Cells.Clear();
        }

        public void Clear() => Cells.Clear();

        /// <summary>A cell can receive part of the held stack: plain storage, empty or holding the same item.</summary>
        public static bool Suits(Cell c, ItemStack hand)
        {
            if (!c.InGestures || !c.Allows(hand)) return false;
            var inside = c.Contents;
            return inside.IsEmpty || StackMoves.Alike(inside, hand);
        }

        /// <summary>The pointer reached a cell while the button is held.</summary>
        public bool Offer(Cell c, ItemStack hand)
        {
            if (c == null || hand.IsEmpty || Cells.Contains(c)) return false;
            bool enoughItems = Kind == SpreadKind.Full || hand.Count > Cells.Count;
            if (!enoughItems || !Suits(c, hand)) return false;
            Cells.Add(c);
            return true;
        }

        /// <summary>Drops cells that stopped suiting the held stack (their contents changed meanwhile).</summary>
        public void Prune(ItemStack hand)
        {
            Cells.RemoveAll(c => !Suits(c, hand));
        }

        private static int CountIn(Cell c)
        {
            var inside = c.Contents;
            return inside.IsEmpty ? 0 : inside.Count;
        }

        /// <summary>Items each crossed cell receives on top of what it holds (before the cell limit).</summary>
        public static int ShareOf(SpreadKind kind, int cellCount, ItemStack stack)
        {
            if (kind == SpreadKind.One) return 1;
            if (kind == SpreadKind.Full) return stack.MaxStack;
            return cellCount > 0 ? stack.Count / cellCount : stack.Count;
        }

        /// <summary>Count a crossed cell would end up with, and whether the cell limit cut it short.</summary>
        public int Planned(Cell c, ItemStack hand, out bool capped)
        {
            int wanted = CountIn(c) + ShareOf(Kind, Cells.Count, hand);
            int limit = Cell.Limit(hand);
            capped = wanted > limit;
            return capped ? limit : wanted;
        }

        /// <summary>
        /// What the hand would keep if the gesture ended now: the held count minus what the crossed cells would gain.
        /// A full-stack spread copies items, so the hand shows a full stack.
        /// </summary>
        public int KeptBy(ItemStack hand)
        {
            if (hand.IsEmpty) return 0;
            if (Kind == SpreadKind.Full) return hand.MaxStack;
            int given = 0;
            foreach (var c in Cells) given += Math.Max(0, Planned(c, hand, out _) - CountIn(c));
            return hand.Count - given;
        }

        /// <summary>
        /// Shares the held stack out over two or more crossed cells and returns what the hand keeps. Cells that no
        /// longer suit it are skipped, and in the even and one-each modes nothing is placed when the hand held fewer
        /// items than there are cells.
        /// </summary>
        public ItemStack Distribute(List<Cell> targets, ItemStack hand)
        {
            var model = hand.Copy();
            int startCount = hand.Count;
            int share = ShareOf(Kind, targets.Count, model);
            int limit = Cell.Limit(model);
            int keep = hand.Count;
            bool allowed = Kind == SpreadKind.Full || startCount >= targets.Count;
            foreach (var c in targets)
            {
                if (!allowed || !Suits(c, model)) continue;
                int had = CountIn(c);
                int now = Math.Max(had, Math.Min(had + share, limit));
                keep -= now - had;
                StackMoves.Write(c, model.WithCount(now));
            }
            return keep > 0 ? model.WithCount(keep) : ItemStack.Empty;
        }
    }
}
