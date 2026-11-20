using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using LiteNetLib.Layers;

namespace Krut.Netcode
{
    /// <summary>
    /// Authenticated encryption layer (AES-256-GCM) operating on raw UDP datagrams under LiteNetLib.
    /// <para>
    /// Wire layout (trailer format for zero-copy in-place encryption):
    /// <c>[Ciphertext (P bytes)] [Header (13 bytes: 1 B version|keyId, 6 B senderSalt, 6 B big-endian counter)] [Tag (16 bytes)]</c>.
    /// Uses directional HKDF-SHA256 subkeys (preventing reflection attacks) and a 64-packet sliding anti-replay window
    /// per remote endpoint and sender salt.
    /// </para>
    /// </summary>
    public sealed class NetworkEncryptionLayer : PacketLayerBase, IDisposable
    {
        /// <summary>Size of the version/keyId + senderSalt + counter trailer header in bytes.</summary>
        public const int HeaderSizeBytes = 13;

        /// <summary>AES-GCM authentication tag length in bytes.</summary>
        public const int TagSizeBytes = 16;

        /// <summary>Total datagram overhead added by this layer (29 bytes).</summary>
        public const int OverheadBytes = HeaderSizeBytes + TagSizeBytes;

        private const byte VersionNibble = 0x40;
        private const ulong MaxCounter = (1UL << 48) - 1;
        private const int MaxReplayPeers = 1024;

        private static ReadOnlySpan<byte> PskSalt => "krut.netcode.psk.v1"u8;
        private static ReadOnlySpan<byte> ClientToServerInfo => "krut.netcode.v1.c2s"u8;
        private static ReadOnlySpan<byte> ServerToClientInfo => "krut.netcode.v1.s2c"u8;

        private readonly object _outLock = new();
        private readonly object _inLock = new();
        private readonly byte _keyId;
        private readonly bool _enableAntiReplay;
        private readonly byte[] _senderSalt = new byte[6];
        private readonly AesGcm _outboundAes;
        private readonly AesGcm _inboundAes;
        private readonly AesGcm? _previousInboundAes;
        private readonly NetworkEncryptionStats _stats;

        private byte[] _outBuf = new byte[2048];
        private byte[] _inScratch = new byte[2048];
        private ulong _sendCounter;
        private bool _disposed;

        private readonly Dictionary<ReplayKey, int> _replayIndex = new();
        private readonly ReplayKey[] _replayRing = new ReplayKey[MaxReplayPeers];
        private readonly AntiReplayWindow[] _replayWindows = new AntiReplayWindow[MaxReplayPeers];
        private int _replayCount;
        private int _replayRingHead;
        private volatile RendezvousRoute? _rendezvousRoute;

        private sealed class RendezvousRoute
        {
            public readonly IPEndPoint EndPoint;
            public readonly NetworkEncryptionLayer? Layer;
            public readonly bool AllowPlaintextNat;

            public RendezvousRoute(IPEndPoint endPoint, NetworkEncryptionLayer? layer, bool allowPlaintextNat)
            {
                EndPoint = endPoint;
                Layer = layer;
                AllowPlaintextNat = allowPlaintextNat;
            }
        }

        public NetworkEncryptionLayer(
            ReadOnlySpan<byte> preSharedKey,
            bool isServer,
            ReadOnlySpan<byte> previousPreSharedKey = default,
            byte keyId = 1,
            bool enableAntiReplay = true,
            NetworkEncryptionStats? stats = null)
            : base(OverheadBytes)
        {
            if (preSharedKey.Length != NetworkCrypto.KeySizeBytes)
            {
                throw new ArgumentException(
                    $"PreSharedKey must be {NetworkCrypto.KeySizeBytes} bytes.",
                    nameof(preSharedKey));
            }

            if (!previousPreSharedKey.IsEmpty && previousPreSharedKey.Length != NetworkCrypto.KeySizeBytes)
            {
                throw new ArgumentException(
                    $"PreviousPreSharedKey must be {NetworkCrypto.KeySizeBytes} bytes.",
                    nameof(previousPreSharedKey));
            }

            if (!AesGcm.IsSupported)
                throw new PlatformNotSupportedException("AES-GCM is not supported on this platform.");

            _keyId = (byte)(keyId & 0x0F);
            _enableAntiReplay = enableAntiReplay;
            _stats = stats ?? new NetworkEncryptionStats();
            RandomNumberGenerator.Fill(_senderSalt);

            ReadOnlySpan<byte> outInfo = isServer ? ServerToClientInfo : ClientToServerInfo;
            ReadOnlySpan<byte> inInfo = isServer ? ClientToServerInfo : ServerToClientInfo;

            Span<byte> derived = stackalloc byte[NetworkCrypto.KeySizeBytes];
            try
            {
                HKDF.DeriveKey(HashAlgorithmName.SHA256, preSharedKey, derived, PskSalt, outInfo);
                _outboundAes = new AesGcm(derived, TagSizeBytes);

                HKDF.DeriveKey(HashAlgorithmName.SHA256, preSharedKey, derived, PskSalt, inInfo);
                _inboundAes = new AesGcm(derived, TagSizeBytes);

                if (!previousPreSharedKey.IsEmpty)
                {
                    HKDF.DeriveKey(HashAlgorithmName.SHA256, previousPreSharedKey, derived, PskSalt, inInfo);
                    _previousInboundAes = new AesGcm(derived, TagSizeBytes);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derived);
            }
        }

