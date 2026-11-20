using System;
using System.Globalization;
using LiteNetLib.Utils;
using System.Numerics;

namespace Krut.Netcode
{
    /// <summary>
    /// Packing helpers for messages: names, vectors and rotations. Rotation and position are sent quantized (16
    /// bits per component) instead of as floats: a per-tick update with position + rotation is otherwise 28
    /// bytes, and with quantization 12. It shows when a game runs many players at once, and 16 bits is far
    /// more than the game's own resolution needs (see <see cref="DefaultPositionExtent"/>). LiteNetLib ships a
    /// <c>NetSerializer</c> that walks an object graph with reflection, and the library does not use it for
    /// three reasons, all of which are documented in <c>docs/guide.md</c> section 3: reflection-based
    /// serialization does not work in a Native AOT build;
    /// the size of a state packet has to be computable up front, which is what
    /// lets it fit in one UDP datagram and travel unreliable; and an unknown message type has to be survivable,
    /// which means the receiving side decides from the first byte alone what to decode.
    /// </summary>
    public static class NetworkCodec
    {
        /// <summary>Max number of characters in a player name - the name is sent in every handshake and in every lobby update.</summary>
        public const int MaxNameLength = 32;

        /// <summary>Max number of characters in a chat text.</summary>
        public const int MaxTextLength = 512;

        /// <summary>
        /// How large a half extent (world units) the position quantization
        /// counts from. Tuned for centimeter-scale world units where a player is
        /// about 64 units long, so 32,000 covers tracks up to ~640 m across at
        /// ~0.977 units/step (worst-case error ±0.488 units per axis, or up to
        /// 0.85 units 3D Euclidean).
        /// <para>
        /// <b>Meter-scale worlds:</b> if 1 world unit = 1 meter, the default
        /// extent gives ~49 cm worst-case error per axis, which is too coarse
        /// for precision movement. Pass a custom <c>extent</c> matching your
        /// play area (for example <c>500f</c> → ±0.76 cm, <c>2000f</c> → ±3.05 cm),
        /// or use <see cref="WriteVector"/>/<see cref="ReadVector"/> for full
        /// 32-bit float precision. Both sender and receiver must pass the exact
        /// same <c>extent</c> because it is not written to the wire.
        /// </para>
        /// <para>
        /// A non-finite or non-positive <c>extent</c> falls back to this default
        /// rather than throwing or dividing by zero.
        /// </para>
        /// </summary>
        public const float DefaultPositionExtent = 32000f;

        /// <summary>Writes a name (truncated to <see cref="MaxNameLength"/> characters).</summary>
        public static void WriteName(NetDataWriter writer, string name) => writer.Put(name ?? string.Empty, MaxNameLength);

        /// <summary>Reads a name and sanitizes it (empty/whitespace becomes "Player").</summary>
        public static string ReadName(NetDataReader reader) => SanitizeName(reader.GetString(MaxNameLength * 8));

        /// <summary>Writes a text (truncated to <see cref="MaxTextLength"/> characters).</summary>
        public static void WriteText(NetDataWriter writer, string text) => writer.Put(text ?? string.Empty, MaxTextLength);

        /// <summary>Reads text with a bounded length and removes dangerous control and format characters.</summary>
        public static string ReadText(NetDataReader reader) => SanitizeText(reader.GetString(MaxTextLength * 4));

