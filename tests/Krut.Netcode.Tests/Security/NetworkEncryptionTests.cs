using System;
using System.Collections.Generic;
using System.Net;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Krut.Netcode;
using LiteNetLib.Layers;
using LiteNetLib.Utils;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests;

[Collection(NetworkTestCollection.Name)]
public class NetworkEncryptionTests
{
    private const byte EncryptedPingWire = 214;

    private sealed class EncryptedPingMessage : INetworkMessage
    {
        public NetworkMessageType Type => NetworkMessageType.None;
        public byte WireType => EncryptedPingWire;
        public NetworkDelivery Delivery => NetworkDelivery.Unreliable;

        public int Seq;
        public int Value;

        public void Reset()
        {
            Seq = 0;
            Value = 0;
        }

        public void Write(NetDataWriter writer)
        {
            writer.Put(Seq);
            writer.Put(Value);
        }

        public void Read(NetDataReader reader)
        {
            Seq = reader.GetInt();
            Value = reader.GetInt();
        }
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

    private static void EnsureRegistered()
    {
        if (!NetworkMessages.IsRegistered(EncryptedPingWire))
            NetworkMessages.RegisterGameMessage<EncryptedPingMessage>(EncryptedPingWire, NetworkDelivery.Unreliable);
    }

    [Fact]
    public void AntiReplayWindow_HandlesSequentialOutOfOrderDuplicatesAndWindowShift()
    {
        var window = default(AntiReplayWindow);

        Assert.True(window.IsReplay(0));
        Assert.False(window.IsReplay(1));
        window.Record(1);
        Assert.True(window.IsReplay(1));

        // Jump forward, allowing out-of-order packets within [highest - 63 .. highest].
        window.Record(10);
        Assert.False(window.IsReplay(8));
        window.Record(8);
        Assert.True(window.IsReplay(8));

        // Jump far forward: counters older than 64 packets behind highest must be rejected.
        window.Record(100);
        Assert.True(window.IsReplay(36)); // 100 - 36 == 64 -> expired
        Assert.False(window.IsReplay(37)); // 100 - 37 == 63 -> still inside window
        window.Record(37);
        Assert.True(window.IsReplay(37));
        Assert.True(window.IsReplay(100));

        window.Reset();
        Assert.False(window.IsReplay(1));
    }

