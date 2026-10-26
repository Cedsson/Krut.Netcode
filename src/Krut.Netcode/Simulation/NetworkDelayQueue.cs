using System;
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

        private readonly List<DelayedOutbound> _delayed = new();
        private readonly List<PeerLink> _links = new();
        private readonly NetworkSimulationStats _stats = new();

        private NetworkSimulation? _bound;
        private long _originTimestamp;

        /// <summary>Messages dropped by the simulation, including reliable ones that were only delayed.</summary>
        public int DroppedCount => (int)_stats.Dropped;

        public int Count => _delayed.Count;

        /// <summary>Counters of this queue. The same instance every call, so polling it allocates nothing.</summary>
        public NetworkSimulationStats GetStats() => _stats;

        /// <summary>
        /// Queues a sent message according to the link's verdict, or discards it. The decision is taken before
        /// the bytes are copied: a message that never got away should not cost a copy.
        /// </summary>
        public void Enqueue(NetworkSimulation simulation, NetworkPeerState peer, NetDataWriter writer, NetworkDelivery delivery)
        {
            Bind(simulation);
            bool reliable = delivery != NetworkDelivery.Unreliable;
            int wireBytes = writer.Length + (reliable ? ChanneledHeaderBytes : UnreliableHeaderBytes);
            var verdict = LinkFor(peer).Submit(NowUs(), wireBytes, reliable);
            if (!verdict.Deliver)
                return;

            // Own array instead of a reference to writer.Data: the writer is reused by the next Send.
            var data = new byte[writer.Length];
            Array.Copy(writer.Data, data, writer.Length);
            byte channel = NetworkChannels.For(delivery);
            var method = NetworkChannels.ToDeliveryMethod(delivery);
            _delayed.Add(new DelayedOutbound(peer, data, channel, method, verdict.DueUs, reliable));
            if (verdict.Duplicate)
                _delayed.Add(new DelayedOutbound(peer, data, channel, method, verdict.DuplicateDueUs, reliable));
        }

        /// <summary>Sends every queued packet now; used when the simulation is switched off.</summary>
        public void SendAll()
        {
            foreach (var delayed in _delayed)
                delayed.Peer.Peer.Send(delayed.Data, delayed.Channel, delayed.Method);

            _delayed.Clear();
        }

        public void Clear()
        {
            _delayed.Clear();
            _links.Clear();
            _bound = null;
        }

        /// <summary>
        /// Sends the packets whose due time has passed, in the order they were queued, and removes them. A packet
        /// that falls due inside an outage is lost (unreliable) or held until the outage ends (reliable).
        /// </summary>
        public bool FlushDue()
        {
            if (_delayed.Count == 0 || _bound is null)
                return false;

            long now = NowUs();
            long outageEnd = 0;
            bool inOutage = !_bound.DatagramLevel && _bound.TryGetOutage(now, out outageEnd);
            bool sent = false;
            int kept = 0;
            for (int i = 0; i < _delayed.Count; i++)
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
                    }
                    else
                    {
                        _stats.Dropped++;
                        _stats.DroppedOutage++;
                    }

                    continue;
                }

                delayed.Peer.Peer.Send(delayed.Data, delayed.Channel, delayed.Method);
                sent = true;
            }

            _delayed.RemoveRange(kept, _delayed.Count - kept);
            return sent;
        }

        private void Bind(NetworkSimulation simulation)
        {
            if (ReferenceEquals(_bound, simulation))
                return;

            _bound = simulation;
            _originTimestamp = Stopwatch.GetTimestamp();
            _links.Clear();
        }

        private NetworkLinkModel LinkFor(NetworkPeerState peer)
        {
            for (int i = 0; i < _links.Count; i++)
            {
                if (ReferenceEquals(_links[i].Peer, peer))
                    return _links[i].Model;
            }

            var model = new NetworkLinkModel(_bound!, _links.Count, _stats);
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
            public DelayedOutbound(NetworkPeerState peer, byte[] data, byte channel, LiteNetLib.DeliveryMethod method, long dueUs, bool reliable)
            {
                Peer = peer;
                Data = data;
                Channel = channel;
                Method = method;
                DueUs = dueUs;
                Reliable = reliable;
            }

            public NetworkPeerState Peer { get; }

            public byte[] Data { get; }

            public byte Channel { get; }

            public LiteNetLib.DeliveryMethod Method { get; }

            /// <summary>Due time in microseconds since the simulation was bound.</summary>
            public long DueUs { get; set; }

            public bool Reliable { get; }
        }
    }
}
