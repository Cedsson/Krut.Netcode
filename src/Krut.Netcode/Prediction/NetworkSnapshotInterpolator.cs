using System;

namespace Krut.Netcode
{
    /// <summary>
    /// What <see cref="NetworkSnapshotInterpolator{TState}.TrySample(int, out TState)"/> did with its output.
    /// </summary>
    public enum NetworkSampleMode
    {
        /// <summary>The buffer is empty - no output at all (<c>TrySample</c> returns false).</summary>
        Empty,

        /// <summary>The state exists at exactly the requested tick.</summary>
        Exact,

        /// <summary>Interpolated between two snapshots around the requested tick - the normal case.</summary>
        Interpolated,

        /// <summary>Computed forwards from the two most recent snapshots (dead reckoning).</summary>
        Extrapolated,

        /// <summary>Holding the last known state: the buffer isn't full yet, or the extrapolation ran out.</summary>
        Held,
    }

    /// <summary>
    /// Interpolator for <b>other</b> units: the client draws 2-3 snapshot
    /// intervals <i>backwards</i> in time and fills the gap with a linear (or
    /// quaternion) interpolation between the two snapshots that surround the
    /// moment in question.
    /// <para>
    /// <b>Why is it drawn in the past?</b> Two reasons, and the second one is the
    /// whole point:
    /// </para>
    /// <list type="number">
    /// <item>Interpolation <i>needs two</i> samples. Drawing exactly "now" leaves
    /// only the most recent packet - that alone is not enough, and as soon as one
    /// packet is lost there is <i>nothing</i> to draw until the next one arrives.
    /// Then the unit jumps a whole snapshot interval, and a car that is seen to
    /// jump is worse than a car that is seen 70 ms late. Sitting 2-3 intervals
    /// back there is almost always a pair of snapshots around the moment in
    /// question, even if one of them was lost.</item>
    /// <item>Jitter and packet loss are not polite. Without the backwards buffer,
    /// every lost packet is a visible jolt and every early packet is a visible
    /// bump. With it, both are invisible: because the moment in question lies in
    /// the past, such packets only have time to arrive before they are needed.</item>
    /// </list>
    /// <para>
    /// The price is the classic one: an opponent shown where it <i>was</i> is 2-3
    /// intervals wrong in a close fight. That is deliberate - it is the same
    /// trade-off every networked shooter has made for twenty years, and it is the
    /// right one for remotely controlled units.
    /// </para>
    /// <para>
    /// <b>Extrapolation as an escape hatch</b> (dead reckoning,
    /// future-networking sec 7.3): when the buffer's last snapshot is still "the
    /// future" - i.e. the network is so slow that the server's most recent tick
    /// hasn't even arrived - a linear extension of the two most recent snapshots
    /// is drawn instead, for at most
    /// <see cref="NetworkSnapshotInterpolator{TState}.MaxExtrapolationTicks"/>
    /// ticks. Past that limit the last known state is held: a guess that guesses
    /// off in the wrong direction is worse than one that stands still, and the
    /// extrapolation is in addition a <b>lie</b> - it must never be forwarded to
    /// anyone else (neither the server nor other clients), only shown.
    /// </para>
    /// <para>
    /// <b>The render delay, in exact terms.</b> Whatever the game asks, the sample
    /// handed back is always for
    /// <c>currentTick + TickProgress - DelayTicks</c>. That is the whole render
    /// delay: <c>DelayTicks / tickRate</c> seconds, i.e. 33-50 ms for 2-3 ticks
    /// at 60 Hz and 67-100 ms at 30 Hz. It is not "about two snapshots" - it is
    /// a fixed number of ticks, and the snapshot rate only decides how many
    /// snapshots happen to sit inside it.
    /// </para>
    /// <para>
    /// <b>Why that number has to cover the lead as well.</b> The client runs its
    /// own clock <see cref="NetworkTimeSync.ClientLeadTicks"/> ticks AHEAD of the
    /// server, and the snapshots it draws are stamped with <i>server</i> ticks.
    /// So the newest snapshot available is roughly
    /// <c>clientTick - ClientLeadTicks - snapshotOneWayDelay</c>, and the render
    /// point is <c>clientTick - DelayTicks</c>. The gap between them - the margin
    /// the interpolation actually has - is therefore
    /// <c>ClientLeadTicks + snapshotOneWayDelay - DelayTicks</c> ticks. Worked
    /// out at 60 Hz with a 100 ms RTT: <c>ClientLeadTicks</c> is
    /// round(0.100 x 60 x 0.5) + 2 = 3 + 2 = 5 ticks, the snapshot stream is
    /// about one 50 ms half-RTT = 3 ticks behind, so the newest snapshot sits 8
    /// ticks behind the client's clock and <c>DelayTicks = 3</c> renders 5 ticks
    /// behind that. The visible staleness of a remote unit is therefore roughly
    /// 8 + 3 = 11 ticks ~ 183 ms, not 3 ticks - the lead is part of it, and
    /// <c>DelayTicks</c> is only the part this class controls.
    /// </para>
    /// <para>
    /// If that margin goes negative - <c>DelayTicks</c> smaller than the lead
    /// plus the snapshot delay - every single frame lands in the extrapolation
    /// branch instead, and then, once
    /// <see cref="NetworkSnapshotInterpolator{TState}.MaxExtrapolationTicks"/>
    /// runs out, in the <c>Held</c> branch. Nothing throws and nothing is logged:
    /// the units just stop moving. That is why
    /// <see cref="NetworkSnapshotInterpolator{TState}.LastSampleMode"/> exists and
    /// why it belongs in a debug overlay - it is the only symptom.
    /// </para>
    /// <para>
    /// <b>How deep the buffer has to be.</b> Interpolating at the render point
    /// needs one snapshot at or before it and one after it, so the ring must span
    /// at least <c>DelayTicks + 2</c> entries. The constructor's
    /// <c>Math.Max(4, capacity)</c> is exactly that floor for
    /// <c>DelayTicks = 2</c>; for 3 ticks, 5 would be the minimum. The default
    /// 32 leaves room for a burst of snapshots and for
    /// <c>DelayTicks</c> to be raised at runtime without reallocating.
    /// </para>
    /// <para>
    /// The class is standalone from <see cref="NetworkSession"/>: it takes
    /// snapshots with <see cref="Push"/> and an integer (or a float) for the
    /// point in time, so a game can feed it from any session, and a dedicated
    /// server can run several without them seeing each other.
    /// </para>
    /// </summary>
    /// <typeparam name="TState">The game's own state struct (must be unmanaged, i.e. primitives only).</typeparam>
    public sealed class NetworkSnapshotInterpolator<TState>
        where TState : unmanaged
    {
        /// <summary>The snapshot ring; index 0 is the oldest, ticks strictly increase toward the end.</summary>
        private readonly NetworkStateRing<NetworkStateEntry<TState>> _snapshots;

        /// <summary>The render delay <see cref="TrySample(int, out TState)"/> actually used last time (diagnostics).</summary>
        private double _lastRenderTick = double.NaN;

        /// <summary>Number of snapshots that arrived too late (UDP doesn't order) and were discarded.</summary>
        private int _lateCount;

        /// <summary>Number of snapshots that overwrote an older entry in the ring (a burst of snapshots).</summary>
        private int _overwrittenCount;

        public NetworkSnapshotInterpolator(int delayTicks, int capacity = NetworkStateRing<NetworkStateEntry<TState>>.DefaultCapacity)
        {
            DelayTicks = delayTicks;
            _snapshots = new NetworkStateRing<NetworkStateEntry<TState>>(
                Math.Max(4, capacity),
                static entry => entry.Tick);
        }

        /// <summary>Backing field for <see cref="DelayTicks"/>; never negative, so the subtraction in <see cref="RenderTickFor"/> can't run backwards.</summary>
        private int _delayTicks;

        /// <summary>
        /// The render delay in ticks. Unit: ticks, not milliseconds - the game gets milliseconds by dividing by
        /// its tick rate.
        /// </summary>
        public int DelayTicks
        {
            get => _delayTicks;
            set => _delayTicks = value < 0 ? 0 : value;
        }

        /// <summary>
        /// How many ticks it is allowed to guess forwards when snapshots are missing. 2-3 is the normal value:
        /// one tick is extrapolated from one tick with high probability, three past the limit where a single
        /// lost packet still helps, ten only makes a fast car fly into a wall. 0 means extrapolation is off
        /// (the client then holds the last known state instead of guessing).
        /// </summary>
        public int MaxExtrapolationTicks { get; set; } = 3;

        /// <summary>Number of snapshots currently in the buffer (see <see cref="Capacity"/> for why this is bounded).</summary>
        public int BufferedSnapshotCount => _snapshots.Count;

        /// <summary>How many snapshots the buffer holds. Fixed at construction; the ring overwrites the oldest entry when it is full.</summary>
        public int Capacity => _snapshots.Capacity;

        /// <summary>Number of snapshots that arrived too late and were discarded - a measure of how much the network shuffles packets.</summary>
        public int LateSnapshotCount => _lateCount;

        /// <summary>Number of snapshots that overwrote an older entry - should be 0 in a healthy match.</summary>
        public int OverwrittenSnapshotCount => _overwrittenCount;

        /// <summary>Which mode the most recent sampling landed in (see <see cref="NetworkSampleMode"/>).</summary>
        public NetworkSampleMode LastSampleMode { get; private set; } = NetworkSampleMode.Empty;

        /// <summary>The floating-point point in time (in ticks) the most recent sampling actually produced a value for.</summary>
        public double LastRenderTick => _lastRenderTick;

        /// <summary>
        /// How far back <paramref name="currentTick"/> renders - the point in time the game should ask about.
        /// </summary>
        /// <param name="currentTick">The session's current tick. Unit: ticks.</param>
        /// <returns>The render tick, i.e. <c>currentTick - DelayTicks</c>. Unit: ticks.</returns>
        public int RenderTickFor(int currentTick) => currentTick - DelayTicks;

        /// <summary>The function that blends two states.</summary>
        public Func<TState, TState, float, TState> Lerp { get; set; } = NetworkStateBlend.Lerp<TState>;

        /// <summary>The function that guesses a state forwards.</summary>
        public Func<TState, TState, float, TState> Extrapolate { get; set; } = NetworkStateBlend.Extend<TState>;

        /// <summary>Receives a snapshot: the unit's state at exactly <paramref name="tick"/>.</summary>
        /// <param name="tick">
        /// The tick the state applies to. Unit: ticks; must be strictly greater than every tick already
        /// buffered.
        /// </param>
        public void Push(int tick, in TState state)
        {
            // wasFull is read BEFORE the push: the ring is full the moment the last entry is added, so
            // afterwards "full" cannot be told apart from "just became full".
            bool wasFull = _snapshots.Count >= _snapshots.Capacity;
            bool stored = _snapshots.Push(tick, new NetworkStateEntry<TState>(tick, state));
            if (!stored)
                _lateCount++;
            else if (wasFull)
                _overwrittenCount++;
        }

        /// <summary>
        /// What should be drawn now? <paramref name="currentTick"/> is the session's tick
        /// (<see cref="NetworkSession.CurrentTick"/>); the interpolator answers with the state
        /// <see cref="DelayTicks"/> ticks back, interpolated between the two snapshots that surround that point
        /// in time.
        /// </summary>
        /// <param name="currentTick">The session's current tick. Unit: ticks.</param>
        public bool TrySample(int currentTick, out TState state) =>
            TrySampleTime(currentTick - DelayTicks, out state, out _);

        /// <summary>
        /// Same as <see cref="TrySample(int, out TState)"/>, but also says how the value was produced.
        /// </summary>
        /// <param name="currentTick">The session's current tick. Unit: ticks.</param>
        /// <param name="mode">
        /// How the value was produced - see <see cref="NetworkSampleMode"/>, which is what makes this the
        /// version worth calling in a debug build.
        /// </param>
        public bool TrySample(int currentTick, out TState state, out NetworkSampleMode mode) =>
            TrySampleTime(currentTick - DelayTicks, out state, out mode);

        /// <summary>
        /// The variant a game should normally use: <paramref name="renderTick"/> may be fractional, so that the
        /// rendering can interpolate within a tick.
        /// </summary>
        /// <param name="renderTick">
        /// The point in time to draw, fractional. Unit: ticks; normally <c>CurrentTick + TickProgress -
        /// DelayTicks</c>.
        /// </param>
        public bool TrySampleTime(double renderTick, out TState state) =>
            TrySampleTime(renderTick, out state, out _);

        /// <summary>
        /// Same as <see cref="TrySampleTime(double, out TState)"/>, but also says how the value was produced.
        /// </summary>
        /// <param name="renderTick">The point in time to draw, fractional. Unit: ticks.</param>
        public bool TrySampleTime(double renderTick, out TState state, out NetworkSampleMode mode)
        {
            state = default;
            mode = NetworkSampleMode.Empty;
            _lastRenderTick = renderTick;

            if (_snapshots.IsEmpty)
            {
                LastSampleMode = NetworkSampleMode.Empty;
                return false;
            }

            int floorTick = (int)Math.Floor(renderTick);
            int lower = _snapshots.IndexOfTickAtOrBefore(floorTick);

            // The future in the buffer: the moment in question lies BEFORE the oldest we have.
            if (lower < 0)
            {
                state = _snapshots[0].Value;
                mode = NetworkSampleMode.Held;
                LastSampleMode = mode;
                return true;
            }

            int lowerTick = _snapshots[lower].Tick;

            // Exact hit: a snapshot for exactly this tick, and we asked about exactly that.
            if (lowerTick == floorTick && renderTick == floorTick)
            {
                state = _snapshots[lower].Value;
                mode = NetworkSampleMode.Exact;
                LastSampleMode = mode;
                return true;
            }

            if (lower + 1 < _snapshots.Count)
            {
                TState a = _snapshots[lower].Value;
                TState b = _snapshots[lower + 1].Value;
                int tickB = _snapshots[lower + 1].Tick;

                // The fraction between the two points, i.e. 0..1 - renderTick lies here by definition between
                // tickA and tickB. tickB - tickA can be greater than 1 if a snapshot was lost; then the
                // fraction is less than a tick, which is correct: the line 20->30 gives 150 at tick 25, and
                // that is what the "lost snapshot" test verifies.
                float t = (float)(renderTick - lowerTick) / (tickB - lowerTick);
                state = Lerp(a, b, t);
                mode = NetworkSampleMode.Interpolated;
                LastSampleMode = mode;
                return true;
            }

            // The last case: the moment in question lies after the newest snapshot, i.e. the network is slower
            // than the render delay (or a snapshot was lost).
            return TryExtrapolate(renderTick, out state, out mode);
        }

        /// <summary>
        /// Fetches a single snapshot out of the buffer (for games that want to draw development graphs or do
        /// their own things).
        /// </summary>
        /// <param name="tick">The tick to look up. Unit: ticks.</param>
        public bool TryGetSnapshot(int tick, out TState state)
        {
            int index = _snapshots.IndexOfTickAtOrBefore(tick);
            if (index < 0 || _snapshots[index].Tick != tick)
            {
                state = default;
                return false;
            }

            state = _snapshots[index].Value;
            return true;
        }

        /// <summary>Empties the buffer - new connection, new match, or a unit's respawn.</summary>
        public void Clear()
        {
            _snapshots.Clear();
            _lateCount = 0;
            _overwrittenCount = 0;
            LastSampleMode = NetworkSampleMode.Empty;
            _lastRenderTick = double.NaN;
        }

        /// <summary>
        /// Number of snapshots there are for ticks up to and including <paramref name="tick"/> - handy for a
        /// debug overlay.
        /// </summary>
        /// <param name="tick">The last tick to count. Unit: ticks.</param>
        public int SnapshotCountThrough(int tick)
        {
            int index = _snapshots.IndexOfTickAtOrBefore(tick);
            return index < 0 ? 0 : index + 1;
        }

        /// <summary>Dead reckoning: extend the line between the two most recent snapshots.</summary>
        private bool TryExtrapolate(double renderTick, out TState state, out NetworkSampleMode mode)
        {
            int newestIndex = _snapshots.Count - 1;
            int newestTick = _snapshots[newestIndex].Tick;
            TState newest = _snapshots[newestIndex].Value;

            // Two snapshots are needed to know the direction.
            if (_snapshots.Count < 2 || MaxExtrapolationTicks <= 0)
            {
                state = newest;
                mode = NetworkSampleMode.Held;
                LastSampleMode = mode;
                return true;
            }

            int previousIndex = newestIndex - 1;
            double alpha = renderTick - newestTick;
            if (alpha > MaxExtrapolationTicks)
            {
                state = newest;
                mode = NetworkSampleMode.Held;
                LastSampleMode = mode;
                return true;
            }

            // alpha is ticks past the NEWEST point; Extrapolate takes a fraction of the previous-to-newest interval
            // (0 = the older, 1 = the newest), so the ticks are divided by the gap between the two snapshots.
            TState previous = _snapshots[previousIndex].Value;
            int gapTicks = newestTick - _snapshots[previousIndex].Tick;
            state = Extrapolate(previous, newest, 1f + (float)(alpha / gapTicks));
            mode = NetworkSampleMode.Extrapolated;
            LastSampleMode = mode;
            return true;
        }
    }
}