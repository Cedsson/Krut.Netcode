using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Krut.Netcode;
using LiteNetLib.Utils;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests;

[Collection(NetworkTestCollection.Name)]
public class NetworkOverloadAndSafetyTests
{
    private const byte UnreliableLoadWire = 212;
    private const byte ReliableLoadWire = 213;

    private sealed class UnreliableLoadMessage : INetworkMessage
    {
        public NetworkMessageType Type => NetworkMessageType.None;
        public byte WireType => UnreliableLoadWire;
        public NetworkDelivery Delivery => NetworkDelivery.Unreliable;
        public int Sequence;
        public void Reset() => Sequence = 0;
        public void Write(NetDataWriter writer) => writer.Put(Sequence);
        public void Read(NetDataReader reader) => Sequence = reader.GetInt();
    }

    private sealed class ReliableLoadMessage : INetworkMessage
    {
        public NetworkMessageType Type => NetworkMessageType.None;
        public byte WireType => ReliableLoadWire;
        public NetworkDelivery Delivery => NetworkDelivery.ReliableOrdered;
        public int Sequence;
        public void Reset() => Sequence = 0;
        public void Write(NetDataWriter writer) => writer.Put(Sequence);
        public void Read(NetDataReader reader) => Sequence = reader.GetInt();
    }

    private static void EnsureRegistered()
    {
        if (!NetworkMessages.IsRegistered(UnreliableLoadWire))
            NetworkMessages.RegisterGameMessage<UnreliableLoadMessage>(UnreliableLoadWire, NetworkDelivery.Unreliable);
        if (!NetworkMessages.IsRegistered(ReliableLoadWire))
            NetworkMessages.RegisterGameMessage<ReliableLoadMessage>(ReliableLoadWire, NetworkDelivery.ReliableOrdered);
    }

    private static byte[] EncodeIntPayload(byte wireType, int sequence)
    {
        var writer = new NetDataWriter(true, 16);
        writer.Put(wireType);
        writer.Put(sequence);
        return writer.CopyData();
    }

    private static NetworkPeerState FirstPeer(NetworkServer server)
    {
        foreach (var peer in server.Peers)
            return peer;
        throw new InvalidOperationException("Expected at least one connected peer.");
    }

    [Fact]
    public void InboundFlood_100kMessages_BoundsQueueAndBytes_AndRateLimitsOverloadErrors()
    {
        EnsureRegistered();

        var serverConfig = Config(0, maxClients: 2);
        serverConfig.MaxPendingEvents = 32;
        serverConfig.MaxPendingEventBytes = 4096;
        serverConfig.MaxEventsPerPeerPerPoll = 2000;

        using var server = new NetworkServer(serverConfig);
        int overloadErrors = 0;
        int delivered = 0;
        server.Error += e =>
        {
            if (e.Code == NetworkErrorCode.InboundOverload)
                overloadErrors++;
        };
        server.MessageReceived += e =>
        {
            if (e.Message is UnreliableLoadMessage)
                delivered++;
        };
        Require(server.Start(), "Server failed to start");

        using var client = new NetworkClient(Config(server.Port));
        Require(client.Connect("127.0.0.1", server.Port, "Flooder"), "Client failed to connect");
        Require(WaitUntilReady(5f, client, server, client), "Client did not become ready");

        var peer = FirstPeer(server);
        byte[] payload = EncodeIntPayload(UnreliableLoadWire, 1);
        var reader = new NetDataReader(payload, 0, payload.Length);

        // Simulate 100,000 inbound messages in bursts (1,000 per poll x 100 frames).
        for (int frame = 0; frame < 100; frame++)
        {
            for (int i = 0; i < 1000; i++)
            {
                reader.SetSource(payload, 0, payload.Length);
                server.EnqueueReceived(peer.Peer, reader, NetworkDelivery.Unreliable);
            }

            Assert.InRange(server.PendingEventCount, 1, 32);
            Assert.InRange(server.PendingEventBytes, 1L, 4096L);
            server.Update(1f / 60f);
            Assert.Equal(0, server.PendingEventCount);
            Assert.Equal(0L, server.PendingEventBytes);
        }

        Assert.InRange(server.PeakPendingEventCount, 1, 32);
        Assert.InRange(server.PeakPendingEventBytes, 1L, 4096L);
        Assert.Equal(3200, delivered);
        Assert.Equal(100_000L - 3200L, server.DroppedInboundEventCount);
        Assert.Equal((100_000L - 3200L) * payload.Length, server.DroppedInboundBytes);
        Assert.Equal(100_000L - 3200L, peer.DroppedInboundEvents);
        // Reported at most once per poll epoch, never once per dropped packet.
        Assert.InRange(overloadErrors, 1, 100);
    }

