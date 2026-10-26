using System;
using Krut.Netcode;
using Xunit;

namespace Krut.Netcode.Tests
{
    /// <summary>
    /// Tests for <see cref="NetworkTimeSync"/> - the client side's estimate of the server's tick and of how
    /// many ticks ahead the client should run.
    /// </summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkTimeSyncTests
    {
        private const int TickRate = 60;

        /// <summary>A "real" delay: 20 ms round trip, that is 10 ms one way.</summary>
        private const double BaseRoundTrip = 0.020;

        /// <summary>
        /// Feeds one TimeSync sample through a modelled link. Such a sample is exactly what makes the
        /// <c>rtt/2</c> correction harmful, so it is modelled rather than described in a comment.
        /// </summary>
        private static void Feed(NetworkTimeSync sync, int clientTick, double roundTripSeconds,
            int serverTicksAtResponse, int trueOffset)
        {
            sync.RecordRequest(clientTick);
            sync.RecordResponse(clientTick + trueOffset + serverTicksAtResponse, roundTripSeconds);
        }

        /// <summary>
        /// How many ticks the server's clock gets through during a symmetric round trip at a given tick rate -
        /// the "right" value for a fresh packet.
        /// </summary>
        private static int SymmetricServerTicks(double roundTripSeconds) =>
            (int)Math.Round(roundTripSeconds * TickRate * 0.5);

        /// <summary>
        /// Perfect network: a fixed offset has to be estimated exactly, and the method must not "wobble" -
        /// every sample gives the same number, so the median is stable from the very first sample.
        /// </summary>
        [Fact]
        public void CleanNetwork_EstimatesOffsetExactly()
        {
            const int trueOffset = 500;
            var sync = new NetworkTimeSync(TickRate);

            AssertTrue(!sync.IsSynchronized, "A new time sync must not claim to be synchronized");
            AssertEqual(0, sync.TickOffset, "Without a sample the offset must be 0");

            for (int i = 0; i < 24; i++)
            {
                int clientTick = 1000 + i * 8;
                Feed(sync, clientTick, BaseRoundTrip, SymmetricServerTicks(BaseRoundTrip), trueOffset);

                // Already after the first sample the right number has to come out
                // - a client that waits half a window before guessing has already
                // lost half a second.
                AssertEqual(trueOffset, sync.TickOffset, $"Offset after sample {i + 1}");
            }

            AssertEqual(trueOffset, sync.TickOffset, "Offset after 24 samples");
            AssertEqual(16, sync.SampleCount, "The window should be full after more than 16 samples");

            // The time base is "server tick = client tick + offset".
            AssertEqual(1500, sync.ServerTickFor(1000), "ServerTickFor should add the offset to the client tick");
            AssertEqual(1000, sync.ClientTickFor(1500), "ClientTickFor should subtract the offset from the server tick");
            AssertEqual(499, sync.ServerTickFor(-1), "Negative ticks should be handled without an exception");
        }

        /// <summary>
        /// Jitter: randomized delays inside a normal range must not move the offset tick by tick. The test runs
        /// 200 samples and checks every single one - an estimate that is merely "reasonable on average" would
        /// jump around here, and it is exactly that jumping that shows up as vibrating cars/skeletons in the
        /// game. (That THIS test in particular does not distinguish mean from median is because the model is
        /// symmetric: every sample is then at most half a tick off the truth, so mean and median are per
        /// definition close together.
        /// </summary>
        [Fact]
        public void Jitter_KeepsOffsetStableOverManySamples()
        {
            const int trueOffset = 500;
            var sync = new NetworkTimeSync(TickRate);

            // A simple deterministic random number generator (LCG) - the test has
            // to give the same result every run, otherwise it is not a test.
            ulong state = 0x2545F4914F6CDD1DUL;
            int worstDeviation = 0;

            for (int i = 0; i < 200; i++)
            {
                // 15-45 ms round trip, that is +/-15 ms around 30 ms - fairly nasty
                // jitter, typical for wifi.
                state = state * 6364136223846793005UL + 1442695040888963407UL;
                double roundTrip = 0.015 + (state >> 33) / (double)(1UL << 31) * 0.030;

                Feed(sync, 2000 + i * 8, roundTrip, SymmetricServerTicks(roundTrip), trueOffset);

                int deviation = Math.Abs(sync.TickOffset - trueOffset);
                if (deviation > worstDeviation)
                    worstDeviation = deviation;

                AssertTrue(deviation <= 1,
                    $"The offset should stay within one tick of the truth, but sample {i + 1} (rtt " +
                    $"{roundTrip * 1000.0:0.0} ms) gave {sync.TickOffset} against {trueOffset}");
            }

            AssertTrue(worstDeviation <= 1, $"No sample run may deviate by more than one tick (worst deviation: {worstDeviation})");
            AssertEqual(trueOffset, sync.TickOffset, "Final offset after 200 jittery samples");
            AssertTrue(sync.JitterTicks > 0, "The test should actually have produced jitter");
            AssertTrue(sync.MinRoundTripSeconds < 0.030, "The minimum round trip should be under 30 ms in this model");
        }

        /// <summary>
        /// Spike packet: two in a row that take 10 times as long as normal. The test therefore compares the two
        /// methods on exactly the same sample series: <see cref="NetworkTimeSync.TickOffset"/> (median over the
        /// samples that survived the minimum-RTT filter) against
        /// <see cref="NetworkTimeSync.NaiveAverageOffsetTicks"/> (the mean of everything).
        /// </summary>
        [Fact]
        public void Spike_DoesNotMoveRobustOffset_ButMeanDrifts()
        {
            const int trueOffset = 500;
            // A small window of 4 samples: a spike pair is meant to amount to half the history, which is the
            // stressed but entirely realistic situation right after a wifi hiccup.
            var sync = new NetworkTimeSync(TickRate, sampleCapacity: 4);

            int clientTick = 3000;
            for (int i = 0; i < 2; i++)
            {
                Feed(sync, clientTick, BaseRoundTrip, SymmetricServerTicks(BaseRoundTrip), trueOffset);
                clientTick += 8;
            }

            AssertEqual(trueOffset, sync.TickOffset, "Offset after two healthy samples");
            AssertEqual(trueOffset, sync.NaiveAverageOffsetTicks, "The mean should be right too as long as the network is healthy");

            // The spike pair: 200 ms round trip (10x), but the server only got 1 tick.
            Feed(sync, clientTick, BaseRoundTrip * 10, 1, trueOffset);
            clientTick += 8;
            Feed(sync, clientTick, BaseRoundTrip * 10, 1, trueOffset);
            clientTick += 8;

            int robustError = Math.Abs(sync.TickOffset - trueOffset);
            int naiveError = Math.Abs(sync.NaiveAverageOffsetTicks - trueOffset);

            AssertEqual(0, robustError,
                $"The robust offset should stand completely still on a spike packet (got error {robustError}, " +
                $"rtt {(BaseRoundTrip * 10 * 1000):0} ms)");

            AssertTrue(naiveError >= 2,
                "The mean should drift noticeably from the same spike packet - if it does not, " +
                $"the test proves nothing (error {naiveError}, got {sync.NaiveAverageOffsetTicks} " +
                $"against {trueOffset})");

            // The core argument: the robust method has to be at least one tick
            // better than what a plain mean method gives on the same data.
            AssertTrue(naiveError >= robustError * 4 + 2,
                $"The robust error ({robustError}) should be much smaller than the mean's error ({naiveError})");

            // The latency comes from the MINIMUM round trip, so it should not be affected by the spikes either
            // - otherwise the client would get a second of lead and correct every time.
            AssertEqual(1, sync.LatencyTicks, "The latency should come from the minimum round trip, not the max");
            AssertEqual(3, sync.ClientLeadTicks, "The lead should be latency plus the extra margin");
        }

        /// <summary>
        /// The median against the mean, when the filter does NOT catch the spike in time. A sample sitting
        /// exactly at the threshold (<c>MaxRttRatio</c> times the minimum RTT) gets through, and then the
        /// server's clock is often already more than half a tick behind - which gives an offset that is wrong
        /// by a full <c>rtt/2</c>. That is a sample measuring a queue, exactly like a spike, but one the
        /// threshold cannot tell apart from a healthy one. The mean is dragged along instead, and it is exactly
        /// this window that decides whether a client downloading two files at once ends up five ticks wrong in
        /// its time base.
        /// </summary>
        [Fact]
        public void Median_HoldsWhenFilterLetsTwoBadSamplesThrough()
        {
            const int trueOffset = 500;
            const double slowRoundTrip = 0.200; // 100 ms one way => 6 ticks

            // A window of 5: three healthy samples and two that sit at the
            // threshold.
            var sync = new NetworkTimeSync(TickRate, sampleCapacity: 5);
            int clientTick = 6000;

            for (int i = 0; i < 3; i++)
            {
                Feed(sync, clientTick, slowRoundTrip, SymmetricServerTicks(slowRoundTrip), trueOffset);
                clientTick += 8;
            }

            AssertTrue(sync.TickOffset == trueOffset, "The offset should be right after three healthy samples");

            // A sample whose server clock stood still despite 2x the minimum round trip. The rtt/2 correction
            // then pulls the offset straight down - but 2x the minimum round trip is exactly the threshold, so
            // the filter keeps the sample.
            for (int i = 0; i < 2; i++)
            {
                Feed(sync, clientTick, slowRoundTrip * 2, 0, trueOffset);
                clientTick += 8;
            }

            AssertEqual(5, sync.SampleCount, "All five samples should be inside the window");

            // The core argument: the median stands still, the mean drifts.
            AssertEqual(trueOffset, sync.TickOffset,
                "The median should stand completely still when two bad samples slip past the filter");

            AssertTrue(Math.Abs(sync.NaiveAverageOffsetTicks - trueOffset) >= 2,
                "The mean should drift noticeably from the two queued samples - if it does not, " +
                $"the test proves nothing (got {sync.NaiveAverageOffsetTicks} against {trueOffset})");
        }

        /// <summary>
        /// The lead: <c>ClientLeadTicks = RTT/2 + buffer</c> (sec 6). The test checks both terms and that a
        /// changed margin takes effect immediately - it is that value which decides how aggressive the client's
        /// prediction is allowed to be.
        /// </summary>
        [Fact]
        public void ClientLeadTicks_IsHalfRttPlusConfiguredMargin()
        {
            const int trueOffset = 1200;
            const double roundTrip = 0.100; // 50 ms one way => 3 ticks at 60 Hz

            var sync = new NetworkTimeSync(TickRate, extraLeadTicks: 2);

            for (int i = 0; i < 20; i++)
                Feed(sync, 4000 + i * 8, roundTrip, SymmetricServerTicks(roundTrip), trueOffset);

            AssertEqual(3, sync.LatencyTicks, "Half the round trip in ticks");
            AssertEqual(5, sync.ClientLeadTicks, "The lead should be latency plus the margin");

            // The client should therefore set its clock to the server's tick + 5.
            AssertEqual(1000 + 5, sync.ClientTickAheadOf(1000), "The client's tick should be the lead ahead of the server's");

            // The game may adjust the margin at runtime - e.g. lower it in a final
            // that should feel snappier.
            sync.ExtraLeadTicks = 0;
            AssertEqual(3, sync.ClientLeadTicks, "Without a margin the lead should be just the latency");

            sync.ExtraLeadTicks = 6;
            AssertEqual(9, sync.ClientLeadTicks, "A bigger margin should give a bigger lead");
            AssertEqual(1009, sync.ClientTickAheadOf(1000), "ClientTickAheadOf should follow the margin");
        }

        /// <summary>
        /// Clock drift: the server's and the client's clocks never run equally fast, so the offset changes
        /// slowly over a match. The estimate should follow along, but only after the window has rolled over - a
        /// threshold that never catches up leaves the client 5 ticks wrong after an hour.
        /// </summary>
        [Fact]
        public void ClockDrift_IsFollowedAfterOneWindow()
        {
            var sync = new NetworkTimeSync(TickRate, sampleCapacity: 8);
            int clientTick = 5000;

            for (int i = 0; i < 8; i++)
                Feed(sync, clientTick + i * 8, BaseRoundTrip, SymmetricServerTicks(BaseRoundTrip), 500);

            AssertEqual(500, sync.TickOffset, "Offset before drift");

            // The server runs 2 ticks ahead over the coming samples.
            for (int i = 0; i < 8; i++)
                Feed(sync, clientTick + (8 + i) * 8, BaseRoundTrip, SymmetricServerTicks(BaseRoundTrip), 502);

            AssertEqual(502, sync.TickOffset, "The offset should follow the drift after a full window");
            AssertEqual(502, sync.ServerTickFor(0), "ServerTickFor should mirror the following drift");
        }

        /// <summary>
        /// A PERSISTENT worsening of the network (the player goes from wifi to mobile, 20 ms becomes 60 ms) has
        /// to be followed. The minimum RTT is taken over the WINDOW, not over the whole session - otherwise a
        /// client that once had 15 ms would be stuck at 0 extra margin for the rest of the match, and one that
        /// starts slowly would never get its lead widened.
        /// </summary>
        [Fact]
        public void PersistentLatencyWorsening_IsFollowedAfterOneWindow()
        {
            const int trueOffset = 500;
            var sync = new NetworkTimeSync(TickRate, sampleCapacity: 8);
            int clientTick = 7000;

            for (int i = 0; i < 8; i++)
            {
                Feed(sync, clientTick, BaseRoundTrip, SymmetricServerTicks(BaseRoundTrip), trueOffset);
                clientTick += 8;
            }

            AssertEqual(1, sync.LatencyTicks, "A 20 ms round trip gives one tick of latency");
            AssertEqual(3, sync.ClientLeadTicks, "The lead with a 20 ms round trip");

            // The network worsens permanently: 60 ms round trip (3x), symmetric.
            const double slowRoundTrip = 0.060;
            for (int i = 0; i < 8; i++)
            {
                Feed(sync, clientTick, slowRoundTrip, SymmetricServerTicks(slowRoundTrip), trueOffset);
                clientTick += 8;
            }

            AssertEqual(2, sync.LatencyTicks, "A 60 ms round trip gives two ticks of latency");
            AssertEqual(4, sync.ClientLeadTicks, "The lead should follow the new latency");
            AssertEqual(trueOffset, sync.TickOffset, "The offset should still be the same - only the delay changed");
        }

        /// <summary>
        /// Without responses the time sync has to say out loud that it does not know, and a response without a
        /// request has to be ignored (the server may answer something the client never asked for, e.g. after a
        /// reconnect).
        /// </summary>
        [Fact]
        public void WithoutResponses_IsClearlyUnsynchronized()
        {
            var sync = new NetworkTimeSync(TickRate);

            AssertTrue(!sync.IsSynchronized, "No sync without a sample");
            AssertEqual(0, sync.TickOffset, "No offset without a sample");
            AssertEqual(0, sync.LatencyTicks, "No latency without a sample");
            AssertEqual(2, sync.ClientLeadTicks, "Without a measurement the lead should be just the margin");
            AssertTrue(sync.ToString().Contains("waiting"),
                $"ToString should say that it is waiting, but said: {sync.ToString()}");

            AssertTrue(!sync.RecordResponse(999, 0.02), "A response without a request should be ignored");
            AssertTrue(!sync.IsSynchronized, "An unexpected response should not sync the client");

            // A request must be possible to send several times without losing any
            // of them - but only as long as the window lasts.
            for (int i = 0; i < sync.SampleCapacity; i++)
                sync.RecordRequest(1000 + i);

            AssertEqual(sync.SampleCapacity, sync.PendingRequestCount, "All requests should be inside the window");
            sync.RecordRequest(2000);
            AssertEqual(sync.SampleCapacity, sync.PendingRequestCount,
                "An overflowing window should drop the oldest request, not the newest");
        }

        /// <summary>
        /// <see cref="NetworkTimeSync.Reset"/> must forget old samples on a reconnect - otherwise a request
        /// from the old path matches a response from the new one, and the offset gets a meaningless spike.
        /// </summary>
        [Fact]
        public void Reset_ForgetsOldSamples()
        {
            var sync = new NetworkTimeSync(TickRate);

            for (int i = 0; i < 12; i++)
                Feed(sync, 1000 + i * 8, BaseRoundTrip, SymmetricServerTicks(BaseRoundTrip), 500);

            AssertTrue(sync.IsSynchronized, "The time sync should be synchronized after 12 samples");

            sync.Reset();

            AssertTrue(!sync.IsSynchronized, "After a reset the time sync must no longer claim to be synchronized");
            AssertEqual(0, sync.SampleCount, "The samples should be forgotten");
            AssertEqual(0, sync.TickOffset, "The offset should be forgotten");
            AssertEqual(0, sync.PendingRequestCount, "Unanswered requests should be forgotten");

            // The old request that was left over must not be able to answer.
            AssertTrue(!sync.RecordResponse(4242, 0.02), "After a reset an old request must not be able to give a sample");
        }

        /// <summary>
        /// Garbage from the network (a negative or non-finite value from a broken response) must not destroy
        /// the minimum or the median.
        /// </summary>
        [Fact]
        public void Garbage_IsIgnoredWithoutCorruptingTheEstimate()
        {
            const int trueOffset = 500;
            var sync = new NetworkTimeSync(TickRate, sampleCapacity: 4);

            Feed(sync, 1000, BaseRoundTrip, SymmetricServerTicks(BaseRoundTrip), trueOffset);
            sync.RecordRequest(1008);
            sync.RecordResponse(1008 + trueOffset + 1, double.NaN);
            sync.RecordRequest(1016);
            sync.RecordResponse(1016 + trueOffset + 1, -1d);
            sync.RecordRequest(1024);
            sync.RecordResponse(1024 + trueOffset + 1, double.PositiveInfinity);

            AssertTrue(sync.IsSynchronized, "A valid sample should still have come in");
            AssertEqual(trueOffset, sync.TickOffset, "Garbage samples must not affect the offset");
            AssertEqual(1, sync.SampleCount, "Garbage samples must not count as samples");
        }

        /// <summary>
        /// <see cref="NetworkTimeSync.ShouldProbe"/> has to keep the network quiet in normal operation without
        /// blocking the very first sync.
        /// </summary>
        [Fact]
        public void ShouldProbe_SendsOftenFirstAndRarelyLater()
        {
            var sync = new NetworkTimeSync(TickRate);

            int firstWindow = 0;
            for (int tick = 0; tick < 40; tick++)
            {
                if (sync.ShouldProbe(tick))
                    firstWindow++;
            }

            AssertTrue(firstWindow > 5,
                $"Before the first response the client should try often (sent {firstWindow} times over 40 ticks)");

            // Now the server looks like it is answering: from here on the cadence should be the recommended
            // one, that is one probe per RecommendedProbeIntervalTicks.
            Feed(sync, 100, BaseRoundTrip, SymmetricServerTicks(BaseRoundTrip), 500);

            int steadyWindow = 0;
            for (int tick = 0; tick < 400; tick++)
            {
                if (sync.ShouldProbe(tick))
                {
                    steadyWindow++;

                    // Answer straight away, as the server would.
                    sync.RecordRequest(tick);
                    AssertTrue(sync.RecordResponse(tick + 500 + 1, BaseRoundTrip), "The server's response should be recorded");
                }
            }

            int expected = 400 / sync.RecommendedProbeIntervalTicks;
            AssertEqual(expected, steadyWindow, "In normal operation one probe should be sent per recommended interval");
            AssertTrue(sync.TickOffset == 500, "The offset should survive a long run of synchronized samples");
        }

        /// <summary>
        /// Invalid constructor values have to give clear errors - the tick rate is what makes the conversion
        /// into ticks meaningful, so 0 or less would silently give a time sync that converts everything to 0.
        /// </summary>
        [Fact]
        public void InvalidArguments_ThrowClearErrors()
        {
            // A tick rate of 0 has to throw.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { var broken = new NetworkTimeSync(0); GC.KeepAlive(broken); });

            // Zero samples in the window has to throw.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => { var broken = new NetworkTimeSync(30, sampleCapacity: 0); GC.KeepAlive(broken); });

            // A negative margin is clamped instead of throwing - it comes from the
            // game's settings, not from a programming error.
            var sync = new NetworkTimeSync(TickRate, extraLeadTicks: -3);
            AssertEqual(0, sync.ExtraLeadTicks, "A negative margin should be clamped to zero");
        }

        private static void AssertTrue(bool condition, string explanation) => Assert.True(condition, explanation);

        private static void AssertEqual(int expected, int actual, string explanation) =>
            Assert.True(expected == actual, $"{explanation} - expected {expected}, got {actual}");
    }
}