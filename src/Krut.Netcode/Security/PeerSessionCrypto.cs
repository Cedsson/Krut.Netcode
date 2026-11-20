using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LiteNetLib.Utils;

namespace Krut.Netcode
{
    /// <summary>
    /// Per-peer ephemeral ECDH P-256 + AES-256-GCM session cryptography and handshake helpers.
    /// </summary>
    internal sealed class PeerSessionCrypto : IDisposable
    {
        public enum DecryptResult
        {
            Ok,
            Malformed,
            Replay,
            AuthFailed,
        }

        public const int PublicKeyBytes = 64;
        public const int NonceBytes = 32;
        public const int ConfirmMacBytes = 32;
        public const int SignatureBytes = 64;
        public const int EnvelopeOverheadBytes = 1 + 8 + 16;

        private const int NoncePrefixBytes = 4;
        private const int TagSizeBytes = 16;

        private readonly AesGcm _sendAes;
        private readonly AesGcm _recvAes;
        private readonly byte[] _sendNoncePrefix = new byte[NoncePrefixBytes];
        private readonly byte[] _recvNoncePrefix = new byte[NoncePrefixBytes];
        private readonly bool _enableAntiReplay;

        private AntiReplayWindow _reliableReplayWindow;
        private AntiReplayWindow _unreliableReplayWindow;
        private ulong _sendCounter;
        private bool _disposed;

        private PeerSessionCrypto(
            ReadOnlySpan<byte> sendKey,
            ReadOnlySpan<byte> recvKey,
            ReadOnlySpan<byte> sendNoncePrefix,
            ReadOnlySpan<byte> recvNoncePrefix,
            bool enableAntiReplay)
        {
            _sendAes = new AesGcm(sendKey, TagSizeBytes);
            _recvAes = new AesGcm(recvKey, TagSizeBytes);
            sendNoncePrefix.CopyTo(_sendNoncePrefix);
            recvNoncePrefix.CopyTo(_recvNoncePrefix);
            _enableAntiReplay = enableAntiReplay;
        }

        public static void CreateEphemeralKeyPair(out ECDiffieHellman ecdh, out byte[] publicKey64)
        {
            ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            ECParameters parameters = ecdh.ExportParameters(includePrivateParameters: false);
            publicKey64 = new byte[PublicKeyBytes];
            WriteCoordinate32(parameters.Q.X, publicKey64.AsSpan(0, 32));
            WriteCoordinate32(parameters.Q.Y, publicKey64.AsSpan(32, 32));
        }

