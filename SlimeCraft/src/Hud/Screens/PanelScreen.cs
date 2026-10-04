using System;
using System.Collections.Generic;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Base of the item pages (survival inventory, crafting table, creative palette). Owns a <see cref="Panel"/> of
    /// cells, turns pointer and key events into <see cref="Gesture"/>s, lets the page's <see cref="ClickResolver"/>
    /// pick a <see cref="Move"/> and carries it out. Controls match what Minecraft players expect: pick up, place,
    /// exchange, split, place one, shift-click transfer (routed by each page's <see cref="TransferRoute"/> table),
    /// double-click gathering, drag-spreading, number-key hotbar exchange, the drop key, and throwing the held
    /// stack by clicking beyond the window. Items never vanish: anything that cannot be thrown goes back.
    /// </summary>
    internal abstract class PanelScreen : HudScreen
    {
        /// <summary>Pointer bookkeeping between a press and its release.</summary>
        private enum Phase
        {
            Idle,
            /// <summary>The next release belongs to a press that already acted (or to the click that opened the page).</summary>
            SwallowRelease,
            /// <summary>A button went down with a held stack: a drag-spread may be under way.</summary>
            Spreading,
        }

        /// <summary>A shift-click repeats its transfer while the same item keeps moving, at most this often.</summary>
        private const int TransferRounds = 512;
        /// <summary>Ctrl + drop on a crafting output keeps crafting and throwing, at most this often.</summary>
        private const int ThrowRounds = 64;

        private const string HoverBack = "gui/sprites/container/slot_highlight_back";
        private const string HoverFront = "gui/sprites/container/slot_highlight_front";
        private const uint SpreadTint = 0x80FFFFFFu;

        protected readonly Panel panel = new Panel();
        /// <summary>Rule tables used for this page's clicks.</summary>
        protected ClickResolver Rules = ClickResolver.Standard;
        /// <summary>Shift-click destinations per source block of cells (a cell no route covers transfers nothing).</summary>
        protected TransferRoute[] Routes = new TransferRoute[0];

        /// <summary>Cell under the mouse at the last draw.</summary>
        protected Cell Hovered;

        private readonly SpreadGesture spread = new SpreadGesture();
        private Phase phase = Phase.SwallowRelease;
        private Cell lastPressed;
        private bool gatherArmed;
        /// <summary>What the cell held at the last shift-click (the item a shift + double click sweeps).</summary>
        private ItemStack lastShiftClicked = ItemStack.Empty;
        private readonly List<Cell> sweepList = new List<Cell>();
        private readonly List<Cell> spreadTargets = new List<Cell>();

        /// <summary>What one cell shows this frame (spread previews replace the real stack).</summary>
        private struct CellLook
        {
            public ItemStack Stack;
            public ItemStack Preview;
            public string Label;
            public bool Tinted;
            public bool Hidden;
        }

        private CellLook[] looks = new CellLook[0];
        private readonly ItemStack handPreview = new ItemStack(null, 0);

        protected PlayerInventory PInv => Hud.PInv;

        /// <summary>The stack attached to the mouse (shared with the Hud module, so it survives page switches).</summary>
        protected ItemStack Hand
        {
            get => Hud.Carried;
            set => Hud.Carried = value == null || value.IsEmpty ? ItemStack.Empty : value;
        }

        protected static bool ShiftHeld => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

        // ================================================================== layout

        public override void Open(HudModule hud, int viewW, int viewH)
        {
            base.Open(hud, viewW, viewH);
            panel.Footer.Y = panel.Height - 94;
            Arrange();
        }

        public override void Fit(int viewW, int viewH)
        {
            base.Fit(viewW, viewH);
            Arrange();
        }

        /// <summary>Positions the window (centred) and anything that follows it.</summary>
        protected virtual void Arrange() => panel.Centre(ViewW, ViewH);

        // ================================================================== drawing

        /// <summary>
        /// Screen-space art first, then the window contents in window coordinates (captions, the hovered-cell glow
        /// behind the cells, the cells, the glow in front of them), then the held stack and the tooltip on top.
        /// </summary>
        public override void Draw(Gui g, int mouseX, int mouseY, float partialTick)
        {
            Hovered = panel.CellAt(mouseX, mouseY);
            DimWorld(g);
            DrawWindowArt(g, mouseX, mouseY);

            g.Push();
            g.Translate(panel.Left, panel.Top);
            DrawWindowContents(g);
            g.Pop();

            DrawHand(g, mouseX, mouseY);
            DrawHoverInfo(g, mouseX, mouseY);
        }

        private void DrawWindowContents(Gui g)
        {
            panel.Heading.Draw(g);
            panel.Footer.Draw(g);
            Glow(g, HoverBack, false);
            DrawCells(g);
            Glow(g, HoverFront, true);
        }

        /// <summary>One of the two hovered-cell highlight sprites (24x24, 4 px around the cell), optionally on a new layer.</summary>
        private void Glow(Gui g, string sprite, bool aboveEverything)
        {
            if (Hovered == null) return;
            if (aboveEverything) g.LayerBreak();
            g.DrawSprite(sprite, Hovered.X - 4, Hovered.Y - 4, 24, 24);
        }

        /// <summary>Window art (and anything around it) in screen coordinates.</summary>
        protected abstract void DrawWindowArt(Gui g, int mouseX, int mouseY);

        /// <summary>Tooltip of the hovered cell while the hand is empty.</summary>
        protected virtual void DrawHoverInfo(Gui g, int mouseX, int mouseY)
        {
            if (Hovered != null && Hovered.Holding && Hand.IsEmpty) g.DrawTooltip(mouseX, mouseY, InfoLines(Hovered));
        }

        protected virtual List<string> InfoLines(Cell c) => Gui.TooltipLines(c.Contents);

        private CellLook[] LooksFor(int n)
        {
            if (looks.Length < n)
            {
                looks = new CellLook[Math.Max(n, 64)];
                for (int i = 0; i < looks.Length; i++) looks[i].Preview = new ItemStack(null, 0);
            }
            return looks;
        }

        /// <summary>Cell contents in three passes (tints, icons, then counts and bars) so the icons batch well.</summary>
        private void DrawCells(Gui g)
        {
            int n = panel.Count;
            var look = LooksFor(n);
            for (int i = 0; i < n; i++)
            {
                look[i].Stack = panel[i].Contents;
                look[i].Label = null;
                look[i].Tinted = false;
                look[i].Hidden = false;
            }
            if (phase == Phase.Spreading) PreviewSpread(look);

            for (int i = 0; i < n; i++)
            {
                var c = panel[i];
                if (c.Shown && look[i].Tinted) g.Fill(c.X, c.Y, c.X + Cell.Span, c.Y + Cell.Span, SpreadTint);
            }
            for (int i = 0; i < n; i++)
            {
                var c = panel[i];
                if (c.Shown && !look[i].Hidden) g.DrawItem(c.X, c.Y, look[i].Stack);
            }
            g.LayerBreak();
            for (int i = 0; i < n; i++)
            {
                var c = panel[i];
                if (c.Shown && !look[i].Hidden) g.DrawItemOverlay(c.X, c.Y, look[i].Stack, look[i].Label);
            }
        }

        /// <summary>
        /// While a held stack is being spread, every crossed cell shows what it would receive (white tint; the count
        /// turns yellow where the cell limit cuts it). A spread over a single cell just hides that cell's item.
        /// </summary>
        private void PreviewSpread(CellLook[] look)
        {
            var hand = Hand;
            if (hand.IsEmpty || spread.Count == 0) return;
            spread.Prune(hand);
            if (spread.Count == 0) return;
            if (spread.Count == 1)
            {
                int only = spread.Cells[0].Number;
                if (panel.Has(only)) look[only].Hidden = true;
                return;
            }
            foreach (var c in spread.Cells)
            {
                int k = c.Number;
                if (!panel.Has(k)) continue;
                int count = spread.Planned(c, hand, out bool capped);
                if (capped) look[k].Label = "§e" + Gui.CountString(count);
                var preview = look[k].Preview;
                preview.Id = hand.Id;
                preview.Damage = hand.Damage;
                preview.Count = count;
                look[k].Stack = preview;
                look[k].Tinted = true;
            }
        }

        /// <summary>The held stack follows the mouse; during a spread it shows what the hand would keep.</summary>
        private void DrawHand(Gui g, int mouseX, int mouseY)
        {
            var hand = Hand;
            if (hand.IsEmpty) return;
            var shown = hand;
            string label = null;
            if (phase == Phase.Spreading && spread.Count > 1)
            {
                int kept = spread.KeptBy(hand);
                handPreview.Id = hand.Id;
                handPreview.Damage = hand.Damage;
                handPreview.Count = kept > 0 ? kept : 1;
                if (kept <= 0) label = "§e0";
                shown = handPreview;
            }
            int x = mouseX - 8, y = mouseY - 8;
            g.LayerBreak();
            g.DrawItem(x, y, shown);
            g.LayerBreak();
            g.DrawItemOverlay(x, y, shown, label);
        }

        // ================================================================== pointer

        protected Hit HitAt(double x, double y)
        {
            var c = panel.CellAt(x, y);
            return new Hit { Cell = c, Outside = c == null && IsBeyondPanel(x, y) };
        }

        /// <summary>Whether a point counts as "beyond the window" for throwing the held stack.</summary>
        protected virtual bool IsBeyondPanel(double x, double y) => panel.Beyond(x, y);

        private static Gesture PlainFor(int button) =>
            button == 0 ? Gesture.Primary : (button == 1 ? Gesture.Secondary : Gesture.OtherButton);

        private static Gesture QuickFor(int button) =>
            button == 0 ? Gesture.QuickPrimary : (button == 1 ? Gesture.QuickSecondary : Gesture.QuickOther);

        /// <summary>
        /// The click gesture a press with an empty hand, or a release with a held stack, stands for: the creative
        /// middle click, a shift variant over a cell (remembering what that cell held), or the plain click.
        /// <see cref="Gesture.None"/> when it lands on empty window space.
        /// </summary>
        private Gesture ClickGesture(int button, Hit hit)
        {
            if (button == 2 && PInv.Creative) return Gesture.Middle;
            if (hit.Cell != null && ShiftHeld)
            {
                lastShiftClicked = hit.Cell.Holding ? hit.Cell.Contents.Copy() : ItemStack.Empty;
                return QuickFor(button);
            }
            return hit.Dead ? Gesture.None : PlainFor(button);
        }

        /// <summary>Presses act with the left or right button (the middle one too in creative), never during a spread or on empty window space.</summary>
        private bool PressActs(int button, Hit hit) =>
            (button == 0 || button == 1 || (button == 2 && PInv.Creative)) && phase != Phase.Spreading && !hit.Dead;

        public override bool PointerDown(double x, double y, int button, bool doubleClick)
        {
            var hit = HitAt(x, y);
            gatherArmed = doubleClick && hit.Cell == lastPressed;
            lastPressed = hit.Cell;
            if (phase == Phase.SwallowRelease) phase = Phase.Idle;
            if (!PressActs(button, hit)) return true;

            if (Hand.IsEmpty)
            {
                Perform(ClickGesture(button, hit), hit);
                phase = Phase.SwallowRelease;
            }
            else
            {
                // a held stack waits for the release: it may become a spread over several cells
                spread.Start(button);
                phase = Phase.Spreading;
            }
            return true;
        }

        public override bool PointerMove(double x, double y, int button)
        {
            if (phase == Phase.Spreading) spread.Offer(panel.CellAt(x, y), Hand);
            return true;
        }

        /// <summary>What a button release means, in order of precedence.</summary>
        private enum Release
        {
            /// <summary>Second click of a double click on a plain cell.</summary>
            Gather,
            /// <summary>Another button went up during a spread: the spread is dropped.</summary>
            CancelSpread,
            /// <summary>The press already acted; its release does nothing.</summary>
            Consumed,
            /// <summary>The spread is complete.</summary>
            EndSpread,
            /// <summary>A held stack is released over something: same as clicking there.</summary>
            ClickWithHand,
            Nothing,
        }

        private Release Classify(Hit hit, int button)
        {
            if (gatherArmed && button == 0 && hit.Cell != null && hit.Cell.InGestures) return Release.Gather;
            if (phase == Phase.Spreading && spread.Button != button) return Release.CancelSpread;
            if (phase == Phase.SwallowRelease) return Release.Consumed;
            if (phase == Phase.Spreading && spread.Count > 0) return Release.EndSpread;
            return Hand.IsEmpty ? Release.Nothing : Release.ClickWithHand;
        }

        public override bool PointerUp(double x, double y, int button)
        {
            var hit = HitAt(x, y);
            switch (Classify(hit, button))
            {
                case Release.Gather:
                    if (ShiftHeld) ShiftSweep(hit.Cell);
                    else Perform(Gesture.Gather, hit);
                    gatherArmed = false;
                    break;
                case Release.CancelSpread:
                    // the first button's own release is ignored later
                    spread.Clear();
                    phase = Phase.SwallowRelease;
                    return true;
                case Release.Consumed:
                    phase = Phase.Idle;
                    return true;
                case Release.EndSpread:
                    FinishSpread();
                    break;
                case Release.ClickWithHand:
                    var gesture = ClickGesture(button, hit);
                    if (gesture != Gesture.None) Perform(gesture, hit);
                    break;
            }
            StopSpread();
            return true;
        }

        /// <summary>Abandons any spread in progress.</summary>
        protected void StopSpread()
        {
            spread.Clear();
            if (phase == Phase.Spreading) phase = Phase.Idle;
        }

        /// <summary>
        /// Shift + double click: shift-clicks, one after another, every shown cell of the clicked cell's storage that
        /// holds the item last shift-clicked. Each cell is checked when its turn comes, after the earlier transfers.
        /// </summary>
        private void ShiftSweep(Cell clicked)
        {
            if (lastShiftClicked.IsEmpty) return;
            sweepList.Clear();
            sweepList.AddRange(panel.Cells);
            foreach (var c in sweepList)
                if (SweepTakes(c, clicked)) Perform(Gesture.QuickPrimary, Hit.On(c));
            sweepList.Clear();
        }

        private bool SweepTakes(Cell c, Cell clicked) =>
            c.Shown && c.Holding && c.SharesStore(clicked) && StackMoves.Alike(c.Contents, lastShiftClicked);

        /// <summary>Ends a spread: one crossed cell acts like a plain click, several cells share the held stack.</summary>
        private void FinishSpread()
        {
            spreadTargets.Clear();
            spreadTargets.AddRange(spread.Cells);
            spread.Clear();
            var hand = Hand;
            if (hand.IsEmpty || (spread.Kind == SpreadKind.Full && !PInv.Creative))
            {
                spreadTargets.Clear();
                return;
            }
            if (spreadTargets.Count == 1)
            {
                var only = spreadTargets[0];
                spreadTargets.Clear();
                Perform(PlainFor(spread.Button), Hit.On(only));
                return;
            }
            Hand = spread.Distribute(spreadTargets, hand);
            spreadTargets.Clear();
            PInv.Inv.NotifyChanged();
        }

        // ================================================================== keys

        public override bool Key(KeyCode key, bool ctrl, bool shift)
        {
            var input = SC.Input;
            if (key == KeyCode.Escape || (input != null && key == input.InventoryKey))
            {
                Hud.CloseScreen();
                return true;
            }
            if (TryHotbarKey(key)) return true;
            if (input != null && key == input.DropKey && Hovered != null && Hovered.Holding)
            {
                Perform(ctrl ? Gesture.DropStack : Gesture.DropOne, Hit.On(Hovered));
                return true;
            }
            return false;
        }

        /// <summary>Number keys 1-9 exchange the hovered cell with that hotbar entry (empty hand only).</summary>
        protected bool TryHotbarKey(KeyCode key)
        {
            if (!Hand.IsEmpty || Hovered == null || key < KeyCode.Alpha1 || key > KeyCode.Alpha9) return false;
            Perform(Gesture.Hotbar, Hit.On(Hovered), key - KeyCode.Alpha1);
            return true;
        }

        // ================================================================== moves

        /// <summary>Resolves a gesture with the page's rules, carries the move out and announces the inventory change.</summary>
        protected void Perform(Gesture gesture, Hit hit, int hotbar = -1)
        {
            var move = Rules.Resolve(gesture, hit, Hand);
            if (move != Move.Nothing) Carry(move, gesture, hit.Cell, hotbar);
            // a plain click always refreshes the clicked storage (the crafting grid re-checks its recipe)
            if (hit.Cell != null && (gesture & Gesture.Plain) != 0) StackMoves.Touch(hit.Cell);
            PInv.Inv.NotifyChanged();
        }

        private void Carry(Move move, Gesture gesture, Cell cell, int hotbar)
        {
            var hand = Hand;
            switch (move)
            {
                case Move.TakeAll:
                case Move.TakeHalf:
                {
                    int count = cell.Contents.Count;
                    var part = StackMoves.Pull(cell, move == Move.TakeAll ? count : (count + 1) / 2, int.MaxValue);
                    if (part == null) break;
                    Hand = part;
                    StackMoves.Taken(cell, part);
                    break;
                }
                case Move.PutAll:
                    Hand = StackMoves.Push(cell, hand, hand.Count);
                    break;
                case Move.PutOne:
                    Hand = StackMoves.Push(cell, hand, 1);
                    break;
                case Move.Exchange:
                    Hand = cell.Contents;
                    StackMoves.Write(cell, hand);
                    break;
                case Move.FillHand:
                {
                    var part = StackMoves.Pull(cell, cell.Contents.Count, hand.MaxStack - hand.Count);
                    if (part == null) break;
                    hand.Count += part.Count;
                    Hand = hand;
                    StackMoves.Taken(cell, part);
                    break;
                }
                case Move.QuickTransfer:
                    TransferRepeatedly(cell);
                    break;
                case Move.Gather:
                    GatherInto(cell);
                    break;
                case Move.CloneToHand:
                {
                    var inside = cell.Contents;
                    if (PInv.Creative && hand.IsEmpty && !inside.IsEmpty && inside.Id != Content.Vacpack)
                        Hand = inside.WithCount(inside.MaxStack);
                    break;
                }
                case Move.HotbarExchange:
                    ExchangeWithHotbar(cell, hotbar);
                    break;
                case Move.ThrowHand:
                    if (Hud.DropStack(hand)) Hand = ItemStack.Empty;
                    break;
                case Move.ThrowHandOne:
                {
                    var one = StackMoves.Peel(hand, 1);
                    if (!Hud.DropStack(one)) hand.Count += one.Count;
                    Hand = hand;
                    break;
                }
                case Move.ThrowFromCell:
                    ThrowFrom(cell, gesture == Gesture.DropOne);
                    break;
                case Move.ThrowFromCellOnce:
                    ThrowOnceFrom(cell, gesture == Gesture.DropOne);
                    break;
                case Move.GrabOne:
                    Hand = cell.Contents.WithCount(1);
                    break;
                case Move.GrabStack:
                    Hand = cell.Contents.WithCount(cell.Contents.MaxStack);
                    break;
                case Move.AddOne:
                    hand.Count = Math.Min(hand.Count + 1, hand.MaxStack);
                    Hand = hand;
                    break;
                case Move.FillToMax:
                    hand.Count = hand.MaxStack;
                    Hand = hand;
                    break;
                case Move.LoseOne:
                    hand.Count--;
                    Hand = hand;
                    break;
                case Move.DiscardHand:
                    Hand = ItemStack.Empty;
                    if (hand.Id == Content.Vacpack) ReturnToPlayer(hand); // the vacpack is never destroyed
                    break;
                case Move.PaletteToHotbar:
                    if (hotbar >= 0 && hotbar < 9) OverwriteHotbar(hotbar, cell.Contents.WithCount(cell.Contents.MaxStack));
                    break;
                case Move.PaletteDrop:
                {
                    var inside = cell.Contents;
                    Hud.DropStack(inside.WithCount(gesture == Gesture.DropOne ? 1 : inside.MaxStack));
                    break;
                }
                case Move.ClearInventory:
                    ClearInventoryButVacpack();
                    break;
            }
        }

        /// <summary>Shift-click: runs the cell's transfer route again while it keeps moving the same item (bulk crafting).</summary>
        private void TransferRepeatedly(Cell c)
        {
            int rounds = 0;
            ItemStack moved;
            do
            {
                moved = TransferOnce(c);
                rounds++;
            }
            while (rounds < TransferRounds && !moved.IsEmpty && c.Holding && c.Contents.Id == moved.Id);
        }

        private TransferRoute RouteFor(int number)
        {
            foreach (var r in Routes)
                if (r.Covers(number)) return r;
            return null;
        }

        /// <summary>One shift-click transfer out of a cell. Returns a copy of what the cell held if anything moved.</summary>
        private ItemStack TransferOnce(Cell c)
        {
            var route = RouteFor(c.Number);
            if (route == null) return ItemStack.Empty;
            if (route.Clears)
            {
                var inside = c.Contents;
                if (!inside.IsEmpty && inside.Id != Content.Vacpack) StackMoves.Write(c, ItemStack.Empty);
                return ItemStack.Empty;
            }
            if (!c.Holding) return ItemStack.Empty;
            var live = c.Contents;
            var before = live.Copy();
            foreach (var range in route.Into)
                if (Spill(live, range)) break;
            return Settle(c, live, before);
        }

        /// <summary>
        /// Moves items of <paramref name="stack"/> (changed in place) into a range: first onto matching stacks in walk
        /// order, then into the first vacant cell that allows it. True if anything moved.
        /// </summary>
        private bool Spill(ItemStack stack, CellRange range)
        {
            bool any = stack.MaxStack > 1 && stack.Damage == 0 && TopUpMatching(stack, range);
            if (!stack.IsEmpty && PlaceInFirstVacant(stack, range)) any = true;
            return any;
        }

        private bool TopUpMatching(ItemStack stack, CellRange range)
        {
            bool any = false;
            for (int k = 0; k < range.Length && !stack.IsEmpty; k++)
            {
                int n = range.At(k);
                if (!panel.Has(n) || !panel[n].Shown) continue;
                var c = panel[n];
                var inside = c.Contents;
                if (!StackMoves.Alike(inside, stack)) continue;
                int room = Cell.Limit(inside) - inside.Count;
                if (room <= 0) continue;
                int add = Math.Min(room, stack.Count);
                inside.Count += add;
                stack.Count -= add;
                StackMoves.Touch(c);
                any = true;
            }
            return any;
        }

        private bool PlaceInFirstVacant(ItemStack stack, CellRange range)
        {
            for (int k = 0; k < range.Length; k++)
            {
                int n = range.At(k);
                if (!panel.Has(n)) continue;
                var c = panel[n];
                if (!c.Shown || c.Holding || !c.Allows(stack)) continue;
                StackMoves.Write(c, StackMoves.Peel(stack, Math.Min(stack.Count, Cell.Limit(stack))));
                StackMoves.Touch(c);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Ends a transfer: stores what is left in the source, runs its after-take behaviour if anything moved, and
        /// throws crafted items that found no room (an output cell cannot keep a partial stack).
        /// </summary>
        private ItemStack Settle(Cell source, ItemStack live, ItemStack before)
        {
            int moved = before.Count - live.Count;
            if (moved == 0)
            {
                StackMoves.Touch(source); // nothing found room; the source keeps its whole stack
                return ItemStack.Empty;
            }
            bool drained = live.IsEmpty;
            if (drained) StackMoves.Write(source, ItemStack.Empty);
            else StackMoves.Touch(source);
            StackMoves.Taken(source, live);
            if (!drained && source.Role == CellRole.Output) ThrowSurplus(live);
            return before;
        }

        /// <summary>Crafted items that found no room: thrown, else stored, else put in the hand. Empties <paramref name="live"/>.</summary>
        private void ThrowSurplus(ItemStack live)
        {
            var surplus = live.Copy();
            live.Count = 0;
            if (Hud.DropStack(surplus)) return;
            int left = PInv.Inv.Add(surplus);
            if (left > 0) GiveToHand(surplus.WithCount(left));
        }

        /// <summary>
        /// Double click: fills the held stack from matching plain cells, partial stacks first and full ones after,
        /// as long as the clicked cell itself is empty.
        /// </summary>
        private void GatherInto(Cell clicked)
        {
            var hand = Hand;
            if (hand.IsEmpty || clicked.Holding) return;
            int cap = hand.MaxStack;
            Sweep(hand, cap, false);
            Sweep(hand, cap, true);
            Hand = hand;
        }

        private void Sweep(ItemStack hand, int cap, bool takeFullStacks)
        {
            for (int i = 0; i < panel.Count && hand.Count < cap; i++)
            {
                var c = panel[i];
                if (!c.Shown || !c.InGestures) continue;
                var inside = c.Contents;
                if (!StackMoves.Alike(inside, hand)) continue;
                if (!takeFullStacks && inside.Count >= inside.MaxStack) continue;
                hand.Count += StackMoves.PullAndNotify(c, inside.Count, cap - hand.Count).Count;
            }
        }

        /// <summary>
        /// Number key over a cell: the cell and hotbar entry <paramref name="hotbar"/> trade contents. When the hotbar
        /// stack is larger than the cell takes, the cell gets what fits and its old contents go into the inventory.
        /// </summary>
        private void ExchangeWithHotbar(Cell c, int hotbar)
        {
            if (hotbar < 0 || hotbar > 8) return;
            var inv = PInv.Inv;
            var bar = inv.Get(hotbar);
            var there = c.Contents;
            if (bar.IsEmpty && there.IsEmpty) return;
            if (!bar.IsEmpty && !c.Allows(bar)) return;

            int limit = Cell.Limit(bar);
            if (bar.Count <= limit) // also covers an empty hotbar entry
            {
                inv.Set(hotbar, there);
                StackMoves.Write(c, bar);
                if (!there.IsEmpty) StackMoves.Taken(c, there);
                return;
            }

            StackMoves.Write(c, StackMoves.Peel(bar, limit));
            if (!there.IsEmpty)
            {
                StackMoves.Taken(c, there);
                int left = inv.Add(there);
                if (left > 0) Hud.DropStack(there.WithCount(left));
            }
            inv.NotifyChanged();
        }

        /// <summary>
        /// Drop key over a cell (empty hand): one item, or the whole stack. Over a crafting output the whole-stack
        /// throw keeps crafting while the same item comes out. Anything that cannot be thrown goes back.
        /// </summary>
        private void ThrowFrom(Cell c, bool single)
        {
            if (!Hand.IsEmpty) return;
            var inside = c.Contents;
            if (inside.IsEmpty || inside.Id == Content.Vacpack) return;
            int amount = single ? 1 : inside.Count;
            string id = inside.Id;
            for (int round = 0; round < ThrowRounds; round++)
            {
                var part = StackMoves.PullAndNotify(c, amount, int.MaxValue);
                if (part.IsEmpty) return;
                if (!Hud.DropStack(part))
                {
                    PutBack(c, part);
                    return;
                }
                if (single || !c.Holding || c.Contents.Id != id) return;
            }
        }

        /// <summary>Drop key on the creative inventory tab: one item or up to a full stack; failures go to the inventory.</summary>
        private void ThrowOnceFrom(Cell c, bool single)
        {
            var inside = c.Contents;
            if (inside.IsEmpty || inside.Id == Content.Vacpack) return;
            var part = StackMoves.Pull(c, single ? 1 : inside.MaxStack, int.MaxValue) ?? ItemStack.Empty;
            if (part.IsEmpty || Hud.DropStack(part)) return;
            int left = PInv.Inv.Add(part);
            if (left > 0) ReturnToPlayer(part.WithCount(left));
        }

        /// <summary>Puts items back into the cell if it takes them, else into the inventory, else into the hand.</summary>
        private void PutBack(Cell c, ItemStack st)
        {
            var rest = c.Allows(st) ? StackMoves.Push(c, st, st.Count) : st;
            if (rest.IsEmpty) return;
            int left = PInv.Inv.Add(rest);
            if (left > 0) GiveToHand(rest.WithCount(left));
        }

        /// <summary>Overflow goes into the hand (merged when it matches); as a last resort it is dropped.</summary>
        private void GiveToHand(ItemStack st)
        {
            var hand = Hand;
            if (hand.IsEmpty) Hand = st;
            else if (StackMoves.Alike(hand, st)) hand.Count += st.Count;
            else Hud.DropStack(st, true);
        }

        /// <summary>Overwrites a hotbar entry; a vacpack sitting there moves into the inventory (or the hand) first.</summary>
        private void OverwriteHotbar(int hotbar, ItemStack stack)
        {
            var inv = PInv.Inv;
            var old = inv.Get(hotbar);
            if (old.IsEmpty || old.Id != Content.Vacpack)
            {
                inv.Set(hotbar, stack);
                return;
            }
            int left = inv.Add(old.Copy());
            inv.Set(hotbar, stack);
            if (left > 0) Hand = old;
        }

        private void ClearInventoryButVacpack()
        {
            var inv = PInv.Inv;
            for (int i = 0; i < inv.Size; i++)
            {
                var st = inv.Get(i);
                if (!st.IsEmpty && st.Id != Content.Vacpack) inv.Set(i, ItemStack.Empty);
            }
        }

        /// <summary>Gives a stack to the player; whatever does not fit is dropped in the world.</summary>
        protected void ReturnToPlayer(ItemStack st)
        {
            if (st == null || st.IsEmpty) return;
            int left = PInv.Give(st);
            if (left > 0) Hud.DropStack(st.WithCount(left), true);
        }

        // ================================================================== lifecycle

        public override void Closed()
        {
            if (Hud == null) return; // never opened: nothing to give back
            var hand = Hand;
            Hand = ItemStack.Empty;
            ReturnToPlayer(hand);
            Hud.RequestVacpackCheck();
        }

        /// <summary>Stacks owned by the page itself (crafting grid) that a save made right now must include.</summary>
        public virtual IEnumerable<ItemStack> ScreenOwnedStacks()
        {
            yield break;
        }
    }
}
