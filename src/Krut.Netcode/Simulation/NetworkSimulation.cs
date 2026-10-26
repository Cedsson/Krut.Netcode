using System;
using System.Collections.Generic;
using System.Globalization;

namespace Krut.Netcode
{
    /// <summary>
    /// Network simulation on a real session: delay, jitter, random and burst loss, reorder, duplicates, a
    /// bandwidth cap with a bounded queue, and scheduled outages. It exists so netcode is verified at runtime
    /// in every build; LiteNetLib's own simulation is compiled out of the NuGet package, so the library does its own.
    ///
    /// <para>
    /// <b>Direction.</b> A simulation affects what the session that holds it SENDS: the uplink on a client, the
    /// downlink on a server. Apply one to both sessions to degrade both directions. The decisions are made when
    /// the message is handed to <see cref="NetworkSession"/>.Send, above LiteNetLib: a reliable message that
    /// "drops" is delivered late (about a round trip, or when an outage ends) instead of vanishing, and
    /// LiteNetLib's own pings and acks are not touched.
    /// </para>
    ///
    /// <para>
    /// <b>RTT semantics (stated once).</b> The games read KRUT_NET_LAG_MS and KRUT_NET_JITTER_MS as round-trip
    /// values and give each side half; <see cref="NetworkSimulationProfile"/> and
    /// <see cref="FromEnvironment(NetworkSimulationSide)"/> do the same. <see cref="Create(int, int, float)"/>,
    /// <see cref="Create(NetworkSimulationOptions)"/> and the parameterless <see cref="FromEnvironment()"/> take
    /// one-way values for the session that holds the simulation, as they always did.
    /// </para>
    ///
    /// <para>
    /// <b>Determinism.</b> Each link (one per peer, numbered in order of first send) owns a seeded PRNG stream. The
    /// same seed and the same sequence of (time, size, reliability) calls give the same verdicts, see
    /// <see cref="NetworkLinkModel"/>. Real sockets and wall-clock pacing are not repeatable, so a whole run is not.
    /// </para>
    ///
    /// <para>
    /// Deliberate limits: the delay is applied per message, not per UDP packet; releases happen when the session
    /// Update runs, so the effective resolution is the update cadence; the server's time dilation is not simulated.
    /// </para>
    /// </summary>
    public sealed class NetworkSimulation
    {
        /// <summary>The environment variable for the delay in milliseconds (round trip via <see cref="FromEnvironment(NetworkSimulationSide)"/>).</summary>
        public const string LatencyVariable = "KRUT_NET_LAG_MS";

        /// <summary>The environment variable for the jitter in milliseconds (plus or minus).</summary>
        public const string JitterVariable = "KRUT_NET_JITTER_MS";

        /// <summary>The environment variable for loss as a fraction 0..1. Both 0.02 and 0,02 are read.</summary>
        public const string LossVariable = "KRUT_NET_LOSS";

        /// <summary>The environment variable naming a <see cref="NetworkSimulationProfile"/>.</summary>
        public const string ProfileVariable = "KRUT_NET_PROFILE";

        /// <summary>The environment variable for the seed (unsigned 64-bit integer).</summary>
        public const string SeedVariable = "KRUT_NET_SEED";

        /// <summary>Mean loss burst length in messages.</summary>
        public const string BurstVariable = "KRUT_NET_BURST";

        /// <summary>Reorder fraction.</summary>
        public const string ReorderVariable = "KRUT_NET_REORDER";

        /// <summary>Duplicate fraction.</summary>
        public const string DuplicateVariable = "KRUT_NET_DUP";

        /// <summary>Cap on the server's sends in kbit/s.</summary>
        public const string BandwidthVariable = "KRUT_NET_BW_KBIT";

        /// <summary>Outages as <c>start:duration</c> seconds pairs separated by commas, e.g. <c>15:1,30:5</c>.</summary>
        public const string OutagesVariable = "KRUT_NET_OUTAGES";

