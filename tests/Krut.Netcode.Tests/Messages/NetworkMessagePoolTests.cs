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

        private sealed class ConcurrentGameMessageA : INetworkMessage
        {
            public const byte Wire = 210;
            public NetworkMessageType Type => NetworkMessageType.None;
            public byte WireType => Wire;
            public NetworkDelivery Delivery => NetworkDelivery.Unreliable;
            public int Value;
            public void Reset() => Value = 0;
            public void Write(NetDataWriter writer) => writer.Put(Value);
            public void Read(NetDataReader reader) => Value = reader.GetInt();
        }

        private sealed class ConcurrentGameMessageB : INetworkMessage
        {
            public const byte Wire = 211;
            public NetworkMessageType Type => NetworkMessageType.None;
            public byte WireType => Wire;
            public NetworkDelivery Delivery => NetworkDelivery.ReliableOrdered;
            public int Value;
            public void Reset() => Value = 0;
            public void Write(NetDataWriter writer) => writer.Put(Value);
            public void Read(NetDataReader reader) => Value = reader.GetInt();
        }

        [Fact]
        public void IsRegistered_DistinguishesBuiltInReservedAndGameBytes()
        {
            Assert.False(NetworkMessages.IsRegistered(0));
            Assert.True(NetworkMessages.IsRegistered((byte)NetworkMessageType.Welcome));
            Assert.True(NetworkMessages.IsRegistered((byte)NetworkMessageType.SystemChat));
            Assert.True(NetworkMessages.IsRegistered((byte)NetworkMessageType.Chat));
            Assert.True(NetworkMessages.IsRegistered((byte)NetworkMessageType.PlayerInfo));
            for (byte b = 5; b < NetworkMessages.GameTypeBase; b++)
                Assert.False(NetworkMessages.IsRegistered(b), $"Reserved unassigned byte {b} should not report as registered");

            Assert.False(NetworkMessages.IsRegistered(215));
        }

        [Fact]
        public void Registry_ConcurrentRegistrationAndLookup_IsThreadSafe()
        {
            if (!NetworkMessages.IsRegistered(ConcurrentGameMessageA.Wire))
                NetworkMessages.RegisterGameMessage<ConcurrentGameMessageA>(ConcurrentGameMessageA.Wire, NetworkDelivery.Unreliable);
            if (!NetworkMessages.IsRegistered(ConcurrentGameMessageB.Wire))
                NetworkMessages.RegisterGameMessage<ConcurrentGameMessageB>(ConcurrentGameMessageB.Wire, NetworkDelivery.ReliableOrdered);

            var gate = new ManualResetEventSlim(false);
            int errors = 0;

            void ReaderWorker(byte wire, NetworkDelivery expectedDelivery)
            {
                gate.Wait();
                try
                {
                    for (int i = 0; i < 10_000; i++)
                    {
                        if (!NetworkMessages.IsRegistered(wire))
                            Interlocked.Increment(ref errors);
                        var msg = NetworkMessages.CreateFromWire(wire, out NetworkDelivery? delivery);
                        if (msg is null || delivery != expectedDelivery)
                            Interlocked.Increment(ref errors);
                        NetworkMessages.Recycle(msg, wire);
                    }
                }
                catch
                {
                    Interlocked.Increment(ref errors);
                }
            }

            void DuplicateRegisterWorker()
            {
                gate.Wait();
                for (int i = 0; i < 500; i++)
                {
                    try
                    {
                        NetworkMessages.RegisterGameMessage<ConcurrentGameMessageA>(ConcurrentGameMessageA.Wire);
                        Interlocked.Increment(ref errors);
                    }
                    catch (InvalidOperationException)
                    {
                        // Expected: duplicate registration must throw without corrupting the table.
                    }
                    catch
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            }

            var t1 = new Thread(() => ReaderWorker(ConcurrentGameMessageA.Wire, NetworkDelivery.Unreliable));
            var t2 = new Thread(() => ReaderWorker(ConcurrentGameMessageB.Wire, NetworkDelivery.ReliableOrdered));
            var t3 = new Thread(DuplicateRegisterWorker);
            t1.Start();
            t2.Start();
            t3.Start();
            gate.Set();
            t1.Join();
            t2.Join();
            t3.Join();

            Assert.Equal(0, errors);
        }

        [Fact]
        public void Recycle_CallsResetAndIgnoresDoubleReturn()
        {
            var msg = NetworkSession.Rent<ChatMessage>();
            msg.Text = "stale secret";

            NetworkMessages.Recycle(msg);
            NetworkMessages.Recycle(msg); // double-return must be ignored

            var first = NetworkSession.Rent<ChatMessage>();
            var second = NetworkSession.Rent<ChatMessage>();
            try
            {
                Assert.Same(msg, first);
                Assert.Equal(string.Empty, first.Text);
                Assert.NotSame(first, second);
            }
            finally
            {
                NetworkMessages.Recycle(first);
                NetworkMessages.Recycle(second);
            }
        }
    }
}
