using System;
using System.Collections.Generic;

namespace Krut.Netcode
{
    /// <summary>Which end of the link a simulation is applied on. Each side delays and drops what it sends.</summary>
    public enum NetworkSimulationSide
    {
        /// <summary>Uplink: client to server.</summary>
        Client,

        /// <summary>Downlink: server to client.</summary>
        Server,
    }

    /// <summary>A total outage: everything sent in the window is lost. Times are seconds since the simulation was bound.</summary>
    public readonly record struct NetworkOutage(double StartSeconds, double DurationSeconds)
    {
        public double EndSeconds => StartSeconds + DurationSeconds;
    }

    /// <summary>
    /// The raw knobs of one direction of a link, in one-way terms. <see cref="NetworkSimulation.Create(NetworkSimulationOptions)"/>
    /// clamps them. Zero or false means the effect is off.
    /// </summary>
    public sealed class NetworkSimulationOptions
    {
        /// <summary>One-way delay in ms (fractions allowed).</summary>
        public double LatencyMs { get; set; }

        /// <summary>Uniform +/- variation of the delay in ms.</summary>
        public double JitterMs { get; set; }

        /// <summary>Long-run loss fraction 0..1.</summary>
        public double LossChance { get; set; }

        /// <summary>Mean length of a loss burst in messages. 1 or less means independent loss (Gilbert-Elliott off).</summary>
        public double BurstMeanLength { get; set; }

        /// <summary>Fraction of messages that are overtaken by later ones.</summary>
        public double ReorderChance { get; set; }

        /// <summary>Extra delay of a reordered message in ms (drawn from 1x to 2x this value). 0 selects the default.</summary>
        public double ReorderDelayMs { get; set; }

        /// <summary>Fraction of messages that arrive twice.</summary>
        public double DuplicateChance { get; set; }

        /// <summary>Token-bucket rate in bytes per second of this side's sends. 0 means no cap.</summary>
        public int BandwidthBytesPerSecond { get; set; }

        /// <summary>Token-bucket depth in bytes. 0 selects one MTU (1500).</summary>
        public int BucketBytes { get; set; }

        /// <summary>Bound of the bandwidth queue in bytes. 0 selects max(3 MTU, rate / 4).</summary>
        public int QueueLimitBytes { get; set; }

        /// <summary>Total outages.</summary>
        public List<NetworkOutage> Outages { get; set; } = new();

        /// <summary>
        /// Loss, burst loss and outages are applied to LiteNetLib datagrams by a <see cref="NetworkDatagramSimLayer"/>
        /// instead of to messages (needs a <see cref="NetworkDatagramChain"/> with that layer).
        /// </summary>
        public bool DatagramLevel { get; set; }

        /// <summary>Seed of the random streams.</summary>
        public ulong Seed { get; set; } = NetworkSimulation.DefaultSeed;

        public NetworkSimulationOptions Clone()
        {
            var copy = (NetworkSimulationOptions)MemberwiseClone();
            copy.Outages = new List<NetworkOutage>(Outages);
            return copy;
        }
    }
}
