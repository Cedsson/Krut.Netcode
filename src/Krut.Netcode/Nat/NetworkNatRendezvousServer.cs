using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using LiteNetLib;
using LiteNetLib.Utils;

namespace Krut.Netcode
{
    /// <summary>
    /// Standalone UDP NAT rendezvous server: tracks active rooms with time-bounded leases, authenticates hosts
    /// with a 128-bit <c>HostSecret</c>, rate-limits source IPs and per-room introductions, and brokers mutual
    /// hole-punching introductions (<c>Introduce</c>) with cryptographically random 128-bit punch nonces.
    /// <para>
    /// Supports plaintext UDP or authenticated AES-256-GCM datagram encryption (<see cref="NatRendezvousConfig.PreSharedKey"/>)
    /// and contains zero reflection or dynamic code generation (100% NativeAOT/trim-safe).
    /// </para>
    /// </summary>
    public sealed class NetworkNatRendezvousServer : IDisposable
    {
        private readonly Listener _listener;
        private readonly NetManager _manager;
        private readonly NetworkEncryptionLayer? _encryptionLayer;

        private readonly Dictionary<RoomKey, RoomEntry> _rooms = new();
        private readonly List<RoomKey> _expiredKeysScratch = new();

        private readonly Dictionary<IPAddress, int> _rateIndex = new();
        private readonly List<RateSlot> _rateRing = new(64);
        private int _rateHead;

        private readonly long _leaseTicks;
        private readonly long _sweepIntervalTicks;
        private long _lastSweepTimestamp;
        private bool _sentInTick;
        private bool _disposed;

        /// <summary>Creates a rendezvous server with the supplied settings (or defaults when null).</summary>
        public NetworkNatRendezvousServer(NatRendezvousConfig? config = null)
        {
            Config = (config ?? new NatRendezvousConfig()).Clone();
            Config.Validate();

            _leaseTicks = Config.RoomLeaseSeconds * Stopwatch.Frequency;
            _sweepIntervalTicks = Stopwatch.Frequency;

            if (Config.PreSharedKey is { } psk)
            {
                _encryptionLayer = new NetworkEncryptionLayer(
                    psk,
                    isServer: true,
                    Config.PreviousPreSharedKey,
                    Config.KeyId,
                    Config.EnableAntiReplay,
                    EncryptionStats);
            }

            _listener = new Listener(this);
            _manager = _encryptionLayer is not null
                ? new NetManager(_listener, _encryptionLayer)
                : new NetManager(_listener);

            _manager.IPv6Enabled = Config.IPv6Support;
            _manager.UnconnectedMessagesEnabled = true;
            _manager.UnsyncedEvents = false;
            _manager.AutoRecycle = false;
        }

        /// <summary>Validated configuration snapshot used by this server.</summary>
        public NatRendezvousConfig Config { get; }

        /// <summary>True while the rendezvous socket is bound and listening.</summary>
        public bool IsRunning { get; private set; }

        /// <summary>UDP port currently bound when running, otherwise the configured port.</summary>
        public int Port => IsRunning ? _manager.LocalPort : Config.Port;

        /// <summary>Number of registered rooms currently tracked.</summary>
        public int ActiveRoomCount => _rooms.Count;

        /// <summary>Operational counters for registrations, introductions, expirations, and rate limits.</summary>
        public NatRendezvousStats Stats { get; } = new();

        /// <summary>AES-256-GCM encryption, decryption, and anti-replay counters (when <see cref="NatRendezvousConfig.PreSharedKey"/> is configured).</summary>
        public NetworkEncryptionStats EncryptionStats { get; } = new();

        /// <summary>Raised when a socket or start failure occurs.</summary>
        public event Action<NetworkError>? Error;

        /// <summary>Starts listening on <see cref="NatRendezvousConfig.Port"/>.</summary>
        public bool Start() => Start(null, Config.Port);

        /// <summary>Starts listening on an optional local IPv4 address and UDP port (0 = ephemeral OS port).</summary>
        public bool Start(string? address, int port)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRunning)
                return true;

            if (port is < 0 or > 65535)
            {
                ReportSafely(new NetworkError(NetworkErrorCode.StartFailed, $"Invalid UDP port {port}."));
                return false;
            }

            bool started;
            if (string.IsNullOrWhiteSpace(address))
            {
                started = _manager.Start(port);
            }
            else
            {
                if (!IPAddress.TryParse(address.Trim(), out var ipv4))
                {
                    ReportSafely(new NetworkError(NetworkErrorCode.StartFailed, $"Invalid bind IP address: {address}."));
                    return false;
                }

                IPAddress ipv6 = IPAddress.IsLoopback(ipv4) ? IPAddress.IPv6Loopback : IPAddress.IPv6Any;
                started = _manager.Start(ipv4, ipv6, port);
            }