    [Fact]
    public void PskLayer_RoundTripsAndDropsTamperedReplayedReflectedAndTruncatedPackets()
    {
        byte[] key = NetworkCrypto.GenerateKey();
        var clientStats = new NetworkEncryptionStats();
        var serverStats = new NetworkEncryptionStats();

        using var clientLayer = new NetworkEncryptionLayer(key, isServer: false, stats: clientStats);
        using var serverLayer = new NetworkEncryptionLayer(key, isServer: true, stats: serverStats);

        var ep = new IPEndPoint(IPAddress.Loopback, 25555);
        byte[] plaintext = Encoding.UTF8.GetBytes("TopSecretPayload-12345");

        byte[] Seal(NetworkEncryptionLayer layer, byte[] input)
        {
            byte[] buf = new byte[256];
            Buffer.BlockCopy(input, 0, buf, 0, input.Length);
            int offset = 0;
            int len = input.Length;
            var target = ep;
            layer.ProcessOutBoundPacket(ref target, ref buf, ref offset, ref len);
            Assert.Equal(input.Length + NetworkEncryptionLayer.OverheadBytes, len);
            byte[] wire = new byte[len];
            Buffer.BlockCopy(buf, offset, wire, 0, len);
            return wire;
        }

        byte[] packet1 = Seal(clientLayer, plaintext);
        Assert.True(packet1.AsSpan().IndexOf(plaintext) < 0);

        // Reflection attack: client's own sealed packet fed back into client's inbound must fail auth (directional HKDF).
        {
            byte[] reflected = (byte[])packet1.Clone();
            int len = reflected.Length;
            var target = ep;
            clientLayer.ProcessInboundPacket(ref target, ref reflected, ref len);
            Assert.Equal(0, len);
            Assert.Equal(1L, clientStats.DroppedAuthFailures);
        }

        // Tamper ciphertext, header byte, tag byte, and truncate -> all must fail without poisoning replay window.
        {
            byte[] badCipher = (byte[])packet1.Clone();
            badCipher[0] ^= 0xFF;
            int len = badCipher.Length;
            var target = ep;
            serverLayer.ProcessInboundPacket(ref target, ref badCipher, ref len);
            Assert.Equal(0, len);

            byte[] badHeader = (byte[])packet1.Clone();
            badHeader[plaintext.Length + 1] ^= 0x01; // flip sender salt bit in AAD/nonce
            len = badHeader.Length;
            serverLayer.ProcessInboundPacket(ref target, ref badHeader, ref len);
            Assert.Equal(0, len);

            byte[] badTag = (byte[])packet1.Clone();
            badTag[^1] ^= 0x80;
            len = badTag.Length;
            serverLayer.ProcessInboundPacket(ref target, ref badTag, ref len);
            Assert.Equal(0, len);

            // Forged huge counter must fail auth and must NOT advance the replay window.
            byte[] hugeCounter = (byte[])packet1.Clone();
            int counterOffset = plaintext.Length + 7;
            hugeCounter[counterOffset + 0] = 0xFF;
            hugeCounter[counterOffset + 1] = 0xFF;
            len = hugeCounter.Length;
            serverLayer.ProcessInboundPacket(ref target, ref hugeCounter, ref len);
            Assert.Equal(0, len);

            byte[] truncated = packet1[..NetworkEncryptionLayer.OverheadBytes];
            len = truncated.Length;
            serverLayer.ProcessInboundPacket(ref target, ref truncated, ref len);
            Assert.Equal(0, len);

            Assert.Equal(4L, serverStats.DroppedAuthFailures);
            Assert.Equal(1L, serverStats.DroppedMalformed);
        }

        // Authentic packet1 must still decrypt cleanly (window wasn't poisoned by failed packets).
        {
            byte[] copy = (byte[])packet1.Clone();
            int len = copy.Length;
            var target = ep;
            serverLayer.ProcessInboundPacket(ref target, ref copy, ref len);
            Assert.Equal(plaintext.Length, len);
            Assert.True(copy.AsSpan(0, len).SequenceEqual(plaintext));
            Assert.Equal(1L, serverStats.PacketsDecrypted);
        }

        // Exact replay of packet1 must be dropped.
        {
            byte[] replay = (byte[])packet1.Clone();
            int len = replay.Length;
            var target = ep;
            serverLayer.ProcessInboundPacket(ref target, ref replay, ref len);
            Assert.Equal(0, len);
            Assert.Equal(1L, serverStats.DroppedReplays);
        }

        // Next legitimate sequential packet (counter 2) succeeds cleanly.
        byte[] packet2 = Seal(clientLayer, plaintext);
        {
            int len = packet2.Length;
            var target = ep;
            serverLayer.ProcessInboundPacket(ref target, ref packet2, ref len);
            Assert.Equal(plaintext.Length, len);
            Assert.Equal(2L, serverStats.PacketsDecrypted);
        }
    }

