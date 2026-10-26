using System;
using System.Net;
using LiteNetLib.Layers;

namespace Krut.Netcode
{
    /// <summary>
    /// The single <see cref="PacketLayerBase"/> a session hands to LiteNetLib, composed of an ordered list of layers.
    /// Order is game to wire: outbound datagrams pass the array front to back, inbound datagrams back to front, so
    /// the last layer is the one nearest the socket. The intended chain is [secure layer, simulation layer, probe]:
    /// the simulation drops sealed datagrams as a real network would, and the probe (last) measures exactly what
    /// the socket sends. A layer that drops an outbound datagram sets its length to 0; later layers must ignore
    /// zero-length datagrams, and this chain stops walking.
    /// LiteNetLib copies every outbound packet when a layer is present, so a chain is installed only when a probe,
    /// the simulation or the secure layer is wanted, never by default.
    /// </summary>
    public sealed class NetworkDatagramChain : PacketLayerBase
    {
        /// <summary>
        /// Called by <see cref="NetworkSession"/> for every new session whose <see cref="NetworkConfig.DatagramLayer"/>
        /// is null, so a harness can install layers where the game builds its own config. The session keeps the
        /// result in <see cref="NetworkSession.DatagramChain"/>; the config is not changed. Process-global and meant
        /// for the harness and tests only: play leaves it null. Must return a fresh chain per call, a chain belongs
        /// to one session.
        /// </summary>
        public static Func<NetworkDatagramChain?>? DefaultFactory { get; set; }

        private readonly PacketLayerBase[] _layers;
        private readonly NetworkDatagramSimLayer? _simLayer;

        public NetworkDatagramChain(params PacketLayerBase[] layers)
            : base(SumExtra(layers))
        {
            _layers = (PacketLayerBase[])layers.Clone();
            foreach (var layer in _layers)
            {
                if (layer is NetworkDatagramSimLayer sim)
                    _simLayer = sim;
            }
        }

        /// <summary>True when the chain holds a <see cref="NetworkDatagramSimLayer"/>.</summary>
        public bool HasSimulationLayer => _simLayer is not null;

        /// <summary>The simulation the datagram layer applies, or null. Setting it needs a simulation layer in the chain.</summary>
        public NetworkSimulation? Simulation
        {
            get => _simLayer?.Simulation;
            set
            {
                if (_simLayer is null)
                {
                    if (value is { DatagramLevel: true })
                        throw new InvalidOperationException("The simulation is datagram-level but the chain has no NetworkDatagramSimLayer.");
                    return;
                }

                _simLayer.Simulation = value;
            }
        }

        /// <summary>Counters of the datagram stage, or null without a simulation layer.</summary>
        public NetworkSimulationStats? DatagramStats => _simLayer?.Stats;

        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            for (int i = _layers.Length - 1; i >= 0 && length > 0; i--)
                _layers[i].ProcessInboundPacket(ref endPoint, ref data, ref length);
        }

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            for (int i = 0; i < _layers.Length && length > 0; i++)
                _layers[i].ProcessOutBoundPacket(ref endPoint, ref data, ref offset, ref length);
        }

        private static int SumExtra(PacketLayerBase[] layers)
        {
            int sum = 0;
            foreach (var layer in layers ?? throw new ArgumentNullException(nameof(layers)))
                sum += layer.ExtraPacketSizeForLayer;
            return sum;
        }
    }
}
