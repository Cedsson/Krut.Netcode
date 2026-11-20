using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Numerics;
using Krut.Netcode;
using LiteNetLib;
using LiteNetLib.Utils;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests
{
    /// <summary>Security hardening, malformed input handling, and regression tests.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkSecurityAndRegressionTests
    {
        private struct VecState
        {
            public float X;
            public float Y;
            public float Z;
        }

        private struct VecInput
        {
            public float DeltaX;
        }

        [Fact]
        public void Handshake_MalformedAndEmptyPackets_DoNotThrowOrCrashServer()
        {
            using var server = new NetworkServer(Config(0, maxClients: 2));
            Require(server.Start(), "The server did not start");
            var endPoint = new IPEndPoint(IPAddress.Loopback, server.Port);

            byte[][] payloads =
            {
                Array.Empty<byte>(),
                new byte[] { 0x01 },
                new byte[] { 0xFF, 0x7F, 0x41, 0x42 },
                BuildWriterPayload(w => { w.Put("krut.netcode.test"); w.Put((byte)0x01); }),
                BuildWriterPayload(w => { w.Put("krut.netcode.test"); w.Put(7); w.Put((ushort)500); }),
            };

            foreach (byte[] payload in payloads)
            {
                var listener = new EventBasedNetListener();
                var raw = new NetManager(listener) { UnsyncedEvents = false };
                Require(raw.Start(), "Raw client could not start");
                try
                {
                    raw.Connect(endPoint, payload);
                    for (int i = 0; i < 15; i++)
                    {
                        server.Update(1f / 60f);
                        raw.PollEvents();
                        System.Threading.Thread.Sleep(5);
                    }
                }
                finally
                {
                    raw.Stop();
                }
            }

            Require(server.ClientCount == 0, "Malformed connection requests occupied slots", server.ClientCount.ToString());

            using var client = new NetworkClient(Config(server.Port));
            Require(client.Connect("127.0.0.1", server.Port, "ValidClient"), "Legitimate client could not connect");
            Require(WaitUntilReady(5f, client, server, client), "Legitimate client never became ready after malformed traffic");
        }

        [Fact]
        public void Codec_SanitizeName_DoesNotSplitSurrogatePair_AndStripsFormatChars()
        {
            string withSurrogateAtLimit = new string('a', NetworkCodec.MaxNameLength - 1) + "\uD83D\uDE00";
            string sanitized = NetworkCodec.SanitizeName(withSurrogateAtLimit);
            Require(sanitized.Length == NetworkCodec.MaxNameLength - 1, "Surrogate pair was split at limit", sanitized.Length.ToString());
            Require(!char.IsHighSurrogate(sanitized[^1]), "Trailing high surrogate remained");

            string spaceBeforeSurrogate = new string('a', NetworkCodec.MaxNameLength - 2) + " \uD83D\uDE00";
            string trimmed = NetworkCodec.SanitizeName(spaceBeforeSurrogate);
            Require(trimmed.Length == NetworkCodec.MaxNameLength - 2, "Trailing space exposed by surrogate trim was kept", $"'{trimmed}'");

            string spoofed = "Al\u202Eic\u200Be\u2028Test";
            string cleaned = NetworkCodec.SanitizeName(spoofed);
            Require(cleaned == "Alice Test", "Unicode format or line separator characters were not sanitized", $"'{cleaned}'");
        }

        [Fact]
        public void Codec_NonFiniteFloats_AreSanitizedToZero()
        {
            var writer = new NetDataWriter(true, 64);
            var bad = new Vector3(float.NaN, float.PositiveInfinity, float.NegativeInfinity);
            NetworkCodec.WritePosition(writer, bad);
            NetworkCodec.WriteVector(writer, bad);

            var reader = ReaderFor(writer);
            Vector3 pos = NetworkCodec.ReadPosition(reader);
            Vector3 vec = NetworkCodec.ReadVector(reader);

            Require(pos == Vector3.Zero, "Non-finite position components did not sanitize to zero", pos.ToString());
            Require(vec == Vector3.Zero, "Non-finite vector components did not sanitize to zero", vec.ToString());
        }

        [Fact]
        public void Chat_ControlCharactersAreSanitized_AndNewlinesPreserved()
        {
            var writer = new NetDataWriter(true, 128);
            var original = new ChatMessage { Text = "Hello\0\x1B[31mWorld\u202E\nNextLine" };
            original.Write(writer);

            var decoded = new ChatMessage();
            decoded.Read(ReaderFor(writer));

            Require(decoded.Text == "Hello[31mWorld\nNextLine", "Chat text sanitization mismatch", $"'{decoded.Text}'");

            var oversizedWriter = new NetDataWriter(true, 4096);
            oversizedWriter.Put(new string('x', (NetworkCodec.MaxTextLength * 4) + 10));
            var oversizedDecoded = new SystemChatMessage();
            oversizedDecoded.Read(ReaderFor(oversizedWriter));
            Require(oversizedDecoded.Text == string.Empty, "Oversized wire text was not rejected", oversizedDecoded.Text.Length.ToString());
        }

        [Fact]
        public void Server_RejectsClientSpoofedWelcomeAndPlayerInfo_AndCountsEmptyMessage()
        {
            using var server = new NetworkServer(Config(0, maxClients: 2));
            Require(server.Start(), "Server did not start");

            var serverErrors = new List<NetworkError>();
            var serverReceivedTypes = new List<NetworkMessageType>();
            server.Error += e => serverErrors.Add(e);
            server.MessageReceived += e => serverReceivedTypes.Add(e.Message.Type);

            using var client = new NetworkClient(Config(server.Port));
            Require(client.Connect("127.0.0.1", server.Port, "Tester"), "Client could not connect");
            Require(WaitUntilReady(5f, client, server, client), "Client never became ready");

            // Spoof server-only messages from client.
            var spoofWelcome = NetworkSession.Rent<WelcomeMessage>();
            spoofWelcome.PeerId = 999;
            client.Send(client.ServerPeerId, spoofWelcome);

            var spoofInfo = NetworkSession.Rent<PlayerInfoMessage>();
            spoofInfo.PeerId = 999;
            spoofInfo.PlayerName = "Spoof";
            client.Send(client.ServerPeerId, spoofInfo);

            // Queue a 0-byte received payload as LiteNetLib would for an empty header-only datagram.
            foreach (var peer in server.Peers)
            {
                server.PendingEvents.Add(NetworkEvent.Received(peer.Peer, ArrayPool<byte>.Shared.Rent(0), 0, NetworkDelivery.Unreliable));
                break;
            }
            server.Update(1f / 60f);

            Require(PumpUntil(3f, () => server.MalformedMessageCount >= 3, server, client),
                "Server did not count spoofed and empty messages as malformed",
                $"count {server.MalformedMessageCount}");
            Require(!serverReceivedTypes.Contains(NetworkMessageType.Welcome), "Server delivered client-sent Welcome");
            Require(!serverReceivedTypes.Contains(NetworkMessageType.PlayerInfo), "Server delivered client-sent PlayerInfo");
            Require(serverErrors.Exists(e => e.Code == NetworkErrorCode.MalformedMessage), "No MalformedMessage error reported");
        }

        [Fact]
        public void Client_AppliesWelcomeTickRate_AndIgnoresDuplicateWelcome()
        {
            var serverConfig = Config(0, maxClients: 2);
            serverConfig.TickRate = 60;
            using var server = new NetworkServer(serverConfig);
            Require(server.Start(), "Server did not start");

            var clientConfig = Config(server.Port);
            clientConfig.TickRate = 30;
            using var client = new NetworkClient(clientConfig);

            int readyCount = 0;
            client.SessionReady += () => readyCount++;

            Require(client.Connect("127.0.0.1", server.Port, "TickTester"), "Client could not connect");
            Require(WaitUntilReady(5f, client, server, client), "Client never became ready");

            Require(client.Config.TickRate == 60, "Client did not adopt server TickRate from Welcome", client.Config.TickRate.ToString());
            Require(readyCount == 1, "SessionReady count wrong", readyCount.ToString());

            int originalPeerId = client.LocalPeerId;
            var duplicateWelcome = NetworkSession.Rent<WelcomeMessage>();
            duplicateWelcome.PeerId = 9999;
            duplicateWelcome.TickRate = 20;
            duplicateWelcome.ServerTick = 50000;
            duplicateWelcome.PlayerName = "Hijacked";
            server.Send(originalPeerId, duplicateWelcome);

            Pump(0.2f, server, client);

            Require(readyCount == 1, "Duplicate Welcome triggered SessionReady again", readyCount.ToString());
            Require(client.LocalPeerId == originalPeerId, "Duplicate Welcome overwrote LocalPeerId");
            Require(client.Config.TickRate == 60, "Duplicate Welcome overwrote TickRate");
        }

        [Fact]
        public void Reconciler_ClampsInvalidSmoothingSettings_InConstructorAndSetter()
        {
            var invalid = new VisualSmoothingSettings
            {
                MinCorrectionDistance = -5f,
                MaxCorrectionSeconds = 0.05f,
                MinCorrectionSeconds = 0.5f,
                UseEaseOut = true,
            };

            var reconciler = new NetworkReconciler<VecState, VecInput>(invalid)
            {
                Step = static (s, _, input) => new VecState { X = s.X + input.DeltaX, Y = s.Y, Z = s.Z },
            };

            Require(reconciler.Smoothing.MinCorrectionDistance == 0f, "Constructor did not clamp negative distance");
            Require(reconciler.Smoothing.MinCorrectionSeconds == 0.05f, "Constructor did not clamp MinCorrectionSeconds");

            reconciler.Smoothing = new VisualSmoothingSettings
            {
                MinCorrectionDistance = 2f,
                MaxCorrectionSeconds = 0.1f,
                MinCorrectionSeconds = 0.5f,
                UseEaseOut = true,
            };

            Require(reconciler.Smoothing.MinCorrectionSeconds == 0.1f, "Setter did not clamp MinCorrectionSeconds");

            reconciler.Reset(0, default);
            reconciler.Predict(1, new VecInput { DeltaX = 1f });
            reconciler.Predict(2, new VecInput { DeltaX = 1f });
            reconciler.SetAuthoritativeState(2, new VecState { X = 1.2f });

            int replayed = reconciler.Reconcile(2, 1, static (auth, lastTick, inputs, count) =>
            {
                VecState state = auth;
                for (int i = 0; i < count; i++)
                    state.X += inputs[i].DeltaX;
                return state;
            });

            Require(replayed == 1, "Explicit-count Reconcile replayed wrong count", replayed.ToString());
            Require(MathF.Abs(reconciler.PredictedState.X - 2.2f) < 0.001f, "PredictedState mismatch");

            // Ensure unused scratch slots are zero-cleared after replay.
            reconciler.Predict(3, new VecInput { DeltaX = 5f });
            reconciler.Predict(4, new VecInput { DeltaX = 5f });
            reconciler.SetAuthoritativeState(3, new VecState { X = 2.2f });
            int firstReplayed = reconciler.Reconcile(3, 2, static (auth, _, _) => auth);
            Require(firstReplayed == 2, "Expected two inputs replayed before checking scratch clear", firstReplayed.ToString());

            reconciler.Predict(5, new VecInput { DeltaX = 1f });
            reconciler.SetAuthoritativeState(5, new VecState { X = 2.2f });
            reconciler.Reconcile(5, 4, static (auth, _, inputs) =>
            {
                VecState state = auth;
                for (int i = 0; i < inputs.Length; i++)
                    state.X += inputs[i].DeltaX;
                return state;
            });
            Require(MathF.Abs(reconciler.PredictedState.X - 3.2f) < 0.001f, "Stale scratch input leaked into replay", reconciler.PredictedState.X.ToString());
        }

        [Fact]
        public void TimeSync_ShouldProbe_RecoversAfterLostProbePacket()
        {
            var sync = new NetworkTimeSync(60);
            Require(sync.ShouldProbe(2), "Expected probe before sync");
            sync.RecordRequest(2);

            Require(!sync.ShouldProbe(3), "Should wait inside probe interval");
            Require(sync.ShouldProbe(4), "Should allow new probe after interval elapses on dropped response");

            // Synchronized steady state: interval is RecommendedProbeIntervalTicks (8).
            sync.RecordResponse(100, 0.02);
            Require(sync.ShouldProbe(8), "Expected probe on interval boundary");
            sync.RecordRequest(8);
            Require(!sync.ShouldProbe(10), "Should wait inside synchronized interval");
            Require(sync.ShouldProbe(16), "Should recover after synchronized interval on dropped response");
        }

        [Fact]
        public void Server_PortZero_ReportsBoundPortAfterStart()
        {
            using var server = new NetworkServer(Config(0, maxClients: 1));
            Require(server.Port == 0, "Unstarted Port=0 server should report 0", server.Port.ToString());
            Require(server.Start(), "Server failed to start on ephemeral port");
            Require(server.Port > 0, "Started server did not report bound port", server.Port.ToString());

            using var client = new NetworkClient(Config(server.Port));
            Require(client.Connect("127.0.0.1", server.Port, "PortZeroClient"), "Client could not connect to bound port");
            Require(WaitUntilReady(5f, client, server, client), "Client did not become ready on ephemeral port");
        }

        private static byte[] BuildWriterPayload(Action<NetDataWriter> write)
        {
            var writer = new NetDataWriter(true, 64);
            write(writer);
            return writer.CopyData();
        }
    }
}
