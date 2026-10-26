using System;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Krut.Netcode
{
    /// <summary>
    /// The library's view of a connection: peer id, player name, address and measured ping/packet loss. It stays
    /// readable after the connection closed - that is what lets a disconnect handler still see the name - but
    /// it must not be reused.
    /// </summary>
    public sealed class NetworkPeerState
    {
        /// <summary>Wraps a connected LiteNetLib peer.</summary>
        internal NetworkPeerState(NetPeer peer)
        {
            Peer = peer;
            Id = peer.Id;
            // The address is copied rather than forwarded because the peer can report a different port after a
            // NAT rebinding, and the address as it was at connect time is what belongs in a lobby list.
            Address = new IPEndPoint(peer.Address, peer.Port);
        }

        /// <summary>Peer id (a random int) - the identity used in all messages and stable for the duration of the connection.</summary>
        public int Id { get; }

        /// <summary>
        /// The peer id <see cref="NetworkSession"/> gave the peer just now - <see cref="NetPeer.RemoteId"/> is
        /// the peer's own view of the other party's id, which is something else. A game that wants "what id
        /// does the server call me" wants <see cref="NetworkSession.LocalPeerId"/>, never this property: on a
        /// client <see cref="RemoteId"/> is the server's id, not the client's.
        /// </summary>
        public int RemoteId => Peer.RemoteId;

        /// <summary>The address the peer is connected from (behind NAT the server therefore doesn't see the player's real IP).</summary>
        public IPEndPoint Address { get; }

        /// <summary>
        /// The player name, set by the server from the handshake (see <see cref="NetworkCodec.SanitizeName"/>).
        /// </summary>
        public string Name { get; internal set; } = "Player";

        /// <summary>The server's tick when the peer connected (for lobby sorting: who was here first).</summary>
        public int JoinTick { get; internal set; }

        /// <summary>
        /// The game's own data for the peer (e.g. a reference to the player's entity). <see cref="Tag"/> is the
        /// only field the library never touches - it is the game's slot, and the library neither reads nor clears
        /// it. The game is responsible for nulling it in its own disconnect handler if it holds something that
        /// must not outlive the connection. The type is plain <see cref="object"/> because the library has no
        /// way to know the game's type and must not reference it - that is the whole point of the seam.
        /// </summary>
        public object? Tag { get; set; }

        /// <summary>
        /// The most recently measured one-way latency in ms (from LiteNetLib's keep-alive packets).
        /// </summary>
        public int Ping => Peer.Ping;

        /// <summary>
        /// Round-trip time in ms - in practice twice as large as <see cref="Ping"/> (LiteNetLib's
        /// <c>RoundTripTime</c>). Shown next to <see cref="Ping"/> in the status text because the two move
        /// apart exactly when something is wrong: a large gap between them is the signature of a queue rather
        /// than a long wire, and it's what tells a developer to look at the reliable queue length instead of at
        /// the connection.
        /// </summary>
        public int Rtt => Peer.RoundTripTime;

        /// <summary>Seconds since the last packet from the peer (keep-alive packets count).</summary>
        public float TimeSinceLastPacket => Peer.TimeSinceLastPacket;

        /// <summary>Packets sent to the peer (only when <see cref="NetworkConfig.EnableStatistics"/> is on).</summary>
        public long PacketsSent => Peer.Statistics.PacketsSent;

        /// <summary>Packets received from the peer (only when <see cref="NetworkConfig.EnableStatistics"/> is on).</summary>
        public long PacketsReceived => Peer.Statistics.PacketsReceived;

        /// <summary>
        /// Measured packet loss in percent (only when <see cref="NetworkConfig.EnableStatistics"/> is on).
        /// </summary>
        public int PacketLossPercent => (int)Peer.Statistics.PacketLossPercent;

        /// <summary>
        /// Reliable packets waiting in the queue (rises if the client can't keep up with the network - an early
        /// sign of overload).
        /// </summary>
        public int ReliableQueueLength => Peer.GetPacketsCountInReliableQueue(false);

        /// <summary>The underlying LiteNetLib peer. Internal: game logic goes through this wrapper.</summary>
        internal NetPeer Peer { get; }

        /// <summary>
        /// Disconnects the peer (the equivalent of the server's <c>DisconnectPeer(id, reason)</c>, but without
        /// any server logic behind it - used when the session is shutting down).
        /// </summary>
        /// <param name="additionalData">The bytes to send as the disconnect reason, or null for none.</param>
        internal void Disconnect(NetDataWriter? additionalData)
        {
            if (additionalData is null)
                Peer.Disconnect();
            else
                Peer.Disconnect(additionalData);
        }

        /// <inheritdoc/>
        public override string ToString() => $"#{Id} {Name} ({Address}) ping {Ping}ms";
    }
}