        /// <summary>1 or true moves loss, bursts and outages to the datagram stage (see <see cref="DatagramLevel"/>).</summary>
        public const string DatagramVariable = "KRUT_NET_DATAGRAM";

        /// <summary>The text shown when no simulation is switched on.</summary>
        public const string InactiveDescription = "No network simulation";

        /// <summary>The seed used when none is given, so that an unseeded run is still repeatable.</summary>
        public const ulong DefaultSeed = 0x4B52555452414345UL;

        /// <summary>Extra delay of a reordered message in ms when none is configured.</summary>
        public const float DefaultReorderDelayMs = 20f;

        private const double MaxMs = 3_600_000.0;
        private const int MtuBytes = 1500;

        private readonly NetworkOutage[] _outages;

        private NetworkSimulation(NetworkSimulationOptions o, NetworkOutage[] outages)
        {
            LatencyExactMs = o.LatencyMs;
            JitterExactMs = o.JitterMs;
            LossChance = (float)o.LossChance;
            BurstMeanLength = (float)o.BurstMeanLength;
            ReorderChance = (float)o.ReorderChance;
            ReorderDelayMs = (float)o.ReorderDelayMs;
            DuplicateChance = (float)o.DuplicateChance;
            BandwidthBytesPerSecond = o.BandwidthBytesPerSecond;
            BucketBytes = o.BucketBytes;
            QueueLimitBytes = o.QueueLimitBytes;
            Seed = o.Seed;
            DatagramLevel = o.DatagramLevel;
            _outages = outages;
        }

        /// <summary>True when any effect is switched on.</summary>
        public bool IsActive =>
            LatencyExactMs > 0 || JitterExactMs > 0 || LossChance > 0f || ReorderChance > 0f || DuplicateChance > 0f ||
            BandwidthBytesPerSecond > 0 || _outages.Length > 0;

        /// <summary>One-way delay in whole milliseconds (rounded).</summary>
        public int LatencyMs => (int)Math.Round(LatencyExactMs, MidpointRounding.AwayFromZero);

        /// <summary>Jitter in whole milliseconds (rounded).</summary>
        public int JitterMs => (int)Math.Round(JitterExactMs, MidpointRounding.AwayFromZero);

        /// <summary>One-way delay in ms with fractions.</summary>
        public double LatencyExactMs { get; }

        /// <summary>Jitter in ms with fractions.</summary>
        public double JitterExactMs { get; }

        /// <summary>Long-run fraction of messages that are dropped.</summary>
        public float LossChance { get; }

        /// <summary>Mean loss burst length in messages. 1 or less: independent loss.</summary>
        public float BurstMeanLength { get; }

        public float ReorderChance { get; }

        /// <summary>Extra delay of a reordered message in ms; <see cref="DefaultReorderDelayMs"/> is used when this is 0.</summary>
        public float ReorderDelayMs { get; }

        public float DuplicateChance { get; }

        /// <summary>Token-bucket rate of this side's sends in bytes per second, 0 for no cap.</summary>
        public int BandwidthBytesPerSecond { get; }

        /// <summary>Effective bucket depth in bytes.</summary>
        public int BucketBytes { get; }

        /// <summary>Effective queue bound in bytes.</summary>
        public int QueueLimitBytes { get; }

        /// <summary>
        /// True when loss, bursts and outages are applied to datagrams by the session's datagram layer. The message stage
        /// then ignores them, so an effect is applied exactly once. Delay, jitter, reorder, duplicates and the cap always
        /// stay in the message stage.
        /// </summary>
        public bool DatagramLevel { get; }

        public ulong Seed { get; }

        public IReadOnlyList<NetworkOutage> Outages => _outages;

        /// <summary>A simulation with nothing switched on.</summary>
        public static NetworkSimulation None => Create(new NetworkSimulationOptions());

