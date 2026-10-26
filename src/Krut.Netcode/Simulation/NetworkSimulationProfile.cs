using System;
using System.Collections.Generic;

namespace Krut.Netcode
{
    /// <summary>
    /// A named test link, defined in round-trip terms. <see cref="Resolve"/> turns it
    /// into the one-way <see cref="NetworkSimulation"/> for one side: latency and jitter are halved, loss, burst,
    /// reorder and duplicate rates apply per direction as given, outages hit both sides, and the bandwidth caps
    /// apply to that side's own sends (thin caps only the server's send side, the downlink).
    /// </summary>
    public sealed class NetworkSimulationProfile
    {
        private static readonly NetworkSimulationProfile[] All =
        {
            new("clean", 0, 0, 0),
            new("lan", 2, 1, 0),
            new("gate", 100, 10, 0.02),
            new("stress", 180, 20, 0.02),
            new("bad", 250, 50, 0.05) { BurstMeanLength = 4, ReorderChance = 0.01, DuplicateChance = 0.01 },
            new("blackout", 100, 10, 0.02)
            {
                Outages = { new NetworkOutage(15, 1), new NetworkOutage(30, 5) },
            },
            new("thin", 100, 10, 0.02) { ServerSendBytesPerSecond = 8000 },
        };

        public NetworkSimulationProfile(string name, double rttMs, double rttJitterMs, double lossChance)
        {
            Name = name;
            RttMs = rttMs;
            RttJitterMs = rttJitterMs;
            LossChance = lossChance;
        }

        public string Name { get; }

        /// <summary>Round-trip time in ms.</summary>
        public double RttMs { get; set; }

        /// <summary>Round-trip jitter in ms (+/-).</summary>
        public double RttJitterMs { get; set; }

        /// <summary>Loss per direction.</summary>
        public double LossChance { get; set; }

        public double BurstMeanLength { get; set; }

        public double ReorderChance { get; set; }

        public double DuplicateChance { get; set; }

        /// <summary>Cap on the server's sends (downlink), bytes per second. 0 means none.</summary>
        public int ServerSendBytesPerSecond { get; set; }

        /// <summary>Cap on the client's sends (uplink), bytes per second. 0 means none.</summary>
        public int ClientSendBytesPerSecond { get; set; }

        public List<NetworkOutage> Outages { get; set; } = new();

        /// <summary>Names of the built-in profiles, in definition order.</summary>
        public static IReadOnlyList<string> Names { get; } = Array.ConvertAll(All, p => p.Name);

        /// <summary>Looks a built-in profile up by name, ignoring case. Returns a private copy that may be edited.</summary>
        public static bool TryGet(string? name, out NetworkSimulationProfile profile)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string wanted = name.Trim();
                foreach (var candidate in All)
                {
                    if (string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        profile = candidate.Clone();
                        return true;
                    }
                }
            }

            profile = null!;
            return false;
        }

        public NetworkSimulationProfile Clone()
        {
            var copy = (NetworkSimulationProfile)MemberwiseClone();
            copy.Outages = new List<NetworkOutage>(Outages);
            return copy;
        }

        /// <summary>
        /// The one-way options for <paramref name="side"/>. The stream seed mixes in the side so that the two
        /// directions do not drop the same message indices.
        /// </summary>
        public NetworkSimulationOptions ToOptions(NetworkSimulationSide side, ulong seed)
        {
            return new NetworkSimulationOptions
            {
                LatencyMs = RttMs / 2.0,
                JitterMs = RttJitterMs / 2.0,
                LossChance = LossChance,
                BurstMeanLength = BurstMeanLength,
                ReorderChance = ReorderChance,
                DuplicateChance = DuplicateChance,
                BandwidthBytesPerSecond = side == NetworkSimulationSide.Server ? ServerSendBytesPerSecond : ClientSendBytesPerSecond,
                Outages = new List<NetworkOutage>(Outages),
                Seed = NetworkSimRandom.Mix(seed, side == NetworkSimulationSide.Server ? 2UL : 1UL),
            };
        }

        public NetworkSimulation Resolve(NetworkSimulationSide side, ulong seed) =>
            NetworkSimulation.Create(ToOptions(side, seed));
    }
}
