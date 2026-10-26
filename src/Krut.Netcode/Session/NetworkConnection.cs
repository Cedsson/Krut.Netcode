using System;
using System.Net;

namespace Krut.Netcode
{
    /// <summary>
    /// What the server knows about a client that wants to connect in, well before the connection is up. The
    /// values arrive from the handshake payload, which the client wrote before a single packet of game traffic
    /// existed - so they are exactly as trustworthy as the connection itself, which at this point is a UDP
    /// handshake and nothing more.
    /// </summary>
    public readonly struct NetworkConnectionRequest
    {
        /// <param name="protocolVersion">
        /// The version the client sent, already compared against the server's (see
        /// <see cref="ProtocolVersion"/>).
        /// </param>
        public NetworkConnectionRequest(IPEndPoint remoteEndPoint, int protocolVersion, string applicationId, string playerName)
        {
            RemoteEndPoint = remoteEndPoint;
            ProtocolVersion = protocolVersion;
            ApplicationId = applicationId;
            PlayerName = playerName;
        }

        /// <summary>The address the client is coming from (so behind NAT this is the server's view of it, not the player's real IP).</summary>
        public IPEndPoint RemoteEndPoint { get; }

        /// <summary>Protocol version the client sent (already checked against the server's).</summary>
        public int ProtocolVersion { get; }

        /// <summary>App id the client sent (already checked - it matches, otherwise it would have been rejected).</summary>
        public string ApplicationId { get; }

        /// <summary>The player name the client chose, already sanitized (see <see cref="NetworkCodec.SanitizeName"/>).</summary>
        public string PlayerName { get; }
    }

    /// <summary>
    /// The server's answer to a <see cref="NetworkConnectionRequest"/>: either the connection is accepted, or
    /// it is disconnected with an explanation that is shown as a system message in the client.
    /// </summary>
    public readonly struct NetworkConnectionResult
    {
        private NetworkConnectionResult(bool accepted, string reason)
        {
            Accepted = accepted;
            Reason = reason;
        }

        /// <summary>Is the connection accepted?</summary>
        public bool Accepted { get; }

        /// <summary>Why the connection was rejected.</summary>
        public string Reason { get; }

        /// <summary>Accept the connection.</summary>
        public static NetworkConnectionResult Accept() => new(true, string.Empty);

        /// <summary>Reject the connection with an explanation to the client.</summary>
        public static NetworkConnectionResult Reject(string reason) => new(false, reason);
    }
}