        /// <summary>One-way values for the session that holds the simulation, clamped (negative or NaN becomes 0, loss above 1 becomes 1).</summary>
        public static NetworkSimulation Create(int latencyMs, int jitterMs, float lossChance) =>
            Create(new NetworkSimulationOptions { LatencyMs = latencyMs, JitterMs = jitterMs, LossChance = lossChance });

        /// <summary>The single place values are clamped, so the environment, the profiles and the tests agree on what a value means.</summary>
        public static NetworkSimulation Create(NetworkSimulationOptions options)
        {
            if (options is null)
                throw new ArgumentNullException(nameof(options));

            var o = options.Clone();
            o.LatencyMs = ClampMs(o.LatencyMs);
            o.JitterMs = ClampMs(o.JitterMs);
            o.LossChance = ClampFraction(o.LossChance);
            o.ReorderChance = ClampFraction(o.ReorderChance);
            o.DuplicateChance = ClampFraction(o.DuplicateChance);
            o.BurstMeanLength = double.IsNaN(o.BurstMeanLength) || o.BurstMeanLength < 1.0 ? 1.0 : Math.Min(o.BurstMeanLength, 1_000_000.0);
            o.ReorderDelayMs = ClampMs(o.ReorderDelayMs);
            if (o.BandwidthBytesPerSecond < 0)
                o.BandwidthBytesPerSecond = 0;
            if (o.BucketBytes <= 0)
                o.BucketBytes = MtuBytes;
            if (o.QueueLimitBytes <= 0)
                o.QueueLimitBytes = Math.Max(3 * MtuBytes, o.BandwidthBytesPerSecond / 4);

            var outages = new List<NetworkOutage>(o.Outages.Count);
            foreach (var outage in o.Outages)
            {
                double start = ClampMs(outage.StartSeconds * 1000.0) / 1000.0;
                double length = ClampMs(outage.DurationSeconds * 1000.0) / 1000.0;
                if (length > 0.0)
                    outages.Add(new NetworkOutage(start, length));
            }

            outages.Sort((a, b) => a.StartSeconds.CompareTo(b.StartSeconds));
            return new NetworkSimulation(o, outages.ToArray());
        }

        /// <summary>The built-in profile <paramref name="name"/> for one side (see <see cref="NetworkSimulationProfile"/>).</summary>
        public static NetworkSimulation FromProfile(string name, NetworkSimulationSide side, ulong seed = DefaultSeed)
        {
            if (!NetworkSimulationProfile.TryGet(name, out var profile))
                throw new ArgumentException($"Unknown network profile '{name}'. Known: {string.Join(", ", NetworkSimulationProfile.Names)}.", nameof(name));

            return profile.Resolve(side, seed);
        }

        /// <summary>
        /// Reads LAG, JITTER, LOSS and SEED as one-way values for the session. Broken values are clamped, never
        /// thrown on: an environment variable must not stop a game from starting.
        /// </summary>
        public static NetworkSimulation FromEnvironment()
        {
            return Create(new NetworkSimulationOptions
            {
                LatencyMs = KrutEnv.Int(LatencyVariable, 0),
                JitterMs = KrutEnv.Int(JitterVariable, 0),
                LossChance = KrutEnv.FloatLenient(LossVariable, 0f),
                Seed = ReadSeed(),
            });
        }

        /// <summary>
        /// Reads KRUT_NET_PROFILE (a named link, round-trip terms), then lets KRUT_NET_LAG_MS, _JITTER_MS and
        /// _LOSS override it with round-trip values, and each side takes half of the delay. KRUT_NET_BURST,
        /// _REORDER, _DUP, _OUTAGES override the rest; KRUT_NET_BW_KBIT caps the server's sends. An unknown
        /// profile name falls back to no profile.
        /// </summary>
        public static NetworkSimulation FromEnvironment(NetworkSimulationSide side) => FromEnvironment(side, out _);

