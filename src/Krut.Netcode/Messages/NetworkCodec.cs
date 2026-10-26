using System;
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
        /// counts from. Tuned for a world in "units" where a player is
        /// about 64 units long, so 32,000 is enough for tracks a kilometre
        /// across, and one unit of precision (about 1 cm) is never visible for
        /// a player in motion.
        /// The arithmetic, exactly. A position is three
        /// <see cref="short"/>s, so each axis gets 16 bits over the range
        /// -<paramref name="extent"/> to +<paramref name="extent"/>:
        /// 32,000 / 32767 = 0.977 world units per step, and because the
        /// encoder rounds to the nearest step the worst case - a value that
        /// lands exactly between two steps - is off by half of that, 0.488
        /// world units. That is the error bound a consumer may assume, and it
        /// holds per axis, so the error vector can be at most
        /// 0.488 * sqrt(3) = 0.85 units long.
        /// A game with a smaller world should pass its own
        /// <c>extent</c> to <see cref="WritePosition"/>, which raises the
        /// resolution in proportion: halving the extent halves the error. A
        /// value at or below zero is replaced by this default rather than
        /// throwing, because an extent of zero would divide by zero - and the
        /// symmetry between <see cref="WritePosition"/> and
        /// <see cref="ReadPosition"/> means both sides have to agree on it
        /// anyway, so a wrong value is a bug the test that round-trips a
        /// position will catch.
        /// </summary>
        public const float DefaultPositionExtent = 32000f;

        /// <summary>Writes a name (truncated to <see cref="MaxNameLength"/> characters).</summary>
        public static void WriteName(NetDataWriter writer, string name) => writer.Put(name ?? string.Empty, MaxNameLength);

        /// <summary>Reads a name and sanitizes it (empty/whitespace becomes "Player").</summary>
        public static string ReadName(NetDataReader reader) => SanitizeName(reader.GetString());

        /// <summary>Writes a text (truncated to <see cref="MaxTextLength"/> characters).</summary>
        public static void WriteText(NetDataWriter writer, string text) => writer.Put(text ?? string.Empty, MaxTextLength);

        /// <summary>
        /// Makes a name usable: cuts it, removes control characters and empty spaces. Called on both sides
        /// (handshake and lobby) so that the name in the UI can never destroy the layout. LiteNetLib writes a
        /// string as a 7-bit encoded length followed by the UTF-8 bytes, and
        /// <see cref="WriteName"/>/<see cref="WriteText"/> pass the maximum as a cap on the encoded byte count.
        /// A fixed-width 32-character name would need a separate length byte anyway (short names are the common
        /// case), it would have to pad, and truncating at a fixed byte count can split a multi-byte character
        /// in half. A length prefix with a byte cap has none of those problems and costs one or two extra
        /// bytes. Cost. One <c>char[32]</c> allocation per call - 64 bytes on the managed heap.
        /// </summary>
        public static string SanitizeName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "Player";

            char[] buffer = new char[MaxNameLength];
            int count = 0;
            bool lastWasSpace = false;
            foreach (char raw in name)
            {
                if (count == MaxNameLength)
                    break;
                // Control characters (null, tab, newline ...) are removed - they could destroy both the
                // console's line breaks and the name in the UI.
                char c = char.IsControl(raw) ? ' ' : raw;
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

            // The space that ends the name is never written out - it shows up as an extra character in a name
            // list and becomes part of a file key or a hash value somewhere downstream. At most ONE space can
            // end up last: the loop above sets lastWasSpace, but moves on as soon as something other than a
            // space is written, and that single-run compression means two can never end up next to each other.
            if (lastWasSpace)
                count--;

            return count <= 0 ? "Player" : new string(buffer, 0, count);
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

        /// <summary>Reads a vector as <see cref="WriteVector"/> wrote it.</summary>
        public static Vector3 ReadVector(NetDataReader reader) => new(reader.GetFloat(), reader.GetFloat(), reader.GetFloat());

        /// <summary>
        /// Quantizes a position to three shorts within <paramref name="extent"/> (see
        /// <see cref="DefaultPositionExtent"/> for the exact arithmetic and the error bound).
        /// </summary>
        public static void WritePosition(NetDataWriter writer, Vector3 value, float extent = DefaultPositionExtent)
        {
            if (extent <= 0f)
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
            if (extent <= 0f)
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

        /// <summary>
        /// Unit-scale float to 16 bits. The clamp to <see cref="short.MinValue"/> ..
        /// <see cref="short.MaxValue"/> is the load-bearing part: a value slightly outside -1..1 is quantized
        /// to the end of the range (and dequantizes back to almost exactly the limit), while a value outside
        /// the <see cref="short"/> range would wrap to the opposite sign - a rotation that arrives as its own
        /// inverse, which is the kind of bug that is very hard to see.
        /// </summary>
        private static short Quantize(float value) => (short)Math.Clamp((int)MathF.Round(value * short.MaxValue), short.MinValue, short.MaxValue);

        /// <summary>16 bits back to a unit-scale float. Asymmetric on purpose: +1 maps to short.MaxValue, so the range is -1 to just under +1.</summary>
        private static float Dequantize(short value) => value / (float)short.MaxValue;

        /// <summary>Position component: divide by the extent first, so the whole 16-bit range covers the world.</summary>
        private static short QuantizeScaled(float value, float extent) => Quantize(value / extent);

        /// <summary>Position component back to world units. The multiplication order is exact for the same extent on both sides.</summary>
        private static float DequantizeScaled(short value, float extent) => Dequantize(value) * extent;
    }
}
