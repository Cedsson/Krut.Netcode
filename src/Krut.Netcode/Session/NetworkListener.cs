using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Krut.Netcode
{
    /// <summary>
    /// Listens to LiteNetLib's events and puts them in a queue that <see cref="NetworkSession"/> drains right
    /// after <see cref="NetManager.PollEvents"/> has returned. Why not handle the events directly in the
    /// callback? Two reasons: (1) game logic would then run inside LiteNetLib's own code, which is impossible
    /// to debug ("why is the game standing here in a UDP thread?"), and (2) the <see cref="NetPacketReader"/>
    /// buffer is reused as soon as the callback returns - the message's bytes have to be copied anyway to be
    /// able to handle it in the same frame. It preserves arrival order within one <c>PollEvents</c> call, and
    /// it preserves the bytes (each <see cref="NetworkEvent"/> owns its own array).
    /// </summary>
    internal sealed class NetworkListener : INetEventListener
    {
        private readonly NetworkSession _session;

        public NetworkListener(NetworkSession session) => _session = session;

        /// <summary>The queue is held by the session, which swaps the list when it drains (no allocations per frame).</summary>
        public List<NetworkEvent> Pending => _session.PendingEvents;

        /// <inheritdoc/>
        public void OnPeerConnected(NetPeer peer)
        {
            Pending.Add(NetworkEvent.PeerConnected(peer));
        }

        /// <inheritdoc/>
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            Pending.Add(NetworkEvent.PeerDisconnected(peer, info));
        }

        /// <inheritdoc/>
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod delivery)
        {
            // The session copies the bytes into a pooled buffer - LiteNetLib reuses its own buffer right after we
            // return here, so the copy is not optional.
            _session.EnqueueReceived(peer, reader, NetworkChannels.FromDeliveryMethod(delivery));
        }

        /// <inheritdoc/>
        public void OnNetworkError(IPEndPoint endPoint, SocketError error)
        {
            Pending.Add(NetworkEvent.Socket(endPoint, error));
        }

        /// <inheritdoc/>
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
        {
            // No events: the latency is read straight out of the peer when someone wants it (see
            // NetworkPeerState.Ping) - routing every measurement through a queue would just create work once
            // per second.
        }

        /// <inheritdoc/>
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            // Delivered only when NetworkConfig.UnconnectedMessages is on (LiteNetLib
            // drops them otherwise). Broadcasts are not used by the library.
            if (messageType == UnconnectedMessageType.BasicMessage)
                _session.RaiseUnconnected(remoteEndPoint, reader.GetRemainingBytes());
        }

        /// <inheritdoc/>
        public void OnConnectionRequest(ConnectionRequest request)
        {
            // Must be answered now (before PollEvents returns) - LiteNetLib expects Accept/Reject in the
            // callback.
            _session.HandleConnectionRequest(request);
        }
    }

    /// <summary>
    /// The events <see cref="NetworkListener"/> puts in the queue - a struct, no allocation per event. The four
    /// events are short-lived, they are created and consumed inside a single frame, and they are the only place
    /// the library allocates per received packet (the byte array). Adding a base class and four subclasses would
    /// add a second allocation per packet for a union that is exactly eight fields wide.
    /// </summary>
    internal readonly struct NetworkEvent
    {
        /// <summary>Which event it is (a union instead of a class hierarchy - the events are few and short).</summary>
        public NetworkEventKind Kind { get; private init; }

        /// <summary>The peer, for every kind except <see cref="NetworkEventKind.SocketError"/>. Never null when the kind says otherwise.</summary>
        public NetPeer? Peer { get; private init; }

        /// <summary>
        /// Own copy of the packet bytes in a pooled buffer, only for <see cref="NetworkEventKind.Received"/>. The
        /// buffer is longer than the packet: read <see cref="Length"/> bytes, and return it to the pool when handled.
        /// </summary>
        public byte[]? Data { get; private init; }

        /// <summary>Number of valid bytes in <see cref="Data"/>.</summary>
        public int Length { get; private init; }

        /// <summary>Only for <see cref="NetworkEventKind.PeerDisconnected"/>, and the carrier of the server's disconnect reason text.</summary>
        public DisconnectInfo Disconnect { get; private init; }

        /// <summary>How LiteNetLib actually delivered the packet, mapped back to <see cref="NetworkDelivery"/>.</summary>
        public NetworkDelivery Delivery { get; private init; }

        /// <summary>Only for <see cref="NetworkEventKind.SocketError"/>.</summary>
        public IPEndPoint? EndPoint { get; private init; }

        /// <summary>Only for <see cref="NetworkEventKind.SocketError"/>.</summary>
        public SocketError Error { get; private init; }

        public static NetworkEvent PeerConnected(NetPeer peer) => new() { Kind = NetworkEventKind.PeerConnected, Peer = peer };

        public static NetworkEvent PeerDisconnected(NetPeer peer, DisconnectInfo info)
            => new() { Kind = NetworkEventKind.PeerDisconnected, Peer = peer, Disconnect = info };

        public static NetworkEvent Received(NetPeer peer, byte[] data, int length, NetworkDelivery delivery)
            => new() { Kind = NetworkEventKind.Received, Peer = peer, Data = data, Length = length, Delivery = delivery };

        public static NetworkEvent Socket(IPEndPoint endPoint, SocketError error)
            => new() { Kind = NetworkEventKind.SocketError, EndPoint = endPoint, Error = error };
    }

    /// <summary>
    /// Which of the four queued events this is. The values are internal and the order is not on the wire - a
    /// mismatch with <see cref="NetworkEvent.Kind"/> is a bug the drain switch in <see cref="NetworkSession"/>
    /// would simply not handle, which is why every case there is explicit rather than having a default.
    /// </summary>
    internal enum NetworkEventKind
    {
        PeerConnected,
        PeerDisconnected,
        Received,
        SocketError,
    }
}