    [Fact]
    public void Overload_EvictsUnreliableBeforeReliable_AndNeverDropsLifecycleEvents()
    {
        EnsureRegistered();

        var serverConfig = Config(0, maxClients: 2);
        serverConfig.MaxPendingEvents = 5;
        serverConfig.MaxEventsPerPeerPerPoll = 20;

        using var server = new NetworkServer(serverConfig);
        int unreliableCount = 0;
        var reliableSeqs = new List<int>();
        bool peerDisconnectedFired = false;

        server.MessageReceived += e =>
        {
            if (e.Message is UnreliableLoadMessage)
                unreliableCount++;
            else if (e.Message is ReliableLoadMessage r)
                reliableSeqs.Add(r.Sequence);
        };
        server.PeerDisconnected += (_, _) => peerDisconnectedFired = true;
        Require(server.Start(), "Server failed to start");

        using var client = new NetworkClient(Config(server.Port));
        Require(client.Connect("127.0.0.1", server.Port, "Tester"), "Client failed to connect");
        Require(WaitUntilReady(5f, client, server, client), "Client did not become ready");

        var peer = FirstPeer(server);
        byte[] unreliableBytes = EncodeIntPayload(UnreliableLoadWire, 1);

        for (int i = 0; i < 5; i++)
            server.EnqueueReceived(peer.Peer, new NetDataReader(unreliableBytes, 0, unreliableBytes.Length), NetworkDelivery.Unreliable);

        // Reliable packets arrive when queue is full -> older unreliable events must be evicted first.
        for (int seq = 10; seq < 13; seq++)
        {
            byte[] reliableBytes = EncodeIntPayload(ReliableLoadWire, seq);
            server.EnqueueReceived(peer.Peer, new NetDataReader(reliableBytes, 0, reliableBytes.Length), NetworkDelivery.ReliableOrdered);
        }

        Assert.Equal(5, server.PendingEventCount);
        Assert.Equal(3L, server.DroppedInboundEventCount);

        server.Update(1f / 60f);
        Assert.Equal(2, unreliableCount);
        Assert.Equal(new[] { 10, 11, 12 }, reliableSeqs);

        // Fill queue with reliable packets and verify PeerDisconnected is never dropped.
        for (int seq = 0; seq < 5; seq++)
        {
            byte[] reliableBytes = EncodeIntPayload(ReliableLoadWire, seq);
            server.EnqueueReceived(peer.Peer, new NetDataReader(reliableBytes, 0, reliableBytes.Length), NetworkDelivery.ReliableOrdered);
        }

        var listener = new NetworkListener(server);
        listener.OnPeerDisconnected(peer.Peer, default);
        server.Update(1f / 60f);

        Assert.True(peerDisconnectedFired);
        Assert.Equal(0, server.ClientCount);
    }

