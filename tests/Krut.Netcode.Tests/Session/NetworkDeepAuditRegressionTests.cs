using System;
using System.Collections.Generic;
using Krut.Netcode;
using LiteNetLib.Utils;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests;

[Collection(NetworkTestCollection.Name)]
public class NetworkDeepAuditRegressionTests
{
    private const byte AuditPingWire = 216;
    private const byte AuditOversizedWire = 217;

    private sealed class AuditPingMessage : INetworkMessage
    {
        public NetworkMessageType Type => NetworkMessageType.None;
        public byte WireType => AuditPingWire;
        public NetworkDelivery Delivery => NetworkDelivery.ReliableOrdered;
        public int Seq;
        public void Reset() => Seq = 0;
        public void Write(NetDataWriter writer) => writer.Put(Seq);
        public void Read(NetDataReader reader) => Seq = reader.GetInt();
    }

    private sealed class AuditOversizedUnreliableMessage : INetworkMessage
    {
        public NetworkMessageType Type => NetworkMessageType.None;
        public byte WireType => AuditOversizedWire;
        public NetworkDelivery Delivery => NetworkDelivery.Unreliable;
        public int ByteCount;
        public void Reset() => ByteCount = 0;
        public void Write(NetDataWriter writer)
        {
            for (int i = 0; i < ByteCount; i++)
                writer.Put((byte)(i & 0xFF));
        }
        public void Read(NetDataReader reader)
        {
            while (reader.AvailableBytes > 0)
                _ = reader.GetByte();
        }
    }

    private static void EnsureRegistered()
    {
        if (!NetworkMessages.IsRegistered(AuditPingWire))
            NetworkMessages.RegisterGameMessage<AuditPingMessage>(AuditPingWire, NetworkDelivery.ReliableOrdered);
        if (!NetworkMessages.IsRegistered(AuditOversizedWire))
            NetworkMessages.RegisterGameMessage<AuditOversizedUnreliableMessage>(AuditOversizedWire, NetworkDelivery.Unreliable);
    }

    private struct IntGapState
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    [Fact]
    public void DelayQueue_OversizedPacketUnderSim_DoesNotThrow_AndRemovePeerPurgesDelayedPackets()
    {
        EnsureRegistered();

        int port = FreeUdpPort();
        var serverConfig = Config(port, maxClients: 2);
        using var server = new NetworkServer(serverConfig);
        int delivered = 0;
        server.MessageReceived += e =>
        {
            if (e.Message is AuditPingMessage)
                delivered++;
        };
        Require(server.Start(), "Server failed to start");

        using var client = new NetworkClient(Config(port));
        var clientErrors = new List<NetworkError>();
        client.Error += e => clientErrors.Add(e);
        Require(client.Connect("127.0.0.1", port, "DelayTester"), "Client failed connect");
        Require(WaitUntilReady(5f, client, server, client), "Client did not become ready");

        client.ApplySimulation(NetworkSimulation.Create(latencyMs: 20, jitterMs: 0, lossChance: 0f));

        // Enqueue an oversized unreliable packet (above MTU) AND a normal reliable packet behind it.
        var tooBig = NetworkSession.Rent<AuditOversizedUnreliableMessage>();
        tooBig.ByteCount = 4096;
        client.Send(client.ServerPeerId, tooBig);

        var okMsg = NetworkSession.Rent<AuditPingMessage>();
        okMsg.Seq = 1;
        client.Send(client.ServerPeerId, okMsg);

        // Flushing due simulation packets must report PacketTooLarge instead of throwing and leaking buffers.
        Require(PumpUntil(3f, () => delivered == 1 && clientErrors.Exists(e => e.Code == NetworkErrorCode.PacketTooLarge),
            server, client), "Normal message or PacketTooLarge error did not survive delayed flush");

        // Enqueue another delayed packet and disconnect peer -> RemovePeer must purge queued delayed items for that peer.
        var delayedPing = NetworkSession.Rent<AuditPingMessage>();
        delayedPing.Seq = 2;
        client.Send(client.ServerPeerId, delayedPing);
        Assert.NotNull(client.RemovePeer(client.ServerPeerId));
        Pump(0.1f, client);
        Assert.Equal(1, delivered);
    }

