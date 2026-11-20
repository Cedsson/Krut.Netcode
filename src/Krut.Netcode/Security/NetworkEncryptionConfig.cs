using System;
using System.Security.Cryptography;

namespace Krut.Netcode
{
    /// <summary>Key-generation helpers for <see cref="NetworkEncryptionConfig"/>.</summary>
    public static class NetworkCrypto
    {
        /// <summary>Required length in bytes for AES-256-GCM pre-shared keys (256 bits).</summary>
        public const int KeySizeBytes = 32;

        /// <summary>Generates a cryptographically random 256-bit pre-shared key.</summary>
        public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySizeBytes);

        /// <summary>
        /// Generates a NIST P-256 <see cref="ECDsa"/> identity key pair for authenticating server key exchanges:
        /// <c>PrivateKey</c> in PKCS#8 format for the server and <c>PublicKey</c> in SubjectPublicKeyInfo (SPKI)
        /// format to pin on clients.
        /// </summary>
        public static (byte[] PrivateKey, byte[] PublicKey) GenerateIdentityKeyPair()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return (ecdsa.ExportPkcs8PrivateKey(), ecdsa.ExportSubjectPublicKeyInfo());
        }
    }

    /// <summary>
    /// Configures transport and session encryption for a <see cref="NetworkSession"/>. Supports:
    /// <list type="bullet">
    /// <item><b>Datagram-level AES-256-GCM with a pre-shared key / match token</b>
    /// (<see cref="PreSharedKey"/>): encrypts and authenticates every UDP datagram including the handshake, pings,
    /// and acks with zero extra round trips and zero steady-state heap allocations.</item>
    /// <item><b>Per-peer ephemeral ECDH (P-256) + AES-256-GCM</b> (<see cref="EnableSessionKeyExchange"/>):
    /// establishes unique forward-secret session keys per client (optionally signed/pinned with an <see cref="ECDsa"/>
    /// server identity key to prevent active MITM).</item>
    /// <item><b>Combined</b>: PSK datagram protection around handshake + per-peer forward-secret session encryption.</item>
    /// </list>
    /// </summary>
    public sealed class NetworkEncryptionConfig
    {
        /// <summary>
        /// Optional 32-byte AES-256-GCM pre-shared key or match token. When set, every UDP datagram (including the
        /// connection handshake) is encrypted and authenticated via <see cref="NetworkEncryptionLayer"/>.
        /// </summary>
        public byte[]? PreSharedKey { get; set; }

        /// <summary>
        /// Optional previous 32-byte pre-shared key accepted on inbound packets during zero-downtime key rotation.
        /// Outbound packets always encrypt with <see cref="PreSharedKey"/>.
        /// </summary>
        public byte[]? PreviousPreSharedKey { get; set; }

        /// <summary>4-bit key identifier (0..15) embedded in the datagram header for key-rotation diagnostics.</summary>
        public byte KeyId { get; set; } = 1;

        /// <summary>
        /// Enables per-peer ephemeral ECDH P-256 key exchange after UDP connection and encrypts every session message
        /// in an AES-256-GCM <see cref="NetworkMessageType.EncryptedEnvelope"/>.
        /// </summary>
        public bool EnableSessionKeyExchange { get; set; }

        /// <summary>
        /// Optional PKCS#8 NIST P-256 <see cref="ECDsa"/> private key used by the server to sign its ephemeral
        /// key-exchange parameters.
        /// </summary>
        public byte[]? ServerIdentityPrivateKey { get; set; }

        /// <summary>
        /// Optional SubjectPublicKeyInfo (SPKI) NIST P-256 <see cref="ECDsa"/> public key pinned on the client to
        /// verify the server's ephemeral key-exchange signature and prevent active MITM attacks.
        /// </summary>
        public byte[]? ServerIdentityPublicKey { get; set; }

        /// <summary>Maximum time in milliseconds allowed for per-peer ECDH handshake completion. Minimum 500 ms.</summary>
        public int HandshakeTimeoutMs { get; set; } = 5000;

        /// <summary>Enables 64-packet sliding anti-replay protection per peer/direction. Default true.</summary>
        public bool EnableAntiReplay { get; set; } = true;

        /// <summary>True when either pre-shared key datagram encryption or per-peer ECDH session encryption is enabled.</summary>
        public bool IsEnabled => PreSharedKey is not null || EnableSessionKeyExchange;

        /// <summary>Creates a configuration for 0-RTT AES-256-GCM datagram encryption using a 32-byte pre-shared key.</summary>
        public static NetworkEncryptionConfig CreatePreSharedKey(
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> previousKey = default,
            byte keyId = 1) => new()
        {
            PreSharedKey = key.ToArray(),
            PreviousPreSharedKey = previousKey.IsEmpty ? null : previousKey.ToArray(),
            KeyId = (byte)(keyId & 0x0F),
        };

        /// <summary>Creates a configuration for per-peer ephemeral ECDH P-256 + AES-256-GCM session encryption.</summary>
        public static NetworkEncryptionConfig CreateEphemeral(
            ReadOnlySpan<byte> serverIdentityPrivateKey = default,
            ReadOnlySpan<byte> serverIdentityPublicKey = default) => new()
        {
            EnableSessionKeyExchange = true,
            ServerIdentityPrivateKey = serverIdentityPrivateKey.IsEmpty ? null : serverIdentityPrivateKey.ToArray(),
            ServerIdentityPublicKey = serverIdentityPublicKey.IsEmpty ? null : serverIdentityPublicKey.ToArray(),
        };

        /// <summary>Creates a combined configuration: PSK datagram protection plus per-peer ephemeral ECDH session encryption.</summary>
        public static NetworkEncryptionConfig CreateCombined(
            ReadOnlySpan<byte> preSharedKey,
            ReadOnlySpan<byte> serverIdentityPrivateKey = default,
            ReadOnlySpan<byte> serverIdentityPublicKey = default,
            byte keyId = 1) => new()
        {
            PreSharedKey = preSharedKey.ToArray(),
            KeyId = (byte)(keyId & 0x0F),
            EnableSessionKeyExchange = true,
            ServerIdentityPrivateKey = serverIdentityPrivateKey.IsEmpty ? null : serverIdentityPrivateKey.ToArray(),
            ServerIdentityPublicKey = serverIdentityPublicKey.IsEmpty ? null : serverIdentityPublicKey.ToArray(),
        };

        /// <summary>Validates key lengths, platform AES-GCM support, and clamping rules.</summary>
        public void Validate(bool isServer)
        {
            if (HandshakeTimeoutMs < 500)
                HandshakeTimeoutMs = 500;
            KeyId = (byte)(KeyId & 0x0F);

            if (IsEnabled && !AesGcm.IsSupported)
                throw new PlatformNotSupportedException("AES-GCM is not supported on this platform.");

            if (PreSharedKey is not null && PreSharedKey.Length != NetworkCrypto.KeySizeBytes)
            {
                throw new ArgumentException(
                    $"PreSharedKey must be exactly {NetworkCrypto.KeySizeBytes} bytes (got {PreSharedKey.Length}).",
                    nameof(PreSharedKey));
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

            if (ServerIdentityPrivateKey is not null)
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportPkcs8PrivateKey(ServerIdentityPrivateKey, out int bytesRead);
                if (bytesRead != ServerIdentityPrivateKey.Length || ecdsa.KeySize != 256)
                {
                    throw new ArgumentException(
                        "ServerIdentityPrivateKey must be a valid PKCS#8 NIST P-256 private key.",
                        nameof(ServerIdentityPrivateKey));
                }
            }

            if (ServerIdentityPublicKey is not null)
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(ServerIdentityPublicKey, out int bytesRead);
                if (bytesRead != ServerIdentityPublicKey.Length || ecdsa.KeySize != 256)
                {
                    throw new ArgumentException(
                        "ServerIdentityPublicKey must be a valid SPKI NIST P-256 public key.",
                        nameof(ServerIdentityPublicKey));
                }
            }

            _ = isServer;
        }

        /// <summary>Deep-clones this encryption configuration so caller mutations do not affect running sessions.</summary>
        public NetworkEncryptionConfig Clone()
        {
            var copy = (NetworkEncryptionConfig)MemberwiseClone();
            if (PreSharedKey is not null)
                copy.PreSharedKey = (byte[])PreSharedKey.Clone();
            if (PreviousPreSharedKey is not null)
                copy.PreviousPreSharedKey = (byte[])PreviousPreSharedKey.Clone();
            if (ServerIdentityPrivateKey is not null)
                copy.ServerIdentityPrivateKey = (byte[])ServerIdentityPrivateKey.Clone();
            if (ServerIdentityPublicKey is not null)
                copy.ServerIdentityPublicKey = (byte[])ServerIdentityPublicKey.Clone();
            return copy;
        }
    }
}