    [Fact]
    public void PerPeerBurstLimit_IsolatesFloodingPeerFromHealthyPeer()
    {
        EnsureRegistered();

        var serverConfig = Config(0, maxClients: 2);
        serverConfig.MaxEventsPerPeerPerPoll = 8;
        serverConfig.MaxPendingEvents = 256;

        using var server = new NetworkServer(serverConfig);
        var perPeerDelivered = new Dictionary<int, int>();
        server.MessageReceived += e =>
        {
            if (e.Message is UnreliableLoadMessage or ReliableLoadMessage)
                perPeerDelivered[e.PeerId] = perPeerDelivered.GetValueOrDefault(e.PeerId) + 1;
        };
        Require(server.Start(), "Server failed to start");

        using var c1 = new NetworkClient(Config(server.Port));
        using var c2 = new NetworkClient(Config(server.Port));
        Require(c1.Connect("127.0.0.1", server.Port, "Flooder") && c2.Connect("127.0.0.1", server.Port, "Healthy"), "Clients failed to connect");
        Require(WaitUntilAllReady(5f, new[] { c1, c2 }, server, c1, c2), "Clients did not become ready");

        NetworkPeerState? p1 = null;
        NetworkPeerState? p2 = null;
        foreach (var peer in server.Peers)
        {
            if (peer.Name == "Flooder") p1 = peer;
            else if (peer.Name == "Healthy") p2 = peer;
        }
        Require(p1 is not null && p2 is not null, "Could not locate connected peers");

        byte[] floodPayload = EncodeIntPayload(UnreliableLoadWire, 1);
        byte[] healthyPayload = EncodeIntPayload(ReliableLoadWire, 77);

        for (int i = 0; i < 50; i++)
            server.EnqueueReceived(p1!.Peer, new NetDataReader(floodPayload, 0, floodPayload.Length), NetworkDelivery.Unreliable);
        for (int i = 0; i < 5; i++)
            server.EnqueueReceived(p2!.Peer, new NetDataReader(healthyPayload, 0, healthyPayload.Length), NetworkDelivery.ReliableOrdered);

        server.Update(1f / 60f);

        Assert.Equal(42L, p1!.DroppedInboundEvents);
        Assert.Equal(0L, p2!.DroppedInboundEvents);
        Assert.Equal(8, perPeerDelivered[p1.Id]);
        Assert.Equal(5, perPeerDelivered[p2.Id]);
    }

    [Fact]
    public void OversizedConnectedPacket_AndUnconnectedFlood_AreDroppedAndCounted()
    {
        EnsureRegistered();

        var serverConfig = Config(0, maxClients: 2);
        serverConfig.MaxReceivedPacketBytes = 128;
        serverConfig.MaxUnconnectedEventsPerPoll = 10;
        serverConfig.UnconnectedMessages = true;

        using var server = new NetworkServer(serverConfig);
        int unconnectedDelivered = 0;
        int overloadCount = 0;
        server.UnconnectedReceived += (_, _) => unconnectedDelivered++;
        server.Error += e =>
        {
            if (e.Code == NetworkErrorCode.InboundOverload)
                overloadCount++;
        };
        Require(server.Start(), "Server failed to start");

        using var client = new NetworkClient(Config(server.Port));
        Require(client.Connect("127.0.0.1", server.Port, "BigPacketPeer"), "Client failed to connect");
        Require(WaitUntilReady(5f, client, server, client), "Client did not become ready");

        var peer = FirstPeer(server);
        byte[] oversized = new byte[256];
        server.EnqueueReceived(peer.Peer, new NetDataReader(oversized, 0, oversized.Length), NetworkDelivery.ReliableOrdered);

        Assert.Equal(0, server.PendingEventCount);
        Assert.Equal(1L, server.DroppedInboundEventCount);
        Assert.Equal(256L, server.DroppedInboundBytes);
        Assert.Equal(1L, peer.DroppedInboundEvents);

        byte[] smallUnconnected = new byte[64];
        var ep = new IPEndPoint(IPAddress.Loopback, 40000);
        for (int i = 0; i < 50; i++)
            server.EnqueueUnconnected(ep, new NetDataReader(smallUnconnected, 0, smallUnconnected.Length));

        Assert.Equal(10, server.PendingEventCount);
        Assert.Equal(41L, server.DroppedInboundEventCount);

        server.Update(1f / 60f);
        Assert.Equal(10, unconnectedDelivered);
        Assert.Equal(1, overloadCount);
    }

