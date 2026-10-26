using System;
using System.Collections.Generic;
using System.Diagnostics;
using Krut.Netcode;
using Xunit;

using static Krut.Netcode.Tests.NetworkTestSupport;

namespace Krut.Netcode.Tests
{
    /// <summary>The engine network simulator: seeded determinism, loss models, cap, outages, profiles.</summary>
    public class NetworkLinkModelTests
    {
        private const long StepUs = 16_667;

        private static NetworkSimulationOptions Rich(ulong seed) => new()
        {
            LatencyMs = 50,
            JitterMs = 20,
            LossChance = 0.1,
            BurstMeanLength = 3,
            ReorderChance = 0.05,
            DuplicateChance = 0.05,
            Seed = seed,
        };

        private static List<NetworkLinkVerdict> Run(NetworkSimulation sim, int count, int linkIndex = 0)
        {
            var link = new NetworkLinkModel(sim, linkIndex);
            var list = new List<NetworkLinkVerdict>(count);
            for (int i = 0; i < count; i++)
                list.Add(link.Submit(i * StepUs, 100 + i % 7 * 40, reliable: i % 5 == 0));
            return list;
        }

        [Fact]
        public void SameSeed_GivesTheIdenticalVerdictSequence()
        {
            var a = Run(NetworkSimulation.Create(Rich(42)), 5000);
            var b = Run(NetworkSimulation.Create(Rich(42)), 5000);

            Require(a.Count == b.Count, "different lengths");
            for (int i = 0; i < a.Count; i++)
                Require(a[i] == b[i], "verdicts differ with the same seed", $"message {i}");

            // The sequence must actually exercise every effect, or the equality above proves nothing.
            int dropped = 0, duplicated = 0, reordered = 0;
            foreach (var v in a)
            {
                if (!v.Deliver) dropped++;
                if (v.Duplicate) duplicated++;
                if (v.Reordered) reordered++;
            }

            Require(dropped > 100 && duplicated > 50 && reordered > 50, "effects not exercised",
                $"dropped {dropped}, duplicated {duplicated}, reordered {reordered}");
        }

        [Fact]
        public void DifferentSeeds_GiveDifferentSequences()
        {
            var a = Run(NetworkSimulation.Create(Rich(1)), 2000);
            var b = Run(NetworkSimulation.Create(Rich(2)), 2000);
            var c = Run(NetworkSimulation.Create(Rich(1)), 2000, linkIndex: 1);

            int differBySeed = 0, differByLink = 0;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) differBySeed++;
                if (a[i] != c[i]) differByLink++;
            }

