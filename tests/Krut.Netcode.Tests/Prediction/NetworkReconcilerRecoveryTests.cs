using Krut.Netcode;
using Xunit;

namespace Krut.Netcode.Tests;

public class NetworkReconcilerRecoveryTests
{
    private struct SimState
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    private struct SimInput
    {
        public float DeltaX;
    }

    private static SimState Step(SimState s, int tick, SimInput input)
    {
        s.X += input.DeltaX;
        return s;
    }

    private static SimState Replay(SimState auth, int ackTick, SimInput[] inputs, int count)
    {
        SimState s = auth;
        for (int i = 0; i < count; i++)
            s = Step(s, ackTick + 1 + i, inputs[i]);
        return s;
    }

    private static NetworkReconciler<SimState, SimInput> CreateReconciler(int capacity = NetworkReconciler<SimState, SimInput>.DefaultInputHistoryCapacity)
        => new(VisualSmoothingSettings.Default, capacity)
        {
            Step = Step,
        };

    [Fact]
    public void TenSecondOutageAt60Hz_BoundsBufferSnapsAndRecoversCleanly()
    {
        var reconciler = CreateReconciler(); // DefaultInputHistoryCapacity = 128
        reconciler.Reset(0, default);

        // Simulate 10 seconds @ 60 Hz (600 ticks) with complete server silence.
        for (int tick = 1; tick <= 600; tick++)
            reconciler.Predict(tick, new SimInput { DeltaX = 1f });

        Assert.True(reconciler.IsInputBufferFull);
        Assert.Equal(128, reconciler.UnacknowledgedInputCount);
        Assert.Equal(600 - 128, reconciler.DroppedInputCount);
        Assert.Equal(600f, reconciler.PredictedState.X, 3);

        // Server recovers with an old snapshot whose ack tick (200) fell out of the 128-tick window (473..600).
        reconciler.SetAuthoritativeState(200, new SimState { X = 190f });
        int missReplayed = reconciler.Reconcile(200, 200, Replay);

        Assert.Equal(0, missReplayed);
        Assert.Equal(1, reconciler.RollbackMissCount);
        Assert.Equal(0, reconciler.UnacknowledgedInputCount);
        Assert.Equal(190f, reconciler.PredictedState.X, 3);

        // Client resumes predicting ticks 601..640; server catches up and begins acking live inputs 601..635.
        for (int tick = 601; tick <= 640; tick++)
        {
            reconciler.Predict(tick, new SimInput { DeltaX = 0.05f });
            if (tick >= 610)
            {
                int ack = tick - 5;
                float serverX = 190f + ((ack - 600) * 0.05f);
                reconciler.SetAuthoritativeState(tick, new SimState { X = serverX });
                int replayed = reconciler.Reconcile(tick, ack, Replay);
                Assert.Equal(5, replayed);
            }
            reconciler.UpdateRenderState(1f / 60f);
        }

        // Complete smoothing and verify no permanent divergence and no additional rollback misses.
        reconciler.UpdateRenderState(1f);
        float expectedX = 190f + (40 * 0.05f);
        Assert.Equal(1, reconciler.RollbackMissCount);
        Assert.True(reconciler.ReconcileCount > 25);
        Assert.Equal(expectedX, reconciler.PredictedState.X, 3);
        Assert.Equal(expectedX, reconciler.SmoothedState.X, 3);
    }

    [Fact]
    public void AckEqualToOldestPendingMinusOne_ReplaysAllPendingWithoutRollbackMiss()
    {
        var reconciler = CreateReconciler();
        reconciler.Reset(100, new SimState { X = 10f });

        for (int tick = 101; tick <= 105; tick++)
            reconciler.Predict(tick, new SimInput { DeltaX = 1f });

        // Case 1: server has not yet processed tick 101, so it repeats lastProcessedInputTick == 100 (OldestPendingTick - 1).
        reconciler.SetAuthoritativeState(101, new SimState { X = 10.1f });
        int firstReplayed = reconciler.Reconcile(101, 100, Replay);

        Assert.Equal(5, firstReplayed);
        Assert.Equal(0, reconciler.RollbackMissCount);
        Assert.Equal(5, reconciler.UnacknowledgedInputCount);
        Assert.Equal(15.1f, reconciler.PredictedState.X, 3);

        // Case 2: server advances to ack 103 (pruning <= 103, leaving 104..105), then repeats ack 103 when input 104 is lost.
        reconciler.SetAuthoritativeState(104, new SimState { X = 13.1f });
        Assert.Equal(2, reconciler.Reconcile(104, 103, Replay));
        Assert.Equal(104, reconciler.OldestPendingTick);

        reconciler.SetAuthoritativeState(105, new SimState { X = 13.1f });
        int repeatedReplayed = reconciler.Reconcile(105, 103, Replay);

        Assert.Equal(2, repeatedReplayed);
        Assert.Equal(0, reconciler.RollbackMissCount);
        Assert.Equal(2, reconciler.UnacknowledgedInputCount);
        Assert.Equal(15.1f, reconciler.PredictedState.X, 3);
    }

    [Fact]
    public void AckAtAndAheadOfNewestPending_ClearsHistoryAndCountsFutureAcks()
    {
        var reconciler = CreateReconciler();
        reconciler.Reset(0, default);
        reconciler.Predict(1, new SimInput { DeltaX = 1f });
        reconciler.Predict(2, new SimInput { DeltaX = 1f });

        // Exact match with NewestPendingTick: not a future ack, not a miss.
        reconciler.SetAuthoritativeState(2, new SimState { X = 2f });
        Assert.Equal(0, reconciler.Reconcile(2, 2, Replay));
        Assert.Equal(0, reconciler.FutureAckCount);
        Assert.Equal(0, reconciler.RollbackMissCount);
        Assert.Equal(0, reconciler.UnacknowledgedInputCount);

        reconciler.Predict(3, new SimInput { DeltaX = 1f });
        // Server acks tick 10 ahead of NewestPendingTick (3): clears history, increments FutureAckCount, no RollbackMiss.
        reconciler.SetAuthoritativeState(10, new SimState { X = 5f });
        Assert.Equal(0, reconciler.Reconcile(10, 10, Replay));
        Assert.Equal(1, reconciler.FutureAckCount);
        Assert.Equal(0, reconciler.RollbackMissCount);
        Assert.Equal(0, reconciler.UnacknowledgedInputCount);
        Assert.Equal(5f, reconciler.PredictedState.X, 3);

        reconciler.Reset(20, new SimState { X = 0f });
        Assert.Equal(0, reconciler.FutureAckCount);
    }

    [Fact]
    public void InternalGapInPendingTicks_CountsRollbackMissAndAdoptsAuthoritativeState()
    {
        var reconciler = CreateReconciler();
        reconciler.Reset(100, default);
        reconciler.Predict(101, new SimInput { DeltaX = 1f });
        reconciler.Predict(105, new SimInput { DeltaX = 1f });
        reconciler.Predict(110, new SimInput { DeltaX = 1f });

        reconciler.SetAuthoritativeState(108, new SimState { X = 42f });
        int replayed = reconciler.Reconcile(108, 107, Replay);

        Assert.Equal(0, replayed);
        Assert.Equal(1, reconciler.RollbackMissCount);
        Assert.Equal(0, reconciler.UnacknowledgedInputCount);
        Assert.Equal(42f, reconciler.PredictedState.X, 3);
    }
}
