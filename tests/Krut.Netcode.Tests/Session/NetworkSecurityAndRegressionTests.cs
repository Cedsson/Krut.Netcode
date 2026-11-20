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

            var spoofSystemChat = NetworkSession.Rent<SystemChatMessage>();
            spoofSystemChat.Text = "Admin announcement";
            client.Send(client.ServerPeerId, spoofSystemChat);

            // Queue a 0-byte received payload and a trailing-bytes payload.
            foreach (var peer in server.Peers)
            {
                server.PendingEvents.Add(NetworkEvent.Received(peer.Peer, ArrayPool<byte>.Shared.Rent(0), 0, NetworkDelivery.Unreliable));

                var trailingWriter = new NetDataWriter(true, 64);
                trailingWriter.Put((byte)NetworkMessageType.Chat);
                NetworkCodec.WriteText(trailingWriter, "valid");
                trailingWriter.Put(12345); // Extra trailing unread bytes
                byte[] trailingBuf = ArrayPool<byte>.Shared.Rent(trailingWriter.Length);
                Buffer.BlockCopy(trailingWriter.Data, 0, trailingBuf, 0, trailingWriter.Length);
                server.PendingEvents.Add(NetworkEvent.Received(peer.Peer, trailingBuf, trailingWriter.Length, NetworkDelivery.ReliableOrdered));
                break;
            }
            server.Update(1f / 60f);

            Require(PumpUntil(3f, () => server.MalformedMessageCount >= 5, server, client),
                "Server did not count spoofed, empty, and trailing-byte messages as malformed",
                $"count {server.MalformedMessageCount}");
            Require(!serverReceivedTypes.Contains(NetworkMessageType.Welcome), "Server delivered client-sent Welcome");
            Require(!serverReceivedTypes.Contains(NetworkMessageType.PlayerInfo), "Server delivered client-sent PlayerInfo");
            Require(!serverReceivedTypes.Contains(NetworkMessageType.SystemChat), "Server delivered client-sent SystemChat");
            Require(!serverReceivedTypes.Contains(NetworkMessageType.Chat), "Server delivered Chat message with trailing unread bytes");
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

        [Fact]
        public void ConnectionApproval_StillEnforcesMaxClients_AndSanitizesDisconnectText()
        {
            using var server = new NetworkServer(Config(0, maxClients: 1));
            server.ConnectionApproval = req =>
                req.PlayerName == "Banned"
                    ? NetworkConnectionResult.Reject("Banned\u202E\x07!")
                    : NetworkConnectionResult.Accept();
            Require(server.Start(), "Server failed to start");

            using var banned = new NetworkClient(Config(server.Port));
            string bannedText = string.Empty;
            banned.Closed += (_, text) => bannedText = text;
            Require(banned.Connect("127.0.0.1", server.Port, "Banned"), "Banned client could not send connect");
            Require(PumpUntil(4f, () => bannedText.Length > 0, server, banned), "Banned client did not receive rejection");
            Require(bannedText == "Banned!", "Rejection text was not sanitized on client", $"'{bannedText}'");

            using var first = new NetworkClient(Config(server.Port));
            Require(first.Connect("127.0.0.1", server.Port, "First"), "First client could not connect");
            Require(WaitUntilReady(5f, first, server, first), "First client never became ready");

            using var second = new NetworkClient(Config(server.Port));
            string fullText = string.Empty;
            second.Closed += (_, text) => fullText = text;
            Require(second.Connect("127.0.0.1", server.Port, "Second"), "Second client could not send connect");
            Require(PumpUntil(4f, () => fullText.Length > 0, server, first, second), "Second client was not rejected by MaxClients when ConnectionApproval was set");
            Require(fullText.Contains("full", StringComparison.OrdinalIgnoreCase), "Expected full lobby rejection", $"'{fullText}'");
            Require(server.ClientCount == 1, "MaxClients was bypassed via ConnectionApproval", server.ClientCount.ToString());
        }

        [Fact]
        public void Clock_Interpolator_AndReconciler_IgnoreNonFiniteInputs_WithoutNaNPoisoning()
        {
            var clock = new NetworkClock(60, 5);
            int ticks = 0;
            clock.Advance(float.PositiveInfinity, () => ticks++);
            clock.Advance(float.NaN, () => ticks++);
            clock.Advance(-5f, () => ticks++);
            Require(ticks == 0 && float.IsFinite(clock.TickProgress), "Non-finite delta poisoned NetworkClock", $"ticks {ticks}, progress {clock.TickProgress}");

            clock.Advance(1f / 60f, () => ticks++);
            Require(ticks == 1, "NetworkClock did not advance after non-finite inputs", ticks.ToString());

            var interpolator = new NetworkSnapshotInterpolator<VecState>(2);
            interpolator.Push(10, new VecState { X = 10f });
            interpolator.Push(20, new VecState { X = 20f });

            Require(!interpolator.TrySampleTime(double.NaN, out _), "NaN renderTick should return false");
            Require(!interpolator.TrySampleTime(double.PositiveInfinity, out _), "+Infinity renderTick should return false");
            Require(!interpolator.TrySampleTime(double.NegativeInfinity, out _), "-Infinity renderTick should return false");
            Require(interpolator.TrySampleTime(-1e15, out var heldPast, out var pastMode) && pastMode == NetworkSampleMode.Held && heldPast.X == 10f,
                "Large negative renderTick overflowed int");
            Require(interpolator.TrySampleTime(1e15, out var heldFuture, out var futureMode) && futureMode == NetworkSampleMode.Held && heldFuture.X == 20f,
                "Large positive renderTick overflowed int");

            var sync = new NetworkTimeSync(60);
            sync.RecordRequest(10);
            sync.RecordRequest(18);
            Require(sync.PendingRequestCount == 2, "Expected 2 pending requests");
            sync.RecordResponse(10, 100, 0.02);
            Require(sync.PendingRequestCount == 1, "3-argument RecordResponse did not retire matching pending request", sync.PendingRequestCount.ToString());
        }

        [Fact]
        public void Stop_DeliversShutdownReason_AndAllowsRestart_WhileDisposeThrows()
        {
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port, maxClients: 2));
            Require(server.Start(), "Server failed to start");

            using var client = new NetworkClient(Config(port));
            Require(client.Connect("127.0.0.1", port, "RestartClient"), "Client failed to connect");
            Require(WaitUntilReady(5f, client, server, client), "Client did not become ready");

            string shutdownReason = string.Empty;
            client.Closed += (_, text) => shutdownReason = text;
            server.Stop();

            Require(PumpUntil(4f, () => shutdownReason.Length > 0, server, client), "Client did not receive shutdown disconnect");
            Require(shutdownReason.Contains("shutting down", StringComparison.OrdinalIgnoreCase),
                "Shutdown reason did not reach client", $"'{shutdownReason}'");

            client.Stop();
            Require(server.Start(), "Server could not restart after Stop()");
            Require(client.Connect("127.0.0.1", port, "RestartClient"), "Client could not reconnect after Stop()");
            Require(WaitUntilReady(5f, client, server, client), "Restarted session did not become ready");

            client.Dispose();
            server.Dispose();
            Assert.Throws<ObjectDisposedException>(() => server.Start());
            Assert.Throws<ObjectDisposedException>(() => client.Connect("127.0.0.1", port));
        }

        private const byte BroadcastCounterWire = 239;

        private sealed class BroadcastCounterMessage : INetworkMessage
        {
            public static int WriteCount;

            public NetworkMessageType Type => NetworkMessageType.None;
            public byte WireType => BroadcastCounterWire;
            public NetworkDelivery Delivery => NetworkDelivery.ReliableOrdered;

            public int Value;

            public void Reset() => Value = 0;

            public void Write(NetDataWriter writer)
            {
                System.Threading.Interlocked.Increment(ref WriteCount);
                writer.Put(Value);
            }

            public void Read(NetDataReader reader) => Value = reader.GetInt();
        }

        [Fact]
        public void Broadcast_SerializesOnceForMultiplePeers_AndPoolSurvivesTrackedOverflow()
        {
            if (!NetworkMessages.IsRegistered(BroadcastCounterWire))
                NetworkMessages.RegisterGameMessage<BroadcastCounterMessage>(BroadcastCounterWire, NetworkDelivery.ReliableOrdered);

            // Leak > 4096 rented instances to exceed MaxTracked, then verify an active rented instance still resets & recycles.
            for (int i = 0; i < 4200; i++)
                _ = NetworkSession.Rent<BroadcastCounterMessage>();

            var active = NetworkSession.Rent<BroadcastCounterMessage>();
            active.Value = 777;
            NetworkMessages.Recycle(active, BroadcastCounterWire);
            var reused = NetworkSession.Rent<BroadcastCounterMessage>();
            Require(ReferenceEquals(active, reused) && reused.Value == 0,
                "Pool failed to reset or recycle after MaxTracked overflow", $"same={ReferenceEquals(active, reused)}, val={reused.Value}");
            NetworkMessages.Recycle(reused, BroadcastCounterWire);

            using var server = new NetworkServer(Config(0, maxClients: 3));
            Require(server.Start(), "Server failed to start");

            using var c1 = new NetworkClient(Config(server.Port));
            using var c2 = new NetworkClient(Config(server.Port));
            int received = 0;
            c1.MessageReceived += e => { if (e.Message is BroadcastCounterMessage m && m.Value == 42) received++; };
            c2.MessageReceived += e => { if (e.Message is BroadcastCounterMessage m && m.Value == 42) received++; };

            Require(c1.Connect("127.0.0.1", server.Port, "C1") && c2.Connect("127.0.0.1", server.Port, "C2"), "Clients could not connect");
            Require(WaitUntilAllReady(5f, new[] { c1, c2 }, server, c1, c2), "Clients did not become ready");

            BroadcastCounterMessage.WriteCount = 0;
            var msg = NetworkSession.Rent<BroadcastCounterMessage>();
            msg.Value = 42;
            server.Broadcast(msg);

            Require(BroadcastCounterMessage.WriteCount == 1,
                "Broadcast serialized the message more than once across multiple recipients",
                BroadcastCounterMessage.WriteCount.ToString());
            Require(PumpUntil(3f, () => received == 2, server, c1, c2),
                "Not all recipients received single-serialized broadcast", received.ToString());
        }

        [Fact]
        public void Unconnected_OversizedPayload_IsRejectedAndCountedAsMalformed()
        {
            var serverConfig = Config(0, maxClients: 1);
            serverConfig.UnconnectedMessages = true;
            using var server = new NetworkServer(serverConfig);
            int received = 0;
            server.UnconnectedReceived += (_, _) => received++;
            Require(server.Start(), "Server failed to start");

            var oversized = new NetDataWriter(true, NetworkSession.MaxUnconnectedMessageBytes + 100);
            oversized.Put(new byte[NetworkSession.MaxUnconnectedMessageBytes + 50]);
            var reader = new NetDataReader(oversized.Data, 0, oversized.Length);

            server.EnqueueUnconnected(new IPEndPoint(IPAddress.Loopback, 12345), reader);
            server.Update(1f / 60f);

            Require(received == 0, "Oversized unconnected payload was delivered");
            Require(server.MalformedMessageCount == 1, "Oversized unconnected payload was not counted as malformed", server.MalformedMessageCount.ToString());
        }

        private static byte[] BuildWriterPayload(Action<NetDataWriter> write)
        {
            var writer = new NetDataWriter(true, 64);
            write(writer);
            return writer.CopyData();
        }
    }
}