        /// <summary>Encryption, authentication, and replay counters for this layer.</summary>
        public NetworkEncryptionStats Stats => _stats;

        internal void SetRendezvousRoute(IPEndPoint? endPoint, NetworkEncryptionLayer? rendezvousLayer, bool allowPlaintextNat)
        {
            _rendezvousRoute = endPoint is null
                ? null
                : new RendezvousRoute(NatPunchProtocol.NormalizeEndPoint(endPoint), rendezvousLayer, allowPlaintextNat);
        }

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            var route = _rendezvousRoute;
            if (route is not null && NatPunchProtocol.EndPointsEqual(endPoint, route.EndPoint))
            {
                if (route.Layer is not null)
                {
                    route.Layer.ProcessOutBoundPacket(ref endPoint, ref data, ref offset, ref length);
                    return;
                }

                if (route.AllowPlaintextNat && NatPunchProtocol.IsRawLiteNetLibUnconnectedNatPacket(data, offset, length))
                    return;
            }

            lock (_outLock)
            {
                if (_disposed || data is null || length <= 0 || offset < 0 || offset + length > data.Length)
                {
                    length = 0;
                    return;
                }

                if (_sendCounter >= MaxCounter)
                {
                    length = 0;
                    return;
                }

                ulong counter = ++_sendCounter;
                int payloadLength = length;
                int totalLength = payloadLength + OverheadBytes;

                if (data.Length < offset + totalLength)
                {
                    if (_outBuf.Length < totalLength)
                        _outBuf = new byte[Math.Max(_outBuf.Length * 2, totalLength)];
                    Buffer.BlockCopy(data, offset, _outBuf, 0, payloadLength);
                    data = _outBuf;
                    offset = 0;
                }

                Span<byte> header = data.AsSpan(offset + payloadLength, HeaderSizeBytes);
                header[0] = (byte)(VersionNibble | _keyId);
                _senderSalt.CopyTo(header.Slice(1, 6));
                WriteUInt48BigEndian(header.Slice(7, 6), counter);

                Span<byte> payload = data.AsSpan(offset, payloadLength);
                ReadOnlySpan<byte> nonce = header.Slice(1, 12);
                Span<byte> tag = data.AsSpan(offset + payloadLength + HeaderSizeBytes, TagSizeBytes);

                _outboundAes.Encrypt(nonce, payload, payload, tag, header);
                length = totalLength;
                _stats.IncrementEncrypted();
            }
        }

        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            var route = _rendezvousRoute;
            if (route is not null && NatPunchProtocol.EndPointsEqual(endPoint, route.EndPoint))
            {
                if (route.Layer is not null)
                {
                    route.Layer.ProcessInboundPacket(ref endPoint, ref data, ref length);
                    return;
                }

                if (route.AllowPlaintextNat)
                {
                    if (NatPunchProtocol.IsRawLiteNetLibUnconnectedNatPacket(data, 0, length))
                        return;

                    _stats.IncrementMalformed();
                    length = 0;
                    return;
                }
            }

