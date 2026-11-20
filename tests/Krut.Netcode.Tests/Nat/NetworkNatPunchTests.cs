using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Krut.Netcode;
using LiteNetLib;
using LiteNetLib.Layers;
using LiteNetLib.Utils;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests
{
    [Collection(NetworkTestCollection.Name)]
    public class NetworkNatPunchTests
    {
        private const byte NatPingWire = 218;

        private sealed class NatPingMessage : INetworkMessage
        {
            public NetworkMessageType Type => NetworkMessageType.None;
            public byte WireType => NatPingWire;
            public NetworkDelivery Delivery => NetworkDelivery.ReliableOrdered;

            public int Seq;
            public string? Note;

            public void Reset()
            {
                Seq = 0;
                Note = null;
            }

            public void Write(NetDataWriter writer)
            {
                writer.Put(Seq);
                NetworkCodec.WriteText(writer, Note ?? string.Empty);
            }

            public void Read(NetDataReader reader)
            {
                Seq = reader.GetInt();
                Note = NetworkCodec.ReadText(reader);
            }
        }

        private static void EnsureNatPingRegistered()
        {
            if (!NetworkMessages.IsRegistered(NatPingWire))
                NetworkMessages.RegisterGameMessage<NatPingMessage>(NatPingWire, NetworkDelivery.ReliableOrdered);
        }

        private sealed class WireTapLayer : PacketLayerBase
        {
            private readonly object _lock = new();
            public List<byte[]> Captured { get; } = new();

            public WireTapLayer() : base(0)
            {
            }

            public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
            {
            }

            public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
            {
                if (data is null || length <= 0)
                    return;

                var copy = new byte[length];
                Buffer.BlockCopy(data, offset, copy, 0, length);
                lock (_lock)
                    Captured.Add(copy);
            }

            public bool ContainsAscii(string needle)
            {
                byte[] pattern = Encoding.UTF8.GetBytes(needle);
                lock (_lock)
                {
                    foreach (var pkt in Captured)
                    {
                        if (pkt.AsSpan().IndexOf(pattern) >= 0)
                            return true;
                    }
                }

                return false;
            }
        }

        private sealed class PortRestrictedNatSimLayer : PacketLayerBase
        {
            private readonly HashSet<EndPointKey> _pinholes = new();
            private int _blockedInboundCount;
            private int _droppedUnreachableOutboundCount;

            public PortRestrictedNatSimLayer() : base(0)
            {
            }

            public int BlockedInboundCount => Volatile.Read(ref _blockedInboundCount);
            public int DroppedUnreachableOutboundCount => Volatile.Read(ref _droppedUnreachableOutboundCount);

            public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
            {
                if (data is null || length <= 0 || endPoint is null)
                    return;

                IPAddress addr = NatPunchProtocol.NormalizeAddress(endPoint.Address);
                if (IsSimulatedUnreachableDocIp(addr))
                {
                    Interlocked.Increment(ref _droppedUnreachableOutboundCount);
                    length = 0;
                    return;
                }

                lock (_pinholes)
                    _pinholes.Add(new EndPointKey(addr, endPoint.Port));
            }

            public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
            {
                if (data is null || length <= 0 || endPoint is null)
                    return;

                var key = new EndPointKey(NatPunchProtocol.NormalizeAddress(endPoint.Address), endPoint.Port);
                bool allowed;
                lock (_pinholes)
                    allowed = _pinholes.Contains(key);

                if (!allowed)
                {
                    Interlocked.Increment(ref _blockedInboundCount);
                    length = 0;
                }
            }

            private static bool IsSimulatedUnreachableDocIp(IPAddress address)
            {
                if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    return false;

                byte[] bytes = address.GetAddressBytes();
                return bytes.Length == 4 && bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100;
            }

            private readonly struct EndPointKey : IEquatable<EndPointKey>
            {
                public EndPointKey(IPAddress address, int port)
                {
                    Address = address;
                    Port = port;
                }

                public IPAddress Address { get; }
                public int Port { get; }

                public bool Equals(EndPointKey other) => Port == other.Port && Address.Equals(other.Address);
                public override bool Equals(object? obj) => obj is EndPointKey other && Equals(other);
                public override int GetHashCode() => HashCode.Combine(Address, Port);
            }
        }

        private sealed class RawUnconnectedPeer : NetworkSession
        {
            public RawUnconnectedPeer(int port = 0, NetworkEncryptionConfig? encryption = null)
                : base(new NetworkConfig
                {
                    ApplicationId = "krut.netcode.test",
                    ProtocolVersion = 7,
                    Port = port,
                    UnconnectedMessages = true,
                    Encryption = encryption,
                }, isServer: false)
            {
            }

            public int LocalPort => BoundPort;

            public bool StartPeer()
            {
                if (!StartListener(Config.Port))
                    return false;

                SetRunning(true);
                return true;
            }

            internal override void HandleConnectionRequest(ConnectionRequest request) => request.Reject();
        }

        private static bool PumpAll(
            float seconds,
            Func<bool> until,
            NetworkNatRendezvousServer? rendezvous,
            params NetworkSession[] sessions)
        {
            const double maxStep = 0.05;
            var clock = Stopwatch.StartNew();
            double last = 0;

            while (true)
            {
                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(now - last, maxStep);
                last = now;

                rendezvous?.Update(dt);
                for (int i = 0; i < sessions.Length; i++)
                    sessions[i].Update(dt);

                if (until())
                    return true;
                if (now >= seconds)
                    return false;

                Thread.Sleep(1);
            }
        }

        [Fact]
        public void Protocol_RoundtripsAllSevenPacketTypes_IPv4AndIPv6()
        {
            byte[] secret = RandomNumberGenerator.GetBytes(NatPunchProtocol.SecretBytes);
            byte[] nonce = RandomNumberGenerator.GetBytes(NatPunchProtocol.SecretBytes);
            Span<byte> buf = stackalloc byte[NatPunchProtocol.MaxPacketBytes];

            IPEndPoint[] sampleEndpoints =
            {
                new(IPAddress.Parse("203.0.113.42"), 51234),
                new(IPAddress.Parse("2001:db8::77"), 62001),
            };

            foreach (var ep in sampleEndpoints)
            {
                int len1 = NatPunchProtocol.WriteHostRegister(buf, "ROOM-123", "krut.netcode.test", secret, ep, 7);
                Assert.True(NatPunchProtocol.HasMagicHeader(buf.Slice(0, len1)));
                Assert.True(NatPunchProtocol.TryReadHostRegister(
                    buf.Slice(0, len1),
                    out string rRoom,
                    out string rApp,
                    out ReadOnlySpan<byte> rSecret,
                    out IPEndPoint rEp,
                    out int rVer));
                Assert.Equal("ROOM-123", rRoom);
                Assert.Equal("krut.netcode.test", rApp);
                Assert.True(rSecret.SequenceEqual(secret));
                Assert.True(NatPunchProtocol.EndPointsEqual(ep, rEp));
                Assert.Equal(7, rVer);

                int len2 = NatPunchProtocol.WriteHostRegistered(buf, "ROOM-123", NatRendezvousStatus.Ok, ep, 45);
                Assert.True(NatPunchProtocol.TryReadHostRegistered(
                    buf.Slice(0, len2),
                    out string regRoom,
                    out NatRendezvousStatus regStatus,
                    out IPEndPoint regEp,
                    out ushort regLease));
                Assert.Equal("ROOM-123", regRoom);
                Assert.Equal(NatRendezvousStatus.Ok, regStatus);
                Assert.True(NatPunchProtocol.EndPointsEqual(ep, regEp));
                Assert.Equal((ushort)45, regLease);

                int len3 = NatPunchProtocol.WriteHostUnregister(buf, "ROOM-123", "krut.netcode.test", secret);
                Assert.True(NatPunchProtocol.TryReadHostUnregister(
                    buf.Slice(0, len3),
                    out string uRoom,
                    out string uApp,
                    out ReadOnlySpan<byte> uSecret));
                Assert.Equal("ROOM-123", uRoom);
                Assert.Equal("krut.netcode.test", uApp);
                Assert.True(uSecret.SequenceEqual(secret));

                int len4 = NatPunchProtocol.WriteClientJoinRequest(buf, "ROOM-123", "krut.netcode.test", nonce, ep);
                Assert.True(NatPunchProtocol.TryReadClientJoinRequest(
                    buf.Slice(0, len4),
                    out string jRoom,
                    out string jApp,
                    out ReadOnlySpan<byte> jNonce,
                    out IPEndPoint jEp));
                Assert.Equal("ROOM-123", jRoom);
                Assert.Equal("krut.netcode.test", jApp);
                Assert.True(jNonce.SequenceEqual(nonce));
                Assert.True(NatPunchProtocol.EndPointsEqual(ep, jEp));

                int len5 = NatPunchProtocol.WriteJoinError(buf, nonce, NatRendezvousStatus.RoomNotFound);
                Assert.True(NatPunchProtocol.TryReadJoinError(
                    buf.Slice(0, len5),
                    out ReadOnlySpan<byte> eNonce,
                    out NatRendezvousStatus eStatus));
                Assert.True(eNonce.SequenceEqual(nonce));
                Assert.Equal(NatRendezvousStatus.RoomNotFound, eStatus);

                int len6 = NatPunchProtocol.WriteIntroduce(buf, 1, nonce, secret, sampleEndpoints[0], ep, "ROOM-123");
                Assert.True(NatPunchProtocol.TryReadIntroduce(
                    buf.Slice(0, len6),
                    out byte role,
                    out ReadOnlySpan<byte> iPunchNonce,
                    out ReadOnlySpan<byte> iAuthNonce,
                    out IPEndPoint iInt,
                    out IPEndPoint iExt,
                    out string iRoom));
                Assert.Equal((byte)1, role);
                Assert.True(iPunchNonce.SequenceEqual(nonce));
                Assert.True(iAuthNonce.SequenceEqual(secret));
                Assert.True(NatPunchProtocol.EndPointsEqual(sampleEndpoints[0], iInt));
                Assert.True(NatPunchProtocol.EndPointsEqual(ep, iExt));
                Assert.Equal("ROOM-123", iRoom);

                int len7 = NatPunchProtocol.WritePunch(buf, nonce, NatPunchFlags.FromHost | NatPunchFlags.IsAck);
                Assert.True(NatPunchProtocol.TryReadPunch(
                    buf.Slice(0, len7),
                    out ReadOnlySpan<byte> pNonce,
                    out NatPunchFlags pFlags));
                Assert.True(pNonce.SequenceEqual(nonce));
                Assert.Equal(NatPunchFlags.FromHost | NatPunchFlags.IsAck, pFlags);
            }
        }

        [Fact]
        public void Protocol_RejectsMalformedPackets_AndInvalidRoomCodes()
        {
            Assert.False(NatPunchProtocol.TryValidateRoomCode(null, out _));
            Assert.False(NatPunchProtocol.TryValidateRoomCode("   ", out _));
            Assert.False(NatPunchProtocol.TryValidateRoomCode("BAD\nROOM", out _));
            Assert.False(NatPunchProtocol.TryValidateRoomCode(new string('A', 65), out _));
            Assert.True(NatPunchProtocol.TryValidateRoomCode("  VALID-ROOM  ", out string trimmed));
            Assert.Equal("VALID-ROOM", trimmed);

            byte[] secret = RandomNumberGenerator.GetBytes(NatPunchProtocol.SecretBytes);
            var valid = new byte[NatPunchProtocol.MaxPacketBytes];
            int len = NatPunchProtocol.WritePunch(valid, secret, NatPunchFlags.FromHost);

            // Wrong magic
            var badMagic = valid.AsSpan(0, len).ToArray();
            badMagic[0] = (byte)'X';
            Assert.False(NatPunchProtocol.HasMagicHeader(badMagic));
            Assert.False(NatPunchProtocol.TryReadPunch(badMagic, out _, out _));

            // Wrong version
            var badVersion = valid.AsSpan(0, len).ToArray();
            badVersion[4] = 99;
            Assert.False(NatPunchProtocol.TryReadPunch(badVersion, out _, out _));

            // Unknown flag bit
            var badFlag = valid.AsSpan(0, len).ToArray();
            badFlag[^1] = 0x80;
            Assert.False(NatPunchProtocol.TryReadPunch(badFlag, out _, out _));

            // Trailing garbage
            var withTrailing = new byte[len + 1];
            valid.AsSpan(0, len).CopyTo(withTrailing);
            Assert.False(NatPunchProtocol.TryReadPunch(withTrailing, out _, out _));

            // Truncated HostRegister & port 0 rejection
            Span<byte> regBuf = stackalloc byte[NatPunchProtocol.MaxPacketBytes];
            int regLen = NatPunchProtocol.WriteHostRegister(
                regBuf,
                "ROOM",
                "app",
                secret,
                new IPEndPoint(IPAddress.Loopback, 12345),
                1);
            Assert.False(NatPunchProtocol.TryReadHostRegister(regBuf.Slice(0, regLen - 1), out _, out _, out _, out _, out _));

            var regArr = regBuf.Slice(0, regLen).ToArray();
            regArr[regLen - 6] = 0; // high byte of port
            regArr[regLen - 5] = 0; // low byte of port -> port 0
            Assert.False(NatPunchProtocol.TryReadHostRegister(regArr, out _, out _, out _, out _, out _));
        }

        [Fact]
        public void Plaintext_NatPunch_EndToEnd_And_Unregister()
        {
            EnsureNatPingRegistered();
            int rdvPort = FreeUdpPort();
            using var rdv = new NetworkNatRendezvousServer(new NatRendezvousConfig { Port = rdvPort });
            Require(rdv.Start(), "Rendezvous server did not start");

            var hostConfig = Config(FreeUdpPort());
            using var host = new NetworkServer(hostConfig);
            var clientConfig = Config(0);
            using var client = new NetworkClient(clientConfig);

            NatRegistrationResult? hostRegistered = null;
            NatPunchResult? hostPunched = null;
            NatPunchResult? clientPunched = null;
            bool ready = false;
            NatPingMessage? receivedByHost = null;

            host.NatRoomRegistered += r => hostRegistered = r;
            host.NatPunchSucceeded += p => hostPunched = p;
            host.MessageReceived += e =>
            {
                if (e.Message is NatPingMessage ping)
                    receivedByHost = new NatPingMessage { Seq = ping.Seq, Note = ping.Note };
            };
            client.NatPunchSucceeded += p => clientPunched = p;
            client.SessionReady += () => ready = true;

            Require(host.StartWithNat("127.0.0.1", rdvPort, "PLAIN-ROOM"), "Host StartWithNat failed");
            Require(PumpAll(3f, () => hostRegistered.HasValue && host.IsNatRegistered, rdv, host), "Host never registered");
            Assert.Equal("PLAIN-ROOM", hostRegistered!.Value.RoomCode);
            Assert.Equal(1, rdv.ActiveRoomCount);

            Require(client.ConnectViaNat("127.0.0.1", rdvPort, "PLAIN-ROOM", "Alice"), "Client ConnectViaNat failed");
            Require(PumpAll(5f, () => ready && hostPunched.HasValue && clientPunched.HasValue, rdv, host, client),
                "Client never completed NAT punch + SessionReady");

            Assert.Equal(NetworkClientState.Connected, client.State);
            Assert.Equal("PLAIN-ROOM", clientPunched!.Value.RoomCode);

            var msg = NetworkSession.Rent<NatPingMessage>();
            msg.Seq = 42;
            msg.Note = "hello-over-nat";
            client.Send(client.ServerPeerId, msg);
            client.Flush();
            Require(PumpAll(2f, () => receivedByHost is not null, rdv, host, client), "Message over NAT did not arrive");
            Assert.Equal(42, receivedByHost!.Seq);
            Assert.Equal("hello-over-nat", receivedByHost.Note);

            host.UnregisterNatRoom();
            Require(PumpAll(2f, () => rdv.ActiveRoomCount == 0, rdv, host), "Room was not unregistered");
            Assert.False(host.IsNatRegistered);
            Assert.Null(host.NatRoomCode);
        }

        [Fact]
        public void SimulatedPortRestrictedConeNat_BlocksDirectConnect_ButAllowsNatPunchthrough()
        {
            EnsureNatPingRegistered();
            int rdvPort = FreeUdpPort();
            using var rdv = new NetworkNatRendezvousServer(new NatRendezvousConfig { Port = rdvPort });
            Require(rdv.Start(), "Rendezvous server did not start");

            var hostNat = new PortRestrictedNatSimLayer();
            var hostConfig = Config(FreeUdpPort());
            hostConfig.DatagramLayer = new NetworkDatagramChain(hostNat);
            hostConfig.NatPunch = new NatPunchConfig
            {
                LocalEndPointOverride = new IPEndPoint(IPAddress.Parse("198.51.100.10"), 9999),
                PunchIntervalMs = 80,
                PunchTimeoutMs = 5000,
            };
            using var host = new NetworkServer(hostConfig);

            Require(host.StartWithNat("127.0.0.1", rdvPort, "CONE-ROOM"), "Host StartWithNat failed");
            Require(PumpAll(3f, () => host.IsNatRegistered, rdv, host), "Host never registered behind cone NAT");

            // A client dialing the host directly without punching gets dropped by the host's Port-Restricted Cone NAT.
            var unpunchedCfg = Config(0);
            unpunchedCfg.ConnectRequestAttempts = 2;
            unpunchedCfg.ConnectRequestIntervalMs = 100;
            using var unpunchedClient = new NetworkClient(unpunchedCfg);
            bool unpunchedClosed = false;
            unpunchedClient.Closed += (_, _) => unpunchedClosed = true;
            Require(unpunchedClient.Connect("127.0.0.1", host.Port, "Intruder"), "Unpunched Connect call failed");
            Require(PumpAll(2f, () => unpunchedClosed && hostNat.BlockedInboundCount > 0, rdv, host, unpunchedClient),
                "Unpunched direct connect was not blocked by simulated Port-Restricted Cone NAT");
            Assert.False(unpunchedClient.IsSessionReady);
            Assert.True(hostNat.BlockedInboundCount > 0);
            unpunchedClient.Stop();

            int blockedBeforePunch = hostNat.BlockedInboundCount;

            var clientNat = new PortRestrictedNatSimLayer();
            var clientConfig = Config(0);
            clientConfig.DatagramLayer = new NetworkDatagramChain(clientNat);
            clientConfig.NatPunch = new NatPunchConfig
            {
                LocalEndPointOverride = new IPEndPoint(IPAddress.Parse("198.51.100.20"), 9999),
                PunchIntervalMs = 80,
                PunchTimeoutMs = 5000,
            };
            using var client = new NetworkClient(clientConfig);

            bool clientReady = false;
            NatPingMessage? received = null;
            client.SessionReady += () => clientReady = true;
            host.MessageReceived += e =>
            {
                if (e.Message is NatPingMessage ping)
                    received = new NatPingMessage { Seq = ping.Seq, Note = ping.Note };
            };

            Require(client.ConnectViaNat("127.0.0.1", rdvPort, "CONE-ROOM", "Bob"), "Client ConnectViaNat failed");

            // Deterministic Cone-NAT proof: pump only rendezvous + client until the client sends its first
            // Punch probe and the host's Port-Restricted Cone NAT actively drops it before host opens a pinhole.
            Require(PumpAll(2f, () => hostNat.BlockedInboundCount > blockedBeforePunch, rdv, client),
                "Client's initial punch probe did not hit the host's closed Cone NAT pinhole");

            Require(PumpAll(5f, () => clientReady, rdv, host, client),
                "Client behind simulated Port-Restricted Cone NAT never connected via NAT punchthrough");

            Assert.True(hostNat.BlockedInboundCount > blockedBeforePunch);
            Assert.True(clientNat.DroppedUnreachableOutboundCount > 0);
            Assert.True(client.LastNatPunchResult.HasValue);
            Assert.Equal(NatAddressKind.External, client.LastNatPunchResult!.Value.AddressKind);

            var msg = NetworkSession.Rent<NatPingMessage>();
            msg.Seq = 99;
            msg.Note = "cone-nat-ok";
            client.Send(client.ServerPeerId, msg);
            client.Flush();
            Require(PumpAll(2f, () => received is not null, rdv, host, client), "Message failed through punched cone NATs");
            Assert.Equal(99, received!.Seq);
            Assert.Equal("cone-nat-ok", received.Note);
        }

        [Fact]
        public void NatPunch_WithEphemeralEcdhAndServerIdentityPinning_EstablishesEncryptedSession()
        {
            EnsureNatPingRegistered();
            int rdvPort = FreeUdpPort();
            using var rdv = new NetworkNatRendezvousServer(new NatRendezvousConfig { Port = rdvPort });
            Require(rdv.Start(), "Rendezvous did not start");

            var (privKey, pubKey) = NetworkCrypto.GenerateIdentityKeyPair();

            var hostConfig = Config(FreeUdpPort());
            hostConfig.Encryption = NetworkEncryptionConfig.CreateEphemeral(privKey, default);
            using var host = new NetworkServer(hostConfig);

            var clientConfig = Config(0);
            clientConfig.Encryption = NetworkEncryptionConfig.CreateEphemeral(default, pubKey);
            using var client = new NetworkClient(clientConfig);

            bool ready = false;
            NatPingMessage? received = null;
            client.SessionReady += () => ready = true;
            host.MessageReceived += e =>
            {
                if (e.Message is NatPingMessage p)
                    received = new NatPingMessage { Seq = p.Seq, Note = p.Note };
            };

            Require(host.StartWithNat("127.0.0.1", rdvPort, "ECDH-ROOM"), "Host failed to start with NAT");
            Require(PumpAll(3f, () => host.IsNatRegistered, rdv, host), "Host failed to register");
            Require(client.ConnectViaNat("127.0.0.1", rdvPort, "ECDH-ROOM", "Carol"), "Client ConnectViaNat failed");
            Require(PumpAll(5f, () => ready, rdv, host, client), "Client failed ECDH NAT session setup");

            Assert.True(client.RemotePeer!.IsSessionEncrypted);
            Assert.True(host.EncryptionStats.KeyExchangesCompleted >= 1);
            Assert.True(client.EncryptionStats.KeyExchangesCompleted >= 1);

            var msg = NetworkSession.Rent<NatPingMessage>();
            msg.Seq = 7;
            msg.Note = "ecdh-secret";
            client.Send(client.ServerPeerId, msg);
            client.Flush();
            Require(PumpAll(2f, () => received is not null, rdv, host, client), "ECDH NAT message did not arrive");
            Assert.Equal("ecdh-secret", received!.Note);
        }

        [Fact]
        public void NatPunch_WithMatchPskAndRendezvousPsk_EncryptsAllTraffic()
        {
            EnsureNatPingRegistered();
            byte[] matchKey = NetworkCrypto.GenerateKey();
            byte[] rdvKey = NetworkCrypto.GenerateKey();
            byte[] wrongRdvKey = NetworkCrypto.GenerateKey();

            int rdvPort = FreeUdpPort();
            using var rdv = new NetworkNatRendezvousServer(new NatRendezvousConfig
            {
                Port = rdvPort,
                PreSharedKey = rdvKey,
            });
            Require(rdv.Start(), "Encrypted rendezvous server did not start");

            var tap = new WireTapLayer();
            var hostConfig = Config(FreeUdpPort());
            hostConfig.Encryption = NetworkEncryptionConfig.CreatePreSharedKey(matchKey);
            hostConfig.NatPunch = NatPunchConfig.CreateEncrypted(rdvKey);
            hostConfig.DatagramLayer = new NetworkDatagramChain(tap);
            using var host = new NetworkServer(hostConfig);

            var clientConfig = Config(0);
            clientConfig.Encryption = NetworkEncryptionConfig.CreatePreSharedKey(matchKey);
            clientConfig.NatPunch = NatPunchConfig.CreateEncrypted(rdvKey);
            using var client = new NetworkClient(clientConfig);

            bool ready = false;
            NatPingMessage? received = null;
            client.SessionReady += () => ready = true;
            host.MessageReceived += e =>
            {
                if (e.Message is NatPingMessage p)
                    received = new NatPingMessage { Seq = p.Seq, Note = p.Note };
            };

            Require(host.StartWithNat("127.0.0.1", rdvPort, "SECRET-ROOM-XYZ"), "Host failed");
            Require(PumpAll(3f, () => host.IsNatRegistered, rdv, host), "Host never registered with encrypted rendezvous");
            Require(client.ConnectViaNat("127.0.0.1", rdvPort, "SECRET-ROOM-XYZ", "PlayerSecret"), "Client failed");
            Require(PumpAll(5f, () => ready, rdv, host, client), "Client failed encrypted PSK NAT punch");

            var msg = NetworkSession.Rent<NatPingMessage>();
            msg.Seq = 314;
            msg.Note = "top-secret-payload";
            client.Send(client.ServerPeerId, msg);
            client.Flush();
            Require(PumpAll(2f, () => received is not null, rdv, host, client), "Encrypted PSK message did not arrive");

            Assert.True(rdv.EncryptionStats.PacketsEncrypted > 0);
            Assert.True(rdv.EncryptionStats.PacketsDecrypted > 0);
            Assert.True(host.EncryptionStats.PacketsEncrypted > 0);
            Assert.True(client.EncryptionStats.PacketsEncrypted > 0);
            Assert.False(tap.ContainsAscii("SECRET-ROOM-XYZ"));
            Assert.False(tap.ContainsAscii("PlayerSecret"));
            Assert.False(tap.ContainsAscii("KNAT"));
            Assert.False(tap.ContainsAscii("top-secret-payload"));

            // Client with wrong Rendezvous PSK must be dropped by rendezvous authentication and time out.
            var badClientConfig = Config(0);
            badClientConfig.Encryption = NetworkEncryptionConfig.CreatePreSharedKey(matchKey);
            badClientConfig.NatPunch = NatPunchConfig.CreateEncrypted(wrongRdvKey);
            badClientConfig.NatPunch.PunchTimeoutMs = 600;
            using var badClient = new NetworkClient(badClientConfig);
            string? badFailReason = null;
            badClient.NatPunchFailed += reason => badFailReason = reason;
            Require(badClient.ConnectViaNat("127.0.0.1", rdvPort, "SECRET-ROOM-XYZ", "Eve"), "Bad client failed to start");
            Require(PumpAll(2f, () => badFailReason is not null, rdv, host, badClient), "Bad PSK client did not fail");
            Assert.True(rdv.EncryptionStats.DroppedAuthFailures > 0);
        }

        [Fact]
        public void NatPunch_WithCombinedEncryptionAndRendezvousPsk_WorksEndToEnd()
        {
            EnsureNatPingRegistered();
            byte[] matchKey = NetworkCrypto.GenerateKey();
            byte[] rdvKey = NetworkCrypto.GenerateKey();
            var (privKey, pubKey) = NetworkCrypto.GenerateIdentityKeyPair();

            int rdvPort = FreeUdpPort();
            using var rdv = new NetworkNatRendezvousServer(new NatRendezvousConfig
            {
                Port = rdvPort,
                PreSharedKey = rdvKey,
            });
            Require(rdv.Start(), "Rendezvous failed to start");

            var hostConfig = Config(FreeUdpPort());
            hostConfig.Encryption = NetworkEncryptionConfig.CreateCombined(matchKey, privKey, default);
            hostConfig.NatPunch = NatPunchConfig.CreateEncrypted(rdvKey);
            using var host = new NetworkServer(hostConfig);

            var clientConfig = Config(0);
            clientConfig.Encryption = NetworkEncryptionConfig.CreateCombined(matchKey, default, pubKey);
            clientConfig.NatPunch = NatPunchConfig.CreateEncrypted(rdvKey);
            using var client = new NetworkClient(clientConfig);

            bool ready = false;
            NatPingMessage? received = null;
            client.SessionReady += () => ready = true;
            host.MessageReceived += e =>
            {
                if (e.Message is NatPingMessage p)
                    received = new NatPingMessage { Seq = p.Seq, Note = p.Note };
            };

            Require(host.StartWithNat("127.0.0.1", rdvPort, "COMBINED-ROOM"), "Host failed");
            Require(PumpAll(3f, () => host.IsNatRegistered, rdv, host), "Host registration failed");
            Require(client.ConnectViaNat("127.0.0.1", rdvPort, "COMBINED-ROOM", "Dave"), "Client failed");
            Require(PumpAll(5f, () => ready, rdv, host, client), "Combined encrypted NAT session failed");

            Assert.True(client.RemotePeer!.IsSessionEncrypted);

            var msg = NetworkSession.Rent<NatPingMessage>();
            msg.Seq = 888;
            msg.Note = "combined-ok";
            client.Send(client.ServerPeerId, msg);
            client.Flush();
            Require(PumpAll(2f, () => received is not null, rdv, host, client), "Combined message failed");
            Assert.Equal("combined-ok", received!.Note);
        }

        [Fact]
        public void NatPunch_WithMatchPskAndPlaintextRendezvous_AllowsKnatOnlyAndBlocksSpoofedNonNat()
        {
            EnsureNatPingRegistered();
            byte[] matchKey = NetworkCrypto.GenerateKey();

            int rdvPort = FreeUdpPort();
            using var rdv = new NetworkNatRendezvousServer(new NatRendezvousConfig { Port = rdvPort });
            Require(rdv.Start(), "Plaintext rendezvous failed");

            var hostConfig = Config(FreeUdpPort());
            hostConfig.Encryption = NetworkEncryptionConfig.CreatePreSharedKey(matchKey);
            using var host = new NetworkServer(hostConfig);

            var clientConfig = Config(0);
            clientConfig.Encryption = NetworkEncryptionConfig.CreatePreSharedKey(matchKey);
            using var client = new NetworkClient(clientConfig);

            bool ready = false;
            client.SessionReady += () => ready = true;

            Require(host.StartWithNat("127.0.0.1", rdvPort, "HYBRID-ROOM"), "Host failed");
            Require(PumpAll(3f, () => host.IsNatRegistered, rdv, host), "Host registration failed");
            Require(client.ConnectViaNat("127.0.0.1", rdvPort, "HYBRID-ROOM", "Frank"), "Client failed");
            Require(PumpAll(5f, () => ready, rdv, host, client), "Client failed hybrid match-PSK + plaintext rendezvous");

            // Stop rendezvous server so we can bind an unencrypted attacker socket on the exact same port,
            // proving that the host's PSK layer only permits "KNAT" unconnected packets from the rendezvous endpoint
            // and drops forged non-KNAT packets instead of letting them bypass Match PSK.
            rdv.Stop();

            using var spoof = new RawUnconnectedPeer(rdvPort, encryption: null);
            Require(spoof.StartPeer(), "Spoof peer failed to bind released rendezvous port");
            spoof.SendUnconnected(new IPEndPoint(IPAddress.Loopback, host.Port), Encoding.ASCII.GetBytes("EVIL_NON_KNAT"));
            spoof.Flush();

            Require(PumpAll(1.5f, () => host.EncryptionStats.DroppedMalformed > 0, null, host, spoof),
                "Spoofed non-KNAT packet from rendezvous port was not dropped as malformed");
        }

        [Fact]
        public void Security_RoomHijackBlocked_SpoofedIntroduceRejected_And_SpoofedPunchRejected()
        {
            int rdvPort = FreeUdpPort();
            using var rdv = new NetworkNatRendezvousServer(new NatRendezvousConfig { Port = rdvPort });
            Require(rdv.Start(), "Rendezvous failed");

            using var host = new NetworkServer(Config(FreeUdpPort()));
            Require(host.StartWithNat("127.0.0.1", rdvPort, "LOCKED-ROOM"), "Host failed");
            Require(PumpAll(3f, () => host.IsNatRegistered, rdv, host), "Host registration failed");

            // 1. Hijack host using different HostSecret cannot overwrite LOCKED-ROOM or unregister it.
            using var hijackHost = new NetworkServer(Config(FreeUdpPort()));
            string? hijackFailed = null;
            hijackHost.NatPunchFailed += reason => hijackFailed = reason;
            Require(hijackHost.StartWithNat("127.0.0.1", rdvPort, "LOCKED-ROOM"), "Hijack StartWithNat failed");
            Require(PumpAll(2f, () => hijackFailed is not null, rdv, host, hijackHost), "RoomTaken error was not delivered");
            Assert.Contains("RoomTaken", hijackFailed!, StringComparison.Ordinal);
            Assert.Equal(1, rdv.ActiveRoomCount);

            // Attempt forged HostUnregister with random HostSecret.
            using var attacker = new RawUnconnectedPeer();
            Require(attacker.StartPeer(), "Attacker peer failed");
            Span<byte> buf = stackalloc byte[NatPunchProtocol.MaxPacketBytes];
            int unregLen = NatPunchProtocol.WriteHostUnregister(
                buf,
                "LOCKED-ROOM",
                "krut.netcode.test",
                RandomNumberGenerator.GetBytes(NatPunchProtocol.SecretBytes));
            attacker.SendUnconnected(new IPEndPoint(IPAddress.Loopback, rdvPort), buf.Slice(0, unregLen));
            attacker.Flush();
            PumpAll(0.2f, () => false, rdv, attacker);
            Assert.Equal(1, rdv.ActiveRoomCount);

            // 2. Spoofed Introduce sent to Host from non-rendezvous source with fake secret is rejected cleanly.
            int introLen = NatPunchProtocol.WriteIntroduce(
                buf,
                role: 0,
                RandomNumberGenerator.GetBytes(NatPunchProtocol.SecretBytes),
                RandomNumberGenerator.GetBytes(NatPunchProtocol.SecretBytes),
                new IPEndPoint(IPAddress.Loopback, attacker.LocalPort),
                new IPEndPoint(IPAddress.Loopback, attacker.LocalPort),
                "LOCKED-ROOM");
            attacker.SendUnconnected(new IPEndPoint(IPAddress.Loopback, host.Port), buf.Slice(0, introLen));

            // 3. Spoofed Punch with wrong nonce & FromHost reflection bit sent to Host is rejected.
            int spoofPunchLen = NatPunchProtocol.WritePunch(
                buf,
                RandomNumberGenerator.GetBytes(NatPunchProtocol.SecretBytes),
                NatPunchFlags.FromHost);
            attacker.SendUnconnected(new IPEndPoint(IPAddress.Loopback, host.Port), buf.Slice(0, spoofPunchLen));
            attacker.Flush();

            Require(PumpAll(2f, () => host.NatStats.RejectedPackets >= 2, rdv, host, attacker),
                "Host did not reject spoofed Introduce and Punch packets");
            Assert.Equal(0, host.NatStats.PunchProbesSent);

            // 4. Spoofed Introduce & Punch sent to Punching Client is also rejected cleanly.
            var clientConfig = Config(0);
            clientConfig.NatPunch = new NatPunchConfig { PunchIntervalMs = 2000, PunchTimeoutMs = 3000 };
            using var client = new NetworkClient(clientConfig);
            Require(client.ConnectViaNat("127.0.0.1", rdvPort, "UNKNOWN-WAIT", "Gina"), "Client failed");

            int clientIntroLen = NatPunchProtocol.WriteIntroduce(
                buf,
                role: 1,
                RandomNumberGenerator.GetBytes(NatPunchProtocol.SecretBytes),
                RandomNumberGenerator.GetBytes(NatPunchProtocol.SecretBytes),
                new IPEndPoint(IPAddress.Loopback, attacker.LocalPort),
                new IPEndPoint(IPAddress.Loopback, attacker.LocalPort),
                "UNKNOWN-WAIT");
            attacker.SendUnconnected(new IPEndPoint(IPAddress.Loopback, client.LocalPort), buf.Slice(0, clientIntroLen));
            attacker.Flush();

            Require(PumpAll(2f, () => client.NatStats.RejectedPackets >= 1, null, client, attacker),
                "Client did not reject spoofed Introduce packet");
            Assert.Equal(0, client.NatStats.PunchProbesSent);
        }

        [Fact]
        public void Lifecycle_RoomNotFound_And_Timeout_TransitionClientCleanlyToOffline()
        {
            int rdvPort = FreeUdpPort();
            using var rdv = new NetworkNatRendezvousServer(new NatRendezvousConfig { Port = rdvPort });
            Require(rdv.Start(), "Rendezvous failed");

            using var client = new NetworkClient(Config(0));
            string? notFoundReason = null;
            client.NatPunchFailed += r => notFoundReason = r;

            Require(client.ConnectViaNat("127.0.0.1", rdvPort, "MISSING-ROOM", "Hank"), "ConnectViaNat failed");
            Assert.Equal(NetworkClientState.Punching, client.State);
            Require(PumpAll(2f, () => notFoundReason is not null, rdv, client), "RoomNotFound failure never arrived");
            Assert.Contains("RoomNotFound", notFoundReason!, StringComparison.Ordinal);
            Assert.Equal(NetworkClientState.Offline, client.State);
            Assert.False(client.IsRunning);
            Assert.True(client.NatStats.PunchesFailed >= 1);

            // Blackhole port timeout test
            int deadPort = FreeUdpPort();
            var timeoutConfig = Config(0);
            timeoutConfig.NatPunch = new NatPunchConfig
            {
                PunchIntervalMs = 150,
                PunchTimeoutMs = 500,
            };
            using var timeoutClient = new NetworkClient(timeoutConfig);
            string? timeoutReason = null;
            timeoutClient.NatPunchFailed += r => timeoutReason = r;

            Require(timeoutClient.ConnectViaNat("127.0.0.1", deadPort, "ANY-ROOM", "Ivan"), "Timeout ConnectViaNat failed");
            Require(PumpAll(2f, () => timeoutReason is not null, null, timeoutClient), "Timeout did not trigger");
            Assert.Equal(NetworkClientState.Offline, timeoutClient.State);
            Assert.False(timeoutClient.IsRunning);
            Assert.True(timeoutClient.NatStats.PunchesFailed >= 1);
        }

        [Fact]
        public void RendezvousServer_EnforcesMaxRooms_And_IpRateLimit()
        {
            int rdvPort = FreeUdpPort();
            using var rdv = new NetworkNatRendezvousServer(new NatRendezvousConfig
            {
                Port = rdvPort,
                MaxRooms = 1,
                MaxPacketsPerIpPerSecond = 2,
            });
            Require(rdv.Start(), "Rendezvous failed");

            using var host1 = new NetworkServer(Config(FreeUdpPort()));
            Require(host1.StartWithNat("127.0.0.1", rdvPort, "ROOM-1"), "Host 1 failed");
            Require(PumpAll(2f, () => host1.IsNatRegistered, rdv, host1), "Host 1 failed to register");

            // Host 2 exceeds MaxRooms = 1 -> ServerFull.
            using var host2 = new NetworkServer(Config(FreeUdpPort()));
            string? host2Failure = null;
            host2.NatPunchFailed += r => host2Failure = r;
            Require(host2.StartWithNat("127.0.0.1", rdvPort, "ROOM-2"), "Host 2 failed");
            Require(PumpAll(2f, () => host2Failure is not null, rdv, host2), "Host 2 did not get ServerFull");
            Assert.Contains("ServerFull", host2Failure!, StringComparison.Ordinal);

            // Wait 1.1s for the 1s rate-limit window on 127.0.0.1 to reset, then verify Host 2 succeeds once Host 1 unregisters.
            Thread.Sleep(1100);
            host1.UnregisterNatRoom();
            PumpAll(0.5f, () => rdv.ActiveRoomCount == 0, rdv, host1);

            host2Failure = null;
            Require(host2.RegisterNatRoom("127.0.0.1", rdvPort, "ROOM-2"), "Host 2 re-register failed");
            Require(PumpAll(2f, () => host2.IsNatRegistered, rdv, host2), "Host 2 did not register after slot freed");

            // Flood packets from same IP (> MaxPacketsPerIpPerSecond = 2) -> RateLimitedPackets increments.
            Thread.Sleep(1100);
            using var flooder = new RawUnconnectedPeer();
            Require(flooder.StartPeer(), "Flooder failed");
            Span<byte> buf = stackalloc byte[NatPunchProtocol.MaxPacketBytes];
            int joinLen = NatPunchProtocol.WriteClientJoinRequest(
                buf,
                "ROOM-2",
                "krut.netcode.test",
                RandomNumberGenerator.GetBytes(NatPunchProtocol.SecretBytes),
                new IPEndPoint(IPAddress.Loopback, flooder.LocalPort));
            for (int i = 0; i < 6; i++)
                flooder.SendUnconnected(new IPEndPoint(IPAddress.Loopback, rdvPort), buf.Slice(0, joinLen));
            flooder.Flush();

            Require(PumpAll(2f, () => rdv.Stats.RateLimitedPackets > 0, rdv, flooder),
                "IP rate limiter did not drop excess burst packets");
        }

        [Fact]
        public void AutoReconnect_ViaNat_RePunchesAfterDisconnect()
        {
            int rdvPort = FreeUdpPort();
            using var rdv = new NetworkNatRendezvousServer(new NatRendezvousConfig { Port = rdvPort });
            Require(rdv.Start(), "Rendezvous failed");

            using var host = new NetworkServer(Config(FreeUdpPort()));
            Require(host.StartWithNat("127.0.0.1", rdvPort, "RECONN-ROOM"), "Host failed");
            Require(PumpAll(3f, () => host.IsNatRegistered, rdv, host), "Host never registered");

            var clientConfig = Config(0);
            clientConfig.AutoReconnect = true;
            clientConfig.ReconnectDelayMs = 150;
            clientConfig.MaxConnectAttempts = 4;
            using var client = new NetworkClient(clientConfig);

            int readyCount = 0;
            bool closedOnce = false;
            client.SessionReady += () => readyCount++;
            client.Closed += (_, _) => closedOnce = true;

            Require(client.ConnectViaNat("127.0.0.1", rdvPort, "RECONN-ROOM", "Judy"), "Initial NAT connect failed");
            Require(PumpAll(5f, () => readyCount == 1, rdv, host, client), "Initial NAT connection failed");

            // Disconnect peer from server; AutoReconnect must re-punch via rendezvous and become ready a second time.
            host.DisconnectPeer(client.LocalPeerId, "Temporary kick");
            Require(PumpAll(2f, () => closedOnce, rdv, host, client), "Client did not observe disconnect");
            Require(PumpAll(6f, () => readyCount == 2, rdv, host, client), "Client did not auto-reconnect via NAT");
            Assert.Equal(NetworkClientState.Connected, client.State);
        }

        [Fact]
        public void NatPunch_WithEphemeralEcdhAndRendezvousPsk_UsesSelectiveRendezvousLayer()
        {
            EnsureNatPingRegistered();
            byte[] rdvKey = NetworkCrypto.GenerateKey();
            byte[] wrongRdvKey = NetworkCrypto.GenerateKey();
            var (privKey, pubKey) = NetworkCrypto.GenerateIdentityKeyPair();

            int rdvPort = FreeUdpPort();
            using var rdv = new NetworkNatRendezvousServer(new NatRendezvousConfig
            {
                Port = rdvPort,
                PreSharedKey = rdvKey,
            });
            Require(rdv.Start(), "Encrypted rendezvous server did not start");

            // No Match PSK: only NatRendezvousPacketLayer encrypts traffic to/from rendezvous,
            // while Host <-> Client traffic uses per-peer ephemeral ECDH + ECDsa pinning.
            var hostConfig = Config(FreeUdpPort());
            hostConfig.Encryption = NetworkEncryptionConfig.CreateEphemeral(privKey, default);
            hostConfig.NatPunch = NatPunchConfig.CreateEncrypted(rdvKey);
            using var host = new NetworkServer(hostConfig);

            var clientConfig = Config(0);
            clientConfig.Encryption = NetworkEncryptionConfig.CreateEphemeral(default, pubKey);
            clientConfig.NatPunch = NatPunchConfig.CreateEncrypted(rdvKey);
            using var client = new NetworkClient(clientConfig);

            bool ready = false;
            NatPingMessage? received = null;
            client.SessionReady += () => ready = true;
            host.MessageReceived += e =>
            {
                if (e.Message is NatPingMessage p)
                    received = new NatPingMessage { Seq = p.Seq, Note = p.Note };
            };

            Require(host.StartWithNat("127.0.0.1", rdvPort, "SELECTIVE-ECDH-ROOM"), "Host failed");
            Require(PumpAll(3f, () => host.IsNatRegistered, rdv, host), "Host never registered");
            Require(client.ConnectViaNat("127.0.0.1", rdvPort, "SELECTIVE-ECDH-ROOM", "Klara"), "Client failed");
            Require(PumpAll(5f, () => ready, rdv, host, client), "Client failed ECDH + selective rendezvous PSK NAT punch");

            Assert.True(client.RemotePeer!.IsSessionEncrypted);
            Assert.True(rdv.EncryptionStats.PacketsEncrypted > 0);
            Assert.True(rdv.EncryptionStats.PacketsDecrypted > 0);

            var msg = NetworkSession.Rent<NatPingMessage>();
            msg.Seq = 555;
            msg.Note = "selective-ecdh-ok";
            client.Send(client.ServerPeerId, msg);
            client.Flush();
            Require(PumpAll(2f, () => received is not null, rdv, host, client), "Selective ECDH message failed");
            Assert.Equal("selective-ecdh-ok", received!.Note);

            // Second client with wrong rendezvous PSK must be rejected on rendezvous layer before introduction.
            var badClientConfig = Config(0);
            badClientConfig.Encryption = NetworkEncryptionConfig.CreateEphemeral(default, pubKey);
            badClientConfig.NatPunch = NatPunchConfig.CreateEncrypted(wrongRdvKey);
            badClientConfig.NatPunch.PunchTimeoutMs = 600;
            using var badClient = new NetworkClient(badClientConfig);
            string? badFail = null;
            badClient.NatPunchFailed += r => badFail = r;
            Require(badClient.ConnectViaNat("127.0.0.1", rdvPort, "SELECTIVE-ECDH-ROOM", "Mallory"), "Bad client failed to start");
            Require(PumpAll(2f, () => badFail is not null, rdv, host, badClient), "Bad PSK client did not fail");
            Assert.True(rdv.EncryptionStats.DroppedAuthFailures > 0);
        }
    }
}
