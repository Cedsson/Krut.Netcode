using System;
using System.Collections.Generic;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Krut.Netcode
{
    /// <summary>
    /// The library's server: listens on a UDP port, lets clients in after a
    /// handshake, and is the authoritative side of a session.
    /// <para>
    /// Used both by a normal dedicated server and by a "listen server" in the
    /// game itself: if nothing listens on the port the server is just a bit
    /// of networking code costing a few kilobytes of memory, so a game can
    /// always run the same code path.
    /// </para>
    /// <para>
    /// The server is authoritative for game state: clients send their
    /// requests/intentions, the server decides what happens. That is not a
    /// rule of thumb but a requirement - a client could otherwise cheat with
    /// its own position, and a desynced client would get the server to run
    /// the same thing twice.
    /// </para>
    /// </summary>
    /// <para>
    /// <b>Not <c>sealed</c>.</b> The point of the class is that a game can
    /// build its own session layer on top (e.g. a race server
    /// which counts laps and checkpoints). <c>sealed</c> would have forced
    /// either inheritance straight from <see cref="NetworkSession"/> with its
    /// own thread, or a composed wrapper around the session - and in both
    /// cases that moves the tick pumping from the library to the game, which is
    /// exactly what the library'should own.
    /// </para>
    public class NetworkServer : NetworkSession
    {
        /// <summary>PlayerInfo for peer ids that have already joined - sent to new clients so that their lobby is complete immediately.</summary>
        private readonly Dictionary<int, PlayerInfoMessage> _playerInfo = new();

        private bool _started;

        /// <param name="config">Settings to run with, or null for the defaults (see <see cref="NetworkConfig"/>). Copied, so changing your own instance afterwards has no effect on the running server.</param>
        public NetworkServer(NetworkConfig? config = null)
            : base(config ?? new NetworkConfig(), isServer: true)
        {
        }

        /// <summary>Number of clients connected right now.</summary>
        public int ClientCount => ConnectedPeerCount;

        /// <summary>The port the server listens on (0 before <see cref="Start"/> has run, and the chosen port afterwards).</summary>
        public int Port => Config.Port;

        /// <summary>Who gets to come in.</summary>
        public Func<NetworkConnectionRequest, NetworkConnectionResult>? ConnectionApproval { get; set; }

        /// <summary>Listens on <see cref="NetworkConfig.Port"/>. Returns false if the port could not be bound (e.g. it is taken).</summary>
        public bool Start()
        {
            if (_started)
                return true;

            if (!StartListener(Config.Port))
            {
                Report(new NetworkError(NetworkErrorCode.StartFailed, $"Could not listen on port {Config.Port}."));
                return false;
            }

            _started = true;
            SetRunning(true);
            return true;
        }

        /// <summary>
        /// Stops the server and disconnects all clients with "The server is shutting down". The order matters
        /// and is dangerous to "clarify": <c>IsRunning</c> must not be set to false here.
        /// </summary>
        public void Stop()
        {
            if (!_started)
                return;

            _started = false;
            Dispose();
            _playerInfo.Clear();
        }

        /// <summary>Sends a message to all clients.</summary>
        public void SendToAll(INetworkMessage message) => Broadcast(message);

        /// <summary>
        /// Disconnects a client. A message and a disconnect are two different packets, and the disconnect goes
        /// first - the client closes its socket as soon as it gets the connection packet, so the explanation
        /// never arrives.
        /// </summary>
        public bool DisconnectPeer(int peerId, string? reason = null)
        {
            if (!TryGetPeer(peerId, out var peer))
                return false;

            // The explanation is embedded in the disconnect packet too, not only in a system message
            // beforehand: the text in a message and the text in the disconnect data travel different paths, and
            // only the one in the disconnect data arrives if the connection broke in the middle of a send. The
            // fallback text makes sure the field is never empty - an empty additional data makes the client's
            // reader unsure.
            peer.Disconnect(Reason(reason ?? "The server disconnected you."));
            return true;
        }

        /// <summary>Disconnects all clients (but leaves the server listening).</summary>
        public void DisconnectAll() => DisconnectAll("The server is restarting.");

        /// <summary>Disconnects all clients with an explanation of their own in the log.</summary>
        public void DisconnectAll(string reason)
        {
            foreach (var peer in new List<NetworkPeerState>(Peers))
                DisconnectPeer(peer.Id, reason);
        }

        /// <inheritdoc/>
        /// <para>
        /// The join sequence, in the order it runs, and why:
        /// <list type="number">
        /// <item>The new peer's own <see cref="PlayerInfoMessage"/> is stored
        /// <i>before</i> the lobby is sent, so that the new client gets its
        /// own row too. Otherwise exactly that player is missing from their
        /// own lobby list, and a game that builds its list on PeerId to
        /// PlayerInfo gets a row that cannot be found - and that is a list
        /// that gets counted and sorted.</item>
        /// <item>The welcome message goes out first: the client needs its
        /// peer id before anything else can be sent, and it gets the server's
        /// tick so that both sides count in the same time base.</item>
        /// <item>Then the whole lobby (including the peer's own row), so the
        /// new client doesn't have to ask "who is here?".</item>
        /// <item>Those who were already in get the new player - the peer's
        /// own row it already got above, so the duplication is avoided.</item>
        /// <item>Finally the system chat line, which is what a lobby screen
        /// shows in its log.</item>
        /// </list>
        /// </para>
        protected override void OnPeerConnectedEvent(NetworkPeerState peer)
        {
            var joined = Rent<PlayerInfoMessage>();
            joined.PeerId = peer.Id;
            joined.PlayerName = peer.Name;
            joined.JoinTick = CurrentTick;

            // Stored BEFORE the lobby is sent, so that the new client gets its own row too. Otherwise exactly
            // that player is missing from their own lobby list, and a game that builds its list on PeerId to
            // PlayerInfo gets a row that cannot be found - and that is a list that gets counted and sorted.
            _playerInfo[peer.Id] = joined;

            // The welcome message first: the client needs its peer id before anything else can be sent, and it
            // gets the server's tick so that both sides count in the same time base.
            var welcome = Rent<WelcomeMessage>();
            welcome.PeerId = peer.Id;
            welcome.TickRate = Config.TickRate;
            welcome.ServerTick = CurrentTick;
            welcome.PlayerName = peer.Name;
            Send(peer.Id, welcome);

            // Then the whole lobby (including the peer's own row), so the new
            // client doesn't have to ask "who is here?".
            foreach (var info in _playerInfo.Values)
                Send(peer.Id, CloneInfo(info));

            // Those who were already in get the new player - the peer's own
            // row it already got above, so the duplication is avoided.
            Broadcast(CloneInfo(joined), peer.Id);

            BroadcastSystemChat($"{peer.Name} joined.");
        }

        /// <inheritdoc/>
        protected override void OnPeerDisconnectedEvent(NetworkPeerState peer, DisconnectInfo info)
        {
            // Only a peer that actually joined should be reported in the chat. A connection that was never
            // accepted (rejected app id, wrong version, full lobby) also gives a PeerDisconnected, but without
            // it ever having been in the lobby - writing "Player left" in the log for every stray process on
            // the port would be spam, and a player would see their own "joined" line disappear right away.
            if (!_playerInfo.Remove(peer.Id))
                return;

            BroadcastSystemChat($"{peer.Name} left ({DescribeReason(info.Reason)}).");
        }

        /// <summary>
        /// The handshake, and the one place a connection can be rejected.
        /// <para>
        /// The client sends the app id, the protocol version and the name in a
        /// dedicated area of the first packet (see
        /// <see cref="NetworkClient.Connect"/>). They are read here, while
        /// the connection is not yet up - LiteNetLib expects
        /// <c>Accept</c>/<c>Reject</c> in the
        /// <see cref="NetworkListener.OnConnectionRequest"/> callback and
        /// does nothing at all if neither is called, so this method has
        /// exactly one exit per decision.
        /// </para>
        /// <para>
        /// <b>The accept and reject paths, and what each rejection reason
        /// means to the caller.</b> The order is deliberate: the cheap
        /// identity checks run before the game is asked anything.
        /// <list type="bullet">
        /// <item><b>Wrong application id</b> - rejected <i>silently</i>, with
        /// no additional data. Another game shares the port, and it has
        /// nothing to do with our error message; telling it anyway would leak
        /// our app id to a process that never meant to talk to us.</item>
        /// <item><b>Wrong protocol version</b> - rejected with a reason, so
        /// an old build gets an understandable message ("update the game")
        /// instead of garbled messages for the rest of the session. This is
        /// the only rejection whose text is deliberately informative.</item>
        /// <item><b><see cref="ConnectionApproval"/> refuses</b> - rejected
        /// with the game's own reason, verbatim. What it says is up to the
        /// game; the library only carries it.</item>
        /// <item><b>Lobby full</b> - the built-in refusal when no
        /// <see cref="ConnectionApproval"/> is set and
        /// <see cref="ClientCount"/> is at
        /// <see cref="NetworkConfig.MaxClients"/>. The reason includes the
        /// counts, because "full" without them is hard to act on.</item>
        /// <item><b>Accepted</b> - the peer is attached to the peer list right
        /// here (see <see cref="Attach"/>) with its sanitized name and the
        /// current tick, and the actual welcome sequence runs later from
        /// <see cref="OnPeerConnectedEvent"/>.</item>
        /// </list>
        /// </para>
        /// </summary>
        internal override void HandleConnectionRequest(ConnectionRequest request)
        {
            // The handshake: the client sends app id, protocol version and name in a dedicated area of the
            // first packet.
            var data = request.Data;
            string applicationId = string.Empty;
            int protocolVersion = 0;
            string name = "Player";

            if (data != null && !data.IsNull)
            {
                applicationId = data.GetString() ?? string.Empty;
                data.TryGetInt(out protocolVersion);
                if (data.TryGetString(out var sentName))
                    name = sentName!;
            }

            if (applicationId != Config.ApplicationId)
            {
                // Wrong game on the same port - answer silently (the other
                // app has nothing to do with our error message).
                request.Reject();
                return;
            }

            if (protocolVersion != Config.ProtocolVersion)
            {
                request.Reject(Reason($"Protocol version {protocolVersion} does not match the server's {Config.ProtocolVersion} - update the game."));
                return;
            }

            name = NetworkCodec.SanitizeName(name);
            var approval = ConnectionApproval;
            var result = NetworkConnectionResult.Accept();

            if (approval != null)
            {
                try
                {
                    result = approval(new NetworkConnectionRequest(request.RemoteEndPoint, protocolVersion, applicationId, name));
                }
                catch (Exception exception)
                {
                    // The game's own approval code threw - letting the connection in in an unknown state is
                    // worse than closing the door, and the error is logged so that the bug is visible.
                    Report(new NetworkError(NetworkErrorCode.StartFailed, "Error in ConnectionApproval: " + exception.Message));
                    result = NetworkConnectionResult.Reject("The server could not accept you right now.");
                }
            }
            else if (ClientCount >= Config.MaxClients)
            {
                result = NetworkConnectionResult.Reject($"The lobby is full ({ClientCount}/{Config.MaxClients}).");
            }

            if (!result.Accepted)
            {
                request.Reject(Reason(result.Reason));
                return;
            }

            var netPeer = request.Accept();
            if (netPeer is null)
                return;

            // The name is set already here (the peer object exists) so that OnPeerConnectedEvent doesn't have
            // to wait for a structure of its own.
            DescribePeer(Attach(netPeer), name, CurrentTick);
        }

        /// <inheritdoc/>
        protected override void OnStopping()
        {
            BroadcastSystemChat("The server is shutting down.");
        }

        /// <summary>System message to all clients.</summary>
        private void BroadcastSystemChat(string text)
        {
            var message = Rent<SystemChatMessage>();
            message.Text = text;
            SendToAll(message);
        }

        /// <summary>
        /// Attaches a new peer to the session's peer list (called straight from the handshake, before the
        /// connection is official).
        /// </summary>
        private NetworkPeerState Attach(NetPeer peer) => AddPeerDirect(peer);

        /// <summary>
        /// A fresh instance for each recipient: the lobby row is rented out and recycled by <see cref="Send"/>,
        /// so sending the stored instance to two peers would hand the same object to two sends and the first
        /// one to send would recycle it out from under the second.
        /// </summary>
        private static PlayerInfoMessage CloneInfo(PlayerInfoMessage source)
        {
            var clone = NetworkMessagePool<PlayerInfoMessage>.Rent();
            clone.PeerId = source.PeerId;
            clone.JoinTick = source.JoinTick;
            clone.PlayerName = source.PlayerName;
            return clone;
        }

        /// <summary>
        /// Wraps a reason string in the data section LiteNetLib delivers alongside a disconnect. 64 bytes is
        /// enough for any message a game would put here, and the writer is built per call because this happens
        /// once per disconnect, not per packet.
        /// </summary>
        private NetDataWriter Reason(string text)
        {
            var writer = new NetDataWriter(true, 64);
            writer.Put(text);
            return writer;
        }

        /// <summary>
        /// A short, lower-case fragment for the "X left (...)." chat line. Deliberately not a sentence: it is
        /// spliced into the broadcast, and the client's own disconnect text (which is a full sentence) lives on
        /// the other side of the wire.
        /// </summary>
        private static string DescribeReason(DisconnectReason reason) => reason switch
        {
            DisconnectReason.RemoteConnectionClose => "closed the connection",
            DisconnectReason.DisconnectPeerCalled => "was disconnected by the server",
            DisconnectReason.ConnectionRejected => "was rejected",
            DisconnectReason.Timeout => "went silent",
            DisconnectReason.ConnectionFailed => "the connection failed",
            DisconnectReason.HostUnreachable => "the host did not answer",
            DisconnectReason.NetworkUnreachable => "the network was unreachable",
            _ => reason.ToString(),
        };
    }
}