        public static byte[] SignServerTranscript(
            ECDsa identity,
            string appId,
            int protocolVersion,
            ReadOnlySpan<byte> serverPub64,
            ReadOnlySpan<byte> serverNonce32)
        {
            byte[] transcript = BuildServerSignTranscript(appId, protocolVersion, serverPub64, serverNonce32);
            return identity.SignData(transcript, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        public static bool VerifyServerTranscript(
            ECDsa publicIdentity,
            string appId,
            int protocolVersion,
            ReadOnlySpan<byte> serverPub64,
            ReadOnlySpan<byte> serverNonce32,
            ReadOnlySpan<byte> signature64)
        {
            if (serverPub64.Length != PublicKeyBytes
                || serverNonce32.Length != NonceBytes
                || signature64.Length != SignatureBytes)
            {
                return false;
            }

            byte[] transcript = BuildServerSignTranscript(appId, protocolVersion, serverPub64, serverNonce32);
            return publicIdentity.VerifyData(transcript, signature64, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }

        public static bool TryDerive(
            ECDiffieHellman localEcdh,
            ReadOnlySpan<byte> remotePub64,
            ReadOnlySpan<byte> serverPub64,
            ReadOnlySpan<byte> serverNonce32,
            ReadOnlySpan<byte> clientPub64,
            ReadOnlySpan<byte> clientNonce32,
            string appId,
            int protocolVersion,
            bool isServer,
            bool enableAntiReplay,
            out PeerSessionCrypto? sessionCrypto,
            out byte[]? confirmMac32)
        {
            sessionCrypto = null;
            confirmMac32 = null;

            if (remotePub64.Length != PublicKeyBytes
                || serverPub64.Length != PublicKeyBytes
                || clientPub64.Length != PublicKeyBytes
                || serverNonce32.Length != NonceBytes
                || clientNonce32.Length != NonceBytes)
            {
                return false;
            }

            byte[]? sharedSecret = null;
            byte[] okm = new byte[104];
            try
            {
                var remoteParams = new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint
                    {
                        X = remotePub64.Slice(0, 32).ToArray(),
                        Y = remotePub64.Slice(32, 32).ToArray(),
                    },
                };

                using var remoteEcdh = ECDiffieHellman.Create(remoteParams);
                using var remotePublicKey = remoteEcdh.PublicKey;
                sharedSecret = localEcdh.DeriveRawSecretAgreement(remotePublicKey);

                Span<byte> salt = stackalloc byte[NonceBytes * 2];
                serverNonce32.CopyTo(salt.Slice(0, NonceBytes));
                clientNonce32.CopyTo(salt.Slice(NonceBytes, NonceBytes));

                byte[] context = BuildHkdfContext(appId, protocolVersion);
                HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, okm, salt, context);

                ReadOnlySpan<byte> c2sKey = okm.AsSpan(0, 32);
                ReadOnlySpan<byte> s2cKey = okm.AsSpan(32, 32);
                ReadOnlySpan<byte> c2sIv = okm.AsSpan(64, 4);
                ReadOnlySpan<byte> s2cIv = okm.AsSpan(68, 4);
                ReadOnlySpan<byte> confirmKey = okm.AsSpan(72, 32);

                byte[] confirmTranscript = BuildConfirmTranscript(
                    appId, protocolVersion, serverPub64, serverNonce32, clientPub64, clientNonce32);
                confirmMac32 = HMACSHA256.HashData(confirmKey, confirmTranscript);

                sessionCrypto = isServer
                    ? new PeerSessionCrypto(s2cKey, c2sKey, s2cIv, c2sIv, enableAntiReplay)
                    : new PeerSessionCrypto(c2sKey, s2cKey, c2sIv, s2cIv, enableAntiReplay);
                return true;
            }
            catch (CryptographicException)
            {
                sessionCrypto?.Dispose();
                sessionCrypto = null;
                confirmMac32 = null;
                return false;
            }
            catch (ArgumentException)
            {
                sessionCrypto?.Dispose();
                sessionCrypto = null;
                confirmMac32 = null;
                return false;
            }
            finally
            {
                if (sharedSecret is not null)
                    CryptographicOperations.ZeroMemory(sharedSecret);
                CryptographicOperations.ZeroMemory(okm);
            }
        }

        public bool TryEncryptEnvelope(ReadOnlySpan<byte> plaintext, byte deliveryByte, NetDataWriter output)
        {
            if (_disposed || _sendCounter == ulong.MaxValue)
                return false;

            ulong counter = ++_sendCounter;
            int totalLength = EnvelopeOverheadBytes + plaintext.Length;

            output.Reset();
            output.ResizeIfNeed(totalLength);

            Span<byte> dest = output.Data.AsSpan(0, totalLength);
            dest[0] = (byte)NetworkMessageType.EncryptedEnvelope;
            BinaryPrimitives.WriteUInt64BigEndian(dest.Slice(1, 8), counter);

            Span<byte> nonce = stackalloc byte[12];
            _sendNoncePrefix.CopyTo(nonce.Slice(0, NoncePrefixBytes));
            BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(NoncePrefixBytes, 8), counter);

            Span<byte> aad = stackalloc byte[10];
            aad[0] = (byte)NetworkMessageType.EncryptedEnvelope;
            BinaryPrimitives.WriteUInt64BigEndian(aad.Slice(1, 8), counter);
            aad[9] = deliveryByte;

            Span<byte> ciphertext = dest.Slice(9, plaintext.Length);
            Span<byte> tag = dest.Slice(9 + plaintext.Length, TagSizeBytes);
            _sendAes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            output.SetPosition(totalLength);
            return true;
        }

        public DecryptResult TryDecryptEnvelope(
            ReadOnlySpan<byte> packet,
            byte deliveryByte,
            Span<byte> destination,
            out int plaintextLength)
        {
            plaintextLength = 0;
            if (_disposed || packet.Length <= EnvelopeOverheadBytes || packet[0] != (byte)NetworkMessageType.EncryptedEnvelope)
                return DecryptResult.Malformed;

            int bodyLength = packet.Length - EnvelopeOverheadBytes;
            if (destination.Length < bodyLength)
                return DecryptResult.Malformed;

            ulong counter = BinaryPrimitives.ReadUInt64BigEndian(packet.Slice(1, 8));
            if (counter == 0)
                return DecryptResult.Malformed;

            bool isUnreliable = deliveryByte == (byte)NetworkDelivery.Unreliable;
            if (_enableAntiReplay)
            {
                bool replay = isUnreliable
                    ? _unreliableReplayWindow.IsReplay(counter)
                    : _reliableReplayWindow.IsReplay(counter);
                if (replay)
                    return DecryptResult.Replay;
            }

            Span<byte> nonce = stackalloc byte[12];
            _recvNoncePrefix.CopyTo(nonce.Slice(0, NoncePrefixBytes));
            BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(NoncePrefixBytes, 8), counter);

