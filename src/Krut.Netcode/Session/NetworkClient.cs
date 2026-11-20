using System;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
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

        /// <summary>Cached server peer id so hot-path sends do not enumerate the peer dictionary.</summary>
        private int _serverPeerId = -1;

        private NatPunchConfig? _natConfig;
        private IPEndPoint? _natRendezvousEndPoint;
        private IPEndPoint? _natInternalEndPoint;
        private string? _natRoomCode;
        private byte[]? _natRequestNonce;
        private byte[]? _natPunchNonce;
        private IPEndPoint? _natHostInternal;
        private IPEndPoint? _natHostExternal;
        private long _natDeadlineTimestamp;
        private long _natNextSendTimestamp;
        private bool _natIntroduced;
        private bool _isNatReconnectSession;

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
        public int ServerPeerId => _serverPeerId >= 0 ? _serverPeerId : (RemotePeer?.Id ?? -1);

        /// <summary>
        /// The welcome message has arrived and the session is ready: peer id, tick rate and name are set.
        /// Raised exactly once per connection, after <see cref="LocalPeerId"/> has been given its value.
        /// </summary>
        public event Action? SessionReady;

        /// <summary>The server closed the connection, or it was lost.</summary>
        public event Action<DisconnectReason, string>? Closed;

        /// <summary>Active or configured NAT room code when connecting via a rendezvous server, or null.</summary>
        public string? NatRoomCode => _natRoomCode;

        /// <summary>Endpoint and address classification resolved during the most recent NAT punchthrough, or null.</summary>
        public NatPunchResult? LastNatPunchResult { get; private set; }

        /// <summary>Raised when NAT hole punching succeeds and the direct LiteNetLib handshake starts.</summary>
        public event Action<NatPunchResult>? NatPunchSucceeded;

        /// <summary>Raised when NAT rendezvous lookup or hole punching fails or times out.</summary>
        public event Action<string>? NatPunchFailed;

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

            ClearNatState(keepReconnectTarget: false);

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
                StopListener();
                Report(new NetworkError(NetworkErrorCode.StartFailed, $"Invalid address: {_address}."));
                _state = NetworkClientState.Offline;
                return false;
            }

            NetDataWriter payload = BuildHandshakePayload();
            if (ConnectTo(endPoint, payload.Data.AsSpan(0, payload.Length)) is null)
            {
                StopListener();
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

        /// <summary>Connects to a host registered under <paramref name="roomCode"/> on the specified NAT rendezvous server.</summary>
        public bool ConnectViaNat(string rendezvousHost, int rendezvousPort, string roomCode, string? playerName = null, int localPort = 0)
        {
            if (rendezvousPort is <= 0 or > 65535)
            {
                string reason = $"Invalid rendezvous port: {rendezvousPort}.";
                NatStats.IncrementPunchesFailed();
                ReportErrorSafely(new NetworkError(NetworkErrorCode.NatPunchFailed, reason));
                InvokeNatFailedSafely(reason);
                return false;
            }

            IPEndPoint endPoint;
            try
            {
                string host = string.IsNullOrWhiteSpace(rendezvousHost) ? "127.0.0.1" : rendezvousHost.Trim();
                endPoint = NetUtils.MakeEndPoint(host, rendezvousPort);
            }
            catch (Exception)
            {
                string reason = $"Invalid rendezvous address: {rendezvousHost}.";
                NatStats.IncrementPunchesFailed();
                ReportErrorSafely(new NetworkError(NetworkErrorCode.NatPunchFailed, reason));
                InvokeNatFailedSafely(reason);
                return false;
            }

            return ConnectViaNat(endPoint, roomCode, playerName, localPort);
        }

        /// <summary>Connects to a host registered under <paramref name="roomCode"/> on the specified NAT rendezvous endpoint.</summary>
        public bool ConnectViaNat(IPEndPoint rendezvousEndPoint, string roomCode, string? playerName = null, int localPort = 0)
        {
            ArgumentNullException.ThrowIfNull(rendezvousEndPoint);

            if (IsRunning)
                return false;

            if (rendezvousEndPoint.Port is <= 0 or > 65535)
            {
                string reason = $"Invalid rendezvous port: {rendezvousEndPoint.Port}.";
                NatStats.IncrementPunchesFailed();
                ReportErrorSafely(new NetworkError(NetworkErrorCode.NatPunchFailed, reason));
                InvokeNatFailedSafely(reason);
                return false;
            }

            if (!NatPunchProtocol.TryValidateRoomCode(roomCode, out string validRoom))
            {
                const string reason = "Room code must be non-empty, <= 64 UTF-8 bytes, and contain no control characters.";
                NatStats.IncrementPunchesFailed();
                ReportErrorSafely(new NetworkError(NetworkErrorCode.NatPunchFailed, reason));
                InvokeNatFailedSafely(reason);
                return false;
            }

            if (!NatPunchProtocol.TryValidateAppId(Config.ApplicationId, out _))
            {
                const string reason = "ApplicationId exceeds the maximum length (64 UTF-8 bytes) for NAT join.";
                NatStats.IncrementPunchesFailed();
                ReportErrorSafely(new NetworkError(NetworkErrorCode.NatPunchFailed, reason));
                InvokeNatFailedSafely(reason);
                return false;
            }

            if (playerName != null)
                SetPlayerName(playerName);

            _localPort = localPort is > 0 and <= 65535 ? localPort : 0;

            if (!StartListener(null, _localPort))
            {
                Report(new NetworkError(NetworkErrorCode.StartFailed, "Could not open a local socket for networking."));
                _state = NetworkClientState.Offline;
                return false;
            }

            var natConfig = (Config.NatPunch ?? new NatPunchConfig()).Clone();
            natConfig.Validate();
            _natConfig = natConfig;

            IPEndPoint normalizedRdv = NatPunchProtocol.NormalizeEndPoint(rendezvousEndPoint);
            EnableUnconnectedMessages();
            ConfigureNatRendezvousRoute(normalizedRdv);

            IPEndPoint internalEp = natConfig.LocalEndPointOverride is not null
                ? (natConfig.LocalEndPointOverride.Port > 0
                    ? NatPunchProtocol.NormalizeEndPoint(natConfig.LocalEndPointOverride)
                    : new IPEndPoint(NatPunchProtocol.NormalizeAddress(natConfig.LocalEndPointOverride.Address), BoundPort))
                : NatPunchProtocol.DiscoverLocalEndPoint(BoundPort, normalizedRdv);

            byte[] reqNonce = new byte[NatPunchProtocol.SecretBytes];
            RandomNumberGenerator.Fill(reqNonce);

            _natRendezvousEndPoint = normalizedRdv;
            _natInternalEndPoint = internalEp;
            _natRoomCode = validRoom;
            _natRequestNonce = reqNonce;
            _natPunchNonce = null;
            _natHostInternal = null;
            _natHostExternal = null;
            _natIntroduced = false;
            _isNatReconnectSession = true;
            LastNatPunchResult = null;

            long now = Stopwatch.GetTimestamp();
            _natDeadlineTimestamp = now + MsToTimestampTicks(natConfig.PunchTimeoutMs);
            _natNextSendTimestamp = now + MsToTimestampTicks(natConfig.PunchIntervalMs);

            SetRunning(true);
            _state = NetworkClientState.Punching;
            _attempts = 0;
            _reconnectTimer = 0f;

            SendClientJoinRequest();
            return true;
        }

        /// <summary>Disconnects from the server (voluntarily, e.g. when the player leaves a match).</summary>
        public void Disconnect(string? reason = null)
        {
            ClearNatState(keepReconnectTarget: false);

            if (!IsRunning)
            {
                _state = NetworkClientState.Offline;
                return;
            }

            var peer = RemotePeer;
            if (peer != null)
            {
                if (!string.IsNullOrEmpty(reason))
                {
                    var writer = new NetDataWriter(true, 64);
                    NetworkCodec.WriteText(writer, NetworkCodec.SanitizeText(reason));
                    peer.Disconnect(writer);
                }
                else
                {
                    peer.Disconnect(null);
                }

                PumpDisconnectNotices();
            }

            StopListener();
            SetRunning(false);
            SetLocalPeerId(-1);
            _serverPeerId = -1;
            _state = NetworkClientState.Offline;
            _attempts = 0;
        }

        /// <summary>Stops the client completely, including an ongoing reconnect.</summary>
        public void Stop()
        {
            ClearNatState(keepReconnectTarget: false);
            _state = NetworkClientState.Offline;
            _attempts = 0;
            ShutdownSession();
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

            if (float.IsFinite(deltaSeconds) && deltaSeconds > 0f)
                _reconnectTimer += deltaSeconds;
            if (_reconnectTimer < Config.ReconnectDelayMs / 1000f)
                return;

            _reconnectTimer = 0f;
            int attempts = _attempts + 1;
            // Ensure socket is closed before dialing a fresh attempt.
            StopListener();
            SetRunning(false);
            bool started = _isNatReconnectSession && _natRendezvousEndPoint is not null && !string.IsNullOrEmpty(_natRoomCode)
                ? ConnectViaNat(_natRendezvousEndPoint, _natRoomCode, localPort: _localPort)
                : Connect(_address, _port, localPort: _localPort);
            // Connect resets the counter for a fresh dial; a retry keeps counting the same outage.
            _attempts = attempts;
            if (!started)
            {
                if (_attempts >= Config.MaxConnectAttempts)
                {
                    _state = NetworkClientState.Offline;
                    Report(new NetworkError(NetworkErrorCode.NotConnected, $"Gave up reconnecting to {_address}:{_port} after {_attempts} attempts."));
                }
                else
                {
                    _state = NetworkClientState.Reconnecting;
                }
            }
        }

        /// <summary>
        /// The peer object exists, but the session is not ready yet: the peer id and the server's tick come in
        /// the welcome message, which can arrive a frame after the UDP connection became official.
        /// </summary>
        protected override void OnPeerConnectedEvent(NetworkPeerState peer)
        {
            _serverPeerId = peer.Id;
            base.OnPeerConnectedEvent(peer);
        }

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
            _serverPeerId = -1;

            var reason = info.Reason;
            string text = DescribeReason(reason);

            // The server's own explanation wins over our generic text, no matter HOW the connection broke.
            if (!info.AdditionalData.IsNull && info.AdditionalData.AvailableBytes >= 2)
            {
                try
                {
                    string rawText = info.AdditionalData.GetString(NetworkCodec.MaxTextLength * 4);
                    string sanitized = NetworkCodec.SanitizeText(rawText);
                    if (!string.IsNullOrWhiteSpace(sanitized))
                        text = sanitized;
                }
                catch (Exception)
                {
                    // Ignore malformed additional disconnect data and keep generic reason text.
                }
            }

            // The server's explanation is always more important than our own
            // - and on a rejection we must not try again (it would be
            // rejected again).
            if (_state == NetworkClientState.Connected)
                _attempts = 0;

            bool givenUp = _attempts >= Config.MaxConnectAttempts;
            _state = reason == DisconnectReason.ConnectionRejected || !Config.AutoReconnect || givenUp
                ? NetworkClientState.Offline
                : NetworkClientState.Reconnecting;

            // Stop the underlying socket and mark IsRunning false before firing Closed so callers and
            // handlers observe an inactive transport and can immediately call Connect() without manual Stop().
            StopListener();
            SetRunning(false);

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
        protected override bool AcceptsInternalUnconnected => _state == NetworkClientState.Punching && _natRendezvousEndPoint is not null;

        /// <inheritdoc/>
        protected override void OnSessionUpdate(float deltaSeconds)
        {
            _ = deltaSeconds;
            if (_state != NetworkClientState.Punching || _natRendezvousEndPoint is null || _natConfig is null)
                return;

            long now = Stopwatch.GetTimestamp();
            if (now >= _natDeadlineTimestamp)
            {
                FailNatPunch("NAT punchthrough timed out.", allowAutoReconnect: true);
                return;
            }

            if (now >= _natNextSendTimestamp)
            {
                if (!_natIntroduced)
                {
                    SendClientJoinRequest();
                }
                else if (_natPunchNonce is not null && _natHostExternal is not null)
                {
                    SendClientPunchPacket(_natHostExternal, _natPunchNonce, NatPunchFlags.None, isAck: false);
                    if (_natHostInternal is not null && !NatPunchProtocol.EndPointsEqual(_natHostInternal, _natHostExternal))
                        SendClientPunchPacket(_natHostInternal, _natPunchNonce, NatPunchFlags.None, isAck: false);
                }

                _natNextSendTimestamp = now + MsToTimestampTicks(_natConfig.PunchIntervalMs);
            }
        }

        /// <inheritdoc/>
        protected override void HandleInternalNatPacket(IPEndPoint remoteEndPoint, ReadOnlySpan<byte> payload)
        {
            if (_state != NetworkClientState.Punching
                || _natRendezvousEndPoint is null
                || _natConfig is null
                || _natRoomCode is null
                || _natRequestNonce is null)
            {
                return;
            }

            var type = (NatPacketType)payload[5];
            switch (type)
            {
                case NatPacketType.JoinError:
                {
                    if (!NatPunchProtocol.EndPointsEqual(remoteEndPoint, _natRendezvousEndPoint)
                        || !NatPunchProtocol.TryReadJoinError(payload, out ReadOnlySpan<byte> requestNonce, out NatRendezvousStatus status)
                        || !CryptographicOperations.FixedTimeEquals(requestNonce, _natRequestNonce))
                    {
                        NatStats.IncrementRejectedPackets();
                        return;
                    }

                    bool allowRetry = status != NatRendezvousStatus.RoomNotFound
                        && status != NatRendezvousStatus.InvalidRequest;
                    FailNatPunch($"Rendezvous server rejected join for room '{_natRoomCode}' ({status}).", allowRetry);
                    break;
                }
                case NatPacketType.Introduce:
                {
                    if (!NatPunchProtocol.EndPointsEqual(remoteEndPoint, _natRendezvousEndPoint)
                        || !NatPunchProtocol.TryReadIntroduce(
                            payload,
                            out byte role,
                            out ReadOnlySpan<byte> punchNonce,
                            out ReadOnlySpan<byte> requestNonce,
                            out IPEndPoint hostInternal,
                            out IPEndPoint hostExternal,
                            out string roomCode)
                        || role != 1
                        || !string.Equals(roomCode, _natRoomCode, StringComparison.Ordinal)
                        || !CryptographicOperations.FixedTimeEquals(requestNonce, _natRequestNonce))
                    {
                        NatStats.IncrementRejectedPackets();
                        return;
                    }

                    if (_natIntroduced)
                        return;

                    byte[] punchNonceCopy = new byte[NatPunchProtocol.SecretBytes];
                    punchNonce.CopyTo(punchNonceCopy);
                    _natPunchNonce = punchNonceCopy;
                    _natHostInternal = hostInternal;
                    _natHostExternal = hostExternal;
                    _natIntroduced = true;

                    NatStats.IncrementIntroductionsReceived();

                    SendClientPunchPacket(hostExternal, punchNonceCopy, NatPunchFlags.None, isAck: false);
                    if (!NatPunchProtocol.EndPointsEqual(hostInternal, hostExternal))
                        SendClientPunchPacket(hostInternal, punchNonceCopy, NatPunchFlags.None, isAck: false);

                    _natNextSendTimestamp = Stopwatch.GetTimestamp() + MsToTimestampTicks(_natConfig.PunchIntervalMs);
                    break;
                }
                case NatPacketType.Punch:
                {
                    if (!_natIntroduced
                        || _natPunchNonce is null
                        || !NatPunchProtocol.TryReadPunch(payload, out ReadOnlySpan<byte> punchNonce, out NatPunchFlags flags)
                        || (flags & NatPunchFlags.FromHost) == 0
                        || !CryptographicOperations.FixedTimeEquals(punchNonce, _natPunchNonce))
                    {
                        NatStats.IncrementRejectedPackets();
                        return;
                    }

                    IPEndPoint sender = NatPunchProtocol.NormalizeEndPoint(remoteEndPoint);
                    NatAddressKind kind = _natHostInternal is not null && NatPunchProtocol.EndPointsEqual(sender, _natHostInternal)
                        ? NatAddressKind.Internal
                        : NatAddressKind.External;

                    NatPunchFlags ackFlags = NatPunchFlags.IsAck | (kind == NatAddressKind.Internal ? NatPunchFlags.IsInternal : NatPunchFlags.None);
                    SendClientPunchPacket(sender, _natPunchNonce, ackFlags, isAck: true);
                    SendClientPunchPacket(sender, _natPunchNonce, ackFlags, isAck: true);

                    NetDataWriter handshake = BuildHandshakePayload();
                    if (ConnectTo(sender, handshake.Data.AsSpan(0, handshake.Length)) is null)
                    {
                        FailNatPunch($"Could not send connection request to punched host {sender}.", allowAutoReconnect: true);
                        return;
                    }

                    string completedRoom = _natRoomCode;
                    _address = sender.Address.ToString();
                    _port = sender.Port;
                    _state = NetworkClientState.Connecting;
                    ConfigureNatRendezvousRoute(null);

                    var result = new NatPunchResult(sender, kind, completedRoom);
                    LastNatPunchResult = result;
                    NatStats.IncrementPunchesSucceeded();

                    try
                    {
                        NatPunchSucceeded?.Invoke(result);
                    }
                    catch (Exception exception)
                    {
                        ReportCallbackException(exception);
                    }

                    break;
                }
                default:
                    NatStats.IncrementRejectedPackets();
                    break;
            }
        }

        /// <inheritdoc/>
        protected override void OnStopping()
        {
            base.OnStopping();
            ClearNatState(keepReconnectTarget: false);
            _serverPeerId = -1;
            _state = NetworkClientState.Offline;
        }

        private NetDataWriter BuildHandshakePayload()
        {
            // The handshake is sent as raw bytes in the first packet, so that the server can read app
            // id/version/name before it decides (see NetworkServer.HandleConnectionRequest).
            var payload = new NetDataWriter(true, 96);
            payload.Put(Config.ApplicationId);
            payload.Put(Config.ProtocolVersion);
            NetworkCodec.WriteName(payload, PlayerName);
            return payload;
        }

        private void SendClientJoinRequest()
        {
            if (_natRendezvousEndPoint is null
                || _natRoomCode is null
                || _natRequestNonce is null
                || _natInternalEndPoint is null)
                return;

            Span<byte> buffer = stackalloc byte[NatPunchProtocol.MaxPacketBytes];
            int length = NatPunchProtocol.WriteClientJoinRequest(
                buffer,
                _natRoomCode,
                Config.ApplicationId,
                _natRequestNonce,
                _natInternalEndPoint);
            if (SendUnconnectedRaw(_natRendezvousEndPoint, buffer.Slice(0, length)))
                NatStats.IncrementJoinRequestsSent();
        }

        private void SendClientPunchPacket(IPEndPoint destination, ReadOnlySpan<byte> punchNonce, NatPunchFlags flags, bool isAck)
        {
            Span<byte> buffer = stackalloc byte[NatPunchProtocol.MaxPacketBytes];
            int length = NatPunchProtocol.WritePunch(buffer, punchNonce, flags);
            if (SendUnconnectedRaw(destination, buffer.Slice(0, length)))
            {
                if (isAck)
                    NatStats.IncrementPunchAcksSent();
                else
                    NatStats.IncrementPunchProbesSent();
            }
        }

        private void FailNatPunch(string reason, bool allowAutoReconnect)
        {
            NatStats.IncrementPunchesFailed();
            ConfigureNatRendezvousRoute(null);
            ClearNatState(keepReconnectTarget: allowAutoReconnect);
            StopListener();
            SetRunning(false);

            bool givenUp = _attempts >= Config.MaxConnectAttempts;
            if (allowAutoReconnect && Config.AutoReconnect && !givenUp)
            {
                _state = NetworkClientState.Reconnecting;
                _reconnectTimer = 0f;
            }
            else
            {
                _state = NetworkClientState.Offline;
            }

            ReportErrorSafely(new NetworkError(NetworkErrorCode.NatPunchFailed, reason));
            InvokeNatFailedSafely(reason);
        }

        private void ClearNatState(bool keepReconnectTarget)
        {
            _natConfig = null;
            _natInternalEndPoint = null;
            _natRequestNonce = null;
            _natPunchNonce = null;
            _natHostInternal = null;
            _natHostExternal = null;
            _natDeadlineTimestamp = 0;
            _natNextSendTimestamp = 0;
            _natIntroduced = false;

            if (!keepReconnectTarget)
            {
                _isNatReconnectSession = false;
                _natRendezvousEndPoint = null;
                _natRoomCode = null;
                LastNatPunchResult = null;
            }
        }

        private void InvokeNatFailedSafely(string reason)
        {
            try
            {
                NatPunchFailed?.Invoke(reason);
            }
            catch (Exception exception)
            {
                ReportCallbackException(exception);
            }
        }

        private static long MsToTimestampTicks(int milliseconds)
            => Math.Max(1L, Stopwatch.Frequency * milliseconds / 1000L);

        /// <summary>
        /// Listens for the server's messages: the welcome message sets the peer id, the tick rate, the tick and the name.
        /// </summary>
        private void HandleMessage(NetworkEnvelope envelope)
        {
            if (envelope.Message is WelcomeMessage welcome)
            {
                if (_state != NetworkClientState.Connecting)
                    return;

                if (welcome.PeerId < 0 || welcome.TickRate <= 0 || welcome.ServerTick < 0)
                {
                    Report(new NetworkError(NetworkErrorCode.MalformedMessage, "Received invalid WelcomeMessage parameters from server.", envelope.PeerId));
                    return;
                }

                SetLocalPeerId(welcome.PeerId);
                PlayerName = welcome.PlayerName;
                SetTickRate(welcome.TickRate);
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

        /// <summary>Communicating with the rendezvous server and probing UDP endpoints to punch through NAT.</summary>
        Punching,
    }
}