    [Fact]
    public void MaxEventsPerDrain_PreservesFifoOrderAcrossFrames()
    {
        EnsureRegistered();

        var serverConfig = Config(0, maxClients: 2);
        serverConfig.MaxEventsPerDrain = 10;
        serverConfig.MaxPendingEvents = 100;

        using var server = new NetworkServer(serverConfig);
        var received = new List<int>();
        server.MessageReceived += e =>
        {
            if (e.Message is ReliableLoadMessage msg)
                received.Add(msg.Sequence);
        };
        Require(server.Start(), "Server failed to start");

        using var client = new NetworkClient(Config(server.Port));
        Require(client.Connect("127.0.0.1", server.Port, "BudgetTester"), "Client failed to connect");
        Require(WaitUntilReady(5f, client, server, client), "Client did not become ready");

        var peer = FirstPeer(server);
        for (int seq = 0; seq < 25; seq++)
        {
            byte[] bytes = EncodeIntPayload(ReliableLoadWire, seq);
            server.EnqueueReceived(peer.Peer, new NetDataReader(bytes, 0, bytes.Length), NetworkDelivery.ReliableOrdered);
        }

        Assert.Equal(25, server.PendingEventCount);

        server.Update(1f / 60f);
        Assert.Equal(10, server.LastDrainedEventCount);
        Assert.Equal(15, server.PendingEventCount);
        Assert.Equal(10, received.Count);

        server.Update(1f / 60f);
        Assert.Equal(10, server.LastDrainedEventCount);
        Assert.Equal(5, server.PendingEventCount);
        Assert.Equal(20, received.Count);

        server.Update(1f / 60f);
        Assert.Equal(5, server.LastDrainedEventCount);
        Assert.Equal(0, server.PendingEventCount);
        Assert.Equal(25, received.Count);

        for (int i = 0; i < 25; i++)
            Assert.Equal(i, received[i]);
    }

    [Fact]
    public void CallbackExceptions_AreIsolated_PreventReplayAndBufferLeaks()
    {
        EnsureRegistered();

        var serverConfig = Config(0, maxClients: 2);
        serverConfig.UnconnectedMessages = true;
        using var server = new NetworkServer(serverConfig);

        var delivered = new List<int>();
        int callbackErrors = 0;
        bool throwInError = false;

        server.Error += e =>
        {
            if (e.Code == NetworkErrorCode.CallbackException)
            {
                callbackErrors++;
                if (throwInError)
                    throw new InvalidOperationException("Error handler boom");
            }
        };
        server.PeerConnected += _ => throw new InvalidOperationException("PeerConnected boom");
        server.PeerDisconnected += (_, _) => throw new InvalidOperationException("PeerDisconnected boom");
        server.UnconnectedReceived += (_, _) => throw new InvalidOperationException("Unconnected boom");
        server.MessageReceived += e =>
        {
            if (e.Message is ReliableLoadMessage msg)
            {
                delivered.Add(msg.Sequence);
                if (msg.Sequence == 0)
                    throw new InvalidOperationException("Message #0 boom");
            }
        };
        Require(server.Start(), "Server failed to start");

        using var client = new NetworkClient(Config(server.Port));
        Require(client.Connect("127.0.0.1", server.Port, "ThrowTester"), "Client failed to connect");
        Require(WaitUntilReady(5f, client, server, client), "Client did not become ready");

        // PeerConnected threw once without crashing or blocking readiness.
        Assert.Equal(1L, server.CallbackExceptionCount);
        Assert.Equal(1, server.ClientCount);

        var peer = FirstPeer(server);
        for (int seq = 0; seq < 5; seq++)
        {
            byte[] bytes = EncodeIntPayload(ReliableLoadWire, seq);
            server.EnqueueReceived(peer.Peer, new NetDataReader(bytes, 0, bytes.Length), NetworkDelivery.ReliableOrdered);
        }

        // Throwing in both MessageReceived and Error must still deliver messages 1..4 without replaying on the next frame.
        throwInError = true;
        server.Update(1f / 60f);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, delivered);
        Assert.Equal(2L, server.CallbackExceptionCount);
        Assert.Equal(0, server.PendingEventCount);

        delivered.Clear();
        server.Update(1f / 60f);
        Assert.Empty(delivered);

        throwInError = false;
        byte[] punch = { 1, 2, 3, 4 };
        server.EnqueueUnconnected(new IPEndPoint(IPAddress.Loopback, 41234), new NetDataReader(punch, 0, punch.Length));
        var listener = new NetworkListener(server);
        listener.OnPeerDisconnected(peer.Peer, default);
        server.Update(1f / 60f);