            if (!started)
            {
                ReportSafely(new NetworkError(NetworkErrorCode.StartFailed, $"Could not bind rendezvous UDP socket on port {port}."));
                return false;
            }

            _lastSweepTimestamp = Stopwatch.GetTimestamp();
            IsRunning = true;
            return true;
        }

        /// <summary>Stops listening and clears all room registrations and rate-limit state.</summary>
        public void Stop()
        {
            if (!IsRunning && _rooms.Count == 0 && _rateRing.Count == 0)
                return;

            if (_manager.IsRunning)
                _manager.Stop();

            _rooms.Clear();
            _expiredKeysScratch.Clear();
            _rateIndex.Clear();
            _rateRing.Clear();
            _rateHead = 0;
            Stats.SetActiveRooms(0);
            _encryptionLayer?.ResetReplayState();
            IsRunning = false;
        }

        /// <summary>Polls incoming rendezvous packets and evicts expired room leases.</summary>
        public void Update(float deltaSeconds = 0f)
        {
            _ = deltaSeconds;
            if (_disposed || !IsRunning)
                return;

            _sentInTick = false;
            _manager.PollEvents();

            long now = Stopwatch.GetTimestamp();
            if (now - _lastSweepTimestamp >= _sweepIntervalTicks)
            {
                SweepExpiredRooms(now);
                _lastSweepTimestamp = now;
            }

            if (_sentInTick && IsRunning)
                _manager.TriggerUpdate();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
                return;

            Stop();
            _encryptionLayer?.Dispose();
            _disposed = true;
        }

        private void HandleUnconnected(IPEndPoint remoteEndPoint, NetDataReader reader)
        {
            int length = reader.AvailableBytes;
            if (length <= 0 || length > NatPunchProtocol.MaxPacketBytes)
            {
                Stats.IncrementMalformedPackets();
                return;
            }

            IPEndPoint sender = NatPunchProtocol.NormalizeEndPoint(remoteEndPoint);
            long now = Stopwatch.GetTimestamp();
            if (IsIpRateLimited(sender.Address, now))
            {
                Stats.IncrementRateLimitedPackets();
                return;
            }

            ReadOnlySpan<byte> payload = reader.GetRemainingBytesSpan();
            if (!NatPunchProtocol.HasMagicHeader(payload))
            {
                Stats.IncrementMalformedPackets();
                return;
            }

            var type = (NatPacketType)payload[5];
            switch (type)
            {
                case NatPacketType.HostRegister:
                    HandleHostRegister(sender, payload, now);
                    break;
                case NatPacketType.HostUnregister:
                    HandleHostUnregister(payload);
                    break;
                case NatPacketType.ClientJoinRequest:
                    HandleClientJoinRequest(sender, payload, now);
                    break;
                default:
                    Stats.IncrementMalformedPackets();
                    break;
            }
        }

        private void HandleHostRegister(IPEndPoint sender, ReadOnlySpan<byte> payload, long now)
        {
            if (!NatPunchProtocol.TryReadHostRegister(
                payload,
                out string roomCode,
                out string appId,
                out ReadOnlySpan<byte> hostSecret,
                out IPEndPoint internalEndPoint,
                out _))
            {
                Stats.IncrementMalformedPackets();
                return;
            }

            var key = new RoomKey(appId, roomCode);
            if (_rooms.TryGetValue(key, out RoomEntry? existing))
            {
                if (now >= existing.ExpiresAtTimestamp)
                {
                    _rooms.Remove(key);
                    Stats.IncrementRoomsExpired();
                }
                else if (!CryptographicOperations.FixedTimeEquals(existing.HostSecret, hostSecret))
                {
                    Stats.IncrementRegistrationsRejected();
                    SendHostRegistered(sender, roomCode, NatRendezvousStatus.RoomTaken, sender, 0);
                    return;
                }
                else
                {
                    existing.InternalEndPoint = internalEndPoint;
                    existing.ExternalEndPoint = sender;
                    existing.ExpiresAtTimestamp = now + _leaseTicks;
                    Stats.IncrementRegistrationsAccepted();
                    SendHostRegistered(sender, roomCode, NatRendezvousStatus.Ok, sender, (ushort)Config.RoomLeaseSeconds);
                    return;
                }
            }

            if (_rooms.Count >= Config.MaxRooms)
                SweepExpiredRooms(now);

            if (_rooms.Count >= Config.MaxRooms)
            {
                Stats.IncrementRegistrationsRejected();
                Stats.SetActiveRooms(_rooms.Count);
                SendHostRegistered(sender, roomCode, NatRendezvousStatus.ServerFull, sender, 0);
                return;
            }

            byte[] secretCopy = new byte[NatPunchProtocol.SecretBytes];
            hostSecret.CopyTo(secretCopy);
            var created = new RoomEntry(secretCopy, internalEndPoint, sender, now + _leaseTicks, now);
            _rooms[key] = created;
            Stats.SetActiveRooms(_rooms.Count);
            Stats.IncrementRegistrationsAccepted();
            SendHostRegistered(sender, roomCode, NatRendezvousStatus.Ok, sender, (ushort)Config.RoomLeaseSeconds);
        }

        private void HandleHostUnregister(ReadOnlySpan<byte> payload)
        {
            if (!NatPunchProtocol.TryReadHostUnregister(
                payload,
                out string roomCode,
                out string appId,
                out ReadOnlySpan<byte> hostSecret))
            {
                Stats.IncrementMalformedPackets();
                return;
            }

            var key = new RoomKey(appId, roomCode);
            if (_rooms.TryGetValue(key, out RoomEntry? existing)
                && CryptographicOperations.FixedTimeEquals(existing.HostSecret, hostSecret))
            {
                _rooms.Remove(key);
                Stats.SetActiveRooms(_rooms.Count);
                Stats.IncrementUnregistrations();
            }
        }

        private void HandleClientJoinRequest(IPEndPoint sender, ReadOnlySpan<byte> payload, long now)
        {
            if (!NatPunchProtocol.TryReadClientJoinRequest(
                payload,
                out string roomCode,
                out string appId,
                out ReadOnlySpan<byte> requestNonce,
                out IPEndPoint clientInternal))
            {
                Stats.IncrementMalformedPackets();
                return;
            }

            Stats.IncrementJoinRequestsReceived();

            var key = new RoomKey(appId, roomCode);
            if (!_rooms.TryGetValue(key, out RoomEntry? room))
            {
                Stats.IncrementRoomNotFoundResponses();
                SendJoinError(sender, requestNonce, NatRendezvousStatus.RoomNotFound);
                return;
            }

            if (now >= room.ExpiresAtTimestamp)
            {
                _rooms.Remove(key);
                Stats.SetActiveRooms(_rooms.Count);
                Stats.IncrementRoomsExpired();
                Stats.IncrementRoomNotFoundResponses();
                SendJoinError(sender, requestNonce, NatRendezvousStatus.RoomNotFound);
                return;
            }

            if (now - room.IntroWindowStartTimestamp >= Stopwatch.Frequency)
            {
                room.IntroWindowStartTimestamp = now;
                room.IntrosInWindow = 0;
            }

            if (room.IntrosInWindow >= Config.MaxIntroductionsPerRoomPerSecond)
            {
                Stats.IncrementRateLimitedPackets();
                SendJoinError(sender, requestNonce, NatRendezvousStatus.HostBusy);
                return;
            }

            room.IntrosInWindow++;

            Span<byte> punchNonce = stackalloc byte[NatPunchProtocol.SecretBytes];
            RandomNumberGenerator.Fill(punchNonce);

            Span<byte> buffer = stackalloc byte[NatPunchProtocol.MaxPacketBytes];
            int hostLen = NatPunchProtocol.WriteIntroduce(
                buffer,
                role: 0,
                punchNonce,
                room.HostSecret,
                clientInternal,
                sender,
                roomCode);
            SendRaw(room.ExternalEndPoint, buffer.Slice(0, hostLen));

            int clientLen = NatPunchProtocol.WriteIntroduce(
                buffer,
                role: 1,
                punchNonce,
                requestNonce,
                room.InternalEndPoint,
                room.ExternalEndPoint,
                roomCode);
            SendRaw(sender, buffer.Slice(0, clientLen));

            Stats.IncrementIntroductionsSent();
        }

        private void SendHostRegistered(
            IPEndPoint destination,
            string roomCode,
            NatRendezvousStatus status,
            IPEndPoint observedExternal,
            ushort leaseSeconds)
        {
            Span<byte> buffer = stackalloc byte[NatPunchProtocol.MaxPacketBytes];
            int length = NatPunchProtocol.WriteHostRegistered(buffer, roomCode, status, observedExternal, leaseSeconds);
            SendRaw(destination, buffer.Slice(0, length));
        }

        private void SendJoinError(IPEndPoint destination, ReadOnlySpan<byte> requestNonce, NatRendezvousStatus status)
        {
            Span<byte> buffer = stackalloc byte[NatPunchProtocol.MaxPacketBytes];
            int length = NatPunchProtocol.WriteJoinError(buffer, requestNonce, status);
            SendRaw(destination, buffer.Slice(0, length));
        }

        private void SendRaw(IPEndPoint destination, ReadOnlySpan<byte> data)
        {
            if (_manager.IsRunning && _manager.SendUnconnectedMessage(data, destination))
                _sentInTick = true;
        }

        private void SweepExpiredRooms(long now)
        {
            if (_rooms.Count == 0)
                return;

            _expiredKeysScratch.Clear();
            foreach (var pair in _rooms)
            {
                if (now >= pair.Value.ExpiresAtTimestamp)
                    _expiredKeysScratch.Add(pair.Key);
            }

            if (_expiredKeysScratch.Count == 0)
                return;

            for (int i = 0; i < _expiredKeysScratch.Count; i++)
                _rooms.Remove(_expiredKeysScratch[i]);

            Stats.IncrementRoomsExpired(_expiredKeysScratch.Count);
            Stats.SetActiveRooms(_rooms.Count);
            _expiredKeysScratch.Clear();
        }

        private bool IsIpRateLimited(IPAddress normalizedIp, long now)
        {
            if (_rateIndex.TryGetValue(normalizedIp, out int slotIndex))
            {
                RateSlot slot = _rateRing[slotIndex];
                if (now - slot.WindowStartTimestamp >= Stopwatch.Frequency)
                {
                    slot.WindowStartTimestamp = now;
                    slot.Count = 1;
                }
                else
                {
                    slot.Count++;
                }

                _rateRing[slotIndex] = slot;
                return slot.Count > Config.MaxPacketsPerIpPerSecond;
            }

            if (_rateRing.Count < Config.MaxTrackedRateLimitIps)
            {
                int newIndex = _rateRing.Count;
                var storedIp = new IPAddress(normalizedIp.GetAddressBytes());
                _rateRing.Add(new RateSlot(storedIp, now, 1));
                _rateIndex[storedIp] = newIndex;
                return 1 > Config.MaxPacketsPerIpPerSecond;
            }

            int evictIndex = _rateHead;
            _rateHead = (_rateHead + 1) % _rateRing.Count;
            _rateIndex.Remove(_rateRing[evictIndex].Ip);

            var replacementIp = new IPAddress(normalizedIp.GetAddressBytes());
            _rateRing[evictIndex] = new RateSlot(replacementIp, now, 1);
            _rateIndex[replacementIp] = evictIndex;
            return 1 > Config.MaxPacketsPerIpPerSecond;
        }

        private void ReportSafely(NetworkError error)
        {
            try
            {
                Error?.Invoke(error);
            }
            catch
            {
            }
        }

        private sealed class RoomEntry
        {
            public RoomEntry(
                byte[] hostSecret,
                IPEndPoint internalEndPoint,
                IPEndPoint externalEndPoint,
                long expiresAtTimestamp,
                long introWindowStartTimestamp)
            {
                HostSecret = hostSecret;
                InternalEndPoint = internalEndPoint;
                ExternalEndPoint = externalEndPoint;
                ExpiresAtTimestamp = expiresAtTimestamp;
                IntroWindowStartTimestamp = introWindowStartTimestamp;
            }

            public byte[] HostSecret { get; }
            public IPEndPoint InternalEndPoint { get; set; }
            public IPEndPoint ExternalEndPoint { get; set; }
            public long ExpiresAtTimestamp { get; set; }
            public long IntroWindowStartTimestamp { get; set; }
            public int IntrosInWindow { get; set; }
        }

        private struct RateSlot
        {
            public RateSlot(IPAddress ip, long windowStartTimestamp, int count)
            {
                Ip = ip;
                WindowStartTimestamp = windowStartTimestamp;
                Count = count;
            }

            public IPAddress Ip;
            public long WindowStartTimestamp;
            public int Count;
        }

        private readonly struct RoomKey : IEquatable<RoomKey>
        {
            public RoomKey(string appId, string roomCode)
            {
                AppId = appId;
                RoomCode = roomCode;
            }

            public string AppId { get; }
            public string RoomCode { get; }

            public bool Equals(RoomKey other)
                => string.Equals(AppId, other.AppId, StringComparison.Ordinal)
                    && string.Equals(RoomCode, other.RoomCode, StringComparison.Ordinal);

            public override bool Equals(object? obj) => obj is RoomKey other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(
                    StringComparer.Ordinal.GetHashCode(AppId),
                    StringComparer.Ordinal.GetHashCode(RoomCode));
        }

        private sealed class Listener : INetEventListener
        {
            private readonly NetworkNatRendezvousServer _owner;

            public Listener(NetworkNatRendezvousServer owner) => _owner = owner;

            public void OnPeerConnected(NetPeer peer) { }

            public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) { }

            public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod) { }

            public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

            public void OnConnectionRequest(ConnectionRequest request) => request.Reject();

            public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
                => _owner.ReportSafely(new NetworkError(
                    NetworkErrorCode.Socket,
                    $"Rendezvous socket error: {socketError}.",
                    endPoint: endPoint));

            public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
            {
                if (messageType == UnconnectedMessageType.BasicMessage)
                    _owner.HandleUnconnected(remoteEndPoint, reader);
            }
        }
    }
}
