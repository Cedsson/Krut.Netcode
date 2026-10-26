using BenchmarkDotNet.Attributes;

namespace Krut.Netcode.Benchmarks
{
    public struct BenchState
    {
        public float X, Y, Vx, Vy;
    }

    public struct BenchInput
    {
        public float Ax, Ay;
    }

    public static class BenchSim
    {
        private const float Dt = 1f / 60f;

        public static BenchState Step(BenchState s, int tick, BenchInput input)
        {
            float vx = (s.Vx + input.Ax * Dt) * (1f - 0.6f * Dt);
            float vy = (s.Vy + input.Ay * Dt) * (1f - 0.6f * Dt);
            return new BenchState { X = s.X + vx * Dt, Y = s.Y + vy * Dt, Vx = vx, Vy = vy };
        }

        public static BenchState Replay(BenchState authoritative, int lastProcessedTick, BenchInput[] inputs, int count)
        {
            BenchState state = authoritative;
            for (int i = 0; i < count; i++)
                state = Step(state, lastProcessedTick + 1 + i, inputs[i]);
            return state;
        }
    }

    [MemoryDiagnoser]
    public class ReconcilerBenchmarks
    {
        private NetworkReconciler<BenchState, BenchInput> _reconciler = null!;
        private int _tick;
        private System.Func<BenchState, int, BenchInput[], BenchState> _replay = null!;

        /// <summary>Unacknowledged inputs replayed on every reconcile (about 100 ms at 60 Hz = 6, 300 ms = 18).</summary>
        [Params(6, 18, 60)]
        public int Replays;

        [GlobalSetup]
        public void Setup()
        {
            _reconciler = new NetworkReconciler<BenchState, BenchInput>(VisualSmoothingSettings.Default);
            _reconciler.Step = BenchSim.Step;
            _replay = Replay;
            _reconciler.Reset(0, default);
            _tick = 0;
        }

        [Benchmark]
        public int PredictAndReconcile()
        {
            BenchInput input = new BenchInput { Ax = 3f, Ay = -1f };
            int first = _tick + 1;
            for (int i = 0; i < Replays; i++)
                _reconciler.Predict(++_tick, input);

            BenchState server = new BenchState { X = 0.01f };
            _reconciler.SetAuthoritativeState(first - 1, server);
            int replayed = _reconciler.Reconcile(first - 1, first - 1, _replay);
            _reconciler.Reset(_tick, server);
            return replayed;
        }

        private BenchState Replay(BenchState authoritative, int lastProcessedTick, BenchInput[] inputs) =>
            BenchSim.Replay(authoritative, lastProcessedTick, inputs, _reconciler.PendingReplayCount);
    }

    [MemoryDiagnoser]
    public class InterpolatorBenchmarks
    {
        private NetworkSnapshotInterpolator<BenchState> _interpolator = null!;
        private int _tick;

        [GlobalSetup]
        public void Setup()
        {
            _interpolator = new NetworkSnapshotInterpolator<BenchState>(6);
            for (int t = 0; t < 64; t++)
                _interpolator.Push(t, new BenchState { X = t, Y = -t });
            _tick = 64;
        }

        [Benchmark]
        public bool PushAndSample()
        {
            _interpolator.Push(_tick, new BenchState { X = _tick, Y = -_tick });
            _tick++;
            return _interpolator.TrySampleTime(_tick - 6.5, out _);
        }

        [Benchmark]
        public bool SampleOnly() => _interpolator.TrySampleTime(_tick - 6.5, out _);
    }
}