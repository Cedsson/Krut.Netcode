using System;
using System.Threading;

namespace Krut.Netcode
{
    /// <summary>Why a message was dropped (or, for a reliable message, delayed as if it had been resent).</summary>
    public enum NetworkDropCause
    {
        None,
        Random,
        Burst,
        Overflow,
        Outage,
    }

    /// <summary>
    /// What the link decided for one message. Times are microseconds on the model's own clock. A dropped reliable
    /// message has <see cref="Deliver"/> true and a late <see cref="DueUs"/>; <see cref="Cause"/> says why.
    /// </summary>
    public readonly record struct NetworkLinkVerdict(
        bool Deliver,
        NetworkDropCause Cause,
        long DueUs,
        bool Reordered,
        bool Duplicate,
        long DuplicateDueUs);

    /// <summary>Thread-safe counters of one simulated direction.</summary>
    public sealed class NetworkSimulationStats
    {
        private long _submitted;
        private long _bytesSubmitted;
        private long _dropped;
        private long _droppedRandom;
        private long _droppedBurst;
        private long _droppedOverflow;
        private long _droppedOutage;
        private long _duplicated;
        private long _reordered;
        private long _queueDelayMaxUs;

        public long Submitted { get => Interlocked.Read(ref _submitted); internal set => Interlocked.Exchange(ref _submitted, value); }

        public long BytesSubmitted { get => Interlocked.Read(ref _bytesSubmitted); internal set => Interlocked.Exchange(ref _bytesSubmitted, value); }

        /// <summary>Messages that never arrive plus reliable ones that arrive late because they were "dropped".</summary>
        public long Dropped { get => Interlocked.Read(ref _dropped); internal set => Interlocked.Exchange(ref _dropped, value); }

        public long DroppedRandom { get => Interlocked.Read(ref _droppedRandom); internal set => Interlocked.Exchange(ref _droppedRandom, value); }

        public long DroppedBurst { get => Interlocked.Read(ref _droppedBurst); internal set => Interlocked.Exchange(ref _droppedBurst, value); }

        public long DroppedOverflow { get => Interlocked.Read(ref _droppedOverflow); internal set => Interlocked.Exchange(ref _droppedOverflow, value); }

        public long DroppedOutage { get => Interlocked.Read(ref _droppedOutage); internal set => Interlocked.Exchange(ref _droppedOutage, value); }

        public long Duplicated { get => Interlocked.Read(ref _duplicated); internal set => Interlocked.Exchange(ref _duplicated, value); }

        public long Reordered { get => Interlocked.Read(ref _reordered); internal set => Interlocked.Exchange(ref _reordered, value); }

        /// <summary>Longest wait in the bandwidth queue, microseconds.</summary>
        public long QueueDelayMaxUs { get => Interlocked.Read(ref _queueDelayMaxUs); internal set => Interlocked.Exchange(ref _queueDelayMaxUs, value); }

        public double QueueDelayMaxMs => QueueDelayMaxUs / 1000.0;

        internal void AddSubmitted(int wireBytes)
        {
            Interlocked.Increment(ref _submitted);
            Interlocked.Add(ref _bytesSubmitted, wireBytes);
        }

        internal void IncrementDropped() => Interlocked.Increment(ref _dropped);
        internal void IncrementDroppedRandom() => Interlocked.Increment(ref _droppedRandom);
        internal void IncrementDroppedBurst() => Interlocked.Increment(ref _droppedBurst);
        internal void IncrementDroppedOverflow() => Interlocked.Increment(ref _droppedOverflow);
        internal void IncrementDroppedOutage() => Interlocked.Increment(ref _droppedOutage);
        internal void IncrementDuplicated() => Interlocked.Increment(ref _duplicated);
        internal void IncrementReordered() => Interlocked.Increment(ref _reordered);

        internal void RaiseQueueDelayMaxUs(long waited)
        {
            long seen = Interlocked.Read(ref _queueDelayMaxUs);
            while (waited > seen)
            {
                long previous = Interlocked.CompareExchange(ref _queueDelayMaxUs, waited, seen);
                if (previous == seen)
                    return;
                seen = previous;
            }
        }
    }

