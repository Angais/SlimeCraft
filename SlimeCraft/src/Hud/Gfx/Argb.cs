using UnityEngine;

namespace SlimeCraft.HudUI
{
    /// <summary>
    /// Colour helpers for the HUD. Every HUD colour travels as one packed uint laid out as 0xAARRGGBB
    /// (alpha in the top byte). This class builds such values, recolours them and turns them into Unity
    /// colour structs. All members are pure and allocation free; they run per glyph and per quad.
    /// </summary>
    internal static class Argb
    {
        public const uint White = 0xFFFFFFFFu;
        public const uint Black = 0xFF000000u;
        /// <summary>Dark grey used for container titles ("Crafting", "Inventory").</summary>
        public const uint Label = 0xFF404040u;

        private const uint RgbBits = 0x00FFFFFFu;

        /// <summary>Alpha from the low byte of <paramref name="a"/>, colour from the low 24 bits of <paramref name="rgb"/>.</summary>
        public static uint Color(int a, uint rgb) => ((uint)(a & 0xFF) << 24) | (rgb & RgbBits);

        /// <summary>The alpha byte (0..255).</summary>
        public static int A(uint c) => (int)(c >> 24);

        /// <summary>Same colour, new alpha.</summary>
        public static uint WithAlpha(uint c, int a) => Color(a, c);

        /// <summary>White with a 0..1 opacity.</summary>
        public static uint WhiteA(float alpha) => Color(UnitToByte(alpha), RgbBits);

        /// <summary>Black with a 0..1 opacity.</summary>
        public static uint BlackA(float alpha) => Color(UnitToByte(alpha), 0u);

        /// <summary>
        /// Multiplies red, green and blue by <paramref name="f"/> (truncated, clamped to a byte) and keeps alpha.
        /// With f = 0.25 this gives the dark drop-shadow tint used under HUD text.
        /// </summary>
        public static uint ScaleRGB(uint c, float f)
        {
            int r = ClampByte((int)(((c >> 16) & 0xFF) * f));
            int g = ClampByte((int)(((c >> 8) & 0xFF) * f));
            int b = ClampByte((int)((c & 0xFF) * f));
            return (c & 0xFF000000u) | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }

        public static Color32 ToColor32(uint c) =>
            new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));

        /// <summary>Packs a Unity colour as-is (no gamma conversion); each channel is floored to a byte.</summary>
        public static uint FromColor(UnityEngine.Color c) =>
            ((uint)UnitToByte(c.a) << 24) | ((uint)UnitToByte(c.r) << 16) | ((uint)UnitToByte(c.g) << 8) | (uint)UnitToByte(c.b);

        /// <summary>
        /// Opaque colour from hue (fraction of a full turn, 0 = red, 1/3 = green, 2/3 = blue), saturation and value.
        /// Hue wraps, so 1.0 equals 0.0. Each channel is evaluated on its own with a piecewise-linear ramp.
        /// </summary>
        public static uint Hsv(float h, float s, float v)
        {
            float turn = h - Mathf.Floor(h);
            if (float.IsNaN(turn) || float.IsInfinity(turn)) turn = 0f;
            float sector = turn * 6f;
            int r = HueRamp(5f, sector, s, v);
            int g = HueRamp(3f, sector, s, v);
            int b = HueRamp(1f, sector, s, v);
            return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }

        private static int HueRamp(float offset, float sector, float s, float v)
        {
            float k = (offset + sector) % 6f;
            if (k < 0f) k += 6f;
            float weight = Mathf.Clamp01(Mathf.Min(k, 4f - k));
            float channel = v - v * s * weight;
            float scaled = channel * 255f;
            if (float.IsNaN(scaled)) return 0;
            return ClampByte((int)scaled);
        }

        /// <summary>0..1 float to 0..255, clamped and rounded down (NaN gives 0).</summary>
        private static int UnitToByte(float x)
        {
            if (!(x > 0f)) return 0;
            if (x >= 1f) return 255;
            return (int)(x * 255f);
        }

        private static int ClampByte(int x) => x < 0 ? 0 : (x > 255 ? 255 : x);
    }

    /// <summary>
    /// Deterministic generator that reproduces the java.util.Random integer sequence (48-bit linear congruential
    /// generator, as documented in the JDK API). The HUD seeds it from the tick counter so that the low-health heart
    /// wobble and the hunger shake follow the exact pattern Minecraft players know.
    /// </summary>
    internal sealed class JavaRandom
    {
        private const long Multiplier = 0x5DEECE66DL;
        private const long Addend = 0xBL;
        private const long StateMask = (1L << 48) - 1;

        private long state;

        public JavaRandom(long seed) { SetSeed(seed); }

        public void SetSeed(long seed)
        {
            state = (seed ^ Multiplier) & StateMask;
        }

        /// <summary>Advances the state once and returns its top <paramref name="count"/> bits.</summary>
        private int Advance(int count)
        {
            unchecked
            {
                state = (state * Multiplier + Addend) & StateMask;
            }
            return (int)(state >> (48 - count));
        }

        /// <summary>Uniform integer in [0, bound); 0 for a non-positive bound.</summary>
        public int NextInt(int bound)
        {
            if (bound <= 0) return 0;
            unchecked
            {
                bool powerOfTwo = (bound & (bound - 1)) == 0;
                if (powerOfTwo) return (int)(((long)bound * Advance(31)) >> 31);
                // Reject draws from the incomplete last block of the 31-bit range so every result is equally likely.
                int draw, result;
                do
                {
                    draw = Advance(31);
                    result = draw % bound;
                } while (draw - result + (bound - 1) < 0);
                return result;
            }
        }
    }
}
