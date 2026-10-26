using System;

namespace Krut.Netcode
{
    /// <summary>
    /// The session's time in whole ticks, independent of the frame rate. All network replication normally sends
    /// one state packet per tick, so the replication becomes even and the bandwidth predictable instead of
    /// following the game's FPS (which varies, after all). That matters for determinism: the sub-tick remainder
    /// is what makes the tick boundaries drift relative to wall-clock time instead of snapping to a 33.3 ms
    /// grid, so a client at 144 Hz and a server at 30 Hz still agree about which tick they are in, and the
    /// drift is spread evenly instead of arriving as a visible stutter every few seconds.
    /// </summary>
    public sealed class NetworkClock
    {
        /// <summary>Max number of ticks a single frame is allowed to release, even if the time jumped (see <see cref="NetworkConfig.MaxCatchUpTicks"/>).</summary>
        private readonly int _maxCatchUpTicks;

        /// <summary>
        /// Seconds of un-released time. Always below <see cref="TickInterval"/> after <see cref="Advance"/> -
        /// that invariant is what makes <see cref="TickProgress"/> a valid 0-1 fraction and not an arbitrary
        /// count.
        /// </summary>
        private double _accumulator;

        /// <param name="maxCatchUpTicks">
        /// How many ticks a frame is allowed to catch up before the rest of the time is thrown away. Values
        /// below 1 are clamped to 1, so a frame never releases zero ticks - a clock that could stall would
        /// silently desynchronise the session from the game's time.
        /// </param>
        public NetworkClock(int tickRate, int maxCatchUpTicks)
        {
            TickRate = Math.Max(1, tickRate);
            _maxCatchUpTicks = Math.Max(1, maxCatchUpTicks);
        }

        /// <summary>Ticks per second.</summary>
        public int TickRate { get; }

        /// <summary>Seconds per tick - how far the clock is moved in <see cref="Advance"/> per tick.</summary>
        public float TickInterval => 1f / TickRate;

        /// <summary>Number of whole ticks since the clock was created - the number sent in messages and compared between peers.</summary>
        public int Tick { get; private set; }

        /// <summary>
        /// Where in the ongoing tick we are, 0-1 - for interpolation between ticks. The exact range is [0, 1).
        /// 0 is returned at the instant a tick is released, and the value grows towards 1 as the next tick
        /// approaches; it never reaches 1, because the moment <c>_accumulator</c> would reach
        /// <see cref="TickInterval"/> the <see cref="Advance"/> loop consumes it and increments
        /// <see cref="Tick"/>.
        /// </summary>
        public float TickProgress => (float)(_accumulator * TickRate);

        /// <summary>The session's time in seconds (tick * tick interval), comparable between server and client.</summary>
        public double Time => Tick * TickInterval;

        /// <summary>
        /// Advances time and calls <paramref name="onTick"/> once per whole tick that has passed. Why catch-up
        /// is clamped, and what a clamped frame means for simulation determinism. Without it, a 3 second
        /// alt-tab releases 90 ticks at 30 Hz: 90 <c>OnTick</c> calls, 90 state packets per peer, and the
        /// server - which is very likely still running at 30 Hz - now has to absorb a 90-tick burst while every
        /// other client sees a 3 second freeze followed by 90 snapshots it will never use. The price is a
        /// deliberate, bounded desynchronisation, and this is the part a game has to know about: after a
        /// clamped frame the session's <see cref="Time"/> is behind real time by the discarded remainder,
        /// permanently - the clock does not try to make the time up again, because trying to make it up again
        /// is exactly the catch-up loop that was just clamped. For a server that is harmless (its tick number
        /// is the authority, and clients measure the offset against it with <see cref="NetworkTimeSync"/>). A
        /// game whose own physics must stay deterministic across a hitch should therefore not derive its
        /// simulation step from <paramref name="deltaSeconds"/>: the frame is the unit of loss here, not a
        /// fixed step.
        /// </summary>
        public void Advance(float deltaSeconds, Action onTick)
        {
            if (deltaSeconds > 0f)
            {
                _accumulator += deltaSeconds;
                int released = 0;
                while (_accumulator >= TickInterval && released < _maxCatchUpTicks)
                {
                    _accumulator -= TickInterval;
                    Tick++;
                    released++;
                    onTick();
                }

                if (_accumulator >= TickInterval)
                    _accumulator %= TickInterval;
            }
        }

        /// <summary>
        /// Jumps the clock forward without releasing ticks - e.g. when a client learns the server's tick in
        /// <see cref="NetworkMessageType.Welcome"/> and wants to be in the same time base. Starting at 0 makes
        /// the first post-jump tick behave exactly like every later one.
        /// </summary>
        /// <param name="tick">
        /// The tick to jump to. Negative values are clamped to 0, because a negative tick number is never a
        /// valid position in a session.
        /// </param>
        public void ResetTo(int tick)
        {
            Tick = Math.Max(0, tick);
            _accumulator = 0d;
        }
    }
}
