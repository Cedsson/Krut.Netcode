using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using LiteNetLib.Utils;

namespace Krut.Netcode
{
    /// <summary>
    /// The outbound half of <see cref="NetworkSimulation"/>: asks a per-peer <see cref="NetworkLinkModel"/> what
    /// happens to each sent message and holds the survivors until their due time. Time is
    /// <see cref="Stopwatch.GetTimestamp"/>, counted from the moment the simulation was first seen by the queue.
    /// </summary>
    internal sealed class NetworkDelayQueue
    {
        private const int UnreliableHeaderBytes = 1;
        private const int ChanneledHeaderBytes = 4;
        private const int MaxLinks = 256;
        private const int MaxDelayedPackets = 4096;

        private readonly List<DelayedOutbound> _delayed = new();
        private readonly List<PeerLink> _links = new();
        private readonly NetworkSimulationStats _stats = new();

        private NetworkSimulation? _bound;
        private long _originTimestamp;
        private int _nextLinkIndex;

        /// <summary>Messages dropped by the simulation, including reliable ones that were only delayed.</summary>
        public int DroppedCount => (int)_stats.Dropped;

        public int Count => _delayed.Count;

        /// <summary>Counters of this queue. The same instance every call, so polling it allocates nothing.</summary>
        public NetworkSimulationStats GetStats() => _stats;

        /// <summary>
        /// Queues a sent message according to the link's verdict, or discards it. The decision is taken before
        /// the bytes are copied: a message that never got away should not cost a copy.
        /// </summary>
        public void Enqueue(NetworkSimulation simulation, NetworkPeerState peer, NetDataWriter writer, NetworkDelivery delivery, byte channel)
        {
            Bind(simulation);
            bool reliable = delivery != NetworkDelivery.Unreliable;
            int wireBytes = writer.Length + (reliable ? ChanneledHeaderBytes : UnreliableHeaderBytes);
            var verdict = LinkFor(peer).Submit(NowUs(), wireBytes, reliable);
            if (!verdict.Deliver)
                return;

            if (_delayed.Count >= MaxDelayedPackets && (!reliable || !TryEvictOneUnreliable()))
            {
                _stats.IncrementDropped();
                _stats.IncrementDroppedOverflow();
                return;
            }

            // Pooled array copy instead of a reference to writer.Data: the writer is reused by the next Send.
            int length = writer.Length;
            byte[] data = ArrayPool<byte>.Shared.Rent(length);
            Buffer.BlockCopy(writer.Data, 0, data, 0, length);
            var method = NetworkChannels.ToDeliveryMethod(delivery);
            InsertSorted(new DelayedOutbound(peer, data, length, channel, method, verdict.DueUs, reliable));
            if (verdict.Duplicate)
            {
                if (_delayed.Count >= MaxDelayedPackets && (!reliable || !TryEvictOneUnreliable()))
                {
                    _stats.IncrementDropped();
                    _stats.IncrementDroppedOverflow();
                    return;
                }

                byte[] duplicateData = ArrayPool<byte>.Shared.Rent(length);
                Buffer.BlockCopy(writer.Data, 0, duplicateData, 0, length);
                InsertSorted(new DelayedOutbound(peer, duplicateData, length, channel, method, verdict.DuplicateDueUs, reliable));
            }
        }

        private bool TryEvictOneUnreliable()
        {
            for (int i = 0; i < _delayed.Count; i++)
            {
                if (!_delayed[i].Reliable)
                {
                    ArrayPool<byte>.Shared.Return(_delayed[i].Data);
                    _delayed.RemoveAt(i);
                    _stats.IncrementDropped();
                    _stats.IncrementDroppedOverflow();
                    return true;
                }
            }

            return false;
        }

        private void InsertSorted(in DelayedOutbound item)
        {
            int index = _delayed.Count;
            while (index > 0 && _delayed[index - 1].DueUs > item.DueUs)
                index--;

            if (index == _delayed.Count)
                _delayed.Add(item);
            else
                _delayed.Insert(index, item);
        }

        /// <summary>Removes link state and any queued delayed packets for a disconnected peer.</summary>
        public void RemovePeer(int peerId)
        {
            for (int i = _delayed.Count - 1; i >= 0; i--)
            {
                if (_delayed[i].Peer.Id == peerId)
                {
                    ArrayPool<byte>.Shared.Return(_delayed[i].Data);
                    _delayed.RemoveAt(i);
                }
            }

            for (int i = _links.Count - 1; i >= 0; i--)
            {
                if (_links[i].Peer.Id == peerId)
                    _links.RemoveAt(i);
            }
        }

