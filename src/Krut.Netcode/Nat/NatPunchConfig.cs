using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace Krut.Netcode
{
    /// <summary>Whether NAT hole punching succeeded over the local (LAN) or external (WAN) endpoint.</summary>
    public enum NatAddressKind : byte
    {
        /// <summary>Peer internal (LAN) address.</summary>
        Internal = 0,

        /// <summary>Peer public (WAN) NAT-mapped address.</summary>
        External = 1,
    }

    /// <summary>Status codes returned by <see cref="NetworkNatRendezvousServer"/>.</summary>
    public enum NatRendezvousStatus : byte
    {
        /// <summary>Request succeeded.</summary>
        Ok = 0,

        /// <summary>Room code is already registered under a different host secret.</summary>
        RoomTaken = 1,

        /// <summary>Rendezvous server reached <see cref="NatRendezvousConfig.MaxRooms"/>.</summary>
        ServerFull = 2,

        /// <summary>Caller exceeded the per-IP rate limit.</summary>
        RateLimited = 3,

        /// <summary>Request parameters or wire format were invalid.</summary>
        InvalidRequest = 4,

        /// <summary>The requested room code does not exist or has expired.</summary>
        RoomNotFound = 5,

        /// <summary>The room exceeded its per-room introduction rate limit.</summary>
        HostBusy = 6,
    }

    /// <summary>Describes a completed NAT hole punch between host and client.</summary>
    public readonly struct NatPunchResult
    {
        /// <summary>Creates a NAT punch result.</summary>
        public NatPunchResult(IPEndPoint remoteEndPoint, NatAddressKind addressKind, string roomCode)
        {
            RemoteEndPoint = remoteEndPoint;
            AddressKind = addressKind;
            RoomCode = roomCode;
        }

        /// <summary>Resolved peer endpoint over which packets punched through.</summary>
        public IPEndPoint RemoteEndPoint { get; }

        /// <summary>Whether communication succeeded via LAN (<see cref="NatAddressKind.Internal"/>) or WAN (<see cref="NatAddressKind.External"/>).</summary>
        public NatAddressKind AddressKind { get; }

        /// <summary>Room code used for the rendezvous introduction.</summary>
        public string RoomCode { get; }

        /// <inheritdoc/>
        public override string ToString() => $"{RoomCode} -> {RemoteEndPoint} ({AddressKind})";
    }

    /// <summary>Describes a successful room registration on <see cref="NetworkNatRendezvousServer"/>.</summary>
    public readonly struct NatRegistrationResult
    {
        /// <summary>Creates a NAT registration result.</summary>
        public NatRegistrationResult(string roomCode, IPEndPoint observedExternalEndPoint, int leaseSeconds)
        {
            RoomCode = roomCode;
            ObservedExternalEndPoint = observedExternalEndPoint;
            LeaseSeconds = leaseSeconds;
        }

        /// <summary>Registered room code.</summary>
        public string RoomCode { get; }

        /// <summary>Host public endpoint as observed by the rendezvous server.</summary>
        public IPEndPoint ObservedExternalEndPoint { get; }

        /// <summary>Lease lifetime in seconds before a heartbeat is required.</summary>
        public int LeaseSeconds { get; }

        /// <inheritdoc/>
        public override string ToString() => $"{RoomCode} @ {ObservedExternalEndPoint} (lease {LeaseSeconds}s)";
    }

    /// <summary>Thread-safe counters tracking client/host NAT punchthrough activity.</summary>
    public sealed class NatPunchStats
    {
        private long _registrationsSent;
        private long _heartbeatsSent;
        private long _joinRequestsSent;
        private long _introductionsReceived;
        private long _punchProbesSent;
        private long _punchAcksSent;
        private long _punchesSucceeded;
        private long _punchesFailed;
        private long _rejectedPackets;

        /// <summary>Number of initial room registration packets sent by the host.</summary>
        public long RegistrationsSent => Interlocked.Read(ref _registrationsSent);

        /// <summary>Number of room lease heartbeats sent by the host.</summary>
        public long HeartbeatsSent => Interlocked.Read(ref _heartbeatsSent);

        /// <summary>Number of room join requests sent by the client.</summary>
        public long JoinRequestsSent => Interlocked.Read(ref _joinRequestsSent);

        /// <summary>Number of valid <c>Introduce</c> packets received from the rendezvous server.</summary>
        public long IntroductionsReceived => Interlocked.Read(ref _introductionsReceived);

        /// <summary>Number of peer-to-peer NAT punch probes sent.</summary>
        public long PunchProbesSent => Interlocked.Read(ref _punchProbesSent);

        /// <summary>Number of NAT punch acknowledgements sent.</summary>
        public long PunchAcksSent => Interlocked.Read(ref _punchAcksSent);

        /// <summary>Number of NAT punch handshakes completed.</summary>
        public long PunchesSucceeded => Interlocked.Read(ref _punchesSucceeded);

        /// <summary>Number of NAT punch attempts or registrations that failed or timed out.</summary>
        public long PunchesFailed => Interlocked.Read(ref _punchesFailed);

        /// <summary>Number of malformed, spoofed, or unexpected NAT packets rejected.</summary>
        public long RejectedPackets => Interlocked.Read(ref _rejectedPackets);

        internal void IncrementRegistrationsSent() => Interlocked.Increment(ref _registrationsSent);
        internal void IncrementHeartbeatsSent() => Interlocked.Increment(ref _heartbeatsSent);
        internal void IncrementJoinRequestsSent() => Interlocked.Increment(ref _joinRequestsSent);
        internal void IncrementIntroductionsReceived() => Interlocked.Increment(ref _introductionsReceived);
        internal void IncrementPunchProbesSent() => Interlocked.Increment(ref _punchProbesSent);
        internal void IncrementPunchAcksSent() => Interlocked.Increment(ref _punchAcksSent);
        internal void IncrementPunchesSucceeded() => Interlocked.Increment(ref _punchesSucceeded);
        internal void IncrementPunchesFailed() => Interlocked.Increment(ref _punchesFailed);
        internal void IncrementRejectedPackets() => Interlocked.Increment(ref _rejectedPackets);
    }

    /// <summary>Settings controlling NAT punchthrough on <see cref="NetworkServer"/> and <see cref="NetworkClient"/>.</summary>
    public sealed class NatPunchConfig
    {
        /// <summary>Optional 32-byte AES-256-GCM pre-shared key authenticating and encrypting traffic with the rendezvous server.</summary>
        public byte[]? RendezvousPreSharedKey { get; set; }

        /// <summary>Optional previous 32-byte rendezvous PSK accepted during zero-downtime key rotation.</summary>
        public byte[]? PreviousRendezvousPreSharedKey { get; set; }

        /// <summary>4-bit key identifier (0..15) for rendezvous PSK rotation.</summary>
        public byte RendezvousKeyId { get; set; } = 1;

        /// <summary>Enables 64-packet sliding anti-replay protection on rendezvous datagram encryption.</summary>
        public bool EnableAntiReplay { get; set; } = true;

        /// <summary>Interval in milliseconds between host lease heartbeats sent to the rendezvous server. Minimum 1000 ms.</summary>
        public int HeartbeatIntervalMs { get; set; } = 15_000;

        /// <summary>Retry interval in milliseconds while waiting for initial host registration acknowledgement. Minimum 100 ms.</summary>
        public int RegisterRetryIntervalMs { get; set; } = 500;

        /// <summary>Interval in milliseconds between NAT punch probes / client join retries. Clamped to 50..5000 ms.</summary>
        public int PunchIntervalMs { get; set; } = 200;

        /// <summary>Maximum time in milliseconds allowed for client NAT punchthrough before failing. Minimum 500 ms.</summary>
        public int PunchTimeoutMs { get; set; } = 8_000;

        /// <summary>Maximum simultaneous pending NAT punch states held by a host. Clamped to 1..1024.</summary>
        public int MaxPendingPunches { get; set; } = 64;

        /// <summary>Optional explicit internal endpoint reported to the rendezvous server instead of auto-discovering the LAN address.</summary>
        public IPEndPoint? LocalEndPointOverride { get; set; }

        /// <summary>Creates a <see cref="NatPunchConfig"/> with AES-256-GCM rendezvous encryption enabled.</summary>
        public static NatPunchConfig CreateEncrypted(
            ReadOnlySpan<byte> rendezvousKey,
            ReadOnlySpan<byte> previousKey = default,
            byte keyId = 1) => new()
        {
            RendezvousPreSharedKey = rendezvousKey.ToArray(),
            PreviousRendezvousPreSharedKey = previousKey.IsEmpty ? null : previousKey.ToArray(),
            RendezvousKeyId = (byte)(keyId & 0x0F),
        };

        /// <summary>Validates encryption key sizes and clamps timing/capacity limits to safe bounds.</summary>
        public void Validate()
        {
            if (HeartbeatIntervalMs < 1000)
                HeartbeatIntervalMs = 1000;
            if (RegisterRetryIntervalMs < 100)
                RegisterRetryIntervalMs = 100;
            PunchIntervalMs = Math.Clamp(PunchIntervalMs, 50, 5000);
            if (PunchTimeoutMs < 500)
                PunchTimeoutMs = 500;
            MaxPendingPunches = Math.Clamp(MaxPendingPunches, 1, 1024);
            RendezvousKeyId = (byte)(RendezvousKeyId & 0x0F);

            if (RendezvousPreSharedKey is not null)
            {
                if (!AesGcm.IsSupported)
                    throw new PlatformNotSupportedException("AES-GCM is not supported on this platform.");

                if (RendezvousPreSharedKey.Length != NetworkCrypto.KeySizeBytes)
                {
                    throw new ArgumentException(
                        $"RendezvousPreSharedKey must be exactly {NetworkCrypto.KeySizeBytes} bytes (got {RendezvousPreSharedKey.Length}).",
                        nameof(RendezvousPreSharedKey));
                }
            }

            if (PreviousRendezvousPreSharedKey is not null)
            {
                if (RendezvousPreSharedKey is null)
                {
                    throw new ArgumentException(
                        "PreviousRendezvousPreSharedKey requires RendezvousPreSharedKey to also be set.",
                        nameof(PreviousRendezvousPreSharedKey));
                }

                if (PreviousRendezvousPreSharedKey.Length != NetworkCrypto.KeySizeBytes)
                {
                    throw new ArgumentException(
                        $"PreviousRendezvousPreSharedKey must be exactly {NetworkCrypto.KeySizeBytes} bytes (got {PreviousRendezvousPreSharedKey.Length}).",
                        nameof(PreviousRendezvousPreSharedKey));
                }
            }

            if (LocalEndPointOverride is not null)
                LocalEndPointOverride = NatPunchProtocol.NormalizeEndPoint(LocalEndPointOverride);
        }

        /// <summary>Deep-clones this configuration.</summary>
        public NatPunchConfig Clone()
        {
            var copy = (NatPunchConfig)MemberwiseClone();
            if (RendezvousPreSharedKey is not null)
                copy.RendezvousPreSharedKey = (byte[])RendezvousPreSharedKey.Clone();
            if (PreviousRendezvousPreSharedKey is not null)
                copy.PreviousRendezvousPreSharedKey = (byte[])PreviousRendezvousPreSharedKey.Clone();
            if (LocalEndPointOverride is not null)
            {
                copy.LocalEndPointOverride = new IPEndPoint(
                    new IPAddress(LocalEndPointOverride.Address.GetAddressBytes()),
                    LocalEndPointOverride.Port);
            }

            return copy;
        }
    }

    /// <summary>Settings for <see cref="NetworkNatRendezvousServer"/>.</summary>
    public sealed class NatRendezvousConfig
    {
        /// <summary>Default UDP port for the rendezvous server.</summary>
        public const int DefaultPort = 26000;

        /// <summary>UDP port to bind (0 = ephemeral port assigned by OS). Valid range 0..65535.</summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>Enables dual-stack IPv4/IPv6 socket binding.</summary>
        public bool IPv6Support { get; set; } = true;

        /// <summary>Maximum simultaneous active rooms tracked by the server. Clamped to 1..1,000,000.</summary>
        public int MaxRooms { get; set; } = 4096;

        /// <summary>Room registration lease duration in seconds before expiration. Clamped to 5..3600.</summary>
        public int RoomLeaseSeconds { get; set; } = 45;

        /// <summary>Maximum packets accepted per source IP address per second. Clamped to 1..10,000.</summary>
        public int MaxPacketsPerIpPerSecond { get; set; } = 30;

        /// <summary>Maximum introductions triggered per room per second. Clamped to 1..10,000.</summary>
        public int MaxIntroductionsPerRoomPerSecond { get; set; } = 10;

        /// <summary>Maximum source IPs tracked in the bounded rate-limiter ring. Clamped to 64..1,000,000.</summary>
        public int MaxTrackedRateLimitIps { get; set; } = 8192;

        /// <summary>Optional 32-byte AES-256-GCM pre-shared key matching clients/hosts <see cref="NatPunchConfig.RendezvousPreSharedKey"/>.</summary>
        public byte[]? PreSharedKey { get; set; }

        /// <summary>Optional previous 32-byte PSK accepted during zero-downtime key rotation.</summary>
        public byte[]? PreviousPreSharedKey { get; set; }

        /// <summary>4-bit key identifier (0..15) for PSK rotation.</summary>
        public byte KeyId { get; set; } = 1;

        /// <summary>Enables 64-packet sliding anti-replay protection per peer/direction when <see cref="PreSharedKey"/> is set.</summary>
        public bool EnableAntiReplay { get; set; } = true;

        /// <summary>Validates keys and clamps limits to safe ranges.</summary>
        public void Validate()
        {
            if (Port is < 0 or > 65535)
                Port = DefaultPort;
            MaxRooms = Math.Clamp(MaxRooms, 1, 1_000_000);
            RoomLeaseSeconds = Math.Clamp(RoomLeaseSeconds, 5, 3600);
            MaxPacketsPerIpPerSecond = Math.Clamp(MaxPacketsPerIpPerSecond, 1, 10_000);
            MaxIntroductionsPerRoomPerSecond = Math.Clamp(MaxIntroductionsPerRoomPerSecond, 1, 10_000);
            MaxTrackedRateLimitIps = Math.Clamp(MaxTrackedRateLimitIps, 64, 1_000_000);
            KeyId = (byte)(KeyId & 0x0F);

            if (PreSharedKey is not null)
            {
                if (!AesGcm.IsSupported)
                    throw new PlatformNotSupportedException("AES-GCM is not supported on this platform.");

                if (PreSharedKey.Length != NetworkCrypto.KeySizeBytes)
                {
                    throw new ArgumentException(
                        $"PreSharedKey must be exactly {NetworkCrypto.KeySizeBytes} bytes (got {PreSharedKey.Length}).",
                        nameof(PreSharedKey));
                }
            }

            if (PreviousPreSharedKey is not null)
            {
                if (PreSharedKey is null)
                {
                    throw new ArgumentException(
                        "PreviousPreSharedKey requires PreSharedKey to also be set.",
                        nameof(PreviousPreSharedKey));
                }

                if (PreviousPreSharedKey.Length != NetworkCrypto.KeySizeBytes)
                {
                    throw new ArgumentException(
                        $"PreviousPreSharedKey must be exactly {NetworkCrypto.KeySizeBytes} bytes (got {PreviousPreSharedKey.Length}).",
                        nameof(PreviousPreSharedKey));
                }
            }
        }

        /// <summary>Deep-clones this configuration.</summary>
        public NatRendezvousConfig Clone()
        {
            var copy = (NatRendezvousConfig)MemberwiseClone();
            if (PreSharedKey is not null)
                copy.PreSharedKey = (byte[])PreSharedKey.Clone();
            if (PreviousPreSharedKey is not null)
                copy.PreviousPreSharedKey = (byte[])PreviousPreSharedKey.Clone();
            return copy;
        }
    }

    /// <summary>Thread-safe operational counters exposed by <see cref="NetworkNatRendezvousServer"/>.</summary>
    public sealed class NatRendezvousStats
    {
        private long _activeRooms;
        private long _registrationsAccepted;
        private long _registrationsRejected;
        private long _unregistrations;
        private long _roomsExpired;
        private long _joinRequestsReceived;
        private long _introductionsSent;
        private long _roomNotFoundResponses;
        private long _rateLimitedPackets;
        private long _malformedPackets;

        /// <summary>Current number of registered active rooms.</summary>
        public long ActiveRooms => Interlocked.Read(ref _activeRooms);

        /// <summary>Total accepted host registrations and renewals.</summary>
        public long RegistrationsAccepted => Interlocked.Read(ref _registrationsAccepted);

        /// <summary>Total rejected host registrations (e.g., room taken or server full).</summary>
        public long RegistrationsRejected => Interlocked.Read(ref _registrationsRejected);

        /// <summary>Total explicit host unregistrations processed.</summary>
        public long Unregistrations => Interlocked.Read(ref _unregistrations);

        /// <summary>Total rooms removed due to lease expiration.</summary>
        public long RoomsExpired => Interlocked.Read(ref _roomsExpired);

        /// <summary>Total valid client join requests received.</summary>
        public long JoinRequestsReceived => Interlocked.Read(ref _joinRequestsReceived);

        /// <summary>Total host/client introduction pairs dispatched.</summary>
        public long IntroductionsSent => Interlocked.Read(ref _introductionsSent);

        /// <summary>Total <c>RoomNotFound</c> responses sent.</summary>
        public long RoomNotFoundResponses => Interlocked.Read(ref _roomNotFoundResponses);

        /// <summary>Total packets dropped or rejected due to IP or room rate limits.</summary>
        public long RateLimitedPackets => Interlocked.Read(ref _rateLimitedPackets);

        /// <summary>Total malformed or unrecognized datagrams ignored.</summary>
        public long MalformedPackets => Interlocked.Read(ref _malformedPackets);

        internal void SetActiveRooms(int count) => Interlocked.Exchange(ref _activeRooms, count);
        internal void IncrementRegistrationsAccepted() => Interlocked.Increment(ref _registrationsAccepted);
        internal void IncrementRegistrationsRejected() => Interlocked.Increment(ref _registrationsRejected);
        internal void IncrementUnregistrations() => Interlocked.Increment(ref _unregistrations);
        internal void IncrementRoomsExpired(int count = 1) => Interlocked.Add(ref _roomsExpired, count);
        internal void IncrementJoinRequestsReceived() => Interlocked.Increment(ref _joinRequestsReceived);
        internal void IncrementIntroductionsSent() => Interlocked.Increment(ref _introductionsSent);
        internal void IncrementRoomNotFoundResponses() => Interlocked.Increment(ref _roomNotFoundResponses);
        internal void IncrementRateLimitedPackets() => Interlocked.Increment(ref _rateLimitedPackets);
        internal void IncrementMalformedPackets() => Interlocked.Increment(ref _malformedPackets);
    }
}
