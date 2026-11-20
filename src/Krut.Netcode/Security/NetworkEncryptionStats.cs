using System.Threading;

namespace Krut.Netcode
{
    /// <summary>
    /// Thread-safe counters for datagram and per-peer session encryption, authentication, replay protection, and
    /// key exchanges.
    /// </summary>
    public sealed class NetworkEncryptionStats
    {
        private long _packetsEncrypted;
        private long _packetsDecrypted;
        private long _droppedAuthFailures;
        private long _droppedReplays;
        private long _droppedMalformed;
        private long _keyExchangesCompleted;
        private long _keyExchangesFailed;

        public long PacketsEncrypted => Interlocked.Read(ref _packetsEncrypted);
        public long PacketsDecrypted => Interlocked.Read(ref _packetsDecrypted);
        public long DroppedAuthFailures => Interlocked.Read(ref _droppedAuthFailures);
        public long DroppedReplays => Interlocked.Read(ref _droppedReplays);
        public long DroppedMalformed => Interlocked.Read(ref _droppedMalformed);
        public long KeyExchangesCompleted => Interlocked.Read(ref _keyExchangesCompleted);
        public long KeyExchangesFailed => Interlocked.Read(ref _keyExchangesFailed);

        internal void IncrementEncrypted() => Interlocked.Increment(ref _packetsEncrypted);
        internal void IncrementDecrypted() => Interlocked.Increment(ref _packetsDecrypted);
        internal void IncrementAuthFailures() => Interlocked.Increment(ref _droppedAuthFailures);
        internal void IncrementReplays() => Interlocked.Increment(ref _droppedReplays);
        internal void IncrementMalformed() => Interlocked.Increment(ref _droppedMalformed);
        internal void IncrementKeyExchangesCompleted() => Interlocked.Increment(ref _keyExchangesCompleted);
        internal void IncrementKeyExchangesFailed() => Interlocked.Increment(ref _keyExchangesFailed);
    }
}