    [Fact]
    public void PskConfig_EncryptsHandshakeAndMessages_SupportsKeyRotation_AndComposesWithSimAndProbe()
    {
        EnsureRegistered();

        byte[] keyA = NetworkCrypto.GenerateKey();
        byte[] keyB = NetworkCrypto.GenerateKey();
        byte[] wrongKey = NetworkCrypto.GenerateKey();

        var tap = new WireTapLayer();
        var simLayer = new NetworkDatagramSimLayer();
        var probe = new NetDatagramProbe();

        var serverConfig = Config(0, maxClients: 4);
        serverConfig.Encryption = NetworkEncryptionConfig.CreatePreSharedKey(keyB, previousKey: keyA, keyId: 2);
        serverConfig.DatagramLayer = new NetworkDatagramChain(tap, simLayer, probe);

        using var server = new NetworkServer(serverConfig);
        var serverChats = new List<string>();
        server.MessageReceived += e =>
        {
            if (e.Message is ChatMessage chat)
                serverChats.Add(chat.Text);
        };
        Require(server.Start(), "Encrypted PSK server failed to start");
        Require(server.IsEncrypted, "Server should report IsEncrypted = true");
        Require(server.DatagramChain is { HasEncryptionLayer: true, HasSimulationLayer: true },
            "Server DatagramChain should contain both encryption and sim layers");

        // Key rotation Phase 2: server sends with keyB and still accepts keyA; rotating client sends with keyA and accepts keyB.
        var oldClientConfig = Config(server.Port);
        oldClientConfig.Encryption = NetworkEncryptionConfig.CreatePreSharedKey(keyA, previousKey: keyB, keyId: 1);
        var newClientConfig = Config(server.Port);
        newClientConfig.Encryption = NetworkEncryptionConfig.CreatePreSharedKey(keyB, keyId: 2);

        using var oldClient = new NetworkClient(oldClientConfig);
        using var newClient = new NetworkClient(newClientConfig);

        Require(oldClient.Connect("127.0.0.1", server.Port, "SecretPlayerAlice"), "Old-key client failed connect");
        Require(newClient.Connect("127.0.0.1", server.Port, "SecretPlayerBob"), "New-key client failed connect");
        Require(WaitUntilAllReady(5f, new[] { oldClient, newClient }, server, oldClient, newClient),
            "Clients failed PSK encrypted handshake");

        SendChat(oldClient, "SecretPassword123");
        SendChat(newClient, "RotatedKeyMessage456");
        Require(PumpUntil(4f, () => serverChats.Count == 2, server, oldClient, newClient),
            "Server did not receive both PSK-encrypted chats");

        Assert.False(tap.ContainsAscii("SecretPlayerAlice"), "Player name leaked in plaintext on wire");
        Assert.False(tap.ContainsAscii("SecretPassword123"), "Chat text leaked in plaintext on wire");
        Assert.False(tap.ContainsAscii("RotatedKeyMessage456"), "Rotated chat text leaked in plaintext on wire");
        Assert.True(probe.BytesOut > 0 && probe.BytesIn > 0);
        Assert.True(server.EncryptionStats.PacketsEncrypted > 0 && server.EncryptionStats.PacketsDecrypted > 0);

        // Wrong-key client cannot connect and triggers DroppedAuthFailures on server.
        var badConfig = Config(server.Port);
        badConfig.Encryption = NetworkEncryptionConfig.CreatePreSharedKey(wrongKey);
        badConfig.ConnectRequestAttempts = 2;
        badConfig.ConnectRequestIntervalMs = 50;
        using var badClient = new NetworkClient(badConfig);
        badClient.Connect("127.0.0.1", server.Port, "Attacker");
        Pump(0.4f, server, oldClient, newClient, badClient);

        Assert.False(badClient.IsSessionReady);
        Assert.True(server.EncryptionStats.DroppedAuthFailures > 0);
    }

    [Fact]
    public void EphemeralEcdh_EncryptsPerPeer_IsolatesClientKeys_AndHidesPayloads()
    {
        EnsureRegistered();

        var tap = new WireTapLayer();
        var serverConfig = Config(0, maxClients: 4);
        serverConfig.Encryption = NetworkEncryptionConfig.CreateEphemeral();
        serverConfig.DatagramLayer = new NetworkDatagramChain(tap);

        using var server = new NetworkServer(serverConfig);
        server.MessageReceived += e =>
        {
            if (e.Message is ChatMessage chat)
            {
                var fwd = NetworkSession.Rent<ChatMessage>();
                fwd.Text = chat.Text;
                server.Broadcast(fwd);
            }
        };
        Require(server.Start(), "ECDH server failed to start");

        var c1Config = Config(server.Port);
        c1Config.Encryption = NetworkEncryptionConfig.CreateEphemeral();
        var c2Config = Config(server.Port);
        c2Config.Encryption = NetworkEncryptionConfig.CreateEphemeral();

        using var c1 = new NetworkClient(c1Config);
        using var c2 = new NetworkClient(c2Config);
        var c1Chats = new List<string>();
        var c2Chats = new List<string>();
        c1.MessageReceived += e => { if (e.Message is ChatMessage c) c1Chats.Add(c.Text); };
        c2.MessageReceived += e => { if (e.Message is ChatMessage c) c2Chats.Add(c.Text); };

        Require(c1.Connect("127.0.0.1", server.Port, "Alice"), "c1 connect failed");
        Require(c2.Connect("127.0.0.1", server.Port, "Bob"), "c2 connect failed");
        Require(WaitUntilAllReady(5f, new[] { c1, c2 }, server, c1, c2), "ECDH clients never became ready");

        Assert.Equal(2L, server.EncryptionStats.KeyExchangesCompleted);
        foreach (var peer in server.Peers)
            Assert.True(peer.IsSessionEncrypted);

        SendChat(c1, "EcdhSecretChatPayload!");
        Require(PumpUntil(4f, () => c1Chats.Contains("EcdhSecretChatPayload!") && c2Chats.Contains("EcdhSecretChatPayload!"),
            server, c1, c2), "Broadcast encrypted messages did not arrive at both clients");

        Assert.False(tap.ContainsAscii("EcdhSecretChatPayload!"), "Chat payload appeared in plaintext on wire");

        // Verify per-client session key isolation: advance c1's counter past c2's replay window so TryDecryptEnvelope reaches AES-GCM verification and returns AuthFailed.
        var c1Crypto = c1.RemotePeer!.SessionCrypto!;
        var c2Crypto = c2.RemotePeer!.SessionCrypto!;
        var writer = new NetDataWriter(true, 64);
        for (int i = 0; i < 16; i++)
            Assert.True(c1Crypto.TryEncryptEnvelope(Encoding.UTF8.GetBytes("test"), (byte)NetworkDelivery.ReliableOrdered, writer));
        Span<byte> scratch = stackalloc byte[64];
        Assert.Equal(PeerSessionCrypto.DecryptResult.AuthFailed,
            c2Crypto.TryDecryptEnvelope(writer.Data.AsSpan(0, writer.Length), (byte)NetworkDelivery.ReliableOrdered, scratch, out _));
    }

    [Fact]
    public void EphemeralEcdh_PinnedServerIdentity_VerifiesValidAndRejectsMismatchOrUnsigned()
    {
        var (priv, pub) = NetworkCrypto.GenerateIdentityKeyPair();
        var (_, wrongPub) = NetworkCrypto.GenerateIdentityKeyPair();

        // 1. Signed server + matching pinned public key succeeds.
        var serverConfig = Config(0, maxClients: 2);
        serverConfig.Encryption = NetworkEncryptionConfig.CreateEphemeral(serverIdentityPrivateKey: priv);
        using var server = new NetworkServer(serverConfig);
        Require(server.Start(), "Signed ECDH server failed to start");

        var validConfig = Config(server.Port);
        validConfig.Encryption = NetworkEncryptionConfig.CreateEphemeral(serverIdentityPublicKey: pub);
        using (var validClient = new NetworkClient(validConfig))
        {
            Require(validClient.Connect("127.0.0.1", server.Port, "ValidPinned"), "Connect failed");
            Require(WaitUntilReady(5f, validClient, server, validClient), "Matching pinned client failed to connect");
            Assert.True(validClient.RemotePeer!.IsSessionEncrypted);
        }

        // 2. Wrong pinned public key rejects with SecurityError.
        var badPinConfig = Config(server.Port);
        badPinConfig.Encryption = NetworkEncryptionConfig.CreateEphemeral(serverIdentityPublicKey: wrongPub);
        using (var badPinClient = new NetworkClient(badPinConfig))
        {
            var errors = new List<NetworkError>();
            badPinClient.Error += e => errors.Add(e);
            Require(badPinClient.Connect("127.0.0.1", server.Port, "BadPin"), "Connect failed");
            Require(PumpUntil(4f, () => errors.Exists(e => e.Code == NetworkErrorCode.SecurityError), server, badPinClient),
                "Client with wrong pinned server key did not emit SecurityError");
            Assert.False(badPinClient.IsSessionReady);
            Assert.True(badPinClient.EncryptionStats.KeyExchangesFailed >= 1);
        }

        // 3. Unsigned server against pinned client rejects with SecurityError.
        var unsignedServerConfig = Config(0, maxClients: 2);
        unsignedServerConfig.Encryption = NetworkEncryptionConfig.CreateEphemeral();
        using var unsignedServer = new NetworkServer(unsignedServerConfig);
        Require(unsignedServer.Start(), "Unsigned ECDH server failed to start");

        var pinnedClientConfig = Config(unsignedServer.Port);
        pinnedClientConfig.Encryption = NetworkEncryptionConfig.CreateEphemeral(serverIdentityPublicKey: pub);
        using var pinnedClient = new NetworkClient(pinnedClientConfig);
        var pinnedErrors = new List<NetworkError>();
        pinnedClient.Error += e => pinnedErrors.Add(e);
        Require(pinnedClient.Connect("127.0.0.1", unsignedServer.Port, "StrictClient"), "Connect failed");
        Require(PumpUntil(4f, () => pinnedErrors.Exists(e => e.Code == NetworkErrorCode.SecurityError), unsignedServer, pinnedClient),
            "Pinned client did not reject unsigned server");
        Assert.False(pinnedClient.IsSessionReady);
    }

