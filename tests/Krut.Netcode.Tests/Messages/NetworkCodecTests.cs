using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Krut.Netcode;
using LiteNetLib.Utils;
using System.Numerics;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests
{
    /// <summary>Wire codec round trips and quantization tolerance.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkCodecTests
    {
        // Codec Everything on the wire is quantized to a fixed width before it is sent, so this group is where
        // the precision budget is pinned down. 16 bits per component inside +/-32 000 world units is about one
        // world unit, roughly a centimeter at the scale the engine's world uses - far finer than a player ever
        // notices.

        // Two units of deviation is four times the precision quantization promises, so this bound catches a
        // regression that clearly worsens the resolution and ignores sub-pixel wobble in a float.
        [Fact]

        public void Codec_PositionSurvivesRoundTrip()
        {
            var writer = new NetDataWriter(true, 64);
            var original = new Vector3(1234.5f, -678.25f, 9100f);
            NetworkCodec.WritePosition(writer, original);
            var back = NetworkCodec.ReadPosition(ReaderFor(writer));

            Require((back - original).Length() < 2f, "The position did not survive the round trip",
                $"{original} became {back}, deviation {(back - original).Length():0.###}");
        }

        // A quaternion is renormalized on the way out; the interesting property is not the round trip but that
        // the result still lies ON the unit sphere, since an interpolated rotation that shrinks towards zero
        // drifts further away every single tick.
        [Fact]

        public void Codec_RotationSurvivesRoundTripAndStaysNormalized()
        {
            var writer = new NetDataWriter(true, 64);
            var original = Quaternion.CreateFromYawPitchRoll(0.7f, 0.3f, 1.9f);
            NetworkCodec.WriteRotation(writer, original);
            var back = NetworkCodec.ReadRotation(ReaderFor(writer));

            // 16 bits per component gives an angular deviation on the order of
            // 1e-4, so 1e-3 is a bound with two orders of magnitude of headroom.
            Require(MathF.Abs(back.W - original.W) < 0.001f, "The rotation's W component changed too much",
                $"{original.W} became {back.W}");

            // The normalization is not cosmetic: an interpolated rotation has to
            // lie on the unit sphere, otherwise it shrinks towards zero every tick.
            Require(MathF.Abs(back.LengthSquared() - 1f) < 0.001f, "The rotation was not normalized",
                $"length00B2 became {back.LengthSquared()}");
        }

        [Fact]

        public void Codec_QuantizationTolerance()
        {
            // Worst case: a position in the middle of a quantization step carries the maximum error.
            var writer = new NetDataWriter(true, 64);
            var original = new Vector3(100f, 200f, 300f);
            NetworkCodec.WritePosition(writer, original);
            var back = NetworkCodec.ReadPosition(ReaderFor(writer));

            Require((back - original).Length() < 1f, "Quantization loses more than one unit of precision",
                $"{original} became {back}");
        }

        // Names and text go through different write paths (the name is sanitized and capped at 32 characters,
        // the text at 512), so both are exercised here - and the text deliberately carries a newline and
        // non-ASCII characters, because a pure-ASCII payload would round trip even through a broken encoding.
        [Fact]

        public void Codec_TextSurvivesRoundTrip()
        {
            var writer = new NetDataWriter(true, 128);
            NetworkCodec.WriteName(writer, "Player with a slightly odd name");
            NetworkCodec.WriteText(writer, "Line 1\nLine 2 with 00E9 and 00FC - 12345");
            var reader = ReaderFor(writer);

            string name = NetworkCodec.ReadName(reader);
            string text = reader.GetString();

            Require(name == "Player with a slightly odd name", "The name did not survive the round trip", $"'{name}'");
            Require(text == "Line 1\nLine 2 with 00E9 and 00FC - 12345", "The text did not survive the round trip", $"'{text}'");
        }

        // The welcome message is the one message that MUST arrive before everything else - if it is broken the
        // client stays in "connecting" forever. That is why the whole message is rounded here, not just the
        // codec helpers.
        [Fact]

        public void Codec_WelcomeSurvivesRoundTrip()
        {
            var writer = new NetDataWriter(true, 128);
            var original = new WelcomeMessage
            {
                PeerId = 4711,
                TickRate = 60,
                ServerTick = 12345,
                PlayerName = "Anka",
            };
            original.Write(writer);

            var decoded = new WelcomeMessage();
            decoded.Read(ReaderFor(writer));

            Require(decoded.PeerId == 4711, "The peer id did not survive",
                decoded.PeerId.ToString(CultureInfo.InvariantCulture));
            Require(decoded.TickRate == 60, "The tick rate did not survive",
                decoded.TickRate.ToString(CultureInfo.InvariantCulture));
            Require(decoded.ServerTick == 12345, "The server tick did not survive",
                decoded.ServerTick.ToString(CultureInfo.InvariantCulture));
            Require(decoded.PlayerName == "Anka", "The name did not survive", decoded.PlayerName);
        }

    }
}
