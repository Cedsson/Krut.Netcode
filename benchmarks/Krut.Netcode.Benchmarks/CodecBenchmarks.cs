using System.Numerics;
using BenchmarkDotNet.Attributes;
using LiteNetLib.Utils;

namespace Krut.Netcode.Benchmarks
{
    [MemoryDiagnoser]
    public class CodecBenchmarks
    {
        private readonly NetDataWriter _writer = new NetDataWriter();
        private readonly NetDataReader _reader = new NetDataReader();
        private readonly Vector3 _position = new Vector3(123.456f, 7.89f, -321.0f);
        private readonly Quaternion _rotation = Quaternion.Normalize(new Quaternion(0.1f, 0.7f, -0.2f, 0.6f));
        private byte[] _wire = System.Array.Empty<byte>();

        [GlobalSetup]
        public void Setup()
        {
            _writer.Reset();
            NetworkCodec.WritePosition(_writer, _position);
            NetworkCodec.WriteRotation(_writer, _rotation);
            _wire = _writer.CopyData();
        }

        [Benchmark]
        public int WritePositionAndRotation()
        {
            _writer.Reset();
            NetworkCodec.WritePosition(_writer, _position);
            NetworkCodec.WriteRotation(_writer, _rotation);
            return _writer.Length;
        }

        [Benchmark]
        public float ReadPositionAndRotation()
        {
            _reader.SetSource(_wire);
            Vector3 p = NetworkCodec.ReadPosition(_reader);
            Quaternion q = NetworkCodec.ReadRotation(_reader);
            return p.X + q.W;
        }
    }
}