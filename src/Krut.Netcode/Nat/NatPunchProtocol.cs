using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteNetLib;

namespace Krut.Netcode
{
    internal enum NatPacketType : byte
    {
        HostRegister = 1,
        HostRegistered = 2,
        HostUnregister = 3,
        ClientJoinRequest = 4,
        JoinError = 5,
        Introduce = 6,
        Punch = 7,
    }

    [Flags]
    internal enum NatPunchFlags : byte
    {
        None = 0,
        IsAck = 1 << 0,
        FromHost = 1 << 1,
        IsInternal = 1 << 2,
    }

    /// <summary>
    /// Zero-reflection, NativeAOT-safe binary wire codec and endpoint utilities for Krut NAT punchthrough
    /// (<c>"KNAT"</c> protocol v1).
    /// </summary>
    internal static class NatPunchProtocol
    {
        public static ReadOnlySpan<byte> Magic => "KNAT"u8;

        public const byte ProtocolVersion = 1;
        public const int HeaderSize = 6;
        public const int SecretBytes = 16;
        public const int MaxRoomCodeLength = 64;
        public const int MaxAppIdLength = 64;
        public const int MaxPacketBytes = 512;

        private const byte AddressFamilyIPv4 = 4;
        private const byte AddressFamilyIPv6 = 6;
        private const byte MaxValidStatus = (byte)NatRendezvousStatus.HostBusy;

        public static bool HasMagicHeader(ReadOnlySpan<byte> payload)
            => payload.Length >= HeaderSize
                && payload[0] == (byte)'K'
                && payload[1] == (byte)'N'
                && payload[2] == (byte)'A'
                && payload[3] == (byte)'T'
                && payload[4] == ProtocolVersion
                && payload[5] >= (byte)NatPacketType.HostRegister
                && payload[5] <= (byte)NatPacketType.Punch;

        public static bool IsRawLiteNetLibUnconnectedNatPacket(byte[]? data, int offset, int length)
        {
            if (data is null || offset < 0 || length < NetConstants.HeaderSize + HeaderSize)
                return false;
            if ((uint)(offset + length) > (uint)data.Length)
                return false;

            return HasMagicHeader(data.AsSpan(offset + NetConstants.HeaderSize, length - NetConstants.HeaderSize));
        }

        public static IPAddress NormalizeAddress(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);
            return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        }

        public static IPEndPoint NormalizeEndPoint(IPEndPoint endPoint)
        {
            ArgumentNullException.ThrowIfNull(endPoint);
            if (!endPoint.Address.IsIPv4MappedToIPv6)
                return endPoint;

            return new IPEndPoint(endPoint.Address.MapToIPv4(), endPoint.Port);
        }

        public static bool EndPointsEqual(IPEndPoint? a, IPEndPoint? b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a is null || b is null || a.Port != b.Port)
                return false;

            return NormalizeAddress(a.Address).Equals(NormalizeAddress(b.Address));
        }

        public static bool TryValidateRoomCode(string? roomCode, out string trimmed)
        {
            if (string.IsNullOrWhiteSpace(roomCode))
            {
                trimmed = string.Empty;
                return false;
            }

            trimmed = roomCode.Trim();
            if (ContainsControlChars(trimmed))
            {
                trimmed = string.Empty;
                return false;
            }

            int byteCount = Encoding.UTF8.GetByteCount(trimmed);
            if (byteCount is < 1 or > MaxRoomCodeLength)
            {
                trimmed = string.Empty;
                return false;
            }

            return true;
        }

        public static bool TryValidateAppId(string? appId, out string normalized)
        {
            if (string.IsNullOrEmpty(appId) || ContainsControlChars(appId))
            {
                normalized = string.Empty;
                return false;
            }

            int byteCount = Encoding.UTF8.GetByteCount(appId);
            if (byteCount is < 1 or > MaxAppIdLength)
            {
                normalized = string.Empty;
                return false;
            }

            normalized = appId;
            return true;
        }

        public static IPEndPoint DiscoverLocalEndPoint(int localPort, IPEndPoint rendezvousEndPoint)
        {
            ArgumentNullException.ThrowIfNull(rendezvousEndPoint);
            IPEndPoint target = NormalizeEndPoint(rendezvousEndPoint);
            int port = localPort is > 0 and <= 65535 ? localPort : 1;

            if (IPAddress.IsLoopback(target.Address))
            {
                IPAddress loopback = target.AddressFamily == AddressFamily.InterNetworkV6
                    ? IPAddress.IPv6Loopback
                    : IPAddress.Loopback;
                return new IPEndPoint(loopback, port);
            }

            try
            {
                using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                socket.Connect(target);
                if (socket.LocalEndPoint is IPEndPoint localEp)
                {
                    IPAddress addr = NormalizeAddress(localEp.Address);
                    if (!addr.Equals(IPAddress.Any) && !addr.Equals(IPAddress.IPv6Any))
                        return new IPEndPoint(addr, port);
                }
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }

            IPAddress fallback = target.AddressFamily == AddressFamily.InterNetworkV6
                ? IPAddress.IPv6Loopback
                : IPAddress.Loopback;
            return new IPEndPoint(fallback, port);
        }

        public static int WriteHostRegister(
            Span<byte> buffer,
            string roomCode,
            string appId,
            ReadOnlySpan<byte> hostSecret,
            IPEndPoint internalEndPoint,
            int appProtocolVersion)
        {
            int pos = WriteHeader(buffer, NatPacketType.HostRegister);
            pos += WriteLengthPrefixedString(buffer.Slice(pos), roomCode, MaxRoomCodeLength);
            pos += WriteLengthPrefixedString(buffer.Slice(pos), appId, MaxAppIdLength);
            if (hostSecret.Length != SecretBytes)
                throw new ArgumentException($"Secret must be {SecretBytes} bytes.", nameof(hostSecret));
            hostSecret.CopyTo(buffer.Slice(pos, SecretBytes));
            pos += SecretBytes;
            pos += WriteEndPoint(buffer.Slice(pos), internalEndPoint);
            BinaryPrimitives.WriteInt32BigEndian(buffer.Slice(pos, 4), appProtocolVersion);
            return pos + 4;
        }

        public static bool TryReadHostRegister(
            ReadOnlySpan<byte> payload,
            out string roomCode,
            out string appId,
            out ReadOnlySpan<byte> hostSecret,
            out IPEndPoint internalEndPoint,
            out int appProtocolVersion)
        {
            roomCode = string.Empty;
            appId = string.Empty;
            hostSecret = default;
            internalEndPoint = null!;
            appProtocolVersion = 0;

            int pos = 0;
            if (!TryReadHeader(payload, ref pos, NatPacketType.HostRegister))
                return false;
            if (!TryReadLengthPrefixedString(payload, ref pos, MaxRoomCodeLength, out string rawRoom)
                || !TryValidateRoomCode(rawRoom, out roomCode)
                || !string.Equals(rawRoom, roomCode, StringComparison.Ordinal))
                return false;
            if (!TryReadLengthPrefixedString(payload, ref pos, MaxAppIdLength, out appId)
                || !TryValidateAppId(appId, out _))
                return false;
            if (payload.Length - pos < SecretBytes)
                return false;
            hostSecret = payload.Slice(pos, SecretBytes);
            pos += SecretBytes;
            if (!TryReadEndPoint(payload, ref pos, out internalEndPoint))
                return false;
            if (payload.Length - pos != 4)
                return false;

            appProtocolVersion = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(pos, 4));
            return appProtocolVersion > 0;
        }

        public static int WriteHostRegistered(
            Span<byte> buffer,
            string roomCode,
            NatRendezvousStatus status,
            IPEndPoint observedExternalEndPoint,
            ushort leaseSeconds)
        {
            int pos = WriteHeader(buffer, NatPacketType.HostRegistered);
            pos += WriteLengthPrefixedString(buffer.Slice(pos), roomCode, MaxRoomCodeLength);
            buffer[pos++] = (byte)status;
            pos += WriteEndPoint(buffer.Slice(pos), observedExternalEndPoint);
            BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(pos, 2), leaseSeconds);
            return pos + 2;
        }

        public static bool TryReadHostRegistered(
            ReadOnlySpan<byte> payload,
            out string roomCode,
            out NatRendezvousStatus status,
            out IPEndPoint observedExternalEndPoint,
            out ushort leaseSeconds)
        {
            roomCode = string.Empty;
            status = default;
            observedExternalEndPoint = null!;
            leaseSeconds = 0;

            int pos = 0;
            if (!TryReadHeader(payload, ref pos, NatPacketType.HostRegistered))
                return false;
            if (!TryReadLengthPrefixedString(payload, ref pos, MaxRoomCodeLength, out roomCode)
                || !TryValidateRoomCode(roomCode, out string trimmed)
                || !string.Equals(roomCode, trimmed, StringComparison.Ordinal))
                return false;
            if (pos >= payload.Length || payload[pos] > MaxValidStatus)
                return false;
            status = (NatRendezvousStatus)payload[pos++];
            if (!TryReadEndPoint(payload, ref pos, out observedExternalEndPoint))
                return false;
            if (payload.Length - pos != 2)
                return false;

            leaseSeconds = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(pos, 2));
            return true;
        }

        public static int WriteHostUnregister(
            Span<byte> buffer,
            string roomCode,
            string appId,
            ReadOnlySpan<byte> hostSecret)
        {
            int pos = WriteHeader(buffer, NatPacketType.HostUnregister);
            pos += WriteLengthPrefixedString(buffer.Slice(pos), roomCode, MaxRoomCodeLength);
            pos += WriteLengthPrefixedString(buffer.Slice(pos), appId, MaxAppIdLength);
            if (hostSecret.Length != SecretBytes)
                throw new ArgumentException($"Secret must be {SecretBytes} bytes.", nameof(hostSecret));
            hostSecret.CopyTo(buffer.Slice(pos, SecretBytes));
            return pos + SecretBytes;
        }

        public static bool TryReadHostUnregister(
            ReadOnlySpan<byte> payload,
            out string roomCode,
            out string appId,
            out ReadOnlySpan<byte> hostSecret)
        {
            roomCode = string.Empty;
            appId = string.Empty;
            hostSecret = default;

            int pos = 0;
            if (!TryReadHeader(payload, ref pos, NatPacketType.HostUnregister))
                return false;
            if (!TryReadLengthPrefixedString(payload, ref pos, MaxRoomCodeLength, out roomCode)
                || !TryValidateRoomCode(roomCode, out string trimmed)
                || !string.Equals(roomCode, trimmed, StringComparison.Ordinal))
                return false;
            if (!TryReadLengthPrefixedString(payload, ref pos, MaxAppIdLength, out appId)
                || !TryValidateAppId(appId, out _))
                return false;
            if (payload.Length - pos != SecretBytes)
                return false;

            hostSecret = payload.Slice(pos, SecretBytes);
            return true;
        }

        public static int WriteClientJoinRequest(
            Span<byte> buffer,
            string roomCode,
            string appId,
            ReadOnlySpan<byte> requestNonce,
            IPEndPoint internalEndPoint)
        {
            int pos = WriteHeader(buffer, NatPacketType.ClientJoinRequest);
            pos += WriteLengthPrefixedString(buffer.Slice(pos), roomCode, MaxRoomCodeLength);
            pos += WriteLengthPrefixedString(buffer.Slice(pos), appId, MaxAppIdLength);
            if (requestNonce.Length != SecretBytes)
                throw new ArgumentException($"Nonce must be {SecretBytes} bytes.", nameof(requestNonce));
            requestNonce.CopyTo(buffer.Slice(pos, SecretBytes));
            pos += SecretBytes;
            pos += WriteEndPoint(buffer.Slice(pos), internalEndPoint);
            return pos;
        }

        public static bool TryReadClientJoinRequest(
            ReadOnlySpan<byte> payload,
            out string roomCode,
            out string appId,
            out ReadOnlySpan<byte> requestNonce,
            out IPEndPoint internalEndPoint)
        {
            roomCode = string.Empty;
            appId = string.Empty;
            requestNonce = default;
            internalEndPoint = null!;

            int pos = 0;
            if (!TryReadHeader(payload, ref pos, NatPacketType.ClientJoinRequest))
                return false;
            if (!TryReadLengthPrefixedString(payload, ref pos, MaxRoomCodeLength, out roomCode)
                || !TryValidateRoomCode(roomCode, out string trimmed)
                || !string.Equals(roomCode, trimmed, StringComparison.Ordinal))
                return false;
            if (!TryReadLengthPrefixedString(payload, ref pos, MaxAppIdLength, out appId)
                || !TryValidateAppId(appId, out _))
                return false;
            if (payload.Length - pos < SecretBytes)
                return false;
            requestNonce = payload.Slice(pos, SecretBytes);
            pos += SecretBytes;
            if (!TryReadEndPoint(payload, ref pos, out internalEndPoint))
                return false;

            return pos == payload.Length;
        }

        public static int WriteJoinError(
            Span<byte> buffer,
            ReadOnlySpan<byte> requestNonce,
            NatRendezvousStatus status)
        {
            int pos = WriteHeader(buffer, NatPacketType.JoinError);
            if (requestNonce.Length != SecretBytes)
                throw new ArgumentException($"Nonce must be {SecretBytes} bytes.", nameof(requestNonce));
            requestNonce.CopyTo(buffer.Slice(pos, SecretBytes));
            pos += SecretBytes;
            buffer[pos++] = (byte)status;
            return pos;
        }

        public static bool TryReadJoinError(
            ReadOnlySpan<byte> payload,
            out ReadOnlySpan<byte> requestNonce,
            out NatRendezvousStatus status)
        {
            requestNonce = default;
            status = default;

            int pos = 0;
            if (!TryReadHeader(payload, ref pos, NatPacketType.JoinError))
                return false;
            if (payload.Length - pos != SecretBytes + 1)
                return false;

            requestNonce = payload.Slice(pos, SecretBytes);
            pos += SecretBytes;
            byte rawStatus = payload[pos];
            if (rawStatus > MaxValidStatus)
                return false;

            status = (NatRendezvousStatus)rawStatus;
            return true;
        }

        public static int WriteIntroduce(
            Span<byte> buffer,
            byte role,
            ReadOnlySpan<byte> punchNonce,
            ReadOnlySpan<byte> authOrRequestNonce,
            IPEndPoint peerInternalEndPoint,
            IPEndPoint peerExternalEndPoint,
            string roomCode)
        {
            int pos = WriteHeader(buffer, NatPacketType.Introduce);
            buffer[pos++] = role;
            if (punchNonce.Length != SecretBytes)
                throw new ArgumentException($"Punch nonce must be {SecretBytes} bytes.", nameof(punchNonce));
            punchNonce.CopyTo(buffer.Slice(pos, SecretBytes));
            pos += SecretBytes;
            if (authOrRequestNonce.Length != SecretBytes)
                throw new ArgumentException($"Auth/request nonce must be {SecretBytes} bytes.", nameof(authOrRequestNonce));
            authOrRequestNonce.CopyTo(buffer.Slice(pos, SecretBytes));
            pos += SecretBytes;
            pos += WriteEndPoint(buffer.Slice(pos), peerInternalEndPoint);
            pos += WriteEndPoint(buffer.Slice(pos), peerExternalEndPoint);
            pos += WriteLengthPrefixedString(buffer.Slice(pos), roomCode, MaxRoomCodeLength);
            return pos;
        }

        public static bool TryReadIntroduce(
            ReadOnlySpan<byte> payload,
            out byte role,
            out ReadOnlySpan<byte> punchNonce,
            out ReadOnlySpan<byte> authOrRequestNonce,
            out IPEndPoint peerInternalEndPoint,
            out IPEndPoint peerExternalEndPoint,
            out string roomCode)
        {
            role = 0;
            punchNonce = default;
            authOrRequestNonce = default;
            peerInternalEndPoint = null!;
            peerExternalEndPoint = null!;
            roomCode = string.Empty;

            int pos = 0;
            if (!TryReadHeader(payload, ref pos, NatPacketType.Introduce))
                return false;
            if (pos >= payload.Length)
                return false;
            role = payload[pos++];
            if (role > 1)
                return false;
            if (payload.Length - pos < SecretBytes * 2)
                return false;
            punchNonce = payload.Slice(pos, SecretBytes);
            pos += SecretBytes;
            authOrRequestNonce = payload.Slice(pos, SecretBytes);
            pos += SecretBytes;
            if (!TryReadEndPoint(payload, ref pos, out peerInternalEndPoint))
                return false;
            if (!TryReadEndPoint(payload, ref pos, out peerExternalEndPoint))
                return false;
            if (!TryReadLengthPrefixedString(payload, ref pos, MaxRoomCodeLength, out roomCode)
                || !TryValidateRoomCode(roomCode, out string trimmed)
                || !string.Equals(roomCode, trimmed, StringComparison.Ordinal))
                return false;

            return pos == payload.Length;
        }

        public static int WritePunch(Span<byte> buffer, ReadOnlySpan<byte> punchNonce, NatPunchFlags flags)
        {
            int pos = WriteHeader(buffer, NatPacketType.Punch);
            if (punchNonce.Length != SecretBytes)
                throw new ArgumentException($"Punch nonce must be {SecretBytes} bytes.", nameof(punchNonce));
            punchNonce.CopyTo(buffer.Slice(pos, SecretBytes));
            pos += SecretBytes;
            buffer[pos++] = (byte)flags;
            return pos;
        }

        public static bool TryReadPunch(
            ReadOnlySpan<byte> payload,
            out ReadOnlySpan<byte> punchNonce,
            out NatPunchFlags flags)
        {
            punchNonce = default;
            flags = NatPunchFlags.None;

            int pos = 0;
            if (!TryReadHeader(payload, ref pos, NatPacketType.Punch))
                return false;
            if (payload.Length - pos != SecretBytes + 1)
                return false;

            punchNonce = payload.Slice(pos, SecretBytes);
            byte rawFlags = payload[pos + SecretBytes];
            const byte validMask = (byte)(NatPunchFlags.IsAck | NatPunchFlags.FromHost | NatPunchFlags.IsInternal);
            if ((rawFlags & ~validMask) != 0)
                return false;

            flags = (NatPunchFlags)rawFlags;
            return true;
        }

        private static int WriteHeader(Span<byte> buffer, NatPacketType packetType)
        {
            Magic.CopyTo(buffer);
            buffer[4] = ProtocolVersion;
            buffer[5] = (byte)packetType;
            return HeaderSize;
        }

        private static bool TryReadHeader(ReadOnlySpan<byte> payload, ref int pos, NatPacketType expectedType)
        {
            if (payload.Length < HeaderSize
                || payload[0] != (byte)'K'
                || payload[1] != (byte)'N'
                || payload[2] != (byte)'A'
                || payload[3] != (byte)'T'
                || payload[4] != ProtocolVersion
                || payload[5] != (byte)expectedType)
                return false;

            pos = HeaderSize;
            return true;
        }

        private static int WriteLengthPrefixedString(Span<byte> buffer, string value, int maxBytes)
        {
            int written = Encoding.UTF8.GetBytes(value.AsSpan(), buffer.Slice(1, maxBytes));
            buffer[0] = (byte)written;
            return 1 + written;
        }

        private static bool TryReadLengthPrefixedString(ReadOnlySpan<byte> payload, ref int pos, int maxBytes, out string value)
        {
            value = string.Empty;
            if (pos >= payload.Length)
                return false;

            int byteLen = payload[pos++];
            if (byteLen <= 0 || byteLen > maxBytes || payload.Length - pos < byteLen)
                return false;

            value = Encoding.UTF8.GetString(payload.Slice(pos, byteLen));
            pos += byteLen;
            return !ContainsControlChars(value);
        }

        private static int WriteEndPoint(Span<byte> buffer, IPEndPoint endPoint)
        {
            IPAddress address = NormalizeAddress(endPoint.Address);
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                buffer[0] = AddressFamilyIPv4;
                if (!address.TryWriteBytes(buffer.Slice(1, 4), out int written) || written != 4)
                    throw new InvalidOperationException("Failed to write IPv4 address bytes.");
                BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(5, 2), (ushort)endPoint.Port);
                return 7;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                buffer[0] = AddressFamilyIPv6;
                if (!address.TryWriteBytes(buffer.Slice(1, 16), out int written) || written != 16)
                    throw new InvalidOperationException("Failed to write IPv6 address bytes.");
                BinaryPrimitives.WriteUInt16BigEndian(buffer.Slice(17, 2), (ushort)endPoint.Port);
                return 19;
            }

            throw new ArgumentException($"Unsupported address family: {address.AddressFamily}.", nameof(endPoint));
        }

        private static bool TryReadEndPoint(ReadOnlySpan<byte> payload, ref int pos, out IPEndPoint endPoint)
        {
            endPoint = null!;
            if (pos >= payload.Length)
                return false;

            byte family = payload[pos++];
            int addrBytes = family switch
            {
                AddressFamilyIPv4 => 4,
                AddressFamilyIPv6 => 16,
                _ => 0,
            };

            if (addrBytes == 0 || payload.Length - pos < addrBytes + 2)
                return false;

            var address = new IPAddress(payload.Slice(pos, addrBytes));
            pos += addrBytes;
            ushort port = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(pos, 2));
            pos += 2;
            if (port == 0)
                return false;

            endPoint = NormalizeEndPoint(new IPEndPoint(address, port));
            return true;
        }

        private static bool ContainsControlChars(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c < 0x20 || c == 0x7F)
                    return true;
            }

            return false;
        }
    }
}
