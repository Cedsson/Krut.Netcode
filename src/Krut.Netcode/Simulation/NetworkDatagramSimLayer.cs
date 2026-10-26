using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using LiteNetLib.Layers;

namespace Krut.Netcode
{
    /// <summary>
    /// Applies loss, burst loss and outages to every datagram LiteNetLib sends (acks, pings, resends and connect
    /// packets included), using the same seeded <see cref="NetworkLinkModel"/> as the message-level stage. A layer
    /// is synchronous and cannot hold a datagram, so delay, jitter, reorder, duplicates and the bandwidth cap stay
    /// in the message queue; this stage only drops. It acts on OUTBOUND datagrams, so as everywhere a side degrades
    /// what it sends. Active only for a simulation with <see cref="NetworkSimulation.DatagramLevel"/> set, which also
    /// switches the message stage's loss and outages off, so an effect is never applied twice.
    /// The clock starts at the first datagram after the simulation is set.
    /// </summary>
    public sealed class NetworkDatagramSimLayer : PacketLayerBase
    {
        private readonly object _lock = new();
        private readonly List<PeerLink> _links = new();
        private readonly NetworkSimulationStats _stats = new();

        private NetworkSimulation? _simulation;
        private NetworkSimulation? _dropOnly;
        private long _originTimestamp;
        private bool _clockStarted;

        public NetworkDatagramSimLayer()
            : base(0)
        {
        }

        /// <summary>The simulation in force, or null. Only a <see cref="NetworkSimulation.DatagramLevel"/> simulation drops anything.</summary>
        public NetworkSimulation? Simulation
        {
            get
            {
                lock (_lock)
                    return _simulation;
            }
            set
            {
                lock (_lock)
                {
                    _simulation = value;
                    _dropOnly = value is { DatagramLevel: true } ? value.ForDatagramStage() : null;
                    _links.Clear();
                    _clockStarted = false;
                }
            }
        }

        /// <summary>Datagrams seen, dropped and why. Dropped counts are datagrams, not messages.</summary>
        public NetworkSimulationStats Stats => _stats;

        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
        }

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            lock (_lock)
            {
                if (_dropOnly is null || length <= 0)
                    return;

                if (!_clockStarted)
                {
                    _clockStarted = true;
                    _originTimestamp = Stopwatch.GetTimestamp();
                }

                long ticks = Stopwatch.GetTimestamp() - _originTimestamp;
                long nowUs = (long)(ticks * (1_000_000.0 / Stopwatch.Frequency));
                // Unreliable lane: a datagram is never "resent late" here, LiteNetLib does its own resends.
                var verdict = LinkFor(endPoint).Submit(nowUs, length, reliable: false);
                if (!verdict.Deliver)
                    length = 0;
            }
        }

        private NetworkLinkModel LinkFor(IPEndPoint endPoint)
        {
            for (int i = 0; i < _links.Count; i++)
            {
                if (_links[i].Endpoint.Equals(endPoint))
                    return _links[i].Model;
            }

            var model = new NetworkLinkModel(_dropOnly!, _links.Count, _stats);
            _links.Add(new PeerLink(new IPEndPoint(endPoint.Address, endPoint.Port), model));
            return model;
        }

        private readonly struct PeerLink
        {
            public PeerLink(IPEndPoint endpoint, NetworkLinkModel model)
            {
                Endpoint = endpoint;
                Model = model;
            }

            public IPEndPoint Endpoint { get; }

            public NetworkLinkModel Model { get; }
        }
    }
}
