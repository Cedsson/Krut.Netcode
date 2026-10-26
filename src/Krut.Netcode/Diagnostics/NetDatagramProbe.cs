using System;
using System.Net;
using System.Threading;
using LiteNetLib.Layers;

namespace Krut.Netcode
{
    /// <summary>
    /// A LiteNetLib packet layer that only measures: the largest and the total datagram sent and received, as the socket
    /// sees them (after every other layer). Length 0 means an earlier layer dropped the datagram and counts for nothing.
    /// Counters are updated with interlocked operations because sends and receives come from different threads.
    /// </summary>
    public sealed class NetDatagramProbe : PacketLayerBase
    {
        private long _bytesOut, _bytesIn, _datagramsOut, _datagramsIn;
        private int _maxOut, _maxIn;

        public NetDatagramProbe() : base(0)
        {
        }

        public long BytesOut => Interlocked.Read(ref _bytesOut);
        public long BytesIn => Interlocked.Read(ref _bytesIn);
        public long DatagramsOut => Interlocked.Read(ref _datagramsOut);
        public long DatagramsIn => Interlocked.Read(ref _datagramsIn);
        public int MaxOut => Volatile.Read(ref _maxOut);
        public int MaxIn => Volatile.Read(ref _maxIn);

        public void Reset()
        {
            Interlocked.Exchange(ref _bytesOut, 0);
            Interlocked.Exchange(ref _bytesIn, 0);
            Interlocked.Exchange(ref _datagramsOut, 0);
            Interlocked.Exchange(ref _datagramsIn, 0);
            Volatile.Write(ref _maxOut, 0);
            Volatile.Write(ref _maxIn, 0);
        }

        public override void ProcessInboundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int length)
        {
            if (length <= 0) return;
            Interlocked.Add(ref _bytesIn, length);
            Interlocked.Increment(ref _datagramsIn);
            RaiseTo(ref _maxIn, length);
        }

        public override void ProcessOutBoundPacket(ref IPEndPoint endPoint, ref byte[] data, ref int offset, ref int length)
        {
            if (length <= 0) return;
            Interlocked.Add(ref _bytesOut, length);
            Interlocked.Increment(ref _datagramsOut);
            RaiseTo(ref _maxOut, length);
        }

        private static void RaiseTo(ref int field, int value)
        {
            int seen = Volatile.Read(ref field);
            while (value > seen)
            {
                int previous = Interlocked.CompareExchange(ref field, value, seen);
                if (previous == seen) return;
                seen = previous;
            }
        }
    }
}
