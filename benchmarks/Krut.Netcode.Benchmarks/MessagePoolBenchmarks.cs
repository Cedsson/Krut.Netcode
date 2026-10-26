using BenchmarkDotNet.Attributes;
using LiteNetLib.Utils;

namespace Krut.Netcode.Benchmarks
{
    public sealed class BenchMessage : INetworkMessage
    {
        public const byte Wire = 200;

        public NetworkMessageType Type => NetworkMessageType.None;
        public byte WireType => Wire;
        public NetworkDelivery Delivery => NetworkDelivery.Unreliable;

        public int Tick;
        public float Value;

        public void Write(NetDataWriter writer)
        {
            writer.Put(Tick);
            writer.Put(Value);
        }

        public void Read(NetDataReader reader)
        {
            Tick = reader.GetInt();
            Value = reader.GetFloat();
        }
    }

    [MemoryDiagnoser]
    public class MessagePoolBenchmarks
    {
        private readonly NetDataWriter _writer = new NetDataWriter();
        private readonly NetDataReader _reader = new NetDataReader();

        [GlobalSetup]
        public void Setup()
        {
            if (!NetworkMessages.IsRegistered(BenchMessage.Wire))
                NetworkMessages.RegisterGameMessage<BenchMessage>(BenchMessage.Wire, NetworkDelivery.Unreliable);
        }

        [Benchmark(Baseline = true)]
        public BenchMessage Allocate() => new BenchMessage { Tick = 1, Value = 2f };

        [Benchmark]
        public int RentWriteRecycle()
        {
            BenchMessage message = NetworkSession.Rent<BenchMessage>();
            message.Tick = 1;
            message.Value = 2f;
            _writer.Reset();
            message.Write(_writer);
            NetworkMessages.Recycle(message, BenchMessage.Wire);
            return _writer.Length;
        }

        [Benchmark]
        public int RentWriteReadRecycle()
        {
            BenchMessage send = NetworkSession.Rent<BenchMessage>();
            send.Tick = 1;
            send.Value = 2f;
            _writer.Reset();
            send.Write(_writer);
            NetworkMessages.Recycle(send, BenchMessage.Wire);

            _reader.SetSource(_writer.Data, 0, _writer.Length);
            BenchMessage receive = NetworkSession.Rent<BenchMessage>();
            receive.Read(_reader);
            int tick = receive.Tick;
            NetworkMessages.Recycle(receive, BenchMessage.Wire);
            return tick;
        }
    }
}