using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using Krut.Netcode;
using LiteNetLib.Layers;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests
{
    /// <summary>The datagram stage: chain composition, one-switch rule, and LiteNetLib's own packets under loss and outages.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkDatagramSimTests
    {
        private sealed class Recorder : PacketLayerBase
        {
            private readonly string _name;
            private readonly List<string> _log;

            public Recorder(string name, int extra, List<string> log)
                : base(extra)
            {
                _name = name;
                _log = log;
            }

            public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length) => _log.Add("in:" + _name);

            public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length) => _log.Add("out:" + _name);
        }

        private static readonly IPEndPoint Remote = new(IPAddress.Loopback, 9999);

        private static bool Send(PacketLayerBase layer, int bytes = 50)
        {
            var endpoint = Remote;
            byte[] data = new byte[bytes];
            int offset = 0;
            int length = bytes;
            layer.ProcessOutBoundPacket(ref endpoint, ref data, ref offset, ref length);
            return length > 0;
        }

        [Fact]
        public void Chain_RunsOutboundFrontToBack_InboundBackToFront_AndSumsTheExtraSize()
        {
            var log = new List<string>();
            var chain = new NetworkDatagramChain(new Recorder("secure", 16, log), new Recorder("sim", 0, log), new Recorder("probe", 2, log));
            Require(chain.ExtraPacketSizeForLayer == 18, "extra size is not the sum", chain.ExtraPacketSizeForLayer.ToString());

            Send(chain);
            var endpoint = Remote;
            byte[] data = new byte[10];
            int length = 10;
            chain.ProcessInboundPacket(ref endpoint, ref data, ref length);

            Require(string.Join(",", log) == "out:secure,out:sim,out:probe,in:probe,in:sim,in:secure", "order wrong", string.Join(",", log));
        }

        [Fact]
        public void Chain_StopsWalking_WhenALayerDropsTheDatagram()
        {
            var log = new List<string>();
            var dropper = new NetworkDatagramSimLayer();
            var chain = new NetworkDatagramChain(dropper, new Recorder("probe", 0, log));
            chain.Simulation = NetworkSimulation.Create(new NetworkSimulationOptions { LossChance = 1.0, DatagramLevel = true });

            Require(!Send(chain), "a datagram survived 100 % loss");
            Require(log.Count == 0, "a layer after the drop still saw the datagram", string.Join(",", log));
        }

        [Fact]
        public void SimLayer_IgnoresASimulationThatIsNotDatagramLevel()
        {
            var layer = new NetworkDatagramSimLayer();
            layer.Simulation = NetworkSimulation.Create(new NetworkSimulationOptions { LossChance = 1.0 });
            Require(Send(layer), "message-level simulation dropped a datagram");
            layer.Simulation = null;
            Require(Send(layer), "a cleared layer dropped a datagram");
        }

        [Fact]
        public void SimLayer_AppliesTheSameSeededDropPattern_Twice()
        {
            bool[] Pattern(ulong seed)
            {
                var layer = new NetworkDatagramSimLayer();
                layer.Simulation = NetworkSimulation.Create(new NetworkSimulationOptions { LossChance = 0.2, BurstMeanLength = 3, DatagramLevel = true, Seed = seed });
                var result = new bool[2000];
                for (int i = 0; i < result.Length; i++)
                    result[i] = Send(layer);
                return result;
            }

            var a = Pattern(5);
            var b = Pattern(5);
            var c = Pattern(6);
            int same = 0, differ = 0, lost = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) same++;
                if (a[i] != c[i]) differ++;
                if (!a[i]) lost++;
            }

            Require(same == a.Length, "the same seed gave different drops");
            Require(differ > 100, "different seeds gave the same drops");
            Require(lost > 200 && lost < 600, "loss rate far from 20 %", lost.ToString());
        }

        [Fact]
        public void DatagramLevel_TurnsOffMessageLevelLossAndOutages_SoAnEffectIsAppliedOnce()
        {
            var options = new NetworkSimulationOptions
            {
                LatencyMs = 10,
                LossChance = 1.0,
                Outages = { new NetworkOutage(0.0, 10.0) },
            };

            var message = new NetworkLinkModel(NetworkSimulation.Create(options), 0).Submit(1000, 100, reliable: false);
            Require(!message.Deliver, "message-level stage should drop here");

            options.DatagramLevel = true;
            var datagram = new NetworkLinkModel(NetworkSimulation.Create(options), 0).Submit(1000, 100, reliable: false);
            Require(datagram.Deliver && datagram.Cause == NetworkDropCause.None && datagram.DueUs == 11_000,
                "message stage still applied loss or the outage, or lost the delay", datagram.ToString());
        }

        [Fact]
        public void ApplySimulation_DatagramLevelWithoutALayer_Throws_AndLeavesNoState()
        {
            using var client = new NetworkClient(Config(FreeUdpPort()));
            var sim = NetworkSimulation.Create(new NetworkSimulationOptions { LossChance = 0.1, DatagramLevel = true });
            Assert.Throws<InvalidOperationException>(() => client.ApplySimulation(sim));
            Require(client.Simulation is null, "a refused simulation was stored");
        }

        [Fact]
        public void DefaultFactory_GivesEachSessionItsOwnChain_AndLeavesTheConfigAlone()
        {
            int created = 0;
            NetworkDatagramChain.DefaultFactory = () =>
            {
                created++;
                return new NetworkDatagramChain(new NetworkDatagramSimLayer());
            };

            try
            {
                var config = Config(FreeUdpPort());
                using var first = new NetworkClient(config);
                using var second = new NetworkClient(config);
                Require(created == 2, "the factory was not called once per session", created.ToString());
                Require(first.DatagramChain is not null && second.DatagramChain is not null, "a session got no chain");
                Require(!ReferenceEquals(first.DatagramChain, second.DatagramChain), "two sessions share one chain");
                Require(config.DatagramLayer is null && first.Config.DatagramLayer is null, "the factory result was written into a config");

                // An explicit config chain wins over the factory.
                var explicitChain = new NetworkDatagramChain(new NetworkDatagramSimLayer());
                var withChain = Config(FreeUdpPort());
                withChain.DatagramLayer = explicitChain;
                using var third = new NetworkClient(withChain);
                Require(ReferenceEquals(third.DatagramChain, explicitChain) && created == 2, "the config chain did not win");
            }
            finally
            {
                NetworkDatagramChain.DefaultFactory = null;
            }
        }

        private static (NetworkServer Server, NetworkClient Client) Pair(int port, int timeoutMs, bool autoReconnect = false)
        {
            var serverConfig = Config(port, maxClients: 1);
            serverConfig.DisconnectTimeoutMs = timeoutMs;
            serverConfig.PingIntervalMs = 200;
            serverConfig.DatagramLayer = new NetworkDatagramChain(new NetworkDatagramSimLayer());
            var clientConfig = Config(port);
            clientConfig.DisconnectTimeoutMs = timeoutMs;
            clientConfig.PingIntervalMs = 200;
            clientConfig.AutoReconnect = autoReconnect;
            clientConfig.DatagramLayer = new NetworkDatagramChain(new NetworkDatagramSimLayer());
            return (new NetworkServer(serverConfig), new NetworkClient(clientConfig));
        }

        private static void ApplyBoth(NetworkSimulation sim, NetworkServer server, NetworkClient client)
        {
            server.ApplySimulation(sim);
            client.ApplySimulation(sim.WithSeed(sim.Seed + 1));
        }

        [Fact]
        public void LongOutage_MakesLiteNetLibTimeOut_AtTheConfiguredTimeout()
        {
            int port = FreeUdpPort();
            var (server, client) = Pair(port, timeoutMs: 1500);
            using (server)
            using (client)
            {
                Require(server.Start(), "The server did not start", $"port {port}");
                string? reason = null;
                client.Closed += (r, text) => reason ??= r.ToString();
                Require(client.Connect("127.0.0.1", port, "Tester"), "The client could not connect");
                Require(WaitUntilReady(5f, client, server, client), "The client never became ready");

                var sim = NetworkSimulation.Create(new NetworkSimulationOptions
                {
                    DatagramLevel = true,
                    Outages = { new NetworkOutage(0.0, 6.0) },
                });
                ApplyBoth(sim, server, client);

                // The clock starts at the first datagram after the simulation is set; send one so it starts now.
                var watch = SendChat(client, "start the outage clock");
                Require(PumpUntil(5f, () => reason is not null, server, client), "no timeout during a 6 s outage", client.State.ToString());
                long ms = watch.ElapsedMilliseconds;
                Require(reason == "Timeout", "closed for another reason than a timeout", reason);
                Require(ms >= 1200 && ms <= 3500, "timeout not near the configured 1500 ms", $"{ms} ms");
            }
        }

        [Fact]
        public void ShortOutage_DoesNotDisconnect_AndReliableTrafficResumes()
        {
            int port = FreeUdpPort();
            var (server, client) = Pair(port, timeoutMs: 4000);
            using (server)
            using (client)
            {
                Require(server.Start(), "The server did not start", $"port {port}");
                bool closed = false;
                client.Closed += (r, text) => closed = true;
                var received = new List<string>();
                server.MessageReceived += e =>
                {
                    if (e.Message is ChatMessage chat)
                        received.Add(chat.Text);
                };

                Require(client.Connect("127.0.0.1", port, "Tester"), "The client could not connect");
                Require(WaitUntilReady(5f, client, server, client), "The client never became ready");

                ApplyBoth(NetworkSimulation.Create(new NetworkSimulationOptions
                {
                    DatagramLevel = true,
                    Outages = { new NetworkOutage(0.0, 1.0) },
                }), server, client);

                var watch = SendChat(client, "sent into a 1 s outage");
                Pump(0.5f, server, client);
                Require(received.Count == 0, "a message crossed a total outage", string.Join(",", received));

                Require(PumpUntil(4f, () => received.Count == 1, server, client), "the reliable message was lost for good");
                Require(watch.ElapsedMilliseconds >= 900, "the message arrived before the outage ended", $"{watch.ElapsedMilliseconds} ms");
                Require(!closed && client.IsSessionReady, "a 1 s outage disconnected a 4 s timeout", client.State.ToString());

                var after = SendChat(client, "after");
                Require(PumpUntil(2f, () => received.Count == 2, server, client), "traffic did not resume after the outage");
                Require(after.ElapsedMilliseconds < 1000, "recovery too slow", $"{after.ElapsedMilliseconds} ms");

                var datagrams = client.DatagramChain!.DatagramStats!;
                Require(datagrams.DroppedOutage > 0, "no datagram was dropped by the outage");
                Require(client.SimulationStats.Dropped == 0, "the message stage also dropped: an effect was applied twice");
            }
        }

        /// <summary>
        /// Drops the first outbound datagram of at least <see cref="MinLength"/> bytes once <see cref="Armed"/> is set,
        /// and nothing else. The chat datagrams are the only ones that large after the handshake, so the drop lands on
        /// a reliable message; acks, pings and pongs stay small and are never touched.
        /// </summary>
        private sealed class DropFirstLargeDatagramLayer : PacketLayerBase
        {
            public DropFirstLargeDatagramLayer(int minLength)
                : base(0)
            {
                MinLength = minLength;
            }

            public int MinLength { get; }

            public bool Armed { get; set; }

            public int Dropped { get; private set; }

            public int LargestSeen { get; private set; }

            public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
            {
            }

            public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
            {
                if (!Armed || Dropped > 0 || length <= 0)
                    return;

                LargestSeen = Math.Max(LargestSeen, length);
                if (length < MinLength)
                    return;

                Dropped++;
                length = 0;
            }
        }

        private static (long Dropped, int Delivered) ReliableRun(int messages, double loss)
        {
            int port = FreeUdpPort();
            var (server, client) = Pair(port, timeoutMs: 8000);
            using (server)
            using (client)
            {
                Require(server.Start(), "The server did not start", $"port {port}");
                int delivered = 0;
                server.MessageReceived += e =>
                {
                    if (e.Message is ChatMessage)
                        delivered++;
                };

                Require(client.Connect("127.0.0.1", port, "Tester"), "The client could not connect");
                Require(WaitUntilReady(5f, client, server, client), "The client never became ready");

                ApplyBoth(NetworkSimulation.Create(new NetworkSimulationOptions { DatagramLevel = true, LossChance = loss, Seed = 123 }), server, client);

                for (int i = 0; i < messages; i++)
                {
                    SendChat(client, "m" + i + new string('x', 200));
                    Pump(0.02f, server, client);
                }

                Require(PumpUntil(20f, () => delivered == messages, server, client), "reliable messages were lost at datagram-level loss", $"{delivered} of {messages}");
                var stats = client.DatagramChain!.DatagramStats!;
                Require(client.SimulationStats.Dropped == 0, "the message stage dropped as well");
                return (stats.Dropped, delivered);
            }
        }

        [Fact]
        public void LostReliableDatagram_IsSentAgain_AndTheMessageArrives()
        {
            const int messages = 20;
            int port = FreeUdpPort();
            var serverConfig = Config(port, maxClients: 1);
            serverConfig.DisconnectTimeoutMs = 8000;
            serverConfig.PingIntervalMs = 200;
            var clientConfig = Config(port);
            clientConfig.DisconnectTimeoutMs = 8000;
            clientConfig.PingIntervalMs = 200;
            var dropOnce = new DropFirstLargeDatagramLayer(minLength: 100);
            clientConfig.DatagramLayer = new NetworkDatagramChain(dropOnce);

            using var server = new NetworkServer(serverConfig);
            using var client = new NetworkClient(clientConfig);
            Require(server.Start(), "The server did not start", $"port {port}");
            int delivered = 0;
            server.MessageReceived += e =>
            {
                if (e.Message is ChatMessage)
                    delivered++;
            };

            Require(client.Connect("127.0.0.1", port, "Tester"), "The client could not connect");
            Require(WaitUntilReady(5f, client, server, client), "The client never became ready");

            // Only the chat datagrams after this point are candidates for the one drop.
            dropOnce.Armed = true;
            for (int i = 0; i < messages; i++)
            {
                SendChat(client, "m" + i + new string('x', 200));
                Pump(0.02f, server, client);
            }

            Require(PumpUntil(20f, () => delivered == messages, server, client),
                "a lost reliable datagram was never sent again", $"{delivered} of {messages} arrived");
            Require(dropOnce.Dropped == 1, "the layer did not drop exactly one reliable datagram", $"dropped {dropOnce.Dropped}, largest outbound datagram {dropOnce.LargestSeen} bytes");
            // The delivery above is the proof: the only way the dropped message can arrive is a resend.
        }

        [Fact]
        public void FivePercentLoss_DropsDatagrams_AndEveryReliableMessageStillArrives()
        {
            const int messages = 300;
            var run = ReliableRun(messages, 0.05);

            Require(run.Dropped >= 5, "5 % loss dropped almost nothing", $"{run.Dropped} datagrams dropped");
            Require(run.Delivered == messages, "not every reliable message arrived", $"{run.Delivered} of {messages}");
        }
    }
}
