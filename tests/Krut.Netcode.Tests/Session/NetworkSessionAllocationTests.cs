using System;
using Krut.Netcode;
using LiteNetLib.Utils;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests
{
    /// <summary>G8 on the engine side: a steady stream of game messages must not allocate on the polling thread.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkSessionAllocationTests
    {
        private readonly Xunit.Abstractions.ITestOutputHelper _output;

        public NetworkSessionAllocationTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

        internal const byte PingByte = 230;

        /// <summary>A game message of two ints, the shape of the racing hot-path messages.</summary>
        internal sealed class PingMessage : INetworkMessage
        {
            public NetworkMessageType Type => NetworkMessageType.None;

            public byte WireType => PingByte;

            public NetworkDelivery Delivery => NetworkDelivery.Unreliable;

            public int A;
            public int B;

            public void Write(NetDataWriter writer)
            {
                writer.Put(A);
                writer.Put(B);
            }

            public void Read(NetDataReader reader)
            {
                A = reader.GetInt();
                B = reader.GetInt();
            }
        }

        internal static void EnsureRegistered()
        {
            if (!NetworkMessages.IsRegistered(PingByte))
                NetworkMessages.RegisterGameMessage<PingMessage>(PingByte, NetworkDelivery.Unreliable);
        }

        [Fact]
        public void SteadyGameMessageTraffic_AllocatesNothingPerMessage_OnThePollingThread()
        {
            EnsureRegistered();
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port, maxClients: 1));
            Require(server.Start(), "The server did not start", $"port {port}");
            using var client = new NetworkClient(Config(port));
            int received = 0;
            server.MessageReceived += e =>
            {
                if (e.Message is PingMessage)
                    received++;
            };

            Require(client.Connect("127.0.0.1", port, "Tester"), "The client could not connect");
            Require(WaitUntilReady(5f, client, server, client), "The client never became ready");

            // Each round advances the sessions by the real time it took, as the wall-clock pumps do.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            double lastRound = 0;
            void Round()
            {
                var ping = NetworkSession.Rent<PingMessage>();
                ping.A = received;
                ping.B = 7;
                client.Send(client.ServerPeerId, ping);
                client.Flush();
                double now = clock.Elapsed.TotalSeconds;
                float dt = (float)Math.Min(now - lastRound, 0.1);
                lastRound = now;
                client.Update(dt);
                server.Update(dt);
                System.Threading.Thread.Sleep(0);
            }

            // Warm-up lets the JIT tier up and LiteNetLib's pools and queues reach their steady size.
            for (int i = 0; i < 3000; i++)
                Round();

            // Both sessions run on this thread, so this counts the send path and the receive path together.
            // One-off growth (pool refills, queue resizes, timer-driven pings) can land in any window; a real
            // per-message allocation shows in every window, so the least-allocating window is the one to judge.
            const int messages = 2000;
            const int windows = 3;
            long least = long.MaxValue;
            for (int w = 0; w < windows; w++)
            {
                int before = received;
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < messages; i++)
                    Round();
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

                int arrived = received - before;
                Require(arrived > messages / 2, "most messages should have arrived on loopback", $"{arrived} of {messages}");
                _output.WriteLine($"window {w}: allocated {(double)allocated / messages:0.00} bytes per message round, {arrived} arrived");
                least = Math.Min(least, allocated);
            }

            double perMessage = (double)least / messages;
            Require(least <= messages * 8L, "game messages allocate on the polling thread",
                $"{perMessage:0.0} bytes per message round (send + two updates, least of {windows} windows)");
        }
    }
}