    [Fact]
    public void Client_InvalidAddressAndDisconnectWithoutStop_CleanUpSocketAndAllowReconnect()
    {
        int port = FreeUdpPort();
        using var server = new NetworkServer(Config(port, maxClients: 1));
        Require(server.Start(), "Server failed to start");

        int localPort = FreeUdpPort();
        var clientConfig = Config(port);
        clientConfig.AutoReconnect = false;
        using var client = new NetworkClient(clientConfig);

        // Failed Connect with unresolvable host must clean up socket so same localPort can be reused immediately.
        Assert.False(client.Connect("not a valid host !!!", port, "BadHost", localPort: localPort));
        Assert.False(client.IsRunning);
        Assert.Equal(NetworkClientState.Offline, client.State);

        Require(client.Connect("127.0.0.1", port, "FirstClient", localPort: localPort),
            "Could not reuse localPort after failed Connect");
        Require(WaitUntilReady(5f, client, server, client), "Client did not become ready");

        // Fill server, reject a second client, and verify second client becomes !IsRunning and can reconnect immediately without Stop().
        var secondConfig = Config(port);
        secondConfig.AutoReconnect = false;
        using var second = new NetworkClient(secondConfig);
        bool closedFired = false;
        second.Closed += (_, _) => closedFired = true;
        Require(second.Connect("127.0.0.1", port, "SecondClient"), "Second client failed connect");
        Require(PumpUntil(4f, () => closedFired, server, client, second), "Rejection Closed event did not fire");

        Assert.Equal(NetworkClientState.Offline, second.State);
        Assert.False(second.IsRunning);

        // Kick first client -> second client can directly call Connect() without calling Stop() first.
        Assert.True(server.DisconnectPeer(client.LocalPeerId, "Making room"));
        Require(PumpUntil(4f, () => server.ClientCount == 0 && !client.IsRunning, server, client, second), "First client did not disconnect");
        Assert.False(client.IsRunning);

        Require(second.Connect("127.0.0.1", port, "SecondClient"),
            "Client could not call Connect() directly after rejection without manual Stop()");
        Require(WaitUntilReady(5f, second, server, second), "Second client did not become ready on direct reconnect");
    }

    [Fact]
    public void Session_NullArgsReentrancyAndWriterSafety_HoldInvariants()
    {
        EnsureRegistered();

        int port = FreeUdpPort();
        var serverConfig = Config(port, maxClients: 4);
        serverConfig.MaxPendingEvents = 10;
        serverConfig.MaxPendingEventBytes = 100;
        serverConfig.MaxReceivedPacketBytes = 256;

        using var server = new NetworkServer(serverConfig);
        Assert.Throws<ArgumentNullException>(() => server.Send(1, null!));
        Assert.Throws<ArgumentNullException>(() => server.Broadcast(null!));

        int reentrantErrors = 0;
        server.Error += e =>
        {
            if (e.Code == NetworkErrorCode.CallbackException)
                reentrantErrors++;
        };
        server.MessageReceived += e =>
        {
            if (e.Message is AuditPingMessage)
            {
                // Re-entrant Update from inside callback must be blocked without corrupting queues.
                server.Update(1f / 60f);
            }
        };
        Require(server.Start(), "Server failed to start");

        using var c1 = new NetworkClient(Config(port));
        using var c2 = new NetworkClient(Config(port));
        Require(c1.Connect("127.0.0.1", port, "C1") && c2.Connect("127.0.0.1", port, "C2"), "Clients failed connect");
        Require(WaitUntilAllReady(5f, new[] { c1, c2 }, server, c1, c2), "Clients did not become ready");

        var msg = NetworkSession.Rent<AuditPingMessage>();
        msg.Seq = 123;
        c1.Send(c1.ServerPeerId, msg);
        Require(PumpUntil(3f, () => reentrantErrors == 1, server, c1, c2), "Re-entrant Update was not reported");
        Assert.Equal(1L, server.CallbackExceptionCount);

        // Enqueuing a packet larger than MaxPendingEventBytes (100) must drop it before evicting valid queued packets.
        NetworkPeerState? peer = null;
        foreach (var p in server.Peers) { peer = p; break; }
        var smallWriter = new NetDataWriter(true, 16);
        smallWriter.Put(AuditPingWire);
        smallWriter.Put(7);
        byte[] smallBytes = smallWriter.CopyData();
        server.EnqueueReceived(peer!.Peer, new NetDataReader(smallBytes, 0, smallBytes.Length), NetworkDelivery.Unreliable);
        Assert.Equal(1, server.PendingEventCount);

        byte[] tooManyBytes = new byte[150]; // <= MaxReceivedPacketBytes (256), > MaxPendingEventBytes (100)
        server.EnqueueReceived(peer.Peer, new NetDataReader(tooManyBytes, 0, tooManyBytes.Length), NetworkDelivery.ReliableOrdered);
        Assert.Equal(1, server.PendingEventCount); // existing valid packet was NOT evicted!
        Assert.Equal(1L, server.DroppedInboundEventCount);
    }

