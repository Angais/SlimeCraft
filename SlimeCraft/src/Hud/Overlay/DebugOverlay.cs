using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// F3-style debug text overlay laid out like Minecraft's debug screen: a left and a right column of short lines,
    /// each non-empty line on its own translucent grey box. The content is our own report of Slime Rancher and Unity
    /// state, with several lines worded like the familiar Minecraft entries. Also owns the fps counter it shows.
    /// </summary>
    internal sealed class DebugOverlay
    {
        private const float RefreshSeconds = 0.05f; // about one rebuild per 20 Hz tick
        private const int Pitch = 9;
        private const uint BoxColor = 0x90505050u;
        private const uint TextColor = 0xFFE0E0E0u;
        private const float TargetReach = 20f;
        private const long MiB = 1048576L;

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static readonly string[] CompassNames = { "south", "west", "north", "east" };
        private static readonly string[] CompassAxes = { "Towards positive Z", "Towards negative X", "Towards negative Z", "Towards positive X" };

        private int framesThisWindow;
        private float windowSeconds;
        private int fps;

        private float nextRefresh = float.NegativeInfinity;
        private readonly List<string> leftLines = new List<string>(32);
        private readonly List<string> rightLines = new List<string>(32);
        private readonly List<int> leftWidths = new List<int>(32);
        private readonly List<int> rightWidths = new List<int>(32);

        // ------------------------------------------------------------------ fps

        /// <summary>Counts one rendered frame (called every frame, visible or not).</summary>
        public void CountFrame()
        {
            framesThisWindow++;
            windowSeconds += Time.unscaledDeltaTime;
            if (windowSeconds < 1f) return;
            fps = framesThisWindow;
            framesThisWindow = 0;
            windowSeconds -= 1f;
            if (windowSeconds > 1f) windowSeconds = 0f; // long hitch: start a fresh window
        }

        // ------------------------------------------------------------------ drawing

        public void Render(Gui g, HudModule hud)
        {
            float now = Time.unscaledTime;
            if (now >= nextRefresh)
            {
                nextRefresh = now + RefreshSeconds;
                Refresh(g, hud);
            }
            if (g.Font == null || !g.Font.Loaded) return;
            g.LayerBreak();
            PaintColumn(g, PlaceColumn(g, leftLines, leftWidths, false));
            PaintColumn(g, PlaceColumn(g, rightLines, rightWidths, true));
        }

        /// <summary>Where one non-empty line goes: its text origin and its measured width.</summary>
        private struct LinePlacement
        {
            public string Text;
            public int X, Y, W;
        }

        private readonly List<LinePlacement> placed = new List<LinePlacement>(32);

        /// <summary>
        /// Lays out a column: line i sits at y = 2 + 9i, hugging the left edge or (right column) the right edge with a
        /// 2 px margin. Empty lines keep their row but get no entry. The result is reused by the next call.
        /// </summary>
        private List<LinePlacement> PlaceColumn(Gui g, List<string> lines, List<int> widths, bool rightAligned)
        {
            placed.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                if (string.IsNullOrEmpty(lines[i])) continue;
                int w = i < widths.Count ? widths[i] : g.Font.Width(lines[i]);
                placed.Add(new LinePlacement { Text = lines[i], X = rightAligned ? g.Width - 2 - w : 2, Y = 2 + Pitch * i, W = w });
            }
            return placed;
        }

        /// <summary>
        /// Every backing box of the column before any text, so no box can cover text (underlines reach down). A box
        /// runs from one pixel left of and above the text origin to one pixel past its width, 9 px tall in all.
        /// </summary>
        private static void PaintColumn(Gui g, List<LinePlacement> column)
        {
            foreach (var p in column) g.Fill(p.X - 1, p.Y - 1, p.X + p.W + 1, p.Y + 8, BoxColor);
            foreach (var p in column) g.Text(p.Text, p.X, p.Y, TextColor, false);
        }

        private void Refresh(Gui g, HudModule hud)
        {
            leftLines.Clear();
            rightLines.Clear();
            try { BuildLeft(hud); }
            catch (Exception e) { HudModule.LogLimited("debug", "F3 build failed: " + e); }
            try { BuildRight(hud); }
            catch (Exception e) { HudModule.LogLimited("debug", "F3 build failed: " + e); }
            Measure(g, leftLines, leftWidths);
            Measure(g, rightLines, rightWidths);
        }

        private static void Measure(Gui g, List<string> lines, List<int> widths)
        {
            widths.Clear();
            for (int i = 0; i < lines.Count; i++) widths.Add(g.Font != null ? g.Font.Width(lines[i]) : 0);
        }

        // ------------------------------------------------------------------ left column

        private void BuildLeft(HudModule hud)
        {
            string version = SC.Assets?.McVersion ?? "?";
            leftLines.Add("SlimeCraft " + SC.Version + " (Minecraft " + version + " assets, Slime Rancher)");
            int target = Application.targetFrameRate;
            leftLines.Add(fps.ToString(Inv) + " fps T: " + (target <= 0 ? "inf" : target.ToString(Inv))
                + (QualitySettings.vSyncCount > 0 ? " vsync" : ""));
            leftLines.Add("");

            var sr = SC.SR;
            if (sr == null || !sr.InGame) return;

            Vector3 feet = sr.PlayerFeet;
            leftLines.Add("XYZ: " + feet.x.ToString("0.000", Inv) + " / " + feet.y.ToString("0.00000", Inv) + " / " + feet.z.ToString("0.000", Inv));
            int bx = Mathf.FloorToInt(feet.x), by = Mathf.FloorToInt(feet.y), bz = Mathf.FloorToInt(feet.z);
            leftLines.Add("Block: " + N(bx) + " " + N(by) + " " + N(bz));
            int cx = FloorDiv(bx, 16), cy = FloorDiv(by, 16), cz = FloorDiv(bz, 16);
            leftLines.Add("Chunk: " + N(cx) + " " + N(cy) + " " + N(cz) + " [" + N(FloorMod(cx, 32)) + " " + N(FloorMod(cz, 32))
                + " in r." + N(FloorDiv(cx, 32)) + "." + N(FloorDiv(cz, 32)) + ".mca]");
            leftLines.Add(FacingLine(sr.LookDirection));
            leftLines.Add("slimerancher:" + Snake(sr.ZoneName) + " FC: 0");
            leftLines.Add("");
            leftLines.Add("Biome: " + (sr.ZoneName ?? "?"));

            float dayPart = sr.DayFraction - Mathf.Floor(sr.DayFraction);
            int minutes = (int)(dayPart * 1440f);
            leftLines.Add("Day #" + N(sr.DayNumber) + ", " + (minutes / 60).ToString("00", Inv) + ":" + (minutes % 60).ToString("00", Inv)
                + (sr.IsNight ? " (night)" : ""));
            leftLines.Add("Health: " + N(sr.Health) + "/" + N(sr.MaxHealth) + "  Energy: " + N(sr.Energy) + "/" + N(sr.MaxEnergy)
                + "  Newbucks: " + N(sr.Newbucks));
            leftLines.Add("Mobs: " + N(SC.Entities != null ? SC.Entities.LiveMobCount : 0));
            leftLines.Add("Blocks placed: " + N(SC.Blocks != null ? SC.Blocks.Count : 0));

            var inv = hud.PInv;
            bool creative = inv != null && inv.Creative;
            bool vac = inv != null && inv.VacpackSelected;
            leftLines.Add("Game mode: " + (creative ? "Creative" : "Survival") + (vac ? " (vacpack)" : ""));
            leftLines.Add("Recipes: " + N(Recipes.Count) + (Recipes.Loaded ? "" : " (loading)"));
        }

        /// <summary>"Facing" line using Minecraft's yaw convention (0 = +Z/south, 90 = -X/west; positive pitch = down).</summary>
        private static string FacingLine(Vector3 look)
        {
            float yaw = Mathf.Atan2(-look.x, look.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Asin(Mathf.Clamp(look.y, -1f, 1f)) * Mathf.Rad2Deg;
            int quadrant = FloorMod(Mathf.FloorToInt(yaw / 90f + 0.5f), 4);
            return "Facing: " + CompassNames[quadrant] + " (" + CompassAxes[quadrant] + ") ("
                + WrapDegrees(yaw).ToString("0.0", Inv) + " / " + WrapDegrees(pitch).ToString("0.0", Inv) + ")";
        }

        // ------------------------------------------------------------------ right column

        private void BuildRight(HudModule hud)
        {
            rightLines.Add("Unity: " + Application.unityVersion + " " + N(IntPtr.Size * 8) + "bit");

            long used, reserved;
            try
            {
                used = Profiler.GetTotalAllocatedMemoryLong();
                reserved = Profiler.GetTotalReservedMemoryLong();
            }
            catch
            {
                used = reserved = GC.GetTotalMemory(false);
            }
            long total = Math.Max(1L, SystemInfo.systemMemorySize * MiB);
            rightLines.Add("Mem: " + (used * 100 / total).ToString(Inv).PadLeft(2) + "% " + Mebibytes(used) + "/" + Mebibytes(total) + "MiB");
            rightLines.Add("Allocated: " + (reserved * 100 / total).ToString(Inv).PadLeft(2) + "% " + Mebibytes(reserved) + "MiB");
            rightLines.Add("Mono heap: " + Mebibytes(GC.GetTotalMemory(false)) + "MiB");
            rightLines.Add("");
            rightLines.Add("CPU: " + N(SystemInfo.processorCount) + "x " + SystemInfo.processorType);
            rightLines.Add("");
            rightLines.Add("Display: " + N(UnityEngine.Screen.width) + "x" + N(UnityEngine.Screen.height) + " (" + SystemInfo.graphicsDeviceVendor + ")");
            rightLines.Add(SystemInfo.graphicsDeviceName);
            rightLines.Add(SystemInfo.graphicsDeviceVersion);
            rightLines.Add("GUI scale: " + N(hud.GuiScale));

            var sr = SC.SR;
            if (sr == null || !sr.InGame) return;
            var ray = new Ray(sr.EyePosition, sr.LookDirection);
            AddTargetedBlock(sr, ray);
            AddTargetedEntity(sr, ray);
        }

        private void AddTargetedBlock(ISRBridge sr, Ray ray)
        {
            var blocks = SC.Blocks;
            if (blocks != null && blocks.Raycast(ray, TargetReach, out BlockHit bh))
            {
                rightLines.Add("");
                rightLines.Add("§nTargeted Block: " + N(bh.Pos.x) + ", " + N(bh.Pos.y) + ", " + N(bh.Pos.z));
                rightLines.Add(blocks.GetBlock(bh.Pos) ?? "minecraft:air");
                rightLines.Add("facing=" + blocks.GetFacing(bh.Pos).ToString().ToLowerInvariant());
                return;
            }
            if (sr.RaycastWorld(ray, TargetReach, out RaycastHit hit))
            {
                Vector3 inside = hit.point - hit.normal * 0.01f; // nudge into the surface that was hit
                rightLines.Add("");
                rightLines.Add("§nTargeted Block: " + N(Mathf.FloorToInt(inside.x)) + ", " + N(Mathf.FloorToInt(inside.y)) + ", " + N(Mathf.FloorToInt(inside.z)));
                rightLines.Add("slimerancher:" + Snake(hit.collider != null ? hit.collider.name : "terrain"));
            }
        }

        private void AddTargetedEntity(ISRBridge sr, Ray ray)
        {
            var entities = SC.Entities;
            if (entities == null || !entities.RaycastEntity(ray, TargetReach, out EntityHit eh) || eh.Target == null) return;
            rightLines.Add("");
            rightLines.Add("§nTargeted Entity");
            string id;
            if (eh.IsMcMob) id = eh.Target.name;
            else id = "slimerancher:" + (sr.GetSRActorId(eh.Target) ?? eh.Target.name).ToLowerInvariant();
            rightLines.Add(id);
        }

        // ------------------------------------------------------------------ helpers

        private static string N(int v) => v.ToString(Inv);

        private static string Mebibytes(long bytes) => (bytes / MiB).ToString("000", Inv);

        private static int FloorDiv(int a, int b)
        {
            int q = a / b;
            if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
            return q;
        }

        private static int FloorMod(int a, int b)
        {
            int m = a % b;
            return m < 0 ? m + b : m;
        }

        /// <summary>Wraps an angle into [-180, 180).</summary>
        private static float WrapDegrees(float deg)
        {
            float d = deg % 360f;
            if (d >= 180f) d -= 360f;
            if (d < -180f) d += 360f;
            return d;
        }

        /// <summary>Identifier-style id: lower case letters and digits, any other run becomes a single '_'.</summary>
        private static string Snake(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new StringBuilder(s.Length);
            bool gap = false;
            foreach (char c in s.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (gap && sb.Length > 0) sb.Append('_');
                    gap = false;
                    sb.Append(c);
                }
                else gap = true;
            }
            return sb.ToString();
        }
    }
}
