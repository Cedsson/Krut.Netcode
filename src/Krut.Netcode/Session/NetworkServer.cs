using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
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
        private bool _started;

        private NatPunchConfig? _natConfig;
        private IPEndPoint? _natRendezvousEndPoint;
        private IPEndPoint? _natInternalEndPoint;
        private IPEndPoint? _natExternalEndPoint;
        private string? _natRoomCode;
        private byte[]? _natHostSecret;
        private bool _natRegistered;
        private long _natNextRegisterTimestamp;
        private long _natHeartbeatIntervalTicks;
        private readonly List<PendingHostPunch> _pendingPunches = new();

        /// <param name="config">Settings to run with, or null for the defaults (see <see cref="NetworkConfig"/>). Copied, so changing your own instance afterwards has no effect on the running server.</param>
        public NetworkServer(NetworkConfig? config = null)
            : base(config ?? new NetworkConfig(), isServer: true)
        {
        }

        /// <summary>Number of clients connected right now.</summary>
        public int ClientCount => ConnectedPeerCount;

        /// <summary>The port the server listens on (the bound port while running, else the configured port).</summary>
        public int Port => IsRunning ? BoundPort : Config.Port;

        /// <summary>Who gets to come in.</summary>
        public Func<NetworkConnectionRequest, NetworkConnectionResult>? ConnectionApproval { get; set; }

        /// <summary>True when this host currently has a confirmed room registration on a rendezvous server.</summary>
        public bool IsNatRegistered => _natRegistered;

        /// <summary>Room code currently registered (or pending registration) for NAT punchthrough, or null.</summary>
        public string? NatRoomCode => _natRoomCode;

        /// <summary>Host public endpoint observed by the rendezvous server once registered, or null.</summary>
        public IPEndPoint? NatExternalEndPoint => _natExternalEndPoint;

        /// <summary>Raised when the rendezvous server confirms or renews room registration.</summary>
        public event Action<NatRegistrationResult>? NatRoomRegistered;

        /// <summary>Raised once per client punch when UDP hole punching succeeds.</summary>
        public event Action<NatPunchResult>? NatPunchSucceeded;

        /// <summary>Raised when NAT room registration or punching fails.</summary>
        public event Action<string>? NatPunchFailed;

        /// <summary>Starts the server and registers <paramref name="roomCode"/> on the specified rendezvous server.</summary>
        public bool StartWithNat(string rendezvousHost, int rendezvousPort, string roomCode)
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

            return StartWithNat(endPoint, roomCode);
        }

        /// <summary>Starts the server and registers <paramref name="roomCode"/> on the specified rendezvous endpoint.</summary>
        public bool StartWithNat(IPEndPoint rendezvousEndPoint, string roomCode)
        {
            ArgumentNullException.ThrowIfNull(rendezvousEndPoint);

            if (!Start())
                return false;

            if (!RegisterNatRoom(rendezvousEndPoint, roomCode))
            {
                Stop();
                return false;
            }

            return true;
        }

        /// <summary>Registers (or switches) a room code on a rendezvous server while the server is already running.</summary>
        public bool RegisterNatRoom(string rendezvousHost, int rendezvousPort, string roomCode)
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

            return RegisterNatRoom(endPoint, roomCode);
        }

        /// <summary>Registers (or switches) a room code on a rendezvous endpoint while the server is already running.</summary>
        public bool RegisterNatRoom(IPEndPoint rendezvousEndPoint, string roomCode)
        {
            ArgumentNullException.ThrowIfNull(rendezvousEndPoint);

            if (!IsRunning)
            {
                const string reason = "Server must be started before registering a NAT room.";
                ReportErrorSafely(new NetworkError(NetworkErrorCode.NotConnected, reason));
                return false;
            }

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
                const string reason = "ApplicationId exceeds the maximum length (64 UTF-8 bytes) for NAT registration.";
                NatStats.IncrementPunchesFailed();
                ReportErrorSafely(new NetworkError(NetworkErrorCode.NatPunchFailed, reason));
                InvokeNatFailedSafely(reason);
                return false;
            }

            if (_natRendezvousEndPoint is not null)
                UnregisterNatRoom();

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

            byte[] secret = new byte[NatPunchProtocol.SecretBytes];
            RandomNumberGenerator.Fill(secret);

            _natRendezvousEndPoint = normalizedRdv;
            _natInternalEndPoint = internalEp;
            _natExternalEndPoint = null;
            _natRoomCode = validRoom;
            _natHostSecret = secret;
            _natRegistered = false;
            _natHeartbeatIntervalTicks = MsToTimestampTicks(natConfig.HeartbeatIntervalMs);
            _pendingPunches.Clear();

            SendHostRegisterPacket(isHeartbeat: false);
            _natNextRegisterTimestamp = Stopwatch.GetTimestamp() + MsToTimestampTicks(natConfig.RegisterRetryIntervalMs);
            return true;
        }

        /// <summary>Unregisters the active NAT room (best effort) and clears all pending hole-punching states.</summary>
        public void UnregisterNatRoom()
        {
            if (_natRendezvousEndPoint is { } rdv
                && _natRoomCode is { } room
                && _natHostSecret is { } secret
                && IsRunning)
            {
                Span<byte> buffer = stackalloc byte[NatPunchProtocol.MaxPacketBytes];
                int len = NatPunchProtocol.WriteHostUnregister(buffer, room, Config.ApplicationId, secret);
                SendUnconnectedRaw(rdv, buffer.Slice(0, len));
            }

            _natConfig = null;
            _natRendezvousEndPoint = null;
            _natInternalEndPoint = null;
            _natExternalEndPoint = null;
            _natRoomCode = null;
            _natHostSecret = null;
            _natRegistered = false;
            _natNextRegisterTimestamp = 0;
            _natHeartbeatIntervalTicks = 0;
            _pendingPunches.Clear();
            ConfigureNatRendezvousRoute(null);
        }

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
            ShutdownSession();
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
            peer.JoinTick = CurrentTick;
            peer.HasJoined = true;

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
            foreach (var connected in Peers)
            {
                if (!connected.HasJoined)
                    continue;

                var info = Rent<PlayerInfoMessage>();
                info.PeerId = connected.Id;
                info.PlayerName = connected.Name;
                info.JoinTick = connected.JoinTick;
                Send(peer.Id, info);
            }

            // Those who were already in get the new player - the peer's own
            // row it already got above, so the duplication is avoided.
            var joined = Rent<PlayerInfoMessage>();
            joined.PeerId = peer.Id;
            joined.PlayerName = peer.Name;
            joined.JoinTick = peer.JoinTick;
            Broadcast(joined, peer.Id);

            BroadcastSystemChat($"{peer.Name} joined.");
        }

        /// <inheritdoc/>
        protected override void OnPeerDisconnectedEvent(NetworkPeerState peer, DisconnectInfo info)
        {
            // Only a peer that actually joined should be reported in the chat. A connection that was never
            // accepted (rejected app id, wrong version, full lobby) also gives a PeerDisconnected, but without
            // it ever having been in the lobby - writing "Player left" in the log for every stray process on
            // the port would be spam, and a player would see their own "joined" line disappear right away.
            if (!peer.HasJoined)
                return;

            peer.HasJoined = false;
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
            if (IsPlaintextNatRendezvousEndPoint(request.RemoteEndPoint))
            {
                request.Reject();
                return;
            }

            // The handshake: the client sends app id, protocol version and name in a dedicated area of the
            // first packet.
            var data = request.Data;
            string applicationId = string.Empty;
            int protocolVersion = 0;
            string name = "Player";

            if (data != null && !data.IsNull)
            {
                try
                {
                    if (data.AvailableBytes < 2)
                    {
                        request.Reject();
                        return;
                    }

                    int maxAppIdLength = Math.Max(256, Config.ApplicationId.Length);
                    applicationId = data.GetString(maxAppIdLength) ?? string.Empty;
                    if (applicationId != Config.ApplicationId)
                    {
                        // Wrong game on the same port - answer silently (the other
                        // app has nothing to do with our error message).
                        request.Reject();
                        return;
                    }

                    if (!data.TryGetInt(out protocolVersion))
                    {
                        request.Reject();
                        return;
                    }

                    if (protocolVersion != Config.ProtocolVersion)
                    {
                        request.Reject(Reason($"Protocol version {protocolVersion} does not match the server's {Config.ProtocolVersion} - update the game."));
                        return;
                    }

                    if (data.AvailableBytes > 0)
                        name = data.GetString(NetworkCodec.MaxNameLength * 8) ?? "Player";
                }
                catch (Exception)
                {
                    request.Reject();
                    return;
                }
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

            if (result.Accepted && ClientCount >= Config.MaxClients)
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
        protected override bool AcceptsInternalUnconnected => _natRendezvousEndPoint is not null;

        /// <inheritdoc/>
        protected override void OnSessionUpdate(float deltaSeconds)
        {
            _ = deltaSeconds;
            if (_natRendezvousEndPoint is null || _natConfig is null)
                return;

            long now = Stopwatch.GetTimestamp();
            if (now >= _natNextRegisterTimestamp)
            {
                SendHostRegisterPacket(isHeartbeat: _natRegistered);
                _natNextRegisterTimestamp = now + (_natRegistered
                    ? _natHeartbeatIntervalTicks
                    : MsToTimestampTicks(_natConfig.RegisterRetryIntervalMs));
            }

            for (int i = _pendingPunches.Count - 1; i >= 0; i--)
            {
                var entry = _pendingPunches[i];
                if (now >= entry.ExpiresAtTimestamp)
                {
                    bool failed = !entry.Succeeded;
                    _pendingPunches.RemoveAt(i);
                    if (failed)
                        NatStats.IncrementPunchesFailed();
                    continue;
                }

                if (!entry.Succeeded && now >= entry.NextProbeTimestamp)
                {
                    SendPunchPacket(entry.PeerExternal, entry.PunchNonce, NatPunchFlags.FromHost, isAck: false);
                    if (!NatPunchProtocol.EndPointsEqual(entry.PeerInternal, entry.PeerExternal))
                        SendPunchPacket(entry.PeerInternal, entry.PunchNonce, NatPunchFlags.FromHost, isAck: false);

                    entry.NextProbeTimestamp = now + MsToTimestampTicks(_natConfig.PunchIntervalMs);
                }
            }
        }

        /// <inheritdoc/>
        protected override void HandleInternalNatPacket(IPEndPoint remoteEndPoint, ReadOnlySpan<byte> payload)
        {
            if (_natRendezvousEndPoint is null
                || _natConfig is null
                || _natHostSecret is null
                || _natRoomCode is null)
            {
                return;
            }

            var type = (NatPacketType)payload[5];
            switch (type)
            {
                case NatPacketType.HostRegistered:
                {
                    if (!NatPunchProtocol.EndPointsEqual(remoteEndPoint, _natRendezvousEndPoint)
                        || !NatPunchProtocol.TryReadHostRegistered(
                            payload,
                            out string roomCode,
                            out NatRendezvousStatus status,
                            out IPEndPoint observedExternal,
                            out ushort leaseSeconds)
                        || !string.Equals(roomCode, _natRoomCode, StringComparison.Ordinal))
                    {
                        NatStats.IncrementRejectedPackets();
                        return;
                    }

                    if (status == NatRendezvousStatus.Ok)
                    {
                        _natExternalEndPoint = observedExternal;
                        _natRegistered = true;

                        long maxLeaseHeartbeatMs = Math.Max(1000L, (long)leaseSeconds * 500L);
                        int intervalMs = (int)Math.Min(_natConfig.HeartbeatIntervalMs, maxLeaseHeartbeatMs);
                        _natHeartbeatIntervalTicks = MsToTimestampTicks(intervalMs);
                        _natNextRegisterTimestamp = Stopwatch.GetTimestamp() + _natHeartbeatIntervalTicks;

                        try
                        {
                            NatRoomRegistered?.Invoke(new NatRegistrationResult(roomCode, observedExternal, leaseSeconds));
                        }
                        catch (Exception exception)
                        {
                            ReportCallbackException(exception);
                        }
                    }
                    else
                    {
                        string reason = $"Rendezvous server rejected room '{roomCode}' ({status}).";
                        if (status is NatRendezvousStatus.RoomTaken or NatRendezvousStatus.InvalidRequest)
                            UnregisterNatRoom();

                        NatStats.IncrementPunchesFailed();
                        ReportErrorSafely(new NetworkError(NetworkErrorCode.NatPunchFailed, reason));
                        InvokeNatFailedSafely(reason);
                    }
                    break;
                }
                case NatPacketType.Introduce:
                {
                    if (!NatPunchProtocol.EndPointsEqual(remoteEndPoint, _natRendezvousEndPoint)
                        || !NatPunchProtocol.TryReadIntroduce(
                            payload,
                            out byte role,
                            out ReadOnlySpan<byte> punchNonce,
                            out ReadOnlySpan<byte> authSecret,
                            out IPEndPoint peerInternal,
                            out IPEndPoint peerExternal,
                            out string roomCode)
                        || role != 0
                        || !string.Equals(roomCode, _natRoomCode, StringComparison.Ordinal)
                        || !CryptographicOperations.FixedTimeEquals(authSecret, _natHostSecret))
                    {
                        NatStats.IncrementRejectedPackets();
                        return;
                    }

                    NatStats.IncrementIntroductionsReceived();

                    long now = Stopwatch.GetTimestamp();
                    for (int i = _pendingPunches.Count - 1; i >= 0; i--)
                    {
                        if (now >= _pendingPunches[i].ExpiresAtTimestamp)
                        {
                            if (!_pendingPunches[i].Succeeded)
                                NatStats.IncrementPunchesFailed();
                            _pendingPunches.RemoveAt(i);
                        }
                    }

                    for (int i = 0; i < _pendingPunches.Count; i++)
                    {
                        if (CryptographicOperations.FixedTimeEquals(_pendingPunches[i].PunchNonce, punchNonce))
                            return;
                    }

                    while (_pendingPunches.Count >= _natConfig.MaxPendingPunches)
                    {
                        if (!_pendingPunches[0].Succeeded)
                            NatStats.IncrementPunchesFailed();
                        _pendingPunches.RemoveAt(0);
                    }

                    byte[] nonceCopy = new byte[NatPunchProtocol.SecretBytes];
                    punchNonce.CopyTo(nonceCopy);
                    var entry = new PendingHostPunch(
                        nonceCopy,
                        peerInternal,
                        peerExternal,
                        now + MsToTimestampTicks(_natConfig.PunchTimeoutMs),
                        now + MsToTimestampTicks(_natConfig.PunchIntervalMs));
                    _pendingPunches.Add(entry);

                    SendPunchPacket(peerExternal, nonceCopy, NatPunchFlags.FromHost, isAck: false);
                    if (!NatPunchProtocol.EndPointsEqual(peerInternal, peerExternal))
                        SendPunchPacket(peerInternal, nonceCopy, NatPunchFlags.FromHost, isAck: false);
                    break;
                }
                case NatPacketType.Punch:
                {
                    if (!NatPunchProtocol.TryReadPunch(payload, out ReadOnlySpan<byte> punchNonce, out NatPunchFlags flags)
                        || (flags & NatPunchFlags.FromHost) != 0)
                    {
                        NatStats.IncrementRejectedPackets();
                        return;
                    }

                    PendingHostPunch? match = null;
                    for (int i = _pendingPunches.Count - 1; i >= 0; i--)
                    {
                        if (CryptographicOperations.FixedTimeEquals(_pendingPunches[i].PunchNonce, punchNonce))
                        {
                            match = _pendingPunches[i];
                            break;
                        }
                    }

                    if (match is null)
                    {
                        NatStats.IncrementRejectedPackets();
                        return;
                    }

                    IPEndPoint normalizedSender = NatPunchProtocol.NormalizeEndPoint(remoteEndPoint);
                    bool isInternal = NatPunchProtocol.EndPointsEqual(normalizedSender, match.PeerInternal);
                    NatPunchFlags replyFlags = NatPunchFlags.IsAck | NatPunchFlags.FromHost | (isInternal ? NatPunchFlags.IsInternal : NatPunchFlags.None);

                    if (!match.Succeeded)
                    {
                        match.Succeeded = true;
                        SendPunchPacket(normalizedSender, match.PunchNonce, replyFlags, isAck: true);
                        SendPunchPacket(normalizedSender, match.PunchNonce, replyFlags, isAck: true);
                        NatStats.IncrementPunchesSucceeded();

                        try
                        {
                            NatPunchSucceeded?.Invoke(new NatPunchResult(
                                normalizedSender,
                                isInternal ? NatAddressKind.Internal : NatAddressKind.External,
                                _natRoomCode));
                        }
                        catch (Exception exception)
                        {
                            ReportCallbackException(exception);
                        }
                    }
                    else if ((flags & NatPunchFlags.IsAck) == 0)
                    {
                        SendPunchPacket(normalizedSender, match.PunchNonce, replyFlags, isAck: true);
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
            UnregisterNatRoom();
            _started = false;
            BroadcastSystemChat("The server is shutting down.");
            DisconnectAll("The server is shutting down.");
        }

        private void SendHostRegisterPacket(bool isHeartbeat)
        {
            if (_natRendezvousEndPoint is null
                || _natRoomCode is null
                || _natHostSecret is null
                || _natInternalEndPoint is null)
                return;

            Span<byte> buffer = stackalloc byte[NatPunchProtocol.MaxPacketBytes];
            int length = NatPunchProtocol.WriteHostRegister(
                buffer,
                _natRoomCode,
                Config.ApplicationId,
                _natHostSecret,
                _natInternalEndPoint,
                Config.ProtocolVersion);

            if (SendUnconnectedRaw(_natRendezvousEndPoint, buffer.Slice(0, length)))
            {
                if (isHeartbeat)
                    NatStats.IncrementHeartbeatsSent();
                else
                    NatStats.IncrementRegistrationsSent();
            }
        }

        private void SendPunchPacket(IPEndPoint destination, ReadOnlySpan<byte> punchNonce, NatPunchFlags flags, bool isAck)
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

        private sealed class PendingHostPunch
        {
            public PendingHostPunch(
                byte[] punchNonce,
                IPEndPoint peerInternal,
                IPEndPoint peerExternal,
                long expiresAtTimestamp,
                long nextProbeTimestamp)
            {
                PunchNonce = punchNonce;
                PeerInternal = peerInternal;
                PeerExternal = peerExternal;
                ExpiresAtTimestamp = expiresAtTimestamp;
                NextProbeTimestamp = nextProbeTimestamp;
            }

            public byte[] PunchNonce { get; }
            public IPEndPoint PeerInternal { get; }
            public IPEndPoint PeerExternal { get; }
            public long ExpiresAtTimestamp { get; }
            public long NextProbeTimestamp { get; set; }
            public bool Succeeded { get; set; }
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
        /// Wraps a reason string in the data section LiteNetLib delivers alongside a disconnect. 64 bytes is
        /// enough for any message a game would put here, and the writer is built per call because this happens
        /// once per disconnect, not per packet.
        /// </summary>
        private NetDataWriter Reason(string text)
        {
            var writer = new NetDataWriter(true, 64);
            NetworkCodec.WriteText(writer, NetworkCodec.SanitizeText(text));
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
