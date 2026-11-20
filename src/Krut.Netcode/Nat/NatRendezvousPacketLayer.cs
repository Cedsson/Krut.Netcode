using System;
using System.Net;
using LiteNetLib.Layers;

namespace Krut.Netcode
{
    /// <summary>
    /// Selective <see cref="PacketLayerBase"/> used when a session uses plaintext/ECDH match traffic
    /// alongside an encrypted <see cref="NetworkNatRendezvousServer"/>: applies <see cref="NetworkEncryptionLayer"/>
    /// exclusively to datagrams targeting or arriving from the configured rendezvous server.
    /// </summary>
    internal sealed class NatRendezvousPacketLayer : PacketLayerBase, IDisposable
    {
        private readonly NetworkEncryptionLayer _rendezvousLayer;
        private volatile IPEndPoint? _rendezvousEndPoint;

        public NatRendezvousPacketLayer(NetworkEncryptionLayer rendezvousLayer)
            : base(NetworkEncryptionLayer.OverheadBytes)
        {
            _rendezvousLayer = rendezvousLayer ?? throw new ArgumentNullException(nameof(rendezvousLayer));
        }

        public void SetRendezvousEndPoint(IPEndPoint? endPoint)
        {
            _rendezvousEndPoint = endPoint is null ? null : NatPunchProtocol.NormalizeEndPoint(endPoint);
        }

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            if (data is null || length <= 0)
                return;

            var target = _rendezvousEndPoint;
            if (target is not null && NatPunchProtocol.EndPointsEqual(endPoint, target))
                _rendezvousLayer.ProcessOutBoundPacket(ref endPoint, ref data, ref offset, ref length);
        }

        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            if (data is null || length <= 0)
                return;

            var target = _rendezvousEndPoint;
            if (target is not null && NatPunchProtocol.EndPointsEqual(endPoint, target))
                _rendezvousLayer.ProcessInboundPacket(ref endPoint, ref data, ref length);
        }

        public void ResetReplayState() => _rendezvousLayer.ResetReplayState();

        public void Dispose()
        {
            _rendezvousEndPoint = null;
            _rendezvousLayer.Dispose();
        }
    }
}