        /// <summary>
        /// Sanitizes text received over the wire: strips control characters other than newline and removes Unicode
        /// format/bidirectional override characters while capping length at <see cref="MaxTextLength"/> without
        /// splitting UTF-16 surrogate pairs.
        /// </summary>
        public static string SanitizeText(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            Span<char> buffer = stackalloc char[MaxTextLength];
            int count = 0;
            foreach (char raw in text)
            {
                if (count == MaxTextLength)
                    break;

                if (raw == '\n')
                {
                    buffer[count++] = '\n';
                    continue;
                }

                if (raw == '\r')
                    continue;

                if (raw == '\t')
                {
                    buffer[count++] = ' ';
                    continue;
                }

                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(raw);
                if (category == UnicodeCategory.Format)
                    continue;

                if (category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                {
                    buffer[count++] = '\n';
                    continue;
                }

                if (char.IsControl(raw))
                    continue;

                buffer[count++] = raw;
            }

            if (count > 0 && char.IsHighSurrogate(buffer[count - 1]))
                count--;

            return count <= 0 ? string.Empty : new string(buffer.Slice(0, count));
        }

        /// <summary>
        /// Makes a name usable: cuts it, removes control and format characters and collapses whitespace. Called on
        /// both sides (handshake and lobby) so that the name in the UI can never distort the layout.
        /// </summary>
        public static string SanitizeName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Player";

            Span<char> buffer = stackalloc char[MaxNameLength];
            int count = 0;
            bool lastWasSpace = false;
            foreach (char raw in name)
            {
                if (count == MaxNameLength)
                    break;

                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(raw);
                if (category == UnicodeCategory.Format)
                    continue;

                // Control characters (null, tab, newline ...) and line/paragraph separators are turned into spaces
                // so they cannot break logs, console lines, or UI layout.
                char c = char.IsControl(raw) || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                    ? ' '
                    : raw;
                if (c == ' ')
                {
                    // Double spaces are removed, and a space at the start is skipped entirely (otherwise the
                    // name would look indented in lists).
                    if (count == 0 || lastWasSpace)
                        continue;
                    lastWasSpace = true;
                }
                else
                {
                    lastWasSpace = false;
                }

                buffer[count++] = c;
            }

            if (count > 0 && char.IsHighSurrogate(buffer[count - 1]))
                count--;

            // Never leave a trailing space (and removing an unpaired high surrogate could expose one).
            if (count > 0 && buffer[count - 1] == ' ')
                count--;

            return count <= 0 ? "Player" : new string(buffer.Slice(0, count));
        }

        /// <summary>
        /// Writes a vector as three floats (wider range, larger packets - e.g. for rare global events). 12
        /// bytes instead of the 6 a quantized position costs. Use <see cref="WritePosition"/> for anything
        /// per-tick; use this when the value is genuinely unbounded (a camera target far outside the play area,
        /// a colour, a velocity that is not a position) and is sent rarely enough that the six bytes do not
        /// matter.
        /// </summary>
        public static void WriteVector(NetDataWriter writer, Vector3 value)
        {
            writer.Put(value.X);
            writer.Put(value.Y);
            writer.Put(value.Z);
        }

        /// <summary>Reads a vector as <see cref="WriteVector"/> wrote it, replacing non-finite values with 0.</summary>
        public static Vector3 ReadVector(NetDataReader reader) => new(
            SanitizeFloat(reader.GetFloat()),
            SanitizeFloat(reader.GetFloat()),
            SanitizeFloat(reader.GetFloat()));

        /// <summary>
        /// Quantizes a position to three shorts within <paramref name="extent"/> (see
        /// <see cref="DefaultPositionExtent"/> for the exact arithmetic and the error bound).
        /// </summary>
        public static void WritePosition(NetDataWriter writer, Vector3 value, float extent = DefaultPositionExtent)
        {
            if (!float.IsFinite(extent) || extent <= 0f)
                extent = DefaultPositionExtent;

            writer.Put(QuantizeScaled(value.X, extent));
            writer.Put(QuantizeScaled(value.Y, extent));
            writer.Put(QuantizeScaled(value.Z, extent));
        }

        /// <summary>
        /// Reads a quantized position written by <see cref="WritePosition"/>. <paramref name="extent"/> must be
        /// the value the sender used - the extent is not on the wire, so a mismatch does not throw, it silently
        /// scales the world.
        /// </summary>
        public static Vector3 ReadPosition(NetDataReader reader, float extent = DefaultPositionExtent)
        {
            if (!float.IsFinite(extent) || extent <= 0f)
                extent = DefaultPositionExtent;

            return new Vector3(DequantizeScaled(reader.GetShort(), extent), DequantizeScaled(reader.GetShort(), extent), DequantizeScaled(reader.GetShort(), extent));
        }

