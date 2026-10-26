using System;
using System.Globalization;

namespace Krut.Netcode
{
    /// <summary>
    /// The client side's time sync: estimates how the server's tick relates to
    /// the client's own tick, and how many ticks of lead the client should keep
    /// so that the server always has the client's input sitting in its jitter
    /// buffer. This is NTP-like measurement with a fixed tick as the timestamp
    /// (design: docs/guide.md section 6).
    /// <para>
    /// <b>Why measurement is needed.</b> The handshake only says the server's
    /// tick AT the connection, and that tick is wrong as soon as the packet
    /// arrives. Without continuous measurement the client's and the server's
    /// clocks drift apart with their respective tick errors, and after a few
    /// minutes the difference is so large that the client's input arrives after
    /// the server has already simulated the tick. The measurement is therefore
    /// not a luxury - it is the difference between playable and unplayable.
    /// </para>
    /// <para>
    /// <b>Why not a plain average.</b> A plain average is WRONG in a network
    /// with jitter. A single spike - a packet that for some reason gets stuck in
    /// a queue for 200 ms instead of 20 - gives a completely wrong estimate, and
    /// because the error is permanent (the clocks keep running away from each
    /// other) the client follows the server's tick incorrectly until the next
    /// measurement comes in. An average also shifts the offset on every new
    /// sample, even when everything is healthy, so the clock oscillates. The
    /// method here is instead the classic <i>minimum-RTT</i> filtering combined
    /// with a median over the samples that survive the filter - see
    /// <see cref="Recompute"/> for the whole mechanism.
    /// </para>
    /// <para>
    /// <b>How a game wires this up</b> (the library'sends no messages on the
    /// game's behalf, since the protocol has no TimeSync - see
    /// <c>NetworkMessageType</c>):
    /// <list type="number">
    /// <item>Create an instance with the session's tick rate:
    /// <c>new NetworkTimeSync(config.TickRate)</c>.</item>
    /// <item>Every frame (or every Nth frame, see
    /// <see cref="RecommendedProbeIntervalTicks"/>) call
    /// <c>RecordRequest(CurrentTick)</c> and send a TimeSync message on
    /// <see cref="NetworkChannels.Control"/> with that tick in it.</item>
    /// <item>The server replies (immediately, no simulation work) with the
    /// server's <i>current</i> tick and a copy of the client's tick.</item>
    /// <item>The client measures the round trip locally (e.g. with
    /// <see cref="Environment.TickCount64"/> or a <c>Stopwatch</c> around the
    /// call above) and calls
    /// <c>RecordResponse(clientTickAtSend, serverTick, roundTripSeconds)</c>.</item>
    /// <item>The client sets its own clock to
    /// <c>sync.ClientTickAheadOf(serverTick)</c> - i.e. the server's tick PLUS
    /// <see cref="ClientLeadTicks"/>. That makes the client a "client that runs
    /// ahead", exactly as sec 6 requires. The library's own
    /// <c>NetworkSession.ResetClockTo(int)</c> is <c>public</c> since it is part
    /// of <see cref="NetworkTimeSync"/>'s design, so the game can call it
    /// directly; if that ever changes to <c>internal</c>, the game has to expose
    /// its own surface (e.g. a public method on its session wrapper).</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class NetworkTimeSync
    {
        /// <summary>
        /// Default number of time sync samples in the window. 16 is enough to see through spike packets (one
        /// that happens to arrive while 15 others are healthy must not be able to move the estimate) while
        /// being long enough that a temporary degradation of the network doesn't change the time base every
        /// time.
        /// </summary>
        public const int DefaultSampleCapacity = 16;

        /// <summary>Default margin on top of half the RTT, in ticks.</summary>
        public const int DefaultExtraLeadTicks = 2;

        /// <summary>
        /// How large a share of the smallest measured RTT a sample may have to be counted in the offset
        /// calculation. 2.0 means that samples with up to twice the round-trip time of the best one so far are
        /// counted in and everything else (queues, spikes, retransmissions) is discarded.
        /// </summary>
        public const double MaxRttRatio = 2.0;

        /// <summary>Each sample's round-trip time, in seconds, in ring form.</summary>
        private readonly double[] _roundTrip;

        /// <summary>Each sample's raw offset estimate, in ticks, in ring form (may be fractional).</summary>
        private readonly double[] _offsets;

        /// <summary>The client's tick at send time, for every sent but unanswered sample.</summary>
        private readonly int[] _pendingTicks;

        /// <summary>Scratch space for the median calculation - reused so nothing is allocated per sample.</summary>
        private readonly double[] _medianScratch;

        /// <summary>Valid entries in the sample window: 0..<see cref="SampleCapacity"/>. Only entries 0.._sampleCount-1 are read.</summary>
        private int _sampleCount;

        /// <summary>Where the next sample is written: 0..<see cref="SampleCapacity"/>-1. Wraps to 0 when it reaches the capacity.</summary>
        private int _nextSample;

        /// <summary>Number of outstanding requests in the pending window: 0..<see cref="SampleCapacity"/>.</summary>
        private int _pendingCount;

        /// <summary>Index of the OLDEST outstanding request - the one <see cref="RecordResponse(int, double)"/> matches against.</summary>
        private int _pendingHead;

        /// <summary>Smallest round trip in the window, in seconds; 0 when no sample has arrived.</summary>
        private double _minRoundTripSeconds;

        /// <summary>Largest round trip in the window, in seconds; 0 when no sample has arrived (so jitter is 0 too).</summary>
        private double _maxRoundTripSeconds;

        /// <summary>The filtered offset estimate, in whole ticks: <c>serverTick = clientTick + _offsetTicks</c>.</summary>
        private int _offsetTicks;

        /// <summary>Plain mean of every sample's raw offset, in ticks. Diagnostics only - never used to set the clock.</summary>
        private double _naiveAverageTicks;

        /// <summary>Creates a time sync.</summary>
        /// <param name="tickRate">
        /// The session's tick rate (the same as <see cref="NetworkConfig.TickRate"/>). Unit: Hz; valid range 1
        /// and up.
        /// </param>
        /// <param name="sampleCapacity">
        /// How many of the most recent samples to weigh in. 1 works (it becomes a pure minimum-RTT method) but
        /// is more sensitive; see <see cref="DefaultSampleCapacity"/>. Unit: samples; valid range 1 and up.
        /// </param>
        /// <param name="extraLeadTicks">
        /// The margin on top of half the RTT, see <see cref="ClientLeadTicks"/> and
        /// <see cref="DefaultExtraLeadTicks"/>. Unit: ticks; negative values are clamped to 0, which leaves the
        /// lead equal to the latency alone.
        /// </param>
        public NetworkTimeSync(int tickRate, int sampleCapacity = DefaultSampleCapacity,
            int extraLeadTicks = DefaultExtraLeadTicks)
        {
            if (tickRate < 1)
                throw new ArgumentOutOfRangeException(nameof(tickRate), tickRate,
                    "The tick rate must be at least 1 Hz for the time sync to convert to ticks.");
            if (sampleCapacity < 1)
                throw new ArgumentOutOfRangeException(nameof(sampleCapacity), sampleCapacity,
                    "The time sync needs at least one sample in the window.");

            TickRate = tickRate;
            SampleCapacity = sampleCapacity;
            ExtraLeadTicks = Math.Max(0, extraLeadTicks);

            _roundTrip = new double[sampleCapacity];
            _offsets = new double[sampleCapacity];
            _pendingTicks = new int[sampleCapacity];
            _medianScratch = new double[sampleCapacity];
        }

        /// <summary>The session's tick rate - every reported value is in whole ticks.</summary>
        public int TickRate { get; }

        /// <summary>The size of the sample window (see <see cref="DefaultSampleCapacity"/>).</summary>
        public int SampleCapacity { get; }

        /// <summary>The margin on top of half the RTT, in ticks.</summary>
        public int ExtraLeadTicks { get; set; }

        /// <summary>
        /// True as soon as at least one sample has arrived. False means offset and lead are 0 - the game should
        /// then rely on the handshake's <see cref="NetworkMessageType.Welcome"/> and not on this class.
        /// </summary>
        public bool IsSynchronized => _sampleCount > 0;

        /// <summary>Number of valid samples in the window (counted down when they age out).</summary>
        public int SampleCount => _sampleCount;

        /// <summary>Number of TimeSync requests that have been sent but not yet answered.</summary>
        public int PendingRequestCount => _pendingCount;

        /// <summary>
        /// Estimated difference in ticks: <c>serverTick = clientTick + TickOffset</c>. Derived from the median
        /// of the samples that survived the minimum-RTT filter, rounded to whole ticks - a sub-tick offset is
        /// not usable when both sides count in whole numbers, and the rounding also gives a "deadband" that
        /// keeps the offset from being adjusted for every tiny measurement fluctuation.
        /// </summary>
        public int TickOffset => _offsetTicks;

        /// <summary>Estimated one-way latency in ticks, computed from the smallest RTT in the window.</summary>
        public int LatencyTicks => (int)Math.Round(_minRoundTripSeconds * TickRate * 0.5);

        /// <summary>How much the RTT varies in the window, in ticks (max - min).</summary>
        public int JitterTicks => (int)Math.Round((_maxRoundTripSeconds - _minRoundTripSeconds) * TickRate);

        /// <summary>Smallest measured round-trip time in the window, in seconds (the "clean" latency).</summary>
        public double MinRoundTripSeconds => _minRoundTripSeconds;

        /// <summary>Largest measured round-trip time in the window, in seconds.</summary>
        public double MaxRoundTripSeconds => _maxRoundTripSeconds;

        /// <summary>
        /// How many ticks the client should run AHEAD of the server, i.e.
        /// <c>RTT/2 + buffer</c> (sec 6). This IS the value that governs how
        /// aggressive the client's prediction may be: the larger the lead, the
        /// longer the client can run ahead of the server without the server having
        /// to guess the client's input - and the larger the errors become when a
        /// correction comes back (sec 7.1).
        /// <para>
        /// Computed from the <b>smallest</b> RTT (never the average): the
        /// smallest RTT is the best estimate of the one-way latency, and choosing
        /// the larger latency "to be safe" only adds delay without covering a
        /// spike packet, which can't be guessed away anyway.
        /// </para>
        /// <para>
        /// The three cases the caller should tell apart:
        /// <list type="bullet">
        /// <item><b>Positive</b> - a normal, healthy connection. This is the lead
        /// to use, and it is at least 0 (never negative) because
        /// <see cref="ExtraLeadTicks"/> is clamped.</item>
        /// <item><b>Zero</b> - either there are no samples yet
        /// (<see cref="IsSynchronized"/> is false, so the lead is just
        /// <see cref="ExtraLeadTicks"/>), or <see cref="ExtraLeadTicks"/> is 0 on
        /// a link with zero measured latency. The first case is the one that
        /// matters: the game should not start time-dilating the client based on a
        /// lead that was never measured.</item>
        /// <item><b>Larger than the server can afford</b> - at 60 Hz, 10 ticks of
        /// lead is 167 ms, which the player feels as input lag. Treat a lead that
        /// is not growing with the measured latency as a sign that
        /// <see cref="ExtraLeadTicks"/> was raised rather than that the network
        /// got slower.</item>
        /// </list>
        /// </para>
        /// </summary>
        public int ClientLeadTicks => Math.Max(0, LatencyTicks + ExtraLeadTicks);

        /// <summary>
        /// How often a TimeSync sample should be sent, in ticks. 8 ticks gives a new measurement every ~0.13 s
        /// at 60 Hz: rarely enough not to fill the network, often enough that a new client is synchronized
        /// within half a second. The server replies with a single tick pair and no simulation work, so the cost
        /// is a couple of bytes.
        /// </summary>
        public int RecommendedProbeIntervalTicks => 8;

        /// <summary>
        /// The corresponding interval before the first response has arrived - shorter, so that a new client
        /// synchronizes in a fraction of a second instead of waiting a full second for the first sample.
        /// </summary>
        public int FirstSyncProbeIntervalTicks => 2;

        /// <summary>The server's tick that corresponds to the client's <paramref name="clientTick"/>.</summary>
        /// <param name="clientTick">A tick on the client's own clock. Unit: ticks.</param>
        public int ServerTickFor(int clientTick) => clientTick + _offsetTicks;

        /// <summary>The client's tick that corresponds to the server's <paramref name="serverTick"/>.</summary>
        /// <param name="serverTick">A tick on the server's clock. Unit: ticks.</param>
        public int ClientTickFor(int serverTick) => serverTick - _offsetTicks;

        /// <summary>
        /// The tick the client's own clock should be set to for the client to run <see cref="ClientLeadTicks"/>
        /// ticks ahead of the server right now.
        /// </summary>
        /// <param name="serverTick">
        /// The server's tick right now (e.g. from <see cref="NetworkMessageType.Welcome"/> or from a state
        /// packet). Unit: ticks.
        /// </param>
        public int ClientTickAheadOf(int serverTick) => serverTick + ClientLeadTicks;

        /// <summary>
        /// Registers that a TimeSync sample is being sent now, and returns the tick to send with it.
        /// </summary>
        /// <param name="clientTick">The client's current tick. Unit: ticks.</param>
        public int RecordRequest(int clientTick)
        {
            if (_pendingCount == SampleCapacity)
            {
                // The window is full of unanswered requests: it means the responses aren't coming through (e.g.
                // UDP with high loss) or that the game forgot to answer.
                _pendingHead = (_pendingHead + 1) % SampleCapacity;
                _pendingCount--;
            }

            _pendingTicks[(_pendingHead + _pendingCount) % SampleCapacity] = clientTick;
            _pendingCount++;
            return clientTick;
        }

        /// <summary>
        /// Files a response and recomputes the estimate. A response that arrives very late is still a valid
        /// sample - it is just a bad sample, with a large RTT. That is exactly why it is filtered out in
        /// <see cref="Recompute"/> instead of being thrown away right here: that way we also lose the cases
        /// where the right response could wrongly have been paired with the wrong request.
        /// </summary>
        /// <param name="roundTripSeconds">
        /// Measured round-trip time in seconds. Must be finite and 0 or greater; a negative or non-finite value
        /// is discarded as junk.
        /// </param>
        public bool RecordResponse(int serverTick, double roundTripSeconds)
        {
            if (_pendingCount == 0)
                return false;

            int clientTickAtSend = _pendingTicks[_pendingHead];
            _pendingHead = (_pendingHead + 1) % SampleCapacity;
            _pendingCount--;
            AddSample(clientTickAtSend, serverTick, roundTripSeconds);
            return true;
        }

        /// <summary>
        /// Files a response with an explicit tick at send time. Used by games that put their own sequence
        /// number in the TimeSync message and can therefore match the response exactly, even if requests and
        /// responses cross each other in the network.
        /// </summary>
        /// <param name="clientTickAtSend">The client's tick when the request was sent. Unit: ticks.</param>
        /// <param name="serverTick">The server's tick when the response was sent. Unit: ticks.</param>
        /// <param name="roundTripSeconds">
        /// Measured round-trip time in seconds. Must be finite and 0 or greater; a negative or non-finite value
        /// is discarded as junk.
        /// </param>
        public void RecordResponse(int clientTickAtSend, int serverTick, double roundTripSeconds) =>
            AddSample(clientTickAtSend, serverTick, roundTripSeconds);

        /// <summary>
        /// Should a new sample be sent now? True every <see cref="RecommendedProbeIntervalTicks"/> tick, and
        /// always if no sample has arrived at all (then it should try again as soon as possible).
        /// </summary>
        /// <param name="clientTick">
        /// The client's current tick. Unit: ticks; negative values are never produced by the session clock.
        /// </param>
        public bool ShouldProbe(int clientTick)
        {
            // A request is already out in the network: sending another adds nothing, because the response to
            // the first one can't arrive any earlier anyway.
            if (_pendingCount > 0)
                return false;

            int interval = IsSynchronized ? RecommendedProbeIntervalTicks : FirstSyncProbeIntervalTicks;
            return clientTick % interval == 0;
        }

        /// <summary>Forgets all samples and sent requests, so the next estimate is built from zero.</summary>
        public void Reset()
        {
            Array.Clear(_roundTrip, 0, _roundTrip.Length);
            Array.Clear(_offsets, 0, _offsets.Length);
            Array.Clear(_pendingTicks, 0, _pendingTicks.Length);
            _sampleCount = 0;
            _nextSample = 0;
            _pendingCount = 0;
            _pendingHead = 0;
            _minRoundTripSeconds = 0d;
            _maxRoundTripSeconds = 0d;
            _offsetTicks = 0;
            _naiveAverageTicks = 0d;
        }

        /// <summary>
        /// The median of all the samples, rounded - that is, the method the server's client should not use.
        /// </summary>
        public int NaiveAverageOffsetTicks => (int)Math.Round(_naiveAverageTicks);

        /// <summary><inheritdoc/></summary>
        public override string ToString()
        {
            if (!IsSynchronized)
                return "Time sync: waiting for the first sample";

            // "15.0 ms" and not "15,0 ms": the text must be readable by a machine algorithm (log, HUD, report)
            // regardless of the game's language.
            string minRtt = (_minRoundTripSeconds * 1000d).ToString("0.0", CultureInfo.InvariantCulture);
            return $"Time sync: offset {TickOffset} ticks, latency {LatencyTicks}, jitter {JitterTicks}, " +
                   $"lead {ClientLeadTicks} ({SampleCount}/{SampleCapacity} samples, min RTT {minRtt} ms)";
        }

        /// <summary>
        /// Files a sample and recomputes everything derived from the window.
        /// <para>
        /// The mechanism in three steps:
        /// <list type="number">
        /// <item>We store <c>serverTick - clientTickAtSend - rtt/2</c>, i.e. the
        /// offset the sample on its own claims. Correcting with <c>rtt/2</c> is
        /// the entire NTP idea: the server's tick the response carried applies to
        /// the moment <c>rtt/2</c> after the client sent, so without the
        /// correction every sample would look like it says "the server is further
        /// ahead than it is".</item>
        /// <item>We find the smallest RTT in the window and discard every sample
        /// more than <see cref="MaxRttRatio"/> times as long as that. It is the
        /// minimum-RTT filter: a sample with a high RTT doesn't measure the
        /// network, it measures a queue, and its <c>rtt/2</c> correction is then
        /// not just unverified but actively harmful - it moves the estimate in
        /// the wrong direction.</item>
        /// <item>We take the median of what is left. The median is needed in
        /// addition to the filter because the filter never becomes perfect: a
        /// machine running two heavy downloads at once can produce two spike
        /// samples in a row, and the median differs from an average in a way that
        /// matters - two bad values cannot move the median in a window with a
        /// handful of good ones.</item>
        /// </list>
        /// No allocation happens here: the median is sorted in
        /// <see cref="_medianScratch"/>, which is as large as the window.
        /// </para>
        /// </summary>
        private void AddSample(int clientTickAtSend, int serverTick, double roundTripSeconds)
        {
            // Junk data (a negative or non-finite round-trip time) would destroy both the minimum and the
            // median, so it is discarded instead of repaired.
            if (!(roundTripSeconds >= 0d) || double.IsInfinity(roundTripSeconds))
                return;

            _roundTrip[_nextSample] = roundTripSeconds;
            _offsets[_nextSample] = serverTick - clientTickAtSend - roundTripSeconds * TickRate * 0.5;
            _nextSample = (_nextSample + 1) % SampleCapacity;
            if (_sampleCount < SampleCapacity)
                _sampleCount++;

            Recompute();
        }

        /// <summary>Recomputes offset, latency and jitter from the whole window.</summary>
        private void Recompute()
        {
            if (_sampleCount == 0)
                return;

            double minRtt = double.MaxValue;
            double maxRtt = 0d;
            double sum = 0d;
            for (int i = 0; i < _sampleCount; i++)
            {
                double rtt = _roundTrip[i];
                if (rtt < minRtt)
                    minRtt = rtt;
                if (rtt > maxRtt)
                    maxRtt = rtt;
                sum += _offsets[i];
            }

            _minRoundTripSeconds = minRtt;
            _maxRoundTripSeconds = maxRtt;
            _naiveAverageTicks = sum / _sampleCount;

            // The filter's threshold: the best sample always gets in, and so does
            // every sample at most MaxRttRatio times as long as the best one.
            double threshold = minRtt * MaxRttRatio;
            int kept = 0;
            for (int i = 0; i < _sampleCount; i++)
            {
                if (_roundTrip[i] > threshold)
                    continue;

                _medianScratch[kept++] = _offsets[i];
            }

            if (kept == 0)
            {
                // Cannot happen (the smallest RTT is by definition at or below the threshold), but a fallback
                // to one value is cheaper than an IndexOutOfRangeException in the middle of a tick.
                _offsetTicks = (int)Math.Round(_naiveAverageTicks);
                return;
            }

            // The median: an even count takes the mean of the two middle values, which is the most common
            // definition and the one that moves least on a new extreme (e.g. 1,2,3,4 -> 2.5, not 1).
            _medianScratch.AsSpan(0, kept).Sort();
            double median = kept % 2 == 1
                ? _medianScratch[kept / 2]
                : (_medianScratch[kept / 2 - 1] + _medianScratch[kept / 2]) * 0.5;

            _offsetTicks = (int)Math.Round(median);
        }
    }
}