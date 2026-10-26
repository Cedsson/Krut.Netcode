using System;
using System.Globalization;

namespace Krut.Netcode
{
    /// <summary>
    /// The one reader for KRUT_* environment variables. A missing, blank or unparsable value gives the
    /// fallback, never a throw. Numbers are read with the invariant culture, so "0.02" means the same on every
    /// machine.
    /// </summary>
    public static class KrutEnv
    {
        /// <summary>The trimmed value, or null when the variable is unset or blank.</summary>
        public static string? Text(string name)
        {
            string? raw = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
        }

        public static bool TryParseInt(string text, out int value)
            => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        public static bool TryParseFloat(string text, out float value)
            => float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        /// <summary>Invariant first, then the current culture, so a comma typed on a Swedish keyboard is not silently lost.</summary>
        public static bool TryParseFloatLenient(string text, out float value)
            => TryParseFloat(text, out value)
               || float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);

        public static int Int(string name, int fallback)
            => Text(name) is { } t && TryParseInt(t, out int v) ? v : fallback;

        /// <summary>Any parsed float, NaN and infinity included.</summary>
        public static float Float(string name, float fallback)
            => Text(name) is { } t && TryParseFloat(t, out float v) ? v : fallback;

        public static float FiniteFloat(string name, float fallback)
            => Text(name) is { } t && TryParseFloat(t, out float v) && float.IsFinite(v) ? v : fallback;

        public static float FloatLenient(string name, float fallback)
            => Text(name) is { } t && TryParseFloatLenient(t, out float v) ? v : fallback;

        public static double Double(string name, double fallback)
            => Text(name) is { } t
               && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;

        /// <summary>The parsed value clamped to the range; the fallback is returned as given when the variable is unset or broken.</summary>
        public static float Clamped(string name, float fallback, float min, float max)
            => Text(name) is { } t && TryParseFloat(t, out float v) ? Math.Clamp(v, min, max) : fallback;
    }
}