        /// <summary>
        /// Quantizes a rotation to four shorts - the smallest normalized unit
        /// quaternion that fits in -32767..32767.
        /// Why four shorts and not three. A quaternion has four
        /// components with the constraint that their length is 1, so three
        /// would be enough information - but three numbers on the wire cannot
        /// be told apart from a non-unit quaternion, and every consumer would
        /// have to renormalize before using it. Sending the fourth costs two
        /// bytes and removes the question. (Storing Euler angles instead would
        /// save a further 2 bytes and reintroduce gimbal lock, which is not a
        /// trade a per-tick message can afford.)
        /// Each component is a unit-scale value in -1..1 mapped onto
        /// -32767..32767, so one step is 2 / 65534 = 1 / 32767 = 3.05e-5 and
        /// the worst-case error per component is half that, 1.53e-5. A
        /// quaternion built from four such components is off in length by at
        /// most 4 * 1.53e-5 = 6.1e-5 - which is why
        /// <see cref="ReadRotation"/> renormalizes rather than assuming a unit
        /// sphere, and why a consumer must not either: a quaternion used to
        /// build a matrix without normalizing scales the matrix, and a model
        /// whose matrix is off by 6e-5 in scale is off by 6e-5 in every
        /// dimension, which accumulates visibly over a hierarchy.
        /// </summary>
        public static void WriteRotation(NetDataWriter writer, Quaternion value)
        {
            writer.Put(Quantize(value.X));
            writer.Put(Quantize(value.Y));
            writer.Put(Quantize(value.Z));
            writer.Put(Quantize(value.W));
        }

        /// <summary>
        /// Reads a rotation written by <see cref="WriteRotation"/> (normalized back, because the quantization
        /// moves it slightly off the unit sphere). That is two orders of magnitude above the worst quantization
        /// error of 6.1e-5, so it can only fire on a genuinely degenerate quaternion (an all-zero one, which is
        /// what a caller that never assigned the field gives).
        /// </summary>
        public static Quaternion ReadRotation(NetDataReader reader)
        {
            var value = new Quaternion(Dequantize(reader.GetShort()), Dequantize(reader.GetShort()), Dequantize(reader.GetShort()), Dequantize(reader.GetShort()));
            float lengthSquared = value.LengthSquared();
            return lengthSquared > 1e-6f ? value * (1f / MathF.Sqrt(lengthSquared)) : Quaternion.Identity;
        }

        /// <summary>Reads a 32-bit float and replaces non-finite wire values (<c>NaN</c> or infinity) with <paramref name="fallback"/>.</summary>
        public static float ReadFiniteFloat(NetDataReader reader, float fallback = 0f)
        {
            float value = reader.GetFloat();
            if (float.IsFinite(value))
                return value;

            return float.IsFinite(fallback) ? fallback : 0f;
        }

        /// <summary>
        /// Reads a 32-bit float, replaces non-finite wire values with <paramref name="fallback"/>, and clamps the
        /// result into [<paramref name="min"/>, <paramref name="max"/>].
        /// </summary>
        public static float ReadClampedFloat(NetDataReader reader, float min, float max, float fallback = 0f)
        {
            if (min > max)
                (min, max) = (max, min);

            float value = ReadFiniteFloat(reader, fallback);
            return Math.Clamp(value, min, max);
        }

        private static float SanitizeFloat(float value) => float.IsFinite(value) ? value : 0f;

        /// <summary>
        /// Unit-scale float to 16 bits. Non-finite values map to 0; values outside -1..1 clamp to -1..1 before
        /// scaling so large inputs never overflow <see cref="int"/> and flip sign.
        /// </summary>
        private static short Quantize(float value)
        {
            if (!float.IsFinite(value))
                return 0;

            float clamped = Math.Clamp(value, -1f, 1f);
            return (short)MathF.Round(clamped * short.MaxValue);
        }

        /// <summary>
        /// 16 bits back to a symmetric unit-scale float in [-1, +1]; wire <see cref="short.MinValue"/> (-32768)
        /// clamps to -32767 (-1f) to match the encoder range.
        /// </summary>
        private static float Dequantize(short value) => Math.Max((int)value, -short.MaxValue) / (float)short.MaxValue;

        /// <summary>Position component: divide by the extent first, so the whole 16-bit range covers the world.</summary>
        private static short QuantizeScaled(float value, float extent) => Quantize(value / extent);

        /// <summary>Position component back to world units. The multiplication order is exact for the same extent on both sides.</summary>
        private static float DequantizeScaled(short value, float extent) => Dequantize(value) * extent;
    }
}
