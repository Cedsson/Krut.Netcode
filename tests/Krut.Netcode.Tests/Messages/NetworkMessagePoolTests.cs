using System;
using System.Collections.Generic;
using System.Threading;
using Krut.Netcode;
using LiteNetLib.Utils;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests
{
    /// <summary>Ownership rules of the message pool: only rented messages are recycled, and only once.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkMessagePoolTests
    {
        internal const byte ProbeByte = 231;

        internal sealed class ProbeMessage : INetworkMessage
        {
            public NetworkMessageType Type => NetworkMessageType.None;

            public byte WireType => ProbeByte;

            public NetworkDelivery Delivery => NetworkDelivery.ReliableOrdered;

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

        private static void EnsureRegistered()
        {
            if (!NetworkMessages.IsRegistered(ProbeByte))
                NetworkMessages.RegisterGameMessage<ProbeMessage>(ProbeByte, NetworkDelivery.ReliableOrdered);
        }

        private static (NetworkServer Server, NetworkClient[] Clients, int[] Received) Connect(int clients)
        {
            int port = FreeUdpPort();
            var server = new NetworkServer(Config(port, maxClients: clients));
            Require(server.Start(), "The server did not start", $"port {port}");
            var list = new NetworkClient[clients];
            var received = new int[clients];
            for (int i = 0; i < clients; i++)
            {
                int index = i;
                list[i] = new NetworkClient(Config(port));
                list[i].MessageReceived += e =>
                {
                    if (e.Message is ProbeMessage probe && probe.A == 11 && probe.B == 22)
                        received[index]++;
                };
                Require(list[i].Connect("127.0.0.1", port, "C" + i), "A client could not connect");
            }

            var sessions = new List<NetworkSession> { server };
            sessions.AddRange(list);
            Require(WaitUntilAllReady(5f, list, sessions.ToArray()), "Not every client became ready");
            return (server, list, received);
        }

        [Fact]
        public void ANewMessage_SentToThreePeers_IsNeverTakenOverByThePool()
        {
            EnsureRegistered();
            var (server, clients, received) = Connect(3);
            using (server)
            {
                try
                {
                    var message = new ProbeMessage { A = 11, B = 22 };
                    foreach (var peer in server.Peers)
                        server.Send(peer.Id, message);

                    Require(message.A == 11 && message.B == 22, "the sent message was modified");
                    // If the pool had taken it over, the next renter would get the very same instance.
                    var rented = NetworkSession.Rent<ProbeMessage>();
                    Require(!ReferenceEquals(rented, message), "a message created with new was recycled into the pool");
                    NetworkMessages.Recycle(rented, ProbeByte);

                    var all = new List<NetworkSession> { server };
                    all.AddRange(clients);
                    Require(PumpUntil(5f, () => received[0] == 1 && received[1] == 1 && received[2] == 1, all.ToArray()),
                        "not every peer got the message", string.Join(",", received));
                }
                finally
                {
                    foreach (var client in clients)
                        client.Dispose();
                }
            }
        }

        [Fact]
        public void ARentedMessage_IsRecycledOnce_EvenIfSentToSeveralPeers()
        {
            EnsureRegistered();
            var (server, clients, received) = Connect(3);
            using (server)
            {
                try
                {
                    var message = NetworkSession.Rent<ProbeMessage>();
                    message.A = 11;
                    message.B = 22;
                    foreach (var peer in server.Peers)
                        server.Send(peer.Id, message);

                    var first = NetworkSession.Rent<ProbeMessage>();
                    var second = NetworkSession.Rent<ProbeMessage>();
                    Require(ReferenceEquals(first, message), "the sent rented message did not come back from the pool");
                    Require(!ReferenceEquals(second, message), "the same instance was handed out twice");
                    NetworkMessages.Recycle(first, ProbeByte);
                    NetworkMessages.Recycle(second, ProbeByte);
                }
                finally
                {
                    foreach (var client in clients)
                        client.Dispose();
                }
            }
        }

        [Fact]
        public void TwoThreadsRentingAndReturning_NeverShareAnInstance()
        {
            EnsureRegistered();
            // Drain whatever other tests left in the pool so both threads start from the same state.
            int violations = 0;
            int done = 0;
            var gate = new ManualResetEventSlim(false);

            void Worker(int id)
            {
                gate.Wait();
                for (int i = 0; i < 200_000; i++)
                {
                    var message = NetworkSession.Rent<ProbeMessage>();
                    message.A = id;
                    message.B = i;
                    if (i % 64 == 0)
                        Thread.SpinWait(20);
                    if (message.A != id || message.B != i)
                        Interlocked.Increment(ref violations);
                    NetworkMessages.Recycle(message, ProbeByte);
                }

                Interlocked.Increment(ref done);
            }

            var a = new Thread(() => Worker(1));
            var b = new Thread(() => Worker(2));
            a.Start();
            b.Start();
            gate.Set();
            a.Join();
            b.Join();

            Require(done == 2, "a worker did not finish");
            Require(violations == 0, "two threads held the same instance", violations.ToString());
        }
    }
}