    [Fact]
    public void Session_IPv4OnlyConfig_ConnectsAndDeliversMessages()
    {
        EnsureRegistered();

        int port = FreeUdpPort();
        var serverConfig = Config(port, maxClients: 2);
        serverConfig.IPv6Support = false;
        var clientConfig = Config(port);
        clientConfig.IPv6Support = false;

        using var server = new NetworkServer(serverConfig);
        int arrived = 0;
        server.MessageReceived += e => { if (e.Message is AuditPingMessage) arrived++; };
        Require(server.Start(), "IPv4-only server failed to start");

        using var client = new NetworkClient(clientConfig);
        Require(client.Connect("127.0.0.1", port, "IPv4Only"), "IPv4-only client failed to connect");
        Require(WaitUntilReady(5f, client, server, client), "IPv4-only session never became ready");

        var ping = NetworkSession.Rent<AuditPingMessage>();
        ping.Seq = 42;
        client.Send(client.ServerPeerId, ping);
        Require(PumpUntil(3f, () => arrived == 1, server, client), "IPv4-only message did not arrive");
    }

    [Fact]
    public void TimeSync_Ring_Interpolator_Sim_AndEnv_HandleEdgeCasesCleanly()
    {
        // NetworkTimeSync: negative ExtraLeadTicks setter clamped, garbage RTT doesn't retire pending or overflow, zero-RTT preserves sub-ms samples, negative tick in ShouldProbe returns false.
        var sync = new NetworkTimeSync(60);
        sync.ExtraLeadTicks = -10;
        Assert.Equal(0, sync.ExtraLeadTicks);
        Assert.False(sync.ShouldProbe(-1));

        sync.RecordRequest(100);
        sync.RecordResponse(100, 200, double.MaxValue);
        Assert.Equal(1, sync.PendingRequestCount);
        Assert.False(sync.IsSynchronized);

        sync.RecordResponse(100, 150, 0.0);
        sync.RecordRequest(108);
        sync.RecordResponse(108, 158, 0.0005); // 0.5 ms sample should survive when minRtt == 0
        Assert.Equal(2, sync.SampleCount);
        Assert.Equal(50, sync.TickOffset);

        // NetworkDatagramChain: null element throws ArgumentException.
        Assert.Throws<ArgumentException>(() => new NetworkDatagramChain(new NetDatagramProbe(), null!));

        // NetworkStateRing.Set: strictly ascending tick invariant enforced.
        var ring = new NetworkStateRing<NetworkStateEntry<int>>(4, static e => e.Tick);
        ring.Push(10, new NetworkStateEntry<int>(10, 1));
        ring.Push(20, new NetworkStateEntry<int>(20, 2));
        Assert.Throws<ArgumentException>(() => ring.Set(0, new NetworkStateEntry<int>(20, 99)));
        Assert.Throws<ArgumentException>(() => ring.Set(1, new NetworkStateEntry<int>(10, 99)));

        // NetworkSnapshotInterpolator & NetworkReconciler: null delegates throw & huge tick spans avoid int overflow.
        var interp = new NetworkSnapshotInterpolator<IntGapState>(0);
        Assert.Throws<ArgumentNullException>(() => interp.Lerp = null!);
        Assert.Throws<ArgumentNullException>(() => interp.Extrapolate = null!);
        interp.Push(-2_000_000_000, new IntGapState { X = 0f });
        interp.Push(2_000_000_000, new IntGapState { X = 100f });
        Assert.True(interp.TrySampleTime(0.0, out var mid, out var mode));
        Assert.Equal(NetworkSampleMode.Interpolated, mode);
        Assert.Equal(50f, mid.X, 1);

        var reconciler = new NetworkReconciler<IntGapState, float>(VisualSmoothingSettings.Default);
        Assert.Throws<ArgumentNullException>(() => reconciler.DistanceMetric = null!);
        Assert.Throws<ArgumentNullException>(() => reconciler.RenderOffset = null!);

        // KrutEnv, ParseOutages, NetworkLinkModel negative wireBytes.
        string? prevVal = Environment.GetEnvironmentVariable("KRUT_TEST_NAN");
        try
        {
            Environment.SetEnvironmentVariable("KRUT_TEST_NAN", "NaN");
            Assert.Equal(0.5f, KrutEnv.Clamped("KRUT_TEST_NAN", 0.5f, 0f, 1f));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KRUT_TEST_NAN", prevVal);
        }

        Assert.Empty(NetworkSimulation.ParseOutages("Infinity:Infinity,0:NaN,-1:5"));
        var bwSim = NetworkSimulation.Create(new NetworkSimulationOptions { BandwidthBytesPerSecond = 1000 });
        var link = new NetworkLinkModel(bwSim, 0);
        var verdict = link.Submit(0, -500, reliable: false);
        Assert.True(verdict.Deliver);
        Assert.Equal(0L, link.Stats.BytesSubmitted);
    }
}