    [Fact]
    public void EphemeralEcdh_RejectsPlaintextDowngrade_AndTimesOutStalledHandshake()
    {
        // Plaintext client connecting to ECDH server: handshake times out and server rejects plaintext messages.
        var serverConfig = Config(0, maxClients: 2);
        serverConfig.Encryption = new NetworkEncryptionConfig
        {
            EnableSessionKeyExchange = true,
            HandshakeTimeoutMs = 500,
        };
        using var server = new NetworkServer(serverConfig);
        var serverErrors = new List<NetworkError>();
        int serverDelivered = 0;
        server.Error += e => serverErrors.Add(e);
        server.MessageReceived += _ => serverDelivered++;
        Require(server.Start(), "ECDH server failed to start");

        using var plainClient = new NetworkClient(Config(server.Port));
        var clientErrors = new List<NetworkError>();
        plainClient.Error += e => clientErrors.Add(e);
        Require(plainClient.Connect("127.0.0.1", server.Port, "PlaintextClient"), "Plaintext client failed connect");

        // Plaintext client receives KeyExchangeHello as UnknownMessageType and never receives Welcome;
        // sending plaintext ChatMessage from client must be rejected by server as SecurityError.
        Pump(0.15f, server, plainClient);
        if (plainClient.RemotePeer is not null)
            SendChat(plainClient, "DowngradeAttempt");

        Require(PumpUntil(3f, () => server.ClientCount == 0 && server.EncryptionStats.KeyExchangesFailed >= 1, server, plainClient),
            "ECDH server did not time out / disconnect unauthenticated plaintext peer");
        Assert.Equal(0, serverDelivered);
        Assert.Contains(serverErrors, e => e.Code == NetworkErrorCode.SecurityError);
    }

    [Fact]
    public void CombinedEncryption_SurvivesStopAndRestart_AndAllocatesNothingInSteadyState()
    {
        EnsureRegistered();

        var (priv, pub) = NetworkCrypto.GenerateIdentityKeyPair();
        byte[] psk = NetworkCrypto.GenerateKey();

        int port = FreeUdpPort();
        var serverConfig = Config(port, maxClients: 2);
        serverConfig.Encryption = NetworkEncryptionConfig.CreateCombined(psk, priv, pub);

        var clientConfig = Config(port);
        clientConfig.Encryption = NetworkEncryptionConfig.CreateCombined(psk, serverIdentityPublicKey: pub);

        using var server = new NetworkServer(serverConfig);
        int received = 0;
        server.MessageReceived += e =>
        {
            if (e.Message is EncryptedPingMessage)
                received++;
        };
        Require(server.Start(), "Combined server failed to start");

        using var client = new NetworkClient(clientConfig);
        Require(client.Connect("127.0.0.1", port, "CombinedClient"), "Combined client failed connect");
        Require(WaitUntilReady(5f, client, server, client), "Combined session did not become ready");

        var watch = System.Diagnostics.Stopwatch.StartNew();
        double lastRound = 0;
        void Round()
        {
            var msg = NetworkSession.Rent<EncryptedPingMessage>();
            msg.Seq = received;
            msg.Value = 99;
            client.Send(client.ServerPeerId, msg);
            client.Flush();
            double now = watch.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(now - lastRound, 0.1);
            lastRound = now;
            client.Update(dt);
            server.Update(dt);
            System.Threading.Thread.Sleep(0);
        }

        // Steady-state zero-allocation check with both PSK datagram AES-GCM and per-peer ECDH AES-GCM active.
        for (int i = 0; i < 2000; i++)
            Round();

        const int messages = 1000;
        long least = long.MaxValue;
        for (int w = 0; w < 3; w++)
        {
            int before = received;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < messages; i++)
                Round();
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            Require(received - before > messages / 2, "Encrypted loopback traffic dropped unexpectedly");
            least = Math.Min(least, allocated);
        }

        Require(least <= messages * 8L, "Encrypted steady traffic allocated per message on polling thread",
            $"{(double)least / messages:0.0} B per round");

        // Verify Stop() -> Start() / Connect() continues working with combined encryption.
        server.Stop();
        client.Stop();
        Pump(0.1f, server, client);
        Assert.False(server.IsRunning);
        Assert.False(client.IsRunning);

        Require(server.Start(), "Combined server could not restart after Stop()");
        Require(client.Connect("127.0.0.1", port, "CombinedClient"), "Combined client could not reconnect after Stop()");
        Require(WaitUntilReady(5f, client, server, client), "Restarted combined session did not become ready");
        Assert.True(client.RemotePeer!.IsSessionEncrypted);
    }
}
