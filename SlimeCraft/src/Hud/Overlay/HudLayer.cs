using System;
using System.Globalization;
using TMPro;
using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// The in-game HUD drawn in Minecraft's style over Slime Rancher: crosshair, hotbar with a pickup "pop", hearts fed
    /// by Slime Rancher health, food fed by energy, an experience bar fed by newbucks, a strip with the vacpack's ammo
    /// slots, the selected-item name, the action bar, titles and a small clock. Timed state (fades, pops, heart blink)
    /// advances on the Hud module's 20 Hz tick.
    /// </summary>
    internal sealed class HudLayer
    {
        private const string HotbarSprite = "gui/sprites/hud/hotbar";
        private const string HotbarSelectionSprite = "gui/sprites/hud/hotbar_selection";
        private const string CrosshairSprite = "gui/sprites/hud/crosshair";
        private const string HeartContainer = "gui/sprites/hud/heart/container";
        private const string HeartContainerBlinking = "gui/sprites/hud/heart/container_blinking";
        private const string HeartFull = "gui/sprites/hud/heart/full";
        private const string HeartHalf = "gui/sprites/hud/heart/half";
        private const string HeartFullBlinking = "gui/sprites/hud/heart/full_blinking";
        private const string HeartHalfBlinking = "gui/sprites/hud/heart/half_blinking";
        private const string FoodEmpty = "gui/sprites/hud/food_empty";
        private const string FoodHalf = "gui/sprites/hud/food_half";
        private const string FoodFull = "gui/sprites/hud/food_full";
        private const string XpBackground = "gui/sprites/hud/experience_bar_background";
        private const string XpProgress = "gui/sprites/hud/experience_bar_progress";

        private const int PopTicks = 5;
        private const int NameTicks = 40;
        private const int TitleFadeInTicks = 10;
        private const int TitleFadeOutTicks = 20;
        private const int RowY = 39;            // hearts and food sit this far above the bottom edge
        private const uint LevelGreen = 0xFF80FF20u;
        private const uint FullAmmoYellow = 0xFFFFFF55u;
        private const uint NightClock = 0xFFAAAAFFu;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        /// <summary>The level number gets a black copy one pixel away in each of these directions.</summary>
        private static readonly int[] OutlineDx = { 1, -1, 0, 0 }, OutlineDy = { 0, 0, 1, -1 };

        /// <summary>
        /// Heart row bookkeeping: detects damage and healing from health samples, decides when the row blinks, and
        /// keeps a trailing value (shown by the blinking hearts) that catches up with the real one after a second.
        /// </summary>
        private struct HeartState
        {
            private bool primed;
            private int lastRaw;
            private int lastHalves;
            private long trailSinceMs;
            private int blinkUntil;
            private int woundedUntil;

            /// <summary>Half hearts the blinking overlay still shows.</summary>
            public int Trail;

            /// <summary>Damage reported by Slime Rancher: start the hurt window and blink at once.</summary>
            public void Wound(int tick, long nowMs)
            {
                woundedUntil = tick + 20;
                blinkUntil = tick + 20;
                trailSinceMs = nowMs;
            }

            /// <summary>One 20 Hz sample of raw health and the derived half hearts.</summary>
            public void Sample(int tick, int raw, int halves, long nowMs)
            {
                if (!primed)
                {
                    primed = true;
                    lastRaw = raw;
                    lastHalves = halves;
                    Trail = halves;
                    trailSinceMs = nowMs;
                    return;
                }
                if (raw < lastRaw) woundedUntil = tick + 20;
                lastRaw = raw;

                int change = halves - lastHalves;
                lastHalves = halves;
                if (change != 0 && tick < woundedUntil)
                {
                    // losing health blinks for a second, gaining it (inside the hurt window) for half a second
                    blinkUntil = tick + (change < 0 ? 20 : 10);
                    trailSinceMs = nowMs;
                }
                if (nowMs - trailSinceMs > 1000)
                {
                    Trail = halves;
                    trailSinceMs = nowMs;
                }
            }

            /// <summary>Blinking alternates every three ticks while the blink window lasts.</summary>
            public bool Lit(int tick) => blinkUntil > tick && (blinkUntil - tick) / 3 % 2 == 1;
        }

        /// <summary>Hotbar entry memory for the pickup pop.</summary>
        private struct PopTrack
        {
            public int Left;
            public bool Known;
            public string Id;
            public int Count;
        }

        private readonly HudModule hud;
        public readonly VacAmmo Ammo = new VacAmmo();
        private readonly JavaRandom jitter = new JavaRandom(0);
        private readonly PopTrack[] pops = new PopTrack[9];
        private HeartState hearts;

        // selected-item name
        private string nameText;
        private int nameLeft;
        private string nameItemId;

        // vac ammo selection memory (-2 = not tracking: the vacpack is not selected)
        private const int AmmoUntracked = -2;
        private bool ammoValid;
        private int ammoSlotSeen = AmmoUntracked;
        private Identifiable.Id ammoIdSeen = Identifiable.Id.NONE;

        // action bar
        private string barText;
        private int barLeft;
        private bool barRainbow;

        // title
        private string titleText, subtitleText;
        private int titleLeft;
        private int titleHold;

        // cached strings
        private int levelShown = -1;
        private string levelText;
        private int clockKey = int.MinValue;
        private string clockText;
        private float srClockCheckAt;
        private bool srClockShown;

        public HudLayer(HudModule hud)
        {
            this.hud = hud;
        }

        private static long NowMs() => (long)(Time.unscaledTime * 1000f);

        private static int ToTicks(float seconds) => (int)Math.Floor(seconds * 20f + 0.5f);

        // ================================================================== state

        public void Reset()
        {
            hearts = default(HeartState);
            nameLeft = 0;
            nameText = null;
            nameItemId = null;
            barText = null;
            barLeft = 0;
            titleText = null;
            subtitleText = null;
            titleLeft = 0;
            Array.Clear(pops, 0, pops.Length);
            ammoSlotSeen = AmmoUntracked;
            ammoIdSeen = Identifiable.Id.NONE;
            Ammo.ClearCaches();
        }

        /// <summary>20 Hz update while in game.</summary>
        public void Tick()
        {
            if (barLeft > 0) barLeft--;
            if (titleLeft > 0 && --titleLeft == 0)
            {
                titleText = null;
                subtitleText = null;
            }
            for (int i = 0; i < pops.Length; i++)
                if (pops[i].Left > 0) pops[i].Left--;
            StepItemName();
            StepAmmoName();   // after the item name, so an ammo change wins in the same tick
            StepHearts();
        }

        public void StartPop(int slot)
        {
            if (slot >= 0 && slot < pops.Length) pops[slot].Left = PopTicks;
        }

        /// <summary>Pops hotbar entries whose stack grew since the previous frame (an item was picked up).</summary>
        public void TrackPickups(bool screenOpen)
        {
            var inv = hud.PInv?.Inv;
            if (inv == null) return;
            for (int i = 0; i < pops.Length; i++)
            {
                var now = inv.Get(i);
                ref var seen = ref pops[i];
                bool grew = seen.Known && !now.IsEmpty && now.Id == seen.Id && now.Count > seen.Count;
                if (grew && !screenOpen) StartPop(i);
                seen.Known = !now.IsEmpty;
                seen.Id = now.IsEmpty ? null : now.Id;
                seen.Count = now.IsEmpty ? 0 : now.Count;
            }
        }

        public void ShowActionBar(string text, float seconds, bool animate = false)
        {
            barText = text;
            barLeft = Math.Max(1, ToTicks(seconds));
            barRainbow = animate;
        }

        public void ShowTitle(string title, string subtitle, float seconds)
        {
            titleText = title;
            subtitleText = subtitle;
            titleHold = Math.Max(0, ToTicks(seconds));
            titleLeft = TitleFadeInTicks + titleHold + TitleFadeOutTicks;
        }

        public void PlayerHurt() => hearts.Wound(hud.TickCount, NowMs());

        private void StepItemName()
        {
            var inv = hud.PInv;
            if (inv == null) return;
            var held = inv.Selected;
            if (held == null || held.IsEmpty)
            {
                nameLeft = 0;
                nameItemId = null;
                return;
            }
            if (held.Id == nameItemId)
            {
                if (nameLeft > 0) nameLeft--;
                return;
            }
            var lines = Gui.TooltipLines(held);
            nameText = lines.Count > 0 ? lines[0] : "";
            nameItemId = held.Id;
            nameLeft = NameTicks;
        }

        private void StepAmmoName()
        {
            var inv = hud.PInv;
            if (inv == null || !inv.VacpackSelected)
            {
                ammoSlotSeen = AmmoUntracked;
                ammoIdSeen = Identifiable.Id.NONE;
                return;
            }
            if (!ammoValid) return;
            int sel = Ammo.Selected;
            if (sel < 0 || sel >= Ammo.SlotCount) return;
            var id = Ammo.Ids[sel];
            if (sel == ammoSlotSeen && id == ammoIdSeen) return;
            bool firstLook = ammoSlotSeen == AmmoUntracked;
            ammoSlotSeen = sel;
            ammoIdSeen = id;
            if (firstLook) return;
            nameText = Ammo.Name(id) ?? ("§7" + Gui.Tr("gui.none", "Nothing"));
            nameLeft = NameTicks;
        }

        /// <summary>Slime Rancher health mapped onto 20 half hearts (any health left shows at least half a heart).</summary>
        private static int HalfHearts(ISRBridge sr)
        {
            if (sr == null || sr.MaxHealth <= 0) return 20;
            int health = sr.Health;
            int halves = Mathf.CeilToInt(Mathf.Clamp01(health / (float)sr.MaxHealth) * 20f);
            return health > 0 ? Math.Max(1, halves) : halves;
        }

        private void StepHearts()
        {
            var sr = SC.SR;
            if (sr == null || !sr.InGame || sr.MaxHealth <= 0) return;
            hearts.Sample(hud.TickCount, sr.Health, HalfHearts(sr), NowMs());
        }

        // ================================================================== frame

        public void Render(Gui g, float partialTick, bool mcHud)
        {
            int w = g.Width, h = g.Height;
            var inv = hud.PInv;
            bool vacSelected = inv != null && inv.VacpackSelected;
            ammoValid = vacSelected && ReadAmmo();

            if (!mcHud || inv == null)
            {
                DrawActionBar(g, w, h, 49, partialTick);
                DrawTitle(g, w, h, partialTick);
                return;
            }

            g.DrawSprite(CrosshairSprite, (w - 15) / 2, (h - 15) / 2, 15, 15);
            DrawHotbar(g, inv, w, h, partialTick);

            bool survival = !inv.Creative;
            int lift = RowY;    // how far above the bottom the text lines must start
            if (survival)
            {
                jitter.SetSeed(hud.TickCount * 312871L);
                DrawHearts(g, w, h - RowY);
                DrawFood(g, w, h - RowY);
                DrawExperience(g, w, h);
                lift = RowY + 10;
            }

            bool stripShown = vacSelected && ammoValid && HudConfig.AmmoStrip && Ammo.SlotCount > 0;
            if (stripShown)
            {
                int stripBase = survival ? lift : 25;
                DrawAmmoStrip(g, w, h, stripBase);
                lift = stripBase + 26;
            }

            DrawItemName(g, w, h, lift, survival || stripShown);
            DrawActionBar(g, w, h, lift, partialTick);
            DrawClock(g, w);
            DrawTitle(g, w, h, partialTick);
        }

        private bool ReadAmmo()
        {
            try { return Ammo.Read(); }
            catch (Exception e)
            {
                HudModule.LogLimited("ammo", "vac ammo read failed: " + e.Message);
                return false;
            }
        }

        private void DrawHotbar(Gui g, PlayerInventory inv, int w, int h, float partialTick)
        {
            int mid = w / 2;
            g.DrawSprite(HotbarSprite, mid - 91, h - 22, 182, 22);
            g.DrawSprite(HotbarSelectionSprite, mid - 92 + 20 * inv.SelectedSlot, h - 23, 24, 23);

            int iconY = h - 19;
            for (int i = 0; i < 9; i++)
            {
                var st = inv.Inv.Get(i);
                if (!st.IsEmpty) DrawHotbarIcon(g, st, mid - 88 + 20 * i, iconY, pops[i].Left - partialTick);
            }
            g.LayerBreak();
            for (int i = 0; i < 9; i++)
            {
                var st = inv.Inv.Get(i);
                if (!st.IsEmpty) g.DrawItemOverlay(mid - 88 + 20 * i, iconY, st);
            }
        }

        /// <summary>
        /// A popping icon starts narrow and tall and eases back to its normal shape around a point near its base.
        /// </summary>
        private static void DrawHotbarIcon(Gui g, ItemStack st, int x, int y, float popLeft)
        {
            if (popLeft <= 0f)
            {
                g.DrawItem(x, y, st);
                return;
            }
            float stretch = 1f + popLeft / PopTicks;
            float px = x + 8f, py = y + 12f;
            g.Push();
            g.Translate(px, py);
            g.Scale(1f / stretch, (stretch + 1f) / 2f);
            g.Translate(-px, -py);
            g.DrawItem(x, y, st);
            g.Pop();
        }

        /// <summary>Which fill sprite icon <paramref name="index"/> of a 10-icon row shows for a value in halves (null = none).</summary>
        private static string FillFor(int value, int index, string full, string half)
        {
            int firstHalf = 2 * index + 1;
            if (value > firstHalf) return full;
            return value == firstHalf ? half : null;
        }

        private void DrawHearts(Gui g, int w, int rowY)
        {
            var sr = SC.SR;
            if (sr == null || sr.MaxHealth <= 0) return;
            int value = HalfHearts(sr);
            bool lit = hearts.Lit(hud.TickCount);
            bool trembling = value <= 4;
            int rowLeft = w / 2 - 91;

            // drawn right to left so every heart overlaps its right neighbour; one random draw per heart when low
            for (int i = 9; i >= 0; i--)
            {
                int x = rowLeft + 8 * i;
                int y = trembling ? rowY + jitter.NextInt(2) : rowY;
                g.DrawSprite(lit ? HeartContainerBlinking : HeartContainer, x, y, 9, 9);
                string trail = lit ? FillFor(hearts.Trail, i, HeartFullBlinking, HeartHalfBlinking) : null;
                if (trail != null) g.DrawSprite(trail, x, y, 9, 9);
                string fill = FillFor(value, i, HeartFull, HeartHalf);
                if (fill != null) g.DrawSprite(fill, x, y, 9, 9);
            }
        }

        private void DrawFood(Gui g, int w, int rowY)
        {
            var sr = SC.SR;
            if (sr == null || sr.MaxEnergy <= 0) return;
            float fraction = Mathf.Clamp01(sr.Energy / (float)sr.MaxEnergy);
            int value = Mathf.CeilToInt(fraction * 20f);
            // low energy stands in for exhausted saturation: the emptier the bar, the more often it shakes
            bool shaking = fraction < 0.5f && hud.TickCount % (value * 3 + 1) == 0;
            int rowRight = w / 2 + 91;
            for (int i = 0; i < 10; i++)
            {
                int x = rowRight - 9 - 8 * i;
                int y = shaking ? rowY + jitter.NextInt(3) - 1 : rowY;
                g.DrawSprite(FoodEmpty, x, y, 9, 9);
                string fill = FillFor(value, i, FoodFull, FoodHalf);
                if (fill != null) g.DrawSprite(fill, x, y, 9, 9);
            }
        }

        private void DrawExperience(Gui g, int w, int h)
        {
            var sr = SC.SR;
            int bucks = Math.Max(0, sr != null ? sr.Newbucks : 0);
            int left = (w - 182) / 2, top = h - 29;
            g.DrawSprite(XpBackground, left, top, 182, 5);
            int fill = Math.Min(182, (int)((bucks % 1000) / 1000f * 183f));
            if (fill > 0) g.DrawSheetPart(XpProgress, left, top, fill, 5, 0, 0, 182, 5);
            if (bucks > 0) DrawLevel(g, w, h, bucks);
        }

        private void DrawLevel(Gui g, int w, int h, int level)
        {
            if (level != levelShown)
            {
                levelShown = level;
                levelText = level.ToString(Inv);
            }
            int x = (w - g.Font.Width(levelText)) / 2, y = h - 35;
            for (int k = 0; k < OutlineDx.Length; k++)
                g.Text(levelText, x + OutlineDx[k], y + OutlineDy[k], Argb.Black, false);
            g.Text(levelText, x, y, LevelGreen, false);
        }

        private void DrawAmmoStrip(Gui g, int w, int h, int stripBase)
        {
            int n = Math.Min(Ammo.SlotCount, VacAmmo.MaxSlots);
            int x0 = w / 2 - (20 * n + 2) / 2;
            int y0 = h - stripBase - 24;

            // a hotbar cut down to n cells plus its one-pixel right border
            g.DrawSheetPart(HotbarSprite, x0, y0, 1 + 20 * n, 22, 0, 0, 182, 22);
            g.DrawSheetPart(HotbarSprite, x0 + 1 + 20 * n, y0, 1, 22, 181, 0, 182, 22);
            int sel = Ammo.Selected;
            if (sel >= 0 && sel < n) g.DrawSprite(HotbarSelectionSprite, x0 - 1 + 20 * sel, y0 - 1, 24, 23);

            for (int i = 0; i < n; i++)
            {
                int ix = x0 + 3 + 20 * i, iy = y0 + 3;
                var icon = Ammo.Icon(Ammo.Ids[i]);
                if (icon != null) g.DrawSRSprite(icon, ix, iy, 16, 16);
                else if (Ammo.Water[i]) g.Fill(ix + 5, iy + 5, ix + 11, iy + 11, 0x403070FFu);
            }

            g.LayerBreak();
            for (int i = 0; i < n; i++) DrawAmmoGauge(g, i, x0 + 3 + 20 * i, y0 + 3);
        }

        /// <summary>Fill bar in the ammo colour and the count (yellow when the slot is full) of one ammo slot.</summary>
        private void DrawAmmoGauge(Gui g, int i, int ix, int iy)
        {
            var id = Ammo.Ids[i];
            int max = Ammo.Max[i];
            if (id == Identifiable.Id.NONE || max <= 0) return;
            int count = Ammo.Counts[i];
            g.Fill(ix + 2, iy + 13, ix + 15, iy + 15, Argb.Black);
            int barW = Mathf.Clamp((int)Math.Floor(13.0 * count / max + 0.5), 0, 13);
            if (barW > 0) g.Fill(ix + 2, iy + 13, ix + 2 + barW, iy + 14, Ammo.Color(id) | 0xFF000000u);
            if (count == 1) return;
            string label = Gui.CountString(count);
            // two pixels higher than an item count so it clears the fill bar
            g.Text(label, ix + 17 - g.Font.Width(label), iy + 7, count >= max ? FullAmmoYellow : Argb.White, true);
        }

        /// <summary>
        /// Opacity plan of a timed message as phase lengths in ticks: rise, hold, fall. The phase is chosen from the
        /// whole ticks left; the ramp value uses the interpolated time, so fades stay smooth between ticks.
        /// </summary>
        private struct FadeSchedule
        {
            public readonly int Rise, Hold, Fall;

            public FadeSchedule(int rise, int hold, int fall)
            {
                Rise = rise;
                Hold = hold;
                Fall = fall;
            }

            /// <summary>Alpha 0..255 with <paramref name="ticksLeft"/> whole ticks to go; <paramref name="t"/> = ticks left minus the partial tick.</summary>
            public int AlphaAt(int ticksLeft, float t)
            {
                int alpha;
                if (ticksLeft <= Fall) alpha = (int)Math.Floor(t * 255f / Fall);
                else if (Rise > 0 && ticksLeft > Fall + Hold) alpha = (int)Math.Floor((Rise + Hold + Fall - t) * 255f / Rise);
                else return 255;
                return Mathf.Clamp(alpha, 0, 255);
            }
        }

        /// <summary>The action bar has no rise phase: full strength until it fades out over its last 20 ticks.</summary>
        private static readonly FadeSchedule BarFade = new FadeSchedule(0, 0, 20);

        /// <summary>A text line centred on (axisX, axisY) and magnified about that point; lineY is in magnified units.</summary>
        private static void MagnifiedLine(Gui g, string text, int axisX, int axisY, float scale, int lineY, uint argb)
        {
            g.Push();
            g.Translate(axisX, axisY);
            g.Scale(scale, scale);
            g.TextOnAxis(text, 0, lineY, argb);
            g.Pop();
        }

        private void DrawItemName(Gui g, int w, int h, int lift, bool survivalLayout)
        {
            if (nameLeft <= 0 || string.IsNullOrEmpty(nameText)) return;
            // fades during the last ten ticks; with at least one tick left it never becomes fully clear
            uint argb = Argb.WithAlpha(0xFFFFFF, Math.Min(255, nameLeft * 256 / 10));
            int baseline = h - Math.Max(lift + 10, 59) + (survivalLayout ? 0 : 14);
            g.Text(nameText, (w - g.Font.Width(nameText)) / 2, baseline, argb, true);
        }

        private void DrawActionBar(Gui g, int w, int h, int lift, float partialTick)
        {
            if (barText == null || barLeft <= 0) return;
            float t = barLeft - partialTick;
            int alpha = BarFade.AlphaAt(barLeft, t);
            if (alpha == 0) return;
            uint rgb = barRainbow ? Argb.Hsv((t / 50f) % 1f, 0.7f, 0.6f) : 0xFFFFFFu;
            g.LayerBreak();
            g.TextOnAxis(barText, w / 2, h - Math.Max(lift + 23, 72), Argb.WithAlpha(rgb, alpha));
        }

        private void DrawTitle(Gui g, int w, int h, float partialTick)
        {
            if (titleText == null || titleLeft <= 0) return;
            var plan = new FadeSchedule(TitleFadeInTicks, titleHold, TitleFadeOutTicks);
            int alpha = plan.AlphaAt(titleLeft, titleLeft - partialTick);
            if (alpha == 0) return;
            uint argb = Argb.WithAlpha(0xFFFFFF, alpha);
            g.LayerBreak();
            // both lines hang off the screen centre: the title at 4x, the subtitle at 2x below it
            MagnifiedLine(g, titleText, w / 2, h / 2, 4f, -10, argb);
            if (subtitleText != null) MagnifiedLine(g, subtitleText, w / 2, h / 2, 2f, 5, argb);
        }

        private void DrawClock(Gui g, int w)
        {
            if (!HudConfig.Clock || hud.DebugVisible) return;
            var sr = SC.SR;
            if (sr == null || SlimeRancherClockVisible()) return;

            float dayPart = sr.DayFraction - Mathf.Floor(sr.DayFraction);
            int minutes = Mathf.Clamp((int)Math.Floor(dayPart * 1440f), 0, 1439);
            int day = sr.DayNumber;
            int key = day * 1440 + minutes;
            if (key != clockKey || clockText == null)
            {
                clockKey = key;
                clockText = "Day " + day.ToString(Inv) + "  " + (minutes / 60).ToString("00", Inv) + ":" + (minutes % 60).ToString("00", Inv);
            }
            int textW = g.Font.Width(clockText);
            g.Text(clockText, w - 2 - textW, 2, sr.IsNight ? NightClock : Argb.White, true);
        }

        /// <summary>
        /// Whether Slime Rancher's own day/time text is currently on screen (so two clocks never show).
        /// Polled at most twice per second because it touches Unity UI objects.
        /// </summary>
        private bool SlimeRancherClockVisible()
        {
            float now = Time.unscaledTime;
            if (now < srClockCheckAt) return srClockShown;
            srClockCheckAt = now + 0.5f;
            bool shown = false;
            try
            {
                var srHud = SRSingleton<global::HudUI>.Instance;
                TMP_Text label = srHud != null ? srHud.timeText : null;
                if (label != null && label.isActiveAndEnabled)
                {
                    var canvas = label.canvas;
                    shown = canvas != null && canvas.isActiveAndEnabled
                        && label.color.a > 0.05f
                        && label.canvasRenderer.GetInheritedAlpha() > 0.05f;
                }
            }
            catch
            {
                shown = false;
            }
            srClockShown = shown;
            return shown;
        }
    }
}