        Assert.Equal(4L, server.CallbackExceptionCount);
        Assert.Equal(4, callbackErrors);
        Assert.Equal(0, server.ClientCount);
    }

    [Fact]
    public void CallbackCallingStopMidDrain_HaltsRemainingEventsCleanlyAndAllowsRestart()
    {
        EnsureRegistered();

        int port = FreeUdpPort();
        using var server = new NetworkServer(Config(port, maxClients: 2));
        var delivered = new List<int>();
        server.MessageReceived += e =>
        {
            if (e.Message is ReliableLoadMessage msg)
            {
                delivered.Add(msg.Sequence);
                if (msg.Sequence == 1)
                    server.Stop();
            }
        };
        Require(server.Start(), "Server failed to start");

        using var client = new NetworkClient(Config(port));
        Require(client.Connect("127.0.0.1", port, "Stopper"), "Client failed to connect");
        Require(WaitUntilReady(5f, client, server, client), "Client did not become ready");

        var peer = FirstPeer(server);
        for (int seq = 0; seq < 5; seq++)
        {
            byte[] bytes = EncodeIntPayload(ReliableLoadWire, seq);
            server.EnqueueReceived(peer.Peer, new NetDataReader(bytes, 0, bytes.Length), NetworkDelivery.ReliableOrdered);
        }

        server.Update(1f / 60f);

        Assert.False(server.IsRunning);
        Assert.Equal(new[] { 0, 1 }, delivered);
        Assert.Equal(2, server.LastDrainedEventCount);
        Assert.Equal(0, server.PendingEventCount);
        Assert.Equal(0L, server.PendingEventBytes);
        Assert.Equal(0L, server.CallbackExceptionCount);

        client.Stop();
        Require(server.Start(), "Server could not restart after mid-drain Stop()");
        Require(client.Connect("127.0.0.1", port, "Stopper"), "Client could not reconnect after mid-drain Stop()");
        Require(WaitUntilReady(5f, client, server, client), "Session did not become ready after mid-drain Stop()");
    }

    [Fact]
    public void Scaling_24ClientsOverLoopback_RemainBoundedAndStable()
    {
        EnsureRegistered();

        const int clientCount = 24;
        int port = FreeUdpPort();
        var serverConfig = Config(port, maxClients: clientCount);
        serverConfig.ConnectRequestIntervalMs = 200;

        using var server = new NetworkServer(serverConfig);
        int serverReceived = 0;
        server.MessageReceived += e =>
        {
            if (e.Message is ReliableLoadMessage)
                serverReceived++;
        };
        Require(server.Start(), "Server failed to start");

        var clients = new NetworkClient[clientCount];
        var sessions = new NetworkSession[clientCount + 1];
        sessions[0] = server;

        try
        {
            for (int i = 0; i < clientCount; i++)
            {
                var clientConfig = Config(port);
                clientConfig.ConnectRequestIntervalMs = 200;
                clients[i] = new NetworkClient(clientConfig);
                sessions[i + 1] = clients[i];
                Require(clients[i].Connect("127.0.0.1", port, $"Bot{i}"), $"Client {i} failed to initiate connect");
            }

            Require(WaitUntilAllReady(10f, clients, sessions), "Not all 24 clients became ready");
            Assert.Equal(clientCount, server.ClientCount);

            for (int i = 0; i < clientCount; i++)
            {
                var msg = NetworkSession.Rent<ReliableLoadMessage>();
                msg.Sequence = i;
                clients[i].Send(clients[i].ServerPeerId, msg);
            }

            Require(PumpUntil(5f, () => serverReceived == clientCount, sessions),
                "Not all 24 client messages arrived", $"{serverReceived} of {clientCount}");

            Assert.InRange(server.PeakPendingEventCount, 1, server.Config.MaxPendingEvents);
            Assert.Equal(0L, server.DroppedInboundEventCount);
            Assert.Equal(0L, server.CallbackExceptionCount);
            Assert.True(server.LastUpdateDurationMs >= 0d);
            Assert.True(server.LastTickDurationMs >= 0d);
        }
        finally
        {
            for (int i = 0; i < clientCount; i++)
                clients[i]?.Dispose();
        }
    }
}