            lock (_inLock)
            {
                if (_disposed || data is null || length <= OverheadBytes || length > data.Length)
                {
                    if (length > 0)
                        _stats.IncrementMalformed();
                    length = 0;
                    return;
                }

                int payloadLength = length - OverheadBytes;
                ReadOnlySpan<byte> header = data.AsSpan(payloadLength, HeaderSizeBytes);
                if ((header[0] & 0xF0) != VersionNibble)
                {
                    _stats.IncrementMalformed();
                    length = 0;
                    return;
                }

                ulong counter = ReadUInt48BigEndian(header.Slice(7, 6));
                if (counter == 0)
                {
                    _stats.IncrementMalformed();
                    length = 0;
                    return;
                }

                ulong senderSalt = ReadUInt48BigEndian(header.Slice(1, 6));
                var replayKey = _enableAntiReplay
                    ? new ReplayKey(
                        endPoint is not null ? NatPunchProtocol.NormalizeAddress(endPoint.Address) : IPAddress.None,
                        endPoint?.Port ?? 0,
                        senderSalt)
                    : default;

                int existingSlot = -1;
                if (_enableAntiReplay)
                {
                    if (!_replayIndex.TryGetValue(replayKey, out existingSlot))
                        existingSlot = -1;
                    else if (_replayWindows[existingSlot].IsReplay(counter))
                    {
                        _stats.IncrementReplays();
                        length = 0;
                        return;
                    }
                }

                ReadOnlySpan<byte> nonce = header.Slice(1, 12);
                ReadOnlySpan<byte> tag = data.AsSpan(payloadLength + HeaderSizeBytes, TagSizeBytes);
                Span<byte> destination = data.AsSpan(0, payloadLength);

                bool authenticated = false;
                if (_previousInboundAes is null)
                {
                    try
                    {
                        _inboundAes.Decrypt(nonce, destination, tag, destination, header);
                        authenticated = true;
                    }
                    catch (CryptographicException)
                    {
                    }
                }
                else
                {
                    if (_inScratch.Length < payloadLength)
                        _inScratch = new byte[Math.Max(_inScratch.Length * 2, payloadLength)];
                    Buffer.BlockCopy(data, 0, _inScratch, 0, payloadLength);
                    ReadOnlySpan<byte> ciphertext = _inScratch.AsSpan(0, payloadLength);

                    try
                    {
                        _inboundAes.Decrypt(nonce, ciphertext, tag, destination, header);
                        authenticated = true;
                    }
                    catch (CryptographicException)
                    {
                        try
                        {
                            _previousInboundAes.Decrypt(nonce, ciphertext, tag, destination, header);
                            authenticated = true;
                        }
                        catch (CryptographicException)
                        {
                        }
                    }
                }

                if (!authenticated)
                {
                    _stats.IncrementAuthFailures();
                    length = 0;
                    return;
                }

                if (_enableAntiReplay)
                {
                    if (existingSlot < 0)
                    {
                        if (_replayCount < MaxReplayPeers)
                        {
                            existingSlot = _replayCount++;
                        }
                        else
                        {
                            existingSlot = _replayRingHead;
                            _replayIndex.Remove(_replayRing[existingSlot]);
                            _replayWindows[existingSlot].Reset();
                            _replayRingHead = (_replayRingHead + 1) % MaxReplayPeers;
                        }

                        var storedKey = new ReplayKey(new IPAddress(replayKey.Address.GetAddressBytes()), replayKey.Port, senderSalt);
                        _replayRing[existingSlot] = storedKey;
                        _replayIndex[storedKey] = existingSlot;
                    }

                    _replayWindows[existingSlot].Record(counter);
                }

                length = payloadLength;
                _stats.IncrementDecrypted();
            }
        }

        /// <summary>Clears tracked inbound replay windows (e.g., when a session stops and restarts).</summary>
        public void ResetReplayState()
        {
            lock (_inLock)
            {
                _replayIndex.Clear();
                Array.Clear(_replayRing, 0, _replayCount);
                Array.Clear(_replayWindows, 0, _replayCount);
                _replayCount = 0;
                _replayRingHead = 0;
            }
        }

        public void Dispose()
        {
            lock (_outLock)
            {
                lock (_inLock)
                {
                    if (_disposed)
                        return;

                    _disposed = true;
                    _rendezvousRoute = null;
                    _outboundAes.Dispose();
                    _inboundAes.Dispose();
                    _previousInboundAes?.Dispose();
                    _replayIndex.Clear();
                    Array.Clear(_replayRing, 0, _replayRing.Length);
                    Array.Clear(_replayWindows, 0, _replayWindows.Length);
                }
            }
        }

        private static void WriteUInt48BigEndian(Span<byte> dest, ulong value)
        {
            dest[0] = (byte)(value >> 40);
            dest[1] = (byte)(value >> 32);
            dest[2] = (byte)(value >> 24);
            dest[3] = (byte)(value >> 16);
            dest[4] = (byte)(value >> 8);
            dest[5] = (byte)value;
        }

        private static ulong ReadUInt48BigEndian(ReadOnlySpan<byte> src) =>
            ((ulong)src[0] << 40)
            | ((ulong)src[1] << 32)
            | ((ulong)src[2] << 24)
            | ((ulong)src[3] << 16)
            | ((ulong)src[4] << 8)
            | src[5];

        private readonly struct ReplayKey : IEquatable<ReplayKey>
        {
            public ReplayKey(IPAddress address, int port, ulong salt)
            {
                Address = address;
                Port = port;
                Salt = salt;
            }

            public IPAddress Address { get; }
            public int Port { get; }
            public ulong Salt { get; }

            public bool Equals(ReplayKey other) =>
                Port == other.Port && Salt == other.Salt && Address.Equals(other.Address);

            public override bool Equals(object? obj) => obj is ReplayKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(Address, Port, Salt);
        }
    }
}