            Span<byte> aad = stackalloc byte[10];
            aad[0] = (byte)NetworkMessageType.EncryptedEnvelope;
            BinaryPrimitives.WriteUInt64BigEndian(aad.Slice(1, 8), counter);
            aad[9] = deliveryByte;

            ReadOnlySpan<byte> ciphertext = packet.Slice(9, bodyLength);
            ReadOnlySpan<byte> tag = packet.Slice(9 + bodyLength, TagSizeBytes);

            try
            {
                _recvAes.Decrypt(nonce, ciphertext, tag, destination.Slice(0, bodyLength), aad);
            }
            catch (CryptographicException)
            {
                return DecryptResult.AuthFailed;
            }

            if (_enableAntiReplay)
            {
                if (isUnreliable)
                    _unreliableReplayWindow.Record(counter);
                else
                    _reliableReplayWindow.Record(counter);
            }

            plaintextLength = bodyLength;
            return DecryptResult.Ok;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _sendAes.Dispose();
            _recvAes.Dispose();
            CryptographicOperations.ZeroMemory(_sendNoncePrefix);
            CryptographicOperations.ZeroMemory(_recvNoncePrefix);
            _reliableReplayWindow.Reset();
            _unreliableReplayWindow.Reset();
        }

        private static void WriteCoordinate32(byte[]? coord, Span<byte> dest32)
        {
            dest32.Clear();
            if (coord is null || coord.Length == 0)
                return;

            if (coord.Length <= 32)
                coord.CopyTo(dest32.Slice(32 - coord.Length));
            else
                coord.AsSpan(coord.Length - 32, 32).CopyTo(dest32);
        }

        private static byte[] BuildServerSignTranscript(
            string appId,
            int protocolVersion,
            ReadOnlySpan<byte> serverPub64,
            ReadOnlySpan<byte> serverNonce32)
        {
            ReadOnlySpan<byte> label = "krut.netcode.ecdh.server.v1"u8;
            byte[] appBytes = Encoding.UTF8.GetBytes(appId);
            byte[] result = new byte[label.Length + 4 + appBytes.Length + 4 + PublicKeyBytes + NonceBytes];

            int pos = 0;
            label.CopyTo(result.AsSpan(pos)); pos += label.Length;
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(pos, 4), appBytes.Length); pos += 4;
            appBytes.CopyTo(result.AsSpan(pos)); pos += appBytes.Length;
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(pos, 4), protocolVersion); pos += 4;
            serverPub64.CopyTo(result.AsSpan(pos)); pos += PublicKeyBytes;
            serverNonce32.CopyTo(result.AsSpan(pos));
            return result;
        }

        private static byte[] BuildConfirmTranscript(
            string appId,
            int protocolVersion,
            ReadOnlySpan<byte> serverPub64,
            ReadOnlySpan<byte> serverNonce32,
            ReadOnlySpan<byte> clientPub64,
            ReadOnlySpan<byte> clientNonce32)
        {
            ReadOnlySpan<byte> label = "krut.netcode.ecdh.confirm.v1"u8;
            byte[] appBytes = Encoding.UTF8.GetBytes(appId);
            byte[] result = new byte[label.Length + 4 + appBytes.Length + 4 + PublicKeyBytes + NonceBytes + PublicKeyBytes + NonceBytes];

            int pos = 0;
            label.CopyTo(result.AsSpan(pos)); pos += label.Length;
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(pos, 4), appBytes.Length); pos += 4;
            appBytes.CopyTo(result.AsSpan(pos)); pos += appBytes.Length;
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(pos, 4), protocolVersion); pos += 4;
            serverPub64.CopyTo(result.AsSpan(pos)); pos += PublicKeyBytes;
            serverNonce32.CopyTo(result.AsSpan(pos)); pos += NonceBytes;
            clientPub64.CopyTo(result.AsSpan(pos)); pos += PublicKeyBytes;
            clientNonce32.CopyTo(result.AsSpan(pos));
            return result;
        }

        private static byte[] BuildHkdfContext(string appId, int protocolVersion)
        {
            ReadOnlySpan<byte> label = "krut.netcode.ecdh.v1"u8;
            byte[] appBytes = Encoding.UTF8.GetBytes(appId);
            byte[] result = new byte[label.Length + 4 + appBytes.Length + 4];
            int pos = 0;
            label.CopyTo(result.AsSpan(pos)); pos += label.Length;
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(pos, 4), appBytes.Length); pos += 4;
            appBytes.CopyTo(result.AsSpan(pos)); pos += appBytes.Length;
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(pos, 4), protocolVersion);
            return result;
        }
    }
}
