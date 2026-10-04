using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SlimeCraft.Core
{
    /// <summary>
    /// The yellow, tilted, pulsing line of text on Slime Rancher's title screen, matching the look and feel of the
    /// splash under Minecraft's logo. A new line is chosen on every visit to the title screen, either from the
    /// player's own Minecraft jar (<c>texts/splashes.txt</c>, read at runtime) or from a short list written for
    /// SlimeCraft. It is drawn with IMGUI from <see cref="CoreModule"/> and only while the main title panel is shown.
    /// </summary>
    internal sealed class SplashText
    {
        private const string JarSplashPath = "assets/minecraft/texts/splashes.txt";
        private const float MenuSearchInterval = 1f;

        // Layout facts, in Minecraft GUI units unless noted.
        private const float TiltDegrees = 20f;        // rises toward the right
        private const float PulseBase = 1.8f;
        private const float PulseDepth = 0.1f;
        private const float TargetLength = 100f;      // longer lines are shrunk toward this length
        private const int LengthPadding = 32;
        private const int GlyphTop = 8;               // text top sits this many font units above the anchor
        private const float DefaultOffsetX = 123f;
        private const float DefaultOffsetY = 69f;

        // Minecraft-style automatic GUI scale limits.
        private const int MinGuiWidth = 320;
        private const int MinGuiHeight = 240;
        private const int MaxGuiScale = 8;

        /// <summary>One in this many picks comes from SlimeCraft's own lines when the jar list is available.</summary>
        private const int OwnLineOdds = 6;

        private static readonly Color32 SplashYellow = new Color32(255, 255, 0, 255);

        /// <summary>Lines written for SlimeCraft (original mod content).</summary>
        private static readonly string[] OwnLines =
        {
            "Now with plorts!",
            "Largo-compatible!",
            "Don't feed the Tarr!",
            "Vac it up!",
            "100% slime-free! (not really)",
            "Creepers vs. Boom Slimes!",
            "Carrots are a slime's best friend!",
            "Beatrix approved!",
            "Also try Slime Rancher 2!",
            "Ranching in the Far, Far Range!",
            "Mochi's Manor is not for sale!",
            "The Gold Slime got away!",
        };

        private readonly McAssets assets;
        private readonly System.Random random = new System.Random();

        private McFont font;
        private bool fontAttempted;
        private List<string> jarLines;           // null until first needed

        private bool onTitleScreen;
        private MainMenuUI titleMenu;
        private float nextMenuSearch;

        private bool needsNewLine = true;
        private Texture2D lineTexture;
        private int lineAdvance;

        public SplashText(McAssets assets)
        {
            this.assets = assets;
        }

        /// <summary>Per frame: follows title-screen visits and keeps a reference to the title menu.</summary>
        public void Tick()
        {
            bool nowOnTitle = IsTitleSceneActive();
            if (nowOnTitle && !onTitleScreen)
            {
                // a fresh visit: choose a new line on the next draw and look the menu up again
                needsNewLine = true;
                titleMenu = null;
                nextMenuSearch = 0f;
            }
            onTitleScreen = nowOnTitle;
            if (!onTitleScreen) return;

            if (titleMenu == null && Time.unscaledTime >= nextMenuSearch)
            {
                nextMenuSearch = Time.unscaledTime + MenuSearchInterval;
                titleMenu = Object.FindObjectOfType<MainMenuUI>();
            }
        }

        /// <summary>Per IMGUI event: draws the splash on repaint when every visibility condition holds.</summary>
        public void OnGUI()
        {
            if (!onTitleScreen) return;
            var enabled = CoreConfig.ShowSplash;
            if (enabled == null || !enabled.Value) return;

            Matrix4x4 savedMatrix = GUI.matrix;
            try
            {
                if (assets == null || !assets.Ready) return;
                var ev = Event.current;
                if (ev == null || ev.type != EventType.Repaint) return;
                if (titleMenu == null || !titleMenu.isActiveAndEnabled) return;

                if (needsNewLine)
                {
                    if (!EnsureFont()) return;
                    ChooseLine();
                    needsNewLine = false;
                }
                if (lineTexture == null) return;

                Draw(savedMatrix);
            }
            catch (Exception e)
            {
                CoreLog.Rate("splash", e, 60f);
            }
            finally
            {
                GUI.matrix = savedMatrix;
            }
        }

        // ------------------------------------------------------------------ drawing

        private void Draw(Matrix4x4 baseMatrix)
        {
            int gui = AutoGuiScale(Screen.width, Screen.height);
            float offsetX = CoreConfig.SplashOffsetX != null ? CoreConfig.SplashOffsetX.Value : DefaultOffsetX;
            float offsetY = CoreConfig.SplashOffsetY != null ? CoreConfig.SplashOffsetY.Value : DefaultOffsetY;
            var anchor = new Vector3(Screen.width / 2 + offsetX * gui, offsetY * gui, 0f);

            // Two gentle "breaths" per second, driven by the wall clock's millisecond.
            float phase = DateTime.Now.Millisecond / 1000f;
            float pulse = PulseBase - PulseDepth * Mathf.Abs(Mathf.Sin(phase * 2f * Mathf.PI));
            float fit = pulse * TargetLength / (lineAdvance + LengthPadding);
            float scale = gui * fit;

            // IMGUI's y axis points down, so a counter-clockwise tilt on screen is a negative z rotation.
            var local = Matrix4x4.TRS(anchor, Quaternion.Euler(0f, 0f, -TiltDegrees), new Vector3(scale, scale, 1f));
            GUI.matrix = baseMatrix * local;

            var rect = new Rect(-(lineAdvance / 2), -GlyphTop, lineTexture.width, lineTexture.height);
            GUI.DrawTexture(rect, lineTexture, ScaleMode.StretchToFill, true);
        }

        /// <summary>Largest scale (1..8) that still leaves at least 320 x 240 GUI units, like Minecraft's "Auto" setting.</summary>
        private static int AutoGuiScale(int width, int height)
        {
            int scale = 1;
            for (int candidate = 2; candidate <= MaxGuiScale; candidate++)
            {
                if (width / candidate < MinGuiWidth || height / candidate < MinGuiHeight) break;
                scale = candidate;
            }
            return scale;
        }

        // ------------------------------------------------------------------ choosing the line

        private bool EnsureFont()
        {
            if (!fontAttempted)
            {
                fontAttempted = true;
                font = McFont.Load(assets);
            }
            return font != null;
        }

        private void ChooseLine()
        {
            string line = SeasonalLine(DateTime.Now) ?? RandomLine();

            if (lineTexture != null)
            {
                Object.Destroy(lineTexture);
                lineTexture = null;
            }
            lineAdvance = font.Width(line);
            lineTexture = font.Render(line, SplashYellow);
        }

        /// <summary>SlimeCraft's own holiday lines (never Minecraft's), or null on an ordinary day.</summary>
        private static string SeasonalLine(DateTime today)
        {
            if (today.Month == 12 && today.Day >= 24 && today.Day <= 26) return "Wiggly Wonderland is here!";
            if (today.Month == 1 && today.Day == 1) return "A new year on the Far, Far Range!";
            if (today.Month == 10 && today.Day == 31) return "The Tarr come out tonight!";
            return null;
        }

        private string RandomLine()
        {
            var fromJar = JarLines();
            bool useOwn = fromJar.Count == 0 || random.Next(OwnLineOdds) == 0;
            return useOwn ? OwnLines[random.Next(OwnLines.Length)] : fromJar[random.Next(fromJar.Count)];
        }

        /// <summary>Printable-ASCII lines of the jar's splash list, read once per session (empty if absent).</summary>
        private List<string> JarLines()
        {
            if (jarLines != null) return jarLines;
            jarLines = new List<string>();
            string text = assets.ReadJarText(JarSplashPath);
            if (string.IsNullOrEmpty(text)) return jarLines;

            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length > 0 && McFont.CanRender(line)) jarLines.Add(line);
            }
            return jarLines;
        }

        // ------------------------------------------------------------------ scene tracking

        private static bool IsTitleSceneActive()
        {
            try { return SceneManager.GetActiveScene().name == Levels.MAIN_MENU; }
            catch { return false; }
        }
    }
}