        /// <summary>As <see cref="FromEnvironment(NetworkSimulationSide)"/>, with a warning text (or null) for the caller's log.</summary>
        public static NetworkSimulation FromEnvironment(NetworkSimulationSide side, out string? warning)
        {
            warning = null;
            NetworkSimulationProfile profile = new("env", 0, 0, 0);
            string? profileName = KrutEnv.Text(ProfileVariable);
            if (!string.IsNullOrWhiteSpace(profileName) && !NetworkSimulationProfile.TryGet(profileName, out profile))
            {
                profile = new("env", 0, 0, 0);
                warning = $"Unknown {ProfileVariable} '{profileName}', ignored. Known: {string.Join(", ", NetworkSimulationProfile.Names)}.";
            }

            if (KrutEnv.Text(LatencyVariable) is not null)
                profile.RttMs = KrutEnv.Int(LatencyVariable, 0);
            if (KrutEnv.Text(JitterVariable) is not null)
                profile.RttJitterMs = KrutEnv.Int(JitterVariable, 0);
            if (KrutEnv.Text(LossVariable) is not null)
                profile.LossChance = KrutEnv.FloatLenient(LossVariable, 0f);
            if (KrutEnv.Text(BurstVariable) is not null)
                profile.BurstMeanLength = KrutEnv.FloatLenient(BurstVariable, 0f);
            if (KrutEnv.Text(ReorderVariable) is not null)
                profile.ReorderChance = KrutEnv.FloatLenient(ReorderVariable, 0f);
            if (KrutEnv.Text(DuplicateVariable) is not null)
                profile.DuplicateChance = KrutEnv.FloatLenient(DuplicateVariable, 0f);
            if (KrutEnv.Text(BandwidthVariable) is not null)
                profile.ServerSendBytesPerSecond = (int)Math.Min(int.MaxValue, KrutEnv.Int(BandwidthVariable, 0) * 125L);
            if (KrutEnv.Text(OutagesVariable) is { } outageText)
                profile.Outages = ParseOutages(outageText);

            var sim = profile.Resolve(side, ReadSeed());
            string? datagram = KrutEnv.Text(DatagramVariable)?.Trim();
            bool datagramLevel = datagram == "1" || string.Equals(datagram, "true", StringComparison.OrdinalIgnoreCase);
            return datagramLevel ? sim.WithDatagramLevel(true) : sim;
        }

        /// <summary>Parses <c>start:duration</c> pairs in seconds separated by commas; unreadable pairs are skipped.</summary>
        public static List<NetworkOutage> ParseOutages(string? text)
        {
            var list = new List<NetworkOutage>();
            if (string.IsNullOrWhiteSpace(text))
                return list;

            foreach (string pair in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = pair.Split(':');
                if (parts.Length == 2 &&
                    KrutEnv.TryParseFloatLenient(parts[0].Trim(), out float start) &&
                    KrutEnv.TryParseFloatLenient(parts[1].Trim(), out float length) &&
                    start >= 0f && length > 0f)
                {
                    list.Add(new NetworkOutage(start, length));
                }
            }

            return list;
        }

        /// <summary>True when <paramref name="elapsedUs"/> (microseconds since the simulation was bound) lies in an outage; <paramref name="endUs"/> is then its end.</summary>
        public bool TryGetOutage(long elapsedUs, out long endUs)
        {
            for (int i = 0; i < _outages.Length; i++)
            {
                long start = (long)(_outages[i].StartSeconds * 1_000_000.0);
                long end = (long)(_outages[i].EndSeconds * 1_000_000.0);
                if (elapsedUs >= start && elapsedUs < end)
                {
                    endUs = end;
                    return true;
                }
            }

            endUs = 0;
            return false;
        }

        /// <summary>Returns a copy of this simulation with another seed.</summary>
        public NetworkSimulation WithSeed(ulong seed)
        {
            var o = ToOptions();
            o.Seed = seed;
            return Create(o);
        }

