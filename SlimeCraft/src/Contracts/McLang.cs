using System;
using System.Globalization;
using System.Text;

namespace SlimeCraft
{
    /// <summary>
    /// Player-facing text that has a counterpart in Minecraft's language file. The Minecraft wording is never
    /// shipped with SlimeCraft: it is looked up at runtime in the en_us.json of the player's own Minecraft jar
    /// (through <see cref="IMcAssets.Translate"/>, the key is only an identifier). When the jar is not loaded yet
    /// or does not know the key, SlimeCraft's own English wording is used instead.
    /// </summary>
    public static class McLang
    {
        /// <summary>
        /// Text for <paramref name="key"/> with <paramref name="args"/> filled in. The jar's text uses Minecraft's
        /// placeholders (<c>%s</c>, <c>%1$s</c>, <c>%%</c>); <paramref name="fallback"/> is a .NET format string
        /// (<c>{0}</c>, <c>{1}</c>, ...) used when the key cannot be resolved.
        /// </summary>
        public static string Format(string key, string fallback, params object[] args)
        {
            string fromJar = Lookup(key);
            if (fromJar != null) return FillMinecraftPlaceholders(fromJar, args);
            if (fallback == null) return key ?? "";
            if (args == null || args.Length == 0) return fallback;
            try { return string.Format(CultureInfo.InvariantCulture, fallback, args); }
            catch (FormatException) { return fallback; }
        }

        /// <summary>The jar's text for <paramref name="key"/>, or null when it is unknown or the assets are not ready.</summary>
        public static string Lookup(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            try
            {
                var assets = SC.Assets;
                if (assets == null || !assets.Ready) return null;
                string text = assets.Translate(key);
                return string.IsNullOrEmpty(text) || text == key ? null : text;
            }
            catch
            {
                return null;
            }
        }

        // %s takes the next argument, %N$s takes argument N (1-based), %% is a literal percent sign.
        // Missing arguments become empty text so a key with an unexpected shape never throws.
        private static string FillMinecraftPlaceholders(string pattern, object[] args)
        {
            if (pattern.IndexOf('%') < 0) return pattern;
            var sb = new StringBuilder(pattern.Length + 16);
            int sequential = 0;
            int i = 0;
            while (i < pattern.Length)
            {
                char c = pattern[i];
                if (c != '%' || i + 1 >= pattern.Length)
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                char next = pattern[i + 1];
                if (next == '%') { sb.Append('%'); i += 2; continue; }
                if (next == 's' || next == 'd') { sb.Append(Arg(args, sequential++)); i += 2; continue; }

                int j = i + 1;
                int number = 0;
                while (j < pattern.Length && char.IsDigit(pattern[j])) { number = number * 10 + (pattern[j] - '0'); j++; }
                if (j > i + 1 && j + 1 < pattern.Length && pattern[j] == '$' && (pattern[j + 1] == 's' || pattern[j + 1] == 'd'))
                {
                    sb.Append(Arg(args, number - 1));
                    i = j + 2;
                    continue;
                }

                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        private static string Arg(object[] args, int index)
        {
            if (args == null || index < 0 || index >= args.Length || args[index] == null) return "";
            var value = args[index];
            return value is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
        }
    }
}
