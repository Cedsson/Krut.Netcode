using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Krut.Netcode;
using LiteNetLib.Utils;
using System.Numerics;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests
{
    /// <summary>KRUT_NET_* latency and loss simulation.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkSimulationTests
    {
        // Simulation (KRUT_NET_*) The most important rule of R6 is that the simulation must cost nothing when
        // it is off.

        [Fact]

        public void Simulation_WithoutEnvVar()
        {
            // R6's most important rule: without environment variables there must NOT be any simulation at all.
            string? latency = Environment.GetEnvironmentVariable(NetworkSimulation.LatencyVariable);
            string? jitter = Environment.GetEnvironmentVariable(NetworkSimulation.JitterVariable);
            string? loss = Environment.GetEnvironmentVariable(NetworkSimulation.LossVariable);
            try
            {
                Environment.SetEnvironmentVariable(NetworkSimulation.LatencyVariable, null);
                Environment.SetEnvironmentVariable(NetworkSimulation.JitterVariable, null);
                Environment.SetEnvironmentVariable(NetworkSimulation.LossVariable, null);

                var simulation = NetworkSimulation.FromEnvironment();
                Require(!simulation.IsActive, "No environment variable set but the simulation is active",
                    simulation.Describe());
                Require(simulation.LatencyMs == 0 && simulation.JitterMs == 0 && simulation.LossChance == 0f,
                    "The simulation has values even though no variable is set");
                Require(simulation.Describe() == NetworkSimulation.InactiveDescription,
                    "The description of an inactive simulation", simulation.Describe());

                // An inactive simulation must not leave anything behind in a
                // session - that is the core of "zero cost in production".
                using var session = NewCountingSession(tickRate: 30);
                session.Begin();
                session.ApplySimulation(simulation);
                for (int i = 0; i < 10; i++)
                    session.Update(CountingSession.TickIntervalSeconds(30));

                Require(session.TickCount == 10, "Update did not work with an inactive simulation",
                    $"{session.TickCount}");

                session.ClearSimulation();
                Require(session.Simulation is null, "ClearSimulation left a simulation behind");
            }
            finally
            {
                Environment.SetEnvironmentVariable(NetworkSimulation.LatencyVariable, latency);
                Environment.SetEnvironmentVariable(NetworkSimulation.JitterVariable, jitter);
                Environment.SetEnvironmentVariable(NetworkSimulation.LossVariable, loss);
            }
        }

        // The second half of this test exists because developers type KRUT_NET_LOSS by hand: a value written
        // with a Swedish decimal comma has to keep working even though the engine prints and parses invariant
        // numbers.
        [Fact]

        public void Simulation_ClampsInvalidValues()
        {
            // A broken environment variable must never make a game refuse to start, and must never give a
            // negative delay (which would mean "send the packet before it was created").
            var negative = NetworkSimulation.Create(-500, -20, -1f);
            Require(negative.LatencyMs == 0 && negative.JitterMs == 0 && negative.LossChance == 0f,
                "Negative values were not clamped to zero",
                $"{negative.LatencyMs}/{negative.JitterMs}/{negative.LossChance}");
            Require(!negative.IsActive, "A fully clamped simulation is still active");

            var tooMuch = NetworkSimulation.Create(100, 10, 5f);
            Require(tooMuch.LossChance == 1f, "Loss above 1.0 was not clamped to 1.0",
                tooMuch.LossChance.ToString(CultureInfo.InvariantCulture));

            // Reading from the environment: a broken string gives 0, not a throw.
            string? originalLatency = Environment.GetEnvironmentVariable(NetworkSimulation.LatencyVariable);
            string? originalLoss = Environment.GetEnvironmentVariable(NetworkSimulation.LossVariable);
            try
            {
                Environment.SetEnvironmentVariable(NetworkSimulation.LatencyVariable, "a lot");
                Require(NetworkSimulation.FromEnvironment().LatencyMs == 0,
                    "An unparsable latency did not give 0");

                Environment.SetEnvironmentVariable(NetworkSimulation.LatencyVariable, "150");
                Environment.SetEnvironmentVariable(NetworkSimulation.LossVariable, "0,02");
                // A Swedish decimal comma: the environment reader parses in the current culture, so set sv-SE here.
                NetworkSimulation comma = default!;
                WithCulture("sv-SE", () => comma = NetworkSimulation.FromEnvironment());
                Require(comma.LatencyMs == 150, "\"150\" was not read as 150",
                    comma.LatencyMs.ToString(CultureInfo.InvariantCulture));
                Require(MathF.Abs(comma.LossChance - 0.02f) < 0.0001f,
                    "\"0,02\" was not read with a Swedish comma",
                    comma.LossChance.ToString(CultureInfo.InvariantCulture));
            }
            finally
            {
                Environment.SetEnvironmentVariable(NetworkSimulation.LatencyVariable, originalLatency);
                Environment.SetEnvironmentVariable(NetworkSimulation.LossVariable, originalLoss);
            }
        }

        // This string is read by developers and pasted back into KRUT_NET_*, so it has to be machine-readable
        // as well as readable - invariant number format, no padding zeroes, and no parts that are switched off.
        [Fact]

        public void Simulation_DescribePrintsReadableLine()
        {
            var full = NetworkSimulation.Create(150, 30, 0.02f);
            Require(full.IsActive, "150 ms / 30 ms / 2 % does not count as an active simulation");
            Require(full.Describe() == "150 ms latency, 30 ms jitter, 2 % loss",
                "The description of a full simulation", full.Describe());

            // Latency only: the text must not fill up with zeroes.
            Require(NetworkSimulation.Create(150, 0, 0f).Describe() == "150 ms latency",
                "The description when only latency is set",
                NetworkSimulation.Create(150, 0, 0f).Describe());

            // Half a percent has to be written as 0.5, not rounded to 0 or 1 - a
            // developer reading the text must be able to paste the value back.
            Require(NetworkSimulation.Create(0, 0, 0.005f).Describe() == "0.5 % loss",
                "The description of 0.5 % loss",
                NetworkSimulation.Create(0, 0, 0.005f).Describe());
        }

        [Fact]

        public void Simulation_DelaysMessages()
        {
            // This is the test that proves R6 is actually met: without it, "simulation" is just a string in a
            // settings file.
            const int latencyMs = 300;
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port, maxClients: 1));
            Require(server.Start(), "The server did not start", $"port {port}");

            using var client = new NetworkClient(Config(port));
            var received = new List<string>();
            server.MessageReceived += e =>
            {
                if (e.Message is ChatMessage chat)
                    received.Add(chat.Text);
            };

            Require(client.Connect("127.0.0.1", port, "Tester"), "The client could not connect");
            Require(WaitUntilReady(5f, client, server, client), "The client never became ready");

            // The client is the side whose outgoing packets are delayed - the
            // direction a player actually experiences.
            client.ApplySimulation(NetworkSimulation.Create(latencyMs, 0, 0f));

            var watch = SendChat(client, "the delayed message");
            Require(PumpUntil(5f, () => received.Count > 0, server, client),
                "The message never arrived with 300 ms of delay");
            long elapsed = watch.ElapsedMilliseconds;

            // LiteNetLib's own handshake and the pumping have a few milliseconds of delay of their own, so the
            // bound is set below the simulated delay. A test that required exactly 300 ms would flake on CI on
            // a busy machine.
            Require(elapsed >= latencyMs / 2, "The delay was not applied",
                $"the message arrived after {elapsed} ms, expected at least {latencyMs / 2} ms");

            client.ClearSimulation();
        }

        [Fact]

        public void Simulation_JitterVariesDelay()
        {
            // Jitter should produce spread, not a fixed extra number.
            const int latencyMs = 120;
            const int jitterMs = 60;
            const int count = 8;
            int port = FreeUdpPort();

            using var server = new NetworkServer(Config(port, maxClients: 1));
            Require(server.Start(), "The server did not start", $"port {port}");

            using var client = new NetworkClient(Config(port));

            // The server measures the delay when the message is actually delivered to the engine - that is,
            // after both the UDP journey and the engine's own delay.
            var delays = new List<long>();
            var watch = new Stopwatch();
            watch.Start();
            server.MessageReceived += e =>
            {
                if (e.Message is ChatMessage)
                    delays.Add(watch.ElapsedMilliseconds);
            };

            Require(client.Connect("127.0.0.1", port, "Tester"), "The client could not connect");
            Require(WaitUntilReady(5f, client, server, client), "The client never became ready");

            client.ApplySimulation(NetworkSimulation.Create(latencyMs, jitterMs, 0f));

            var sentAt = new long[count];
            for (int i = 0; i < count; i++)
            {
                sentAt[i] = watch.ElapsedMilliseconds;
                var chat = NetworkClient.Rent<ChatMessage>();
                chat.Text = "jitter " + i;
                client.Send(client.ServerPeerId, chat);
                Pump(0.05f, server, client);
            }

            // Pump further without sending, so the delayed messages have time to
            // arrive - otherwise the test only measures how long you wait.
            Pump(1.5f, server, client);

            Require(delays.Count == count, "Not all jitter messages arrived",
                $"{delays.Count} of {count}");

            int min = int.MaxValue;
            int max = int.MinValue;
            for (int i = 0; i < count; i++)
            {
                long delay = delays[i] - sentAt[i];
                if (delay < min)
                    min = (int)delay;
                if (delay > max)
                    max = (int)delay;
            }

            // 60 ms of jitter gives a spread of up to 120 ms between the fastest and the slowest packet.
            Require(max - min >= 30, "The delay did not vary at all",
                $"spread {min}-{max} ms (jitter {jitterMs} ms around {latencyMs} ms)");

            // And every delay should sit around the latency - otherwise something other than jitter is at work.
            Require(min >= latencyMs - jitterMs - 20,
                "The delay was shorter than latency minus jitter",
                $"smallest {min} ms, expected at least {latencyMs - jitterMs} ms");
            Require(max <= latencyMs + jitterMs + 250,
                "The delay was longer than latency plus jitter (and the measurement noise)",
                $"largest {max} ms, expected at most {latencyMs + jitterMs} ms (+250 noise)");

            client.ClearSimulation();
        }

        [Fact]

        public void Simulation_FullLossDelaysReliableMessages()
        {
            // 100 % packet loss on the client's outgoing packets. So with 200 ms of latency nothing may arrive
            // at the plain 200 ms, everything must arrive by the resend, every drop is counted, and the game
            // must NOT hang. Delay and loss therefore apply to the game's own messages - which is exactly what
            // you want to worsen in a test run.
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port, maxClients: 1));
            Require(server.Start(), "The server did not start", $"port {port}");

            using var client = new NetworkClient(Config(port));
            var received = new List<string>();
            server.MessageReceived += e =>
            {
                if (e.Message is ChatMessage chat)
                    received.Add(chat.Text);
            };

            Require(client.Connect("127.0.0.1", port, "Tester"), "The client could not connect");
            Require(WaitUntilReady(5f, client, server, client), "The client never became ready");

            const int latencyMs = 200;
            client.ApplySimulation(NetworkSimulation.Create(latencyMs, 0, 1f));

            for (int i = 0; i < 5; i++)
                SendChat(client, "resent " + i);

            // Past the plain latency but well before the resend (3 x 200 ms) - in WALL time.
            Pump(0.35f, server, client);
            Require(received.Count == 0, "A dropped reliable message arrived without the resend delay",
                string.Join(", ", received));

            Require(PumpUntil(3f, () => received.Count == 5, server, client),
                "A dropped reliable message was lost instead of resent",
                $"{received.Count} of 5 arrived");
            Require(client.DroppedPacketCount >= 5, "The drops were not counted",
                $"{client.DroppedPacketCount}");

            // The game must not freeze: the network is pumped on and the client is
            // still running.
            Require(client.IsRunning, "The client stopped running the network on packet loss",
                client.State.ToString());
            Require(client.IsSessionReady, "The client lost the connection on its own during packet loss",
                client.State.ToString());
        }

        [Fact]

        public void Simulation_ClearSimulationResetsTheStraightPath()
        {
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port, maxClients: 1));
            Require(server.Start(), "The server did not start", $"port {port}");

            using var client = new NetworkClient(Config(port));
            Require(client.Connect("127.0.0.1", port, "Tester"), "The client could not connect");
            Require(WaitUntilReady(5f, client, server, client), "The client never became ready");

            // Queue a message behind an infinitely long delay and then switch the delay off.
            client.ApplySimulation(NetworkSimulation.Create(10_000, 0, 0f));
            var chat = NetworkClient.Rent<ChatMessage>();
            chat.Text = "must not get stuck";
            client.Send(client.ServerPeerId, chat);

            var watch = Stopwatch.StartNew();
            client.ClearSimulation();
            watch.Stop();

            Require(client.Simulation is null, "ClearSimulation left the simulation in place");
            Require(watch.ElapsedMilliseconds < 500, "ClearSimulation took an unnecessarily long time",
                $"{watch.ElapsedMilliseconds} ms - it should be immediate");
        }

        [Fact]

        public void Simulation_BothSidesTogetherDoNotCrash()
        {
            // A full round trip with simulation on both sides: that is how R6 is used in practice, and it is
            // the combination that reaches code paths a one-sided simulation never runs.
            int port = FreeUdpPort();
            using var server = new NetworkServer(Config(port, maxClients: 2));
            Require(server.Start(), "The server did not start", $"port {port}");

            // The server forwards the chat - that is the game's job, not the
            // engine's (see NetworkServer), so the test models a game.
            var chatB = new List<string>();
            server.MessageReceived += e =>
            {
                if (e.Message is not ChatMessage chat)
                    return;

                var forward = NetworkSession.Rent<ChatMessage>();
                forward.Text = chat.Text;
                server.Broadcast(forward);
            };

            using var clientA = new NetworkClient(Config(port));
            using var clientB = new NetworkClient(Config(port));
            clientB.MessageReceived += e =>
            {
                if (e.Message is ChatMessage chat)
                    chatB.Add(chat.Text);
            };

            Require(clientA.Connect("127.0.0.1", port, "A"), "Client A could not connect");
            Require(clientB.Connect("127.0.0.1", port, "B"), "Client B could not connect");
            Require(WaitUntilAllReady(5f, new[] { clientA, clientB }, server, clientA, clientB),
                "Neither client ever became ready");

            var simulation = NetworkSimulation.Create(40, 15, 0.02f);
            server.ApplySimulation(simulation);
            clientA.ApplySimulation(simulation);
            clientB.ApplySimulation(simulation);

            // <b>Several messages, not one.</b> Simulated loss is discarded before the packet leaves the
            // engine, that is before the reliable channel has even seen it - and a reliable channel that never
            // sent the packet has nothing to resend.
            const int messages = 6;
            for (int i = 0; i < messages; i++)
            {
                SendChat(clientA, $"hello over the simulated network {i}");
                Pump(0.2f, server, clientA, clientB);
            }

            // 10 seconds is a generous margin for 30-40 ms of latency.
            Require(PumpUntil(10f, () => chatB.Count > 0, server, clientA, clientB),
                "No message arrived through both delays",
                $"B got {chatB.Count} of {messages}");

        }

    }
}
