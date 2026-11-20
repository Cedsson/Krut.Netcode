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
    /// <summary>The tick clock.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkClockTests
    {
        // The tick clock MaxCatchUpTicks is the difference between "the game paused for two seconds" and "the
        // game sent two seconds of replication in one frame". The two tests here cover both halves of that
        // guarantee: the clock releases exactly as many whole ticks as the elapsed time buys, and never more
        // than the cap however long the frame was.

        [Fact]

        public void Clock_UpdateReleasesRightTickCount()
        {
            // A session that listens to nothing: the clock is the only thing that moves, and Update should
            // still release exactly as many whole ticks as the time buys.
            using var session = NewCountingSession(tickRate: 30);
            session.Begin();

            for (int i = 0; i < 6; i++)
                session.Update(CountingSession.TickIntervalSeconds(30));

            Require(session.TickCount == 6, "Update released the wrong number of ticks",
                $"{session.TickCount} (expected 6)");

            // A step that is short of a whole tick must not release anything.
            session.Update(0.001f);
            Require(session.TickCount == 6, "An incomplete tick step released a tick",
                $"now {session.TickCount}");
        }

        [Fact]

        public void Clock_LongFrameClampedByMaxCatchUpTicks()
        {
            using var session = NewCountingSession(tickRate: 30);
            session.Begin();

            // A frame of 5 seconds (alt-tab, load screen) would release 150 ticks.
            session.Update(5f);

            Require(session.TickCount == session.Config.MaxCatchUpTicks,
                "MaxCatchUpTicks did not clamp a long frame",
                $"{session.TickCount}, cap {session.Config.MaxCatchUpTicks}");
        }

        [Fact]
        public void Clock_CatchUpTelemetry_CountsReleasedAndClampedTicks_IncludingExtremeDelta()
        {
            // Power-of-two tick rate (32 Hz => 1/32 s = 0.03125 exact in binary float) avoids rounding edge noise.
            using var session = NewCountingSession(tickRate: 32);
            session.Begin();

            session.Update(CountingSession.TickIntervalSeconds(32));
            Assert.Equal(1, session.LastReleasedTickCount);
            Assert.Equal(0, session.LastClampedTickCount);
            Assert.Equal(0L, session.CatchUpClampCount);
            Assert.Equal(0L, session.TotalClampedTicks);

            // 5 seconds @ 32 Hz = 160 ticks -> MaxCatchUpTicks (5) released, 155 clamped.
            session.Update(5f);
            Assert.Equal(session.Config.MaxCatchUpTicks, session.LastReleasedTickCount);
            Assert.Equal(155, session.LastClampedTickCount);
            Assert.Equal(1L, session.CatchUpClampCount);
            Assert.Equal(155L, session.TotalClampedTicks);

            // Extreme finite delta should not overflow or produce NaN TickProgress.
            session.Update(float.MaxValue);
            Assert.Equal(int.MaxValue, session.LastClampedTickCount);
            Assert.Equal(2L, session.CatchUpClampCount);
            Assert.True(float.IsFinite(session.TickProgress) && session.TickProgress >= 0f && session.TickProgress < 1f);
        }

    }
}
