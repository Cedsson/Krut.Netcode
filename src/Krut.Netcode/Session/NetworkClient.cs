using System;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Krut.Netcode
{
    /// <summary>
    /// The client side: dials a server, sends the handshake, keeps the
    /// connection alive and tells the game when it is ready.
    /// <para>
    /// Two things are worth knowing about the client: (1) the connection is
    /// <i>up</i> long before <see cref="NetworkMessageType.Welcome"/>
    /// arrives - LiteNetLib's "connected" only means the UDP handshake
    /// worked, and the peer id the game needs for its own entities is first
    /// in the welcome message. That is why both
    /// <see cref="NetworkSession.PeerConnected"/> and
    /// <see cref="SessionReady"/> exist: the first is "the network is
    /// answering", the second is "the game can start replicating". (2) The
    /// server's tick is sent in the welcome message and the client adjusts
    /// its clock after it, so that both sides count in the same time base.
    /// </para>
    /// </summary>
    /// <para>
    /// <b>Not <c>sealed</c>.</b> The same reasoning as <see cref="NetworkServer"/>: a game
    /// must be able to inherit in order to add its own session logic, without the library being forced to give up
    /// ownership of the tick pumping.
    /// </para>
    public class NetworkClient : NetworkSession
    {
        /// <summary>The address last connected to ("127.0.0.1", "game.example.com" ...).</summary>
        private string _address = "127.0.0.1";

        /// <summary>The port last connected to.</summary>
        private int _port;

        /// <summary>The local port the last connect sent from (0 = any). Kept for the reconnect loop.</summary>
        private int _localPort;

        /// <summary>The local UDP port the client is bound to right now (0 when not running).</summary>
        public int LocalPort => IsRunning ? BoundPort : 0;

        /// <summary>
        /// Seconds accumulated in <see cref="NetworkClientState.Reconnecting"/>, counted against
        /// <see cref="NetworkConfig.ReconnectDelayMs"/>.
        /// </summary>
        private float _reconnectTimer;

        /// <summary>
        /// Number of reconnect attempts made since the connection dropped. Reset by <see cref="Connect"/>,
        /// <see cref="Disconnect"/>, <see cref="Stop"/> and by every <see cref="OnPeerDisconnectedEvent"/> - so
        /// it always counts attempts for the current outage and never carries over into the next one.
        /// </summary>
        private int _attempts;

        /// <summary>The state the client is in.</summary>
        private NetworkClientState _state = NetworkClientState.Offline;

        /// <param name="config">Settings to run with, or null for the defaults (see <see cref="NetworkConfig"/>). Copied, so changing your own instance afterwards has no effect on the running client.</param>
        public NetworkClient(NetworkConfig? config = null)
            : base(config ?? new NetworkConfig(), isServer: false)
        {
            _port = Config.Port;

            // Listens for the server's messages right in the constructor, so that the welcome message is always
            // handled BEFORE the game's own listeners - otherwise the game can reply to a message before the
            // peer id/tick are set.
            MessageReceived += HandleMessage;
        }

        /// <summary>What the client is doing right now (see <see cref="NetworkClientState"/>).</summary>
        public NetworkClientState State => _state;

        /// <summary>True when the connection is up AND the welcome message has arrived - the state the game should replicate in.</summary>
        public bool IsSessionReady => _state == NetworkClientState.Connected;

        /// <summary>The address the client is connected to (empty when it is not running).</summary>
        public string Address => _address;

        /// <summary>The port the client is connected to.</summary>
        public int Port => _port;

        /// <summary>The player's name as sent in the handshake (sanitized by the server).</summary>
        public string PlayerName { get; private set; } = "Player";

        /// <summary>The server's peer id (only a client has one).</summary>
        public int ServerPeerId => RemotePeer?.Id ?? -1;

        /// <summary>
        /// The welcome message has arrived and the session is ready: peer id, tick rate and name are set.
        /// Raised exactly once per connection, after <see cref="LocalPeerId"/> has been given its value.
        /// </summary>
        public event Action? SessionReady;

        /// <summary>The server closed the connection, or it was lost.</summary>
        public event Action<DisconnectReason, string>? Closed;

        /// <summary>
        /// Number of reconnect attempts made since the connection was lost (only if
        /// <see cref="NetworkConfig.AutoReconnect"/> is on). It is reset to 0 on every successful
        /// <see cref="Connect"/>, on every <see cref="Disconnect"/> and <see cref="Stop"/>, and on every
        /// disconnect - so it always counts the attempts of the current outage, never a mixture of two.
        /// </summary>
        public int ReconnectAttempts => _attempts;

        /// <summary>
        /// Sets the name that is sent in the handshake. Call before <see cref="Connect"/> (a name changed in
        /// the middle of a match must not swap identity - the peer id is the identity).
        /// </summary>
        public void SetPlayerName(string? name) => PlayerName = NetworkCodec.SanitizeName(name);

        /// <summary>Dials <paramref name="address"/>.</summary>
        public bool Connect(string address, int port = 0, string? playerName = null, int localPort = 0)
        {
            if (IsRunning)
                return false;

            if (playerName != null)
                SetPlayerName(playerName);

            _address = string.IsNullOrWhiteSpace(address) ? "127.0.0.1" : address.Trim();
            _port = port > 0 ? port : Config.Port;
            _localPort = localPort is > 0 and <= 65535 ? localPort : 0;

            if (!StartListener(null, _localPort))
            {
                Report(new NetworkError(NetworkErrorCode.StartFailed, "Could not open a local socket for networking."));
                _state = NetworkClientState.Offline;
                return false;
            }

            IPEndPoint endPoint;
            try
            {
                endPoint = NetUtils.MakeEndPoint(_address, _port);
            }
            catch (Exception)
            {
                Report(new NetworkError(NetworkErrorCode.StartFailed, $"Invalid address: {_address}."));
                _state = NetworkClientState.Offline;
                return false;
            }

            // The handshake is sent as raw bytes in the first packet, so that the server can read app
            // id/version/name before it decides (see NetworkServer.HandleConnectionRequest).
            var payload = new NetDataWriter(true, 96);
            payload.Put(Config.ApplicationId);
            payload.Put(Config.ProtocolVersion);
            NetworkCodec.WriteName(payload, PlayerName);

            if (ConnectTo(endPoint, payload.Data.AsSpan(0, payload.Length)) is null)
            {
                Report(new NetworkError(NetworkErrorCode.NotConnected, $"Could not send the connection request to {_address}:{_port}."));
                _state = NetworkClientState.Offline;
                return false;
            }

            // Set here, not when the connection comes up: Update must run the network from the very first
            // frame, otherwise the server never answers (events are delivered in PollEvents, and it is Update
            // that calls it).
            SetRunning(true);
            _state = NetworkClientState.Connecting;
            _attempts = 0;
            _reconnectTimer = 0f;
            return true;
        }

        /// <summary>Disconnects from the server (voluntarily, e.g. when the player leaves a match).</summary>
        public void Disconnect(string? reason = null)
        {
            if (!IsRunning)
            {
                _state = NetworkClientState.Offline;
                return;
            }

            var peer = RemotePeer;
            if (peer != null && !string.IsNullOrEmpty(reason))
            {
                var notice = Rent<SystemChatMessage>();
                notice.Text = reason!;
                Send(peer.Id, notice);
            }

            peer?.Disconnect(null);
            StopListener();
            SetRunning(false);
            SetLocalPeerId(-1);
            _state = NetworkClientState.Offline;
            _attempts = 0;
        }

        /// <summary>Stops the client completely, including an ongoing reconnect.</summary>
        public void Stop()
        {
            _state = NetworkClientState.Offline;
            _attempts = 0;
            Dispose();
        }

        /// <summary>
        /// The library-level reconnect loop, on top of the base class's frame pump. What must also run outside
        /// that is the reconnect logic below - otherwise the client would never try again after a lost network,
        /// because <c>IsRunning</c> is false then. Rejections are the exception and never retried at all - that
        /// decision lives in <see cref="OnPeerDisconnectedEvent"/>.
        /// </summary>
        public override void Update(float deltaSeconds)
        {
            base.Update(deltaSeconds);

            if (_state != NetworkClientState.Reconnecting)
                return;

            _reconnectTimer += deltaSeconds;
            if (_reconnectTimer < Config.ReconnectDelayMs / 1000f)
                return;

            _reconnectTimer = 0f;
            int attempts = _attempts + 1;
            // The lost connection leaves the socket running, and Connect refuses to dial on a running session.
            StopListener();
            SetRunning(false);
            Connect(_address, _port, localPort: _localPort);
            // Connect resets the counter for a fresh dial; a retry keeps counting the same outage.
            _attempts = attempts;
        }

        /// <summary>
        /// The peer object exists, but the session is not ready yet: the peer id and the server's tick come in
        /// the welcome message, which can arrive a frame after the UDP connection became official.
        /// </summary>
        protected override void OnPeerConnectedEvent(NetworkPeerState peer) => base.OnPeerConnectedEvent(peer);

        /// <inheritdoc/>
        /// <para>
        /// The state transition this drives, and the one case that must never
        /// retry:
        /// <list type="bullet">
        /// <item><b><see cref="NetworkClientState.Reconnecting"/></b> - set
        /// for every loss that isn't a rejection, and only when
        /// <see cref="NetworkConfig.AutoReconnect"/> is on. The local peer
        /// id is cleared to -1 first, so a game that keeps sending during
        /// <c>Closed</c> cannot use a stale id.</item>
        /// <item><b><see cref="NetworkClientState.Offline"/></b> - set on a
        /// <see cref="DisconnectReason.ConnectionRejected"/>, whatever
        /// <see cref="NetworkConfig.AutoReconnect"/> says. A rejection is an
        /// answer, not an outage: retrying would only be rejected again, and
        /// a client hammering a full lobby is indistinguishable from an
        /// attack.</item>
        /// <item><b><c>_attempts = 0</c></b> - when a ready session is lost,
        /// so the counter is attempts within the <i>current</i> outage. A
        /// failed retry keeps counting, and after
        /// <see cref="NetworkConfig.MaxConnectAttempts"/> retries the client
        /// goes <see cref="NetworkClientState.Offline"/>.</item>
        /// </list>
        /// </para>
        protected override void OnPeerDisconnectedEvent(NetworkPeerState peer, DisconnectInfo info)
        {
            base.OnPeerDisconnectedEvent(peer, info);
            SetLocalPeerId(-1);

            var reason = info.Reason;
            string text = DescribeReason(reason);

            // The server's own explanation wins over our generic text, no matter HOW the connection broke.
            if (!info.AdditionalData.IsNull && info.AdditionalData.TryGetString(out var serverText) && !string.IsNullOrWhiteSpace(serverText))
                text = serverText!;

            // The server's explanation is always more important than our own
            // - and on a rejection we must not try again (it would be
            // rejected again).
            if (_state == NetworkClientState.Connected)
                _attempts = 0;

            bool givenUp = _attempts >= Config.MaxConnectAttempts;
            _state = reason == DisconnectReason.ConnectionRejected || !Config.AutoReconnect || givenUp
                ? NetworkClientState.Offline
                : NetworkClientState.Reconnecting;

            if (givenUp && Config.AutoReconnect && reason != DisconnectReason.ConnectionRejected)
                Report(new NetworkError(NetworkErrorCode.NotConnected, $"Gave up reconnecting to {_address}:{_port} after {_attempts} attempts."));

            Closed?.Invoke(reason, text);
        }

        /// <inheritdoc/>
        internal override void HandleConnectionRequest(ConnectionRequest request)
        {
            // A client does not listen for connections.
            request.Reject();
        }

        /// <inheritdoc/>
        protected override void OnStopping()
        {
            base.OnStopping();
            _state = NetworkClientState.Offline;
        }

        /// <summary>
        /// Listens for the server's messages: the welcome message sets the peer id, the tick and the name.
        /// </summary>
        private void HandleMessage(NetworkEnvelope envelope)
        {
            if (envelope.Message is WelcomeMessage welcome)
            {
                SetLocalPeerId(welcome.PeerId);
                PlayerName = welcome.PlayerName;
                ResetClockTo(welcome.ServerTick);
                _state = NetworkClientState.Connected;
                SessionReady?.Invoke();
            }
        }

        /// <summary>
        /// The client-side fallback text for a disconnect reason, used only when the server sent no explanation
        /// of its own. Why a generic client-side text must never replace a server-supplied reason.
        /// </summary>
        private static string DescribeReason(DisconnectReason reason) => reason switch
        {
            DisconnectReason.RemoteConnectionClose => "The server closed the connection.",
            DisconnectReason.DisconnectPeerCalled => "The server disconnected you.",
            DisconnectReason.ConnectionRejected => "The server rejected the connection.",
            DisconnectReason.Timeout => "The server did not answer (timeout).",
            DisconnectReason.ConnectionFailed => "Could not connect to the server.",
            DisconnectReason.HostUnreachable => "The server could not be reached.",
            DisconnectReason.NetworkUnreachable => "The network is unreachable.",
            DisconnectReason.UnknownHost => "The server's address could not be resolved.",
            DisconnectReason.InvalidProtocol => "The server runs a different game/protocol.",
            DisconnectReason.PeerToPeerConnection => "The connection was broken by a peer-to-peer channel.",
            _ => $"The connection ended ({reason}).",
        };
    }

    /// <summary>What <see cref="NetworkClient"/> is doing right now - typically shown in a menu ("Reconnecting...").</summary>
    /// <para>
    /// The state machine, as an explicit list of states and their
    /// transitions. Every edge below is written in the file where the decision
    /// lives:
    /// <list type="bullet">
    /// <item><b>Offline</b> - the starting state, and the only one where
    /// <see cref="NetworkClient.Update"/> does not touch the network. Reached
    /// from <see cref="NetworkClient.Connect"/> when the address or the
    /// connection request fails, from
    /// <see cref="NetworkClient.Disconnect"/>/<see cref="NetworkClient.Stop"/>,
    /// from <see cref="NetworkClient.OnStopping"/>, and from any disconnect
    /// that must not be retried (a rejection, or
    /// <see cref="NetworkConfig.AutoReconnect"/> off). Leaves only to
    /// <see cref="NetworkClient.Connect"/>.</item>
    /// <item><b>Connecting</b> - the request is out, the server has not
    /// answered. Entered at the end of a successful
    /// <see cref="NetworkClient.Connect"/>, which is also where
    /// <c>IsRunning</c> becomes true so that <c>Update</c> pumps the network.
    /// Leaves to <b>Connected</b> (welcome message arrived, via
    /// <see cref="NetworkClient.SessionReady"/>), to <b>Reconnecting</b> (the
    /// request timed out or the connection failed), or back to
    /// <b>Offline</b>.</item>
    /// <item><b>Connected</b> - the only state in which
    /// <see cref="NetworkClient.IsSessionReady"/> is true, and therefore the
    /// only one in which a game may replicate. Entered when the welcome
    /// message has been handled; leaves to <b>Reconnecting</b> or
    /// <b>Offline</b> on any disconnect.</item>
    /// <item><b>Reconnecting</b> - the connection was lost and a new one is
    /// being attempted. Entered from <b>Connected</b> or <b>Connecting</b>
    /// when the connection drops and
    /// <see cref="NetworkConfig.AutoReconnect"/> is on. It is a real state
    /// and not just a flag: <c>IsRunning</c> is false, so the base class does
    /// not pump the network, and only the override's timer keeps the client
    /// alive. Leaves to <b>Connecting</b> after
    /// <see cref="NetworkConfig.ReconnectDelayMs"/>, or back to
    /// <b>Offline</b> if that attempt is rejected.</item>
    /// </list>
    /// </para>
    public enum NetworkClientState
    {
        /// <summary>No connection, no attempt in progress.</summary>
        Offline,

        /// <summary>Request sent - waiting for the server's answer (the UDP handshake).</summary>
        Connecting,

        /// <summary>Connected and the welcome message has arrived - the game can replicate.</summary>
        Connected,

        /// <summary>The connection was lost and a new one is being attempted (see <see cref="NetworkConfig.AutoReconnect"/>).</summary>
        Reconnecting,
    }
}
