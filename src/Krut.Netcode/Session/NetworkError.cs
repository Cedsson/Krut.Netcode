using System;

namespace Krut.Netcode
{
    /// <summary>Why the network reported an error - a code only, never an exception thrown at the game logic.</summary>
    public enum NetworkErrorCode
    {
        /// <summary>Error in the network layer itself (UDP socket, port not permitted, broken firewall ...).</summary>
        Socket,

        /// <summary>The connection was rejected by the server (full, wrong app id, wrong version) - <see cref="NetworkError.Message"/> contains the server's explanation.</summary>
        ConnectionRejected,

        /// <summary>The packet was shorter than the sender wrote (bit error or a different protocol).</summary>
        MalformedMessage,

        /// <summary>The message type is not recognised - most common if a client runs a newer build.</summary>
        UnknownMessageType,

        /// <summary>The message did not fit in a UDP packet (too large for the unreliable channel).</summary>
        PacketTooLarge,

        /// <summary>The peer id no longer exists (the connection just closed).</summary>
        PeerUnknown,

        /// <summary>Tried to send/reply with a client that isn't connected, or to start a session that is already up.</summary>
        NotConnected,

        /// <summary>The server could not listen on the port (taken, not permitted).</summary>
        StartFailed,
    }

    /// <summary>A network error to show/log - the library never throws exceptions at the game logic for network problems.</summary>
    public readonly struct NetworkError
    {
        public NetworkError(NetworkErrorCode code, string message, int peerId = -1, System.Net.IPEndPoint? endPoint = null)
        {
            Code = code;
            Message = message;
            PeerId = peerId;
            EndPoint = endPoint;
        }

        /// <summary>What went wrong.</summary>
        public NetworkErrorCode Code { get; }

        /// <summary>Explanation in English (or the server's own text on a rejected connection) - meant to be shown in the log/UI.</summary>
        public string Message { get; }

        /// <summary>The peer id it applied to, or -1 (e.g. a socket-level error).</summary>
        public int PeerId { get; }

        /// <summary>The address it applied to, if the error came from a specific socket.</summary>
        public System.Net.IPEndPoint? EndPoint { get; }

        /// <inheritdoc/>
        public override string ToString() => PeerId >= 0 ? $"{Code}: {Message} (peer {PeerId})" : $"{Code}: {Message}";
    }
}
