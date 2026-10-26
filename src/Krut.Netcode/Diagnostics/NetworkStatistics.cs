using System;

namespace Krut.Netcode
{
    /// <summary>Numbers about the session's traffic - for the console, the debug overlay and profiling.</summary>
    public readonly struct NetworkStatistics(long packetsSent, long packetsReceived, long bytesSent, long bytesReceived, long packetLossPercent)
    {
        /// <summary>Packets sent (UDP datagrams, all peers, not messages).</summary>
        public long PacketsSent { get; } = packetsSent;

        /// <summary>Packets received.</summary>
        public long PacketsReceived { get; } = packetsReceived;

        /// <summary>Bytes sent.</summary>
        public long BytesSent { get; } = bytesSent;

        /// <summary>Bytes received.</summary>
        public long BytesReceived { get; } = bytesReceived;

        /// <summary>Measured packet loss in percent - LiteNetLib's own estimate, not a measurement.</summary>
        public long PacketLossPercent { get; } = packetLossPercent;

        /// <inheritdoc/>
        public override string ToString() => $"{PacketsSent} sent / {PacketsReceived} received packets, {BytesSent / 1024} kB out / {BytesReceived / 1024} kB in, {PacketLossPercent}% loss";
    }
}
