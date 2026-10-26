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
    /// <summary>Diagnostics text and message pool allocation.</summary>
    [Collection(NetworkTestCollection.Name)]
    public class NetworkDiagnosticsTests
    {
        // Diagnostics The status text is the first thing pasted into a bug report, so it has to carry role,
        // state, tick, per-peer ping/round-trip, traffic, message health and the simulation state.

        // The single-line guarantee is the non-obvious part: this text is meant for a HUD row or a log prefix,
        // and a second line would push whatever follows it off the bottom of the screen.
        [Fact]

        public void Diagnostics_Describe()
        {
            using var session = NewCountingSession(tickRate: 30);
            session.Begin();

            // One single line, unbroken - for a HUD row or a log prefix.
            string text = session.Describe();
            Require(!text.Contains('\n', StringComparison.Ordinal), "Describe() returned more than one line", $"'{text}'");
            Require(text.Contains("server", StringComparison.Ordinal), "Describe() did not state the role", $"'{text}'");
            Require(text.Contains("0 peers", StringComparison.Ordinal), "Describe() did not state the peer count",
                $"'{text}'");

            // After a few ticks the tick and the rate must show. That is exactly the case
            // Clock_UpdateReleasesRightTickCount exists for.
            for (int i = 0; i < 5; i++)
                session.Update(CountingSession.TickIntervalSeconds(30));

            Require(session.Describe().Contains("5 @ 30 Hz", StringComparison.Ordinal),
                "Describe() did not state tick and rate", session.Describe());
        }

        // Pool Message instances are rented from a pool, not allocated.

        [Fact]

        public void Pool_DoesNotAllocate()
        {
            // The strings are built BEFORE the measurement, so the only thing that can allocate during it is a
            // new message instance. Assigning .Text a new value every time would allocate about as much
            // (roughly 40 bytes) as a missed pool hit (roughly 24 bytes), and the test could not tell them
            // apart.
            const int count = 1000;
            var texts = new string[count];
            for (int i = 0; i < count; i++)
                texts[i] = "message " + i;

            // Warmup: the first time a code path runs it is JIT:ed, and that
            // allocation is not part of what is measured.
            for (int i = 0; i < 200; i++)
            {
                var warmup = NetworkMessagePool<ChatMessage>.Rent();
                warmup.Text = texts[i];
                NetworkMessages.Recycle(warmup);
            }

            // <see cref="GC.GetTotalAllocatedBytes"/> counts ALL allocations on the thread, not the test's own.
            // Even with allocation-free code a couple of bytes can come in from outside - xunit's own
            // bookkeeping, a finalizer, another thread. Each extra round is cheap (1000 rented messages,
            // microseconds), while every remaining chance of a finalizer randomly landing in exactly that
            // window is a failure of what is being measured.
            const int rounds = 10;
            long best = long.MaxValue;
            for (int round = 0; round < rounds; round++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long before = GC.GetTotalAllocatedBytes(precise: true);
                for (int i = 0; i < count; i++)
                {
                    var message = NetworkMessagePool<ChatMessage>.Rent();
                    message.Text = texts[i];
                    NetworkMessages.Recycle(message);
                }

                long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                if (allocated < best)
                    best = allocated;
            }

            // A pool hit costs nothing at all. A miss costs one instance every time (about 24 bytes), so 1000
            // misses give about 24 kB. The 4 kB bound therefore has a wide margin below a single pair of misses
            // and still catches the real bug (the pool not being used).
            Require(best < 4096, "The message pool allocates per message",
                $"{best} bytes for {count} rounds (best of {rounds} rounds)");
        }
    }
}