        /// <summary>Returns a copy with loss, bursts and outages moved to (or back from) the datagram stage.</summary>
        public NetworkSimulation WithDatagramLevel(bool datagramLevel)
        {
            var o = ToOptions();
            o.DatagramLevel = datagramLevel;
            return Create(o);
        }

        /// <summary>
        /// The part of this simulation the datagram stage applies: loss, bursts, outages and the seed, with every
        /// delay effect switched off and <see cref="DatagramLevel"/> cleared so the stage's own model applies it.
        /// </summary>
        public NetworkSimulation ForDatagramStage()
        {
            var o = ToOptions();
            o.LatencyMs = 0;
            o.JitterMs = 0;
            o.ReorderChance = 0;
            o.DuplicateChance = 0;
            o.BandwidthBytesPerSecond = 0;
            o.QueueLimitBytes = 0;
            o.DatagramLevel = false;
            return Create(o);
        }

        /// <summary>The options this simulation was made from (already clamped).</summary>
        public NetworkSimulationOptions ToOptions() => new()
        {
            LatencyMs = LatencyExactMs,
            JitterMs = JitterExactMs,
            LossChance = LossChance,
            BurstMeanLength = BurstMeanLength,
            ReorderChance = ReorderChance,
            ReorderDelayMs = ReorderDelayMs,
            DuplicateChance = DuplicateChance,
            BandwidthBytesPerSecond = BandwidthBytesPerSecond,
            BucketBytes = BucketBytes,
            QueueLimitBytes = QueueLimitBytes,
            Outages = new List<NetworkOutage>(_outages),
            Seed = Seed,
            DatagramLevel = DatagramLevel,
        };

        /// <summary>Applies the simulation to a session.</summary>
        public void ApplyTo(NetworkSession session)
        {
            if (session is null)
                throw new ArgumentNullException(nameof(session));

            session.ApplySimulation(this);
        }

        /// <summary>
        /// One English line with only the parts that are on, e.g. <c>150 ms latency, 30 ms jitter, 2 % loss</c>.
        /// Tests assert the format; the invariant decimal point lets the text be pasted back into the variables.
        /// </summary>
        public string Describe()
        {
            if (!IsActive)
                return InactiveDescription;

            var parts = new List<string>();
            if (LatencyExactMs > 0)
                parts.Add($"{Num(LatencyExactMs)} ms latency");
            if (JitterExactMs > 0)
                parts.Add($"{Num(JitterExactMs)} ms jitter");
            if (LossChance > 0f)
                parts.Add($"{Num(LossChance * 100.0)} % loss");
            if (LossChance > 0f && BurstMeanLength > 1f)
                parts.Add($"bursts of {Num(BurstMeanLength)}");
            if (ReorderChance > 0f)
                parts.Add($"{Num(ReorderChance * 100.0)} % reorder");
            if (DuplicateChance > 0f)
                parts.Add($"{Num(DuplicateChance * 100.0)} % duplicate");
            if (BandwidthBytesPerSecond > 0)
                parts.Add($"{Num(BandwidthBytesPerSecond * 8 / 1000.0)} kbit/s cap");
            foreach (var outage in _outages)
                parts.Add($"outage {Num(outage.StartSeconds)}+{Num(outage.DurationSeconds)} s");

            if (DatagramLevel)
                parts.Add("loss at datagram level");

            return string.Join(", ", parts);
        }

        /// <inheritdoc/>
        public override string ToString() => Describe();

        private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        private static ulong ReadSeed() =>
            KrutEnv.Text(SeedVariable) is { } text && ulong.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed)
                ? seed
                : DefaultSeed;

        private static double ClampMs(double value) =>
            double.IsNaN(value) || value < 0.0 ? 0.0 : Math.Min(value, MaxMs);

        private static double ClampFraction(double value) =>
            !(value > 0.0) ? 0.0 : Math.Min(value, 1.0);
    }
}