    /// <summary>
    /// One direction of one link, with no clock and no sockets: the caller passes the time, so a test can replay the
    /// same call sequence and get the same verdicts. Order of effects: outage, bandwidth cap and queue (tail drop),
    /// loss (a lost message still used the bottleneck), delay and jitter, then reorder and duplicate. Delivery is
    /// FIFO per lane (reliable and unreliable are separate lanes) unless a message is picked for reorder.
    /// A simulation with <see cref="NetworkSimulation.DatagramLevel"/> skips the outage and loss steps (the datagram stage does them).
    /// </summary>
    public sealed class NetworkLinkModel
    {
        private const double UsPerMs = 1000.0;

        private readonly NetworkSimulation _sim;
        private readonly NetworkSimRandom _random;
        private readonly double _latencyUs;
        private readonly double _jitterUs;
        private readonly double _reorderUs;
        private readonly double _rateBytesPerUs;
        private readonly double _burstEnter;
        private readonly double _burstExit;
        private readonly bool _burst;

        private bool _inBadState;
        private long _lastReliableDueUs;
        private long _lastUnreliableDueUs;
        private bool _tokensStarted;
        private double _tokens;
        private long _tokenTimeUs;
        private long _lastReleaseUs;

        /// <param name="sim">The configuration (one direction).</param>
        /// <param name="linkIndex">Number of the link within its session; mixed into the seed so links have separate streams.</param>
        /// <param name="sharedStats">One counter object several links add to, so a reader polls one instance without allocating.</param>
        public NetworkLinkModel(NetworkSimulation sim, int linkIndex, NetworkSimulationStats? sharedStats = null)
        {
            _sim = sim ?? throw new ArgumentNullException(nameof(sim));
            Stats = sharedStats ?? new NetworkSimulationStats();
            _random = new NetworkSimRandom(NetworkSimRandom.Mix(sim.Seed, (ulong)linkIndex + 1UL));
            _latencyUs = sim.LatencyExactMs * UsPerMs;
            _jitterUs = sim.JitterExactMs * UsPerMs;
            _reorderUs = (sim.ReorderDelayMs > 0f ? sim.ReorderDelayMs : NetworkSimulation.DefaultReorderDelayMs) * UsPerMs;
            _rateBytesPerUs = sim.BandwidthBytesPerSecond / 1_000_000.0;

            // Gilbert-Elliott with loss 0 in the good state and 1 in the bad state: the mean burst is 1/exit and
            // the long-run loss is enter / (enter + exit).
            if (sim.LossChance > 0f && sim.LossChance < 1f && sim.BurstMeanLength > 1f)
            {
                _burst = true;
                _burstExit = 1.0 / sim.BurstMeanLength;
                double enter = _burstExit * sim.LossChance / (1.0 - sim.LossChance);
                _burstEnter = Math.Min(enter, 1.0);
            }
        }

        public NetworkSimulationStats Stats { get; }