            Require(differBySeed > 1000, "seeds 1 and 2 give nearly the same verdicts", $"{differBySeed} of 2000");
            Require(differByLink > 1000, "links 0 and 1 share a stream", $"{differByLink} of 2000");
        }

        [Fact]
        public void GilbertElliott_MeanBurstAndLossMatchTheConfig()
        {
            const int count = 100_000;
            foreach (double burst in new[] { 2.0, 4.0, 8.0 })
            {
                var sim = NetworkSimulation.Create(new NetworkSimulationOptions { LossChance = 0.05, BurstMeanLength = burst, Seed = 7 });
                var link = new NetworkLinkModel(sim, 0);
                int lost = 0, bursts = 0, run = 0;
                for (int i = 0; i < count; i++)
                {
                    bool drop = !link.Submit(i * StepUs, 100, reliable: false).Deliver;
                    if (drop)
                    {
                        lost++;
                        run++;
                    }
                    else if (run > 0)
                    {
                        bursts++;
                        run = 0;
                    }
                }

                if (run > 0)
                    bursts++;

                double meanBurst = (double)lost / bursts;
                double rate = (double)lost / count;
                Require(Math.Abs(meanBurst - burst) <= burst * 0.10, "mean burst length off by more than 10 %",
                    $"configured {burst}, measured {meanBurst:0.00}");
                Require(Math.Abs(rate - 0.05) <= 0.005, "long-run loss off by more than 0.5 points",
                    $"measured {rate:0.0000}");
            }
        }

        [Fact]
        public void IndependentLoss_MatchesTheRateWithoutBursts()
        {
            var sim = NetworkSimulation.Create(new NetworkSimulationOptions { LossChance = 0.1, Seed = 3 });
            var link = new NetworkLinkModel(sim, 0);
            int lost = 0, bursts = 0, run = 0;
            for (int i = 0; i < 100_000; i++)
            {
                if (!link.Submit(i * StepUs, 100, false).Deliver)
                {
                    lost++;
                    run++;
                }
                else if (run > 0)
                {
                    bursts++;
                    run = 0;
                }
            }

            Require(Math.Abs(lost / 100_000.0 - 0.1) < 0.005, "iid loss rate off", $"{lost}");
            // Geometric run length with p = 0.1 has mean 1 / (1 - 0.1) = 1.11.
            Require(Math.Abs((double)lost / bursts - 1.0 / 0.9) < 0.05, "iid loss shows bursts", $"{(double)lost / bursts:0.00}");
        }

        [Fact]
        public void BandwidthCap_HoldsTheRateWithinFivePercent_AndTheQueueIsBounded()
        {
            const int rate = 8000;
            var sim = NetworkSimulation.Create(new NetworkSimulationOptions { BandwidthBytesPerSecond = rate, Seed = 5 });
            var link = new NetworkLinkModel(sim, 0);

            // 30 packets/s of 700 bytes offered = 21 kB/s against 8 kB/s for 60 s.
            const int bytes = 700;
            const long stepUs = 1_000_000 / 30;
            const long endUs = 60_000_000;
            long delivered = 0;
            long worstWaitUs = 0;
            for (long now = 0; now < endUs; now += stepUs)
            {
                var v = link.Submit(now, bytes, reliable: false);
                if (!v.Deliver)
                    continue;
                if (v.DueUs <= endUs)
                    delivered += bytes;
                worstWaitUs = Math.Max(worstWaitUs, v.DueUs - now);
            }

            double measured = delivered / (endUs / 1e6);
            Require(Math.Abs(measured - rate) <= rate * 0.05, "cap not held within 5 %", $"{measured:0} B/s against {rate}");
            Require(link.Stats.DroppedOverflow > 0, "no tail drop under overload");
            long boundUs = (long)((sim.QueueLimitBytes + bytes) / (rate / 1e6)) + 1000;
            Require(worstWaitUs <= boundUs, "queue delay not bounded", $"{worstWaitUs} us, bound {boundUs} us");
        }

        [Fact]
        public void BandwidthCap_BelowTheRate_DoesNotDelayOrDrop()
        {
            var sim = NetworkSimulation.Create(new NetworkSimulationOptions { BandwidthBytesPerSecond = 8000, Seed = 5 });
            var link = new NetworkLinkModel(sim, 0);
            for (int i = 0; i < 600; i++)
            {
                long now = i * 100_000L; // 10 packets/s of 400 bytes = 4 kB/s
                var v = link.Submit(now, 400, false);
                Require(v.Deliver && v.DueUs == now, "an unsaturated link added delay or loss", $"message {i}");
            }
        }

        [Fact]
        public void Outage_DropsEverythingInItsWindow_AndOnlyThere()
        {
            var sim = NetworkSimulation.Create(new NetworkSimulationOptions
            {
                LatencyMs = 10,
                Outages = { new NetworkOutage(1.0, 0.5) },
            });
            var link = new NetworkLinkModel(sim, 0);
            for (int ms = 0; ms < 3000; ms += 5)
            {
                var v = link.Submit(ms * 1000L, 100, reliable: false);
                bool inside = ms >= 1000 && ms < 1500;
                Require(v.Deliver != inside, "outage window wrong", $"t = {ms} ms, deliver {v.Deliver}");
            }

            var held = link.Submit(1200_000, 100, reliable: true);
            Require(held.Deliver && held.Cause == NetworkDropCause.Outage && held.DueUs >= 1_500_000,
                "a reliable message inside an outage must leave after it", $"due {held.DueUs}");
        }

        [Fact]
        public void Jitter_DoesNotReorder_ButTheReorderRateDoes()
        {
            var jittery = NetworkSimulation.Create(new NetworkSimulationOptions { LatencyMs = 50, JitterMs = 40, Seed = 9 });
            var link = new NetworkLinkModel(jittery, 0);
            long last = long.MinValue;
            for (int i = 0; i < 5000; i++)
            {
                long due = link.Submit(i * StepUs, 100, false).DueUs;
                Require(due >= last, "plain jitter reordered a message", $"message {i}");
                last = due;
            }

            var reorder = NetworkSimulation.Create(new NetworkSimulationOptions { LatencyMs = 50, ReorderChance = 0.05, Seed = 9 });
            link = new NetworkLinkModel(reorder, 0);
            int overtaken = 0;
            last = long.MinValue;
            for (int i = 0; i < 20_000; i++)
            {
                long due = link.Submit(i * StepUs, 100, false).DueUs;
                if (due < last)
                    overtaken++;
                last = Math.Max(last, due);
            }

            // A reordered message is late; each one is overtaken by the next few, so count the late ones through the stats.
            Require(Math.Abs(link.Stats.Reordered / 20_000.0 - 0.05) < 0.01, "reorder rate off", $"{link.Stats.Reordered}");
            Require(overtaken > 500, "no message was actually overtaken", $"{overtaken}");
        }

        [Fact]
        public void Duplicate_RateMatches_AndTheCopyComesLater()
        {
            var sim = NetworkSimulation.Create(new NetworkSimulationOptions { LatencyMs = 20, DuplicateChance = 0.1, Seed = 11 });
            var link = new NetworkLinkModel(sim, 0);
            int dups = 0;
            for (int i = 0; i < 20_000; i++)
            {
                var v = link.Submit(i * StepUs, 100, false);
                if (v.Duplicate)
                {
                    dups++;
                    Require(v.DuplicateDueUs > v.DueUs, "the duplicate does not come after the original");
                }
            }

            Require(Math.Abs(dups / 20_000.0 - 0.1) < 0.01, "duplicate rate off", $"{dups}");
        }

        [Fact]
        public void ReliableMessage_IsDelayedByALoss_NotDropped()
        {
            var sim = NetworkSimulation.Create(new NetworkSimulationOptions { LatencyMs = 100, LossChance = 1.0 });
            var v = new NetworkLinkModel(sim, 0).Submit(0, 100, reliable: true);
            Require(v.Deliver && v.Cause == NetworkDropCause.Random && v.DueUs == 300_000, "resend delay wrong", $"{v}");
        }
    }

    /// <summary>Profiles and environment parsing. Serialised with the other tests that change environment variables.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkSimulationProfileTests
    {
        private static readonly string[] Variables =
        {
            NetworkSimulation.ProfileVariable, NetworkSimulation.LatencyVariable, NetworkSimulation.JitterVariable,
            NetworkSimulation.LossVariable, NetworkSimulation.SeedVariable, NetworkSimulation.BurstVariable,
            NetworkSimulation.ReorderVariable, NetworkSimulation.DuplicateVariable, NetworkSimulation.BandwidthVariable,
            NetworkSimulation.OutagesVariable,
        };

        private static void WithEnvironment(Action body, params (string Name, string Value)[] set)
        {
            var saved = new string?[Variables.Length];
            for (int i = 0; i < Variables.Length; i++)
            {
                saved[i] = Environment.GetEnvironmentVariable(Variables[i]);
                Environment.SetEnvironmentVariable(Variables[i], null);
            }

            try
            {
                foreach (var (name, value) in set)
                    Environment.SetEnvironmentVariable(name, value);
                body();
            }
            finally
            {
                for (int i = 0; i < Variables.Length; i++)
                    Environment.SetEnvironmentVariable(Variables[i], saved[i]);
            }
        }

        [Fact]
        public void AllSevenProfilesExist_AndAreCaseInsensitive()
        {
            string[] expected = { "clean", "lan", "gate", "stress", "bad", "blackout", "thin" };
            Require(NetworkSimulationProfile.Names.Count == 7, "profile count", string.Join(",", NetworkSimulationProfile.Names));
            foreach (string name in expected)
            {
                Require(NetworkSimulationProfile.TryGet(name, out _), "missing profile", name);
                Require(NetworkSimulationProfile.TryGet(name.ToUpperInvariant(), out _), "case matters", name);
            }

            Require(!NetworkSimulationProfile.TryGet("nope", out _), "unknown profile accepted");
            Require(!NetworkSimulationProfile.TryGet(null, out _), "null profile accepted");
            Assert.Throws<ArgumentException>(() => NetworkSimulation.FromProfile("nope", NetworkSimulationSide.Client));
        }

        [Fact]
        public void Profiles_HaveTheMissionValues_PerSide()
        {
            var clean = NetworkSimulation.FromProfile("clean", NetworkSimulationSide.Client);
            Require(!clean.IsActive, "clean must be inactive", clean.Describe());

            var lan = NetworkSimulation.FromProfile("lan", NetworkSimulationSide.Client);
            Require(lan.LatencyExactMs == 1.0 && lan.JitterExactMs == 0.5 && lan.LossChance == 0f, "lan", lan.Describe());

            var gate = NetworkSimulation.FromProfile("gate", NetworkSimulationSide.Client);
            Require(gate.LatencyExactMs == 50 && gate.JitterExactMs == 5 && Math.Abs(gate.LossChance - 0.02f) < 1e-6f, "gate", gate.Describe());

            var stress = NetworkSimulation.FromProfile("stress", NetworkSimulationSide.Server);
            Require(stress.LatencyExactMs == 90 && stress.JitterExactMs == 10 && Math.Abs(stress.LossChance - 0.02f) < 1e-6f, "stress", stress.Describe());

            var bad = NetworkSimulation.FromProfile("bad", NetworkSimulationSide.Client);
            Require(bad.LatencyExactMs == 125 && bad.JitterExactMs == 25 && Math.Abs(bad.LossChance - 0.05f) < 1e-6f, "bad", bad.Describe());
            Require(bad.BurstMeanLength > 1f && Math.Abs(bad.ReorderChance - 0.01f) < 1e-6f && Math.Abs(bad.DuplicateChance - 0.01f) < 1e-6f,
                "bad extras", bad.Describe());

            var blackout = NetworkSimulation.FromProfile("blackout", NetworkSimulationSide.Client);
            Require(blackout.Outages.Count == 2 && blackout.Outages[0].DurationSeconds == 1 && blackout.Outages[1].DurationSeconds == 5,
                "blackout outages", blackout.Describe());

            var thinServer = NetworkSimulation.FromProfile("thin", NetworkSimulationSide.Server);
            var thinClient = NetworkSimulation.FromProfile("thin", NetworkSimulationSide.Client);
            Require(thinServer.BandwidthBytesPerSecond == 8000, "thin caps the server send side at 64 kbit/s", thinServer.Describe());
            Require(thinClient.BandwidthBytesPerSecond == 0, "thin must not cap the client", thinClient.Describe());
        }

        [Fact]
        public void TheTwoSides_UseDifferentStreams()
        {
            var c = NetworkSimulation.FromProfile("gate", NetworkSimulationSide.Client, 5);
            var s = NetworkSimulation.FromProfile("gate", NetworkSimulationSide.Server, 5);
            Require(c.Seed != s.Seed, "client and server share a seed");
            Require(NetworkSimulation.FromProfile("gate", NetworkSimulationSide.Client, 5).Seed == c.Seed, "seed not repeatable");
        }

        [Fact]
        public void Environment_ProfileThenRoundTripOverrides()
        {
            WithEnvironment(() =>
            {
                var sim = NetworkSimulation.FromEnvironment(NetworkSimulationSide.Client, out string? warning);
                Require(warning is null, "unexpected warning", warning);
                // gate with the round trip overridden to 200 ms: each side takes 100 ms; loss stays 2 %.
                Require(sim.LatencyExactMs == 100 && sim.JitterExactMs == 5 && Math.Abs(sim.LossChance - 0.02f) < 1e-6f, "override", sim.Describe());
                Require(sim.Seed == NetworkSimRandomMixForTest(77, 1), "KRUT_NET_SEED not used", sim.Seed.ToString());
            },
            (NetworkSimulation.ProfileVariable, "gate"), (NetworkSimulation.LatencyVariable, "200"), (NetworkSimulation.SeedVariable, "77"));
        }

        [Fact]
        public void Environment_UnknownProfileWarns_AndBrokenValuesDoNotThrow()
        {
            WithEnvironment(() =>
            {
                var sim = NetworkSimulation.FromEnvironment(NetworkSimulationSide.Server, out string? warning);
                Require(warning is not null && warning.Contains("nope"), "no warning for an unknown profile", warning);
                Require(!sim.IsActive, "unknown profile must not switch anything on", sim.Describe());
            },
            (NetworkSimulation.ProfileVariable, "nope"), (NetworkSimulation.SeedVariable, "not a number"), (NetworkSimulation.LossVariable, "a lot"));
        }

        [Fact]
        public void Environment_OutagesAndBandwidthParse()
        {
            WithEnvironment(() =>
            {
                var sim = NetworkSimulation.FromEnvironment(NetworkSimulationSide.Server);
                Require(sim.Outages.Count == 2 && sim.Outages[0].StartSeconds == 2.5 && sim.Outages[1].DurationSeconds == 4, "outages", sim.Describe());
                Require(sim.BandwidthBytesPerSecond == 16_000, "KRUT_NET_BW_KBIT is kbit/s on the server", sim.Describe());
                var client = NetworkSimulation.FromEnvironment(NetworkSimulationSide.Client);
                Require(client.BandwidthBytesPerSecond == 0, "the cap must not reach the client");
            },
            (NetworkSimulation.OutagesVariable, "2.5:1, 10:4, junk, 5:-1"), (NetworkSimulation.BandwidthVariable, "128"));
        }

        [Fact]
        public void Environment_LegacyParameterlessReaderStillOneWay()
        {
            WithEnvironment(() =>
            {
                var sim = NetworkSimulation.FromEnvironment();
                Require(sim.LatencyMs == 150 && sim.JitterMs == 30 && Math.Abs(sim.LossChance - 0.02f) < 1e-6f, "legacy", sim.Describe());
                Require(sim.Describe() == "150 ms latency, 30 ms jitter, 2 % loss", "describe format", sim.Describe());
            },
            (NetworkSimulation.LatencyVariable, "150"), (NetworkSimulation.JitterVariable, "30"), (NetworkSimulation.LossVariable, "0,02"));
        }

        [Fact]
        public void Describe_ListsTheExtras()
        {
            var sim = NetworkSimulation.FromProfile("blackout", NetworkSimulationSide.Client);
            Require(sim.Describe() == "50 ms latency, 5 ms jitter, 2 % loss, outage 15+1 s, outage 30+5 s", "describe", sim.Describe());
            var thin = NetworkSimulation.FromProfile("thin", NetworkSimulationSide.Server);
            Require(thin.Describe().EndsWith("64 kbit/s cap"), "describe cap", thin.Describe());
        }

        [Fact]
        public void StatsAreOneInstance_SoPollingDoesNotAllocate()
        {
            var sim = NetworkSimulation.FromProfile("gate", NetworkSimulationSide.Client);
            var queue = new NetworkDelayQueue();
            Require(ReferenceEquals(queue.GetStats(), queue.GetStats()), "stats object changes between reads");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
                _ = queue.GetStats().Dropped;
            Require(GC.GetAllocatedBytesForCurrentThread() - before == 0, "reading the stats allocates");
            _ = sim;
        }

        // The same mixing the profile uses; kept here so the test fails if the seed derivation changes silently.
        private static ulong NetworkSimRandomMixForTest(ulong seed, ulong sideSalt)
        {
            ulong state = seed ^ (sideSalt * 0x9E3779B97F4A7C15UL);
            ulong z = state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>The simulator on a real session.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkSimulatorSessionTests
    {
        [Fact]
        public void ReliableMessage_SentInsideAnOutage_ArrivesAfterIt()
        {
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

            client.ApplySimulation(NetworkSimulation.Create(new NetworkSimulationOptions
            {
                Outages = { new NetworkOutage(0.0, 0.6) },
            }));

            var watch = SendChat(client, "held by the outage");
            Pump(0.3f, server, client);
            Require(received.Count == 0, "the message got through an outage", string.Join(",", received));
            Require(PumpUntil(3f, () => received.Count == 1, server, client), "the message never arrived after the outage");
            Require(watch.ElapsedMilliseconds >= 500, "the outage did not hold the message", $"{watch.ElapsedMilliseconds} ms");
            Require(client.DroppedPacketCount >= 1, "the outage hit was not counted");
        }
    }
}