        /// <summary>Sends every queued packet now; used when the simulation is switched off.</summary>
        public void SendAll(Action<NetworkError>? onError = null)
        {
            try
            {
                foreach (var delayed in _delayed)
                {
                    try
                    {
                        delayed.Peer.Peer.Send(delayed.Data, 0, delayed.Length, delayed.Channel, delayed.Method);
                    }
                    catch (LiteNetLib.TooBigPacketException)
                    {
                        onError?.Invoke(new NetworkError(
                            NetworkErrorCode.PacketTooLarge,
                            $"Delayed packet ({delayed.Length} bytes) did not fit in a packet - split it up or use a reliable channel.",
                            delayed.Peer.Id));
                    }
                    catch (Exception)
                    {
                        // Never allow a per-packet failure to abort releasing pooled buffers.
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(delayed.Data);
                    }
                }
            }
            finally
            {
                _delayed.Clear();
            }
        }

        public void Clear()
        {
            foreach (var delayed in _delayed)
                ArrayPool<byte>.Shared.Return(delayed.Data);

            _delayed.Clear();
            _links.Clear();
            _nextLinkIndex = 0;
            _bound = null;
        }

        /// <summary>
        /// Sends the packets whose due time has passed, in chronological <c>DueUs</c> order, and removes them. A packet
        /// that falls due inside an outage is lost (unreliable) or held until the outage ends (reliable).
        /// </summary>
        public bool FlushDue(Action<NetworkError>? onError = null)
        {
            if (_delayed.Count == 0 || _bound is null)
                return false;

            long now = NowUs();
            long outageEnd = 0;
            bool inOutage = !_bound.DatagramLevel && _bound.TryGetOutage(now, out outageEnd);
            bool sent = false;
            bool outageRescheduled = false;
            int kept = 0;
            int total = _delayed.Count;
            for (int i = 0; i < total; i++)
            {
                var delayed = _delayed[i];
                if (delayed.DueUs > now)
                {
                    _delayed[kept++] = delayed;
                    continue;
                }

                if (inOutage)
                {
                    if (delayed.Reliable)
                    {
                        delayed.DueUs = outageEnd;
                        _delayed[kept++] = delayed;
                        outageRescheduled = true;
                    }
                    else
                    {
                        ArrayPool<byte>.Shared.Return(delayed.Data);
                        _stats.IncrementDropped();
                        _stats.IncrementDroppedOutage();
                    }

                    continue;
                }

                try
                {
                    delayed.Peer.Peer.Send(delayed.Data, 0, delayed.Length, delayed.Channel, delayed.Method);
                    sent = true;
                }
                catch (LiteNetLib.TooBigPacketException)
                {
                    onError?.Invoke(new NetworkError(
                        NetworkErrorCode.PacketTooLarge,
                        $"Delayed packet ({delayed.Length} bytes) did not fit in a packet - split it up or use a reliable channel.",
                        delayed.Peer.Id));
                }
                catch (Exception)
                {
                    // Never allow a single failed packet send to leak remaining pooled buffers or crash Update.
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(delayed.Data);
                }
            }

            if (kept < _delayed.Count)
                _delayed.RemoveRange(kept, _delayed.Count - kept);
            if (outageRescheduled && _delayed.Count > 1)
                _delayed.Sort(static (a, b) => a.DueUs.CompareTo(b.DueUs));
            return sent;
        }

        private void Bind(NetworkSimulation simulation)
        {
            if (ReferenceEquals(_bound, simulation))
                return;

            _bound = simulation;
            _originTimestamp = Stopwatch.GetTimestamp();
            _links.Clear();
            _nextLinkIndex = 0;
        }

        private NetworkLinkModel LinkFor(NetworkPeerState peer)
        {
            for (int i = 0; i < _links.Count; i++)
            {
                if (ReferenceEquals(_links[i].Peer, peer))
                    return _links[i].Model;
            }

            if (_links.Count >= MaxLinks)
                _links.RemoveAt(0);

            var model = new NetworkLinkModel(_bound!, _nextLinkIndex++, _stats);
            _links.Add(new PeerLink(peer, model));
            return model;
        }

        private long NowUs()
        {
            long ticks = Stopwatch.GetTimestamp() - _originTimestamp;
            return (long)(ticks * (1_000_000.0 / Stopwatch.Frequency));
        }

        private readonly struct PeerLink
        {
            public PeerLink(NetworkPeerState peer, NetworkLinkModel model)
            {
                Peer = peer;
                Model = model;
            }

            public NetworkPeerState Peer { get; }

            public NetworkLinkModel Model { get; }
        }

        /// <summary>
        /// A packet waiting out its simulated delay. It carries its own copy of the bytes: the writer is reused
        /// right after <c>Send</c> returns.
        /// </summary>
        private struct DelayedOutbound
        {
            public DelayedOutbound(NetworkPeerState peer, byte[] data, int length, byte channel, LiteNetLib.DeliveryMethod method, long dueUs, bool reliable)
            {
                Peer = peer;
                Data = data;
                Length = length;
                Channel = channel;
                Method = method;
                DueUs = dueUs;
                Reliable = reliable;
            }

            public NetworkPeerState Peer { get; }

            public byte[] Data { get; }

            public int Length { get; }

            public byte Channel { get; }

            public LiteNetLib.DeliveryMethod Method { get; }

            /// <summary>Due time in microseconds since the simulation was bound.</summary>
            public long DueUs { get; set; }

            public bool Reliable { get; }
        }
    }
}