        /// <summary>
        /// Decides the fate of one message sent at <paramref name="nowUs"/> (microseconds since the simulation was
        /// bound). <paramref name="wireBytes"/> is the size charged to the bandwidth cap.
        /// </summary>
        public NetworkLinkVerdict Submit(long nowUs, int wireBytes, bool reliable)
        {
            if (wireBytes < 0)
                wireBytes = 0;

            Stats.AddSubmitted(wireBytes);

            long resendUs = (long)(2.0 * _latencyUs);

            if (!_sim.DatagramLevel && _sim.TryGetOutage(nowUs, out long outageEnd))
            {
                CountDrop(NetworkDropCause.Outage);
                if (!reliable)
                    return Dropped(NetworkDropCause.Outage);

                // Held above LiteNetLib's reliable channel, so it leaves when the outage ends.
                return new NetworkLinkVerdict(true, NetworkDropCause.Outage, outageEnd + (long)_latencyUs, false, false, 0);
            }

            long releaseUs = nowUs;
            if (_rateBytesPerUs > 0.0)
            {
                double backlog = Math.Max(0L, _lastReleaseUs - nowUs) * _rateBytesPerUs;
                if (!reliable && backlog + wireBytes > _sim.QueueLimitBytes)
                {
                    CountDrop(NetworkDropCause.Overflow);
                    return Dropped(NetworkDropCause.Overflow);
                }

                releaseUs = TakeTokens(nowUs, wireBytes);
                long waited = releaseUs - nowUs;
                if (waited > 0)
                    Stats.RaiseQueueDelayMaxUs(waited);
            }

            NetworkDropCause cause = NetworkDropCause.None;
            if (!_sim.DatagramLevel && DrawLoss(out bool burstLoss))
            {
                cause = burstLoss ? NetworkDropCause.Burst : NetworkDropCause.Random;
                CountDrop(cause);
                if (!reliable)
                    return Dropped(cause);
            }

            double delayUs = _latencyUs;
            if (_jitterUs > 0.0)
                delayUs += (_random.NextDouble() * 2.0 - 1.0) * _jitterUs;
            if (delayUs < 0.0)
                delayUs = 0.0;

            long dueUs = releaseUs + (long)delayUs;
            if (cause != NetworkDropCause.None)
                dueUs += resendUs;

            bool reordered = false;
            if (_sim.ReorderChance > 0f && _random.NextDouble() < _sim.ReorderChance)
            {
                dueUs += (long)(_reorderUs * (1.0 + _random.NextDouble()));
                reordered = true;
                Stats.IncrementReordered();
            }
            else if (reliable)
            {
                if (dueUs < _lastReliableDueUs)
                    dueUs = _lastReliableDueUs;
                _lastReliableDueUs = dueUs;
            }
            else
            {
                if (dueUs < _lastUnreliableDueUs)
                    dueUs = _lastUnreliableDueUs;
                _lastUnreliableDueUs = dueUs;
            }

            bool duplicate = false;
            long duplicateDue = 0;
            if (_sim.DuplicateChance > 0f && _random.NextDouble() < _sim.DuplicateChance)
            {
                duplicate = true;
                Stats.IncrementDuplicated();
                duplicateDue = dueUs + 500 + (long)(_random.NextDouble() * Math.Max(_jitterUs, 2000.0));
            }

            return new NetworkLinkVerdict(true, cause, dueUs, reordered, duplicate, duplicateDue);
        }

        private NetworkLinkVerdict Dropped(NetworkDropCause cause) =>
            new(false, cause, 0, false, false, 0);

        private void CountDrop(NetworkDropCause cause)
        {
            Stats.IncrementDropped();
            switch (cause)
            {
                case NetworkDropCause.Random:
                    Stats.IncrementDroppedRandom();
                    break;
                case NetworkDropCause.Burst:
                    Stats.IncrementDroppedBurst();
                    break;
                case NetworkDropCause.Overflow:
                    Stats.IncrementDroppedOverflow();
                    break;
                case NetworkDropCause.Outage:
                    Stats.IncrementDroppedOutage();
                    break;
            }
        }

        private bool DrawLoss(out bool burstLoss)
        {
            burstLoss = false;
            float loss = _sim.LossChance;
            if (loss <= 0f)
                return false;
            if (loss >= 1f)
                return true;

            if (!_burst)
                return _random.NextDouble() < loss;

            double u = _random.NextDouble();
            if (_inBadState)
            {
                if (u < _burstExit)
                    _inBadState = false;
            }
            else if (u < _burstEnter)
            {
                _inBadState = true;
            }

            burstLoss = _inBadState;
            return _inBadState;
        }

        // The bucket is refilled analytically up to the packet's release time, so a long wait costs one division.
        private long TakeTokens(long nowUs, int wireBytes)
        {
            if (!_tokensStarted)
            {
                _tokensStarted = true;
                _tokens = _sim.BucketBytes;
                _tokenTimeUs = nowUs;
            }

            long t = Math.Max(nowUs, _lastReleaseUs);
            _tokens = Math.Min(_sim.BucketBytes, _tokens + (t - _tokenTimeUs) * _rateBytesPerUs);
            long release = t;
            if (_tokens >= wireBytes)
            {
                _tokens -= wireBytes;
            }
            else
            {
                release = t + (long)Math.Ceiling((wireBytes - _tokens) / _rateBytesPerUs);
                _tokens = 0.0;
            }

            _tokenTimeUs = release;
            _lastReleaseUs = release;
            return release;
        }
    }
}
