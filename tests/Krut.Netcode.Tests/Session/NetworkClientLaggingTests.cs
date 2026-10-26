using System;
using Krut.Netcode;
using System.Numerics;
using Xunit;

namespace Krut.Netcode.Tests;

/// <summary>
/// Tests for the client's lag compensation: snapshot interpolation for other players' entities and
/// reconciliation for the player's own (future-networking sec 7.1-sec 7.3, sec 7.6). The tests use a
/// deliberately boring state struct - three floats, exactly one position - because that is the shape the
/// engine's blending and distance measurement are built for. The whole point of <c>Predict</c>/<c>Reconcile</c>
/// is that the very same step function is run twice - once live on the client, once during replay - and must
/// give the same answer both times (see the class documentation on purity). A pure function over a single float
/// makes that property directly checkable: the arithmetic is small enough to reason about exactly, and a
/// rollback that produces a different end state than the original run cannot hide behind a rounding
/// coincidence.
/// </summary>
public class NetworkClientLaggingTests
{
    /// <summary>A "remote entity": just the position, so the distance in the tests is unambiguous.</summary>
    private struct TestState
    {
        public TestState(float x, float y = 0f, float z = 0f)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X;
        public float Y;
        public float Z;
    }

    /// <summary>An input: how far X moves this tick.</summary>
    private struct TestInput
    {
        public TestInput(float deltaX)
        {
            DeltaX = deltaX;
        }

        public float DeltaX;
    }

    /// <summary>
    /// A pure step: state + input -> new state. The test uses the same function both for the client's "live"
    /// run and for the replay, which is the whole point of <c>Predict</c>/<c>Reconcile</c> (see the class
    /// documentation on purity).
    /// </summary>
    private static TestState Step(TestState state, int tick, TestInput input)
    {
        state.X += input.DeltaX;
        return state;
    }

    private static TestState Replay(TestState authoritative, int lastProcessedInputTick, TestInput[] inputs)
    {
        TestState state = authoritative;
        int tick = lastProcessedInputTick;
        for (int i = 0; i < inputs.Length; i++)
            state = Step(state, ++tick, inputs[i]);

        return state;
    }

    private static NetworkSnapshotInterpolator<TestState> CreateInterpolator(int delayTicks, int capacity = 32)
        => new(delayTicks, capacity);

    // The render moment is 25 (currentTick 27 - 2 ticks of delay) and lies halfway between the snapshots for
    // tick 20 and 30.
    [Fact]
    public void Interpolation_BetweenTwoSnapshots_GivesTheValueInTheMiddle()
    {
        var interpolator = CreateInterpolator(delayTicks: 2);
        interpolator.Push(10, new TestState(0f));
        interpolator.Push(20, new TestState(100f));
        interpolator.Push(30, new TestState(200f));

        bool ok = interpolator.TrySample(27, out TestState state, out NetworkSampleMode mode);

        Assert.True(ok);
        Assert.Equal(NetworkSampleMode.Interpolated, mode);
        Assert.Equal(150f, state.X, 4);
    }

    [Fact]
    public void Interpolation_DoesEachComponentSeparately()
    {
        var interpolator = CreateInterpolator(delayTicks: 2);
        interpolator.Push(10, new TestState(0f, 0f, 0f));
        interpolator.Push(20, new TestState(100f, 20f, -40f));

        // TestState's layout is the one the blending requires - two of the four
        // bytes per component must not be anything else.
        Assert.True(NetworkStateBlend.IsComponentWise<TestState>());

        interpolator.TrySampleTime(12.5, out TestState state);

        // The share between tick 10 and 20 is 0.25 - that is, a quarter of the
        // distance.
        Assert.Equal(25f, state.X, 3);
        Assert.Equal(5f, state.Y, 3);
        Assert.Equal(-10f, state.Z, 3);
    }

    [Fact]
    public void Interpolation_WithFractionalTick_GivesContinuousMotion()
    {
        var interpolator = CreateInterpolator(delayTicks: 2);
        interpolator.Push(10, new TestState(0f));
        interpolator.Push(20, new TestState(100f));
        interpolator.Push(30, new TestState(200f));

        // Half a tick: what a game running at 144 Hz frames actually asks for.
        interpolator.TrySampleTime(25.5, out TestState state, out NetworkSampleMode mode);

        Assert.Equal(NetworkSampleMode.Interpolated, mode);
        Assert.Equal(155f, state.X, 3);
    }

    [Fact]
    public void Interpolation_ExactHit_UsesTheSnapshotDirectly()
    {
        var interpolator = CreateInterpolator(delayTicks: 2);
        interpolator.Push(10, new TestState(0f));
        interpolator.Push(20, new TestState(100f));
        interpolator.Push(30, new TestState(200f));

        interpolator.TrySample(22, out TestState state, out NetworkSampleMode mode);

        Assert.Equal(NetworkSampleMode.Exact, mode);
        Assert.Equal(100f, state.X, 4);
    }

    // Before the first snapshot there is nothing to show, and the mode has to say so: a game that draws the
    // identity matrix here renders every remote entity stacked in the world origin.
    [Fact]
    public void EmptyBuffer_GivesNothingToDraw()
    {
        var interpolator = CreateInterpolator(delayTicks: 2);

        Assert.False(interpolator.TrySample(100, out _));
        Assert.Equal(NetworkSampleMode.Empty, interpolator.LastSampleMode);
    }

    [Fact]
    public void BufferNotFilled_HoldsTheOldestState()
    {
        // Startup: only one snapshot has arrived and the render moment lies
        // BEFORE it.
        var interpolator = CreateInterpolator(delayTicks: 5);
        interpolator.Push(100, new TestState(42f));

        bool ok = interpolator.TrySample(50, out TestState state, out NetworkSampleMode mode);

        Assert.True(ok);
        Assert.Equal(NetworkSampleMode.Held, mode);
        Assert.Equal(42f, state.X, 4);
    }

    // currentTick 35 -> render moment 33, three ticks past the last snapshot; the speed is 10 per tick.
    [Fact]
    public void Extrapolation_GivesContinuedMotionFromTheLatestSnapshots()
    {
        var interpolator = CreateInterpolator(delayTicks: 2);
        interpolator.Push(10, new TestState(0f));
        interpolator.Push(20, new TestState(100f));
        interpolator.Push(30, new TestState(200f));

        bool ok = interpolator.TrySample(35, out TestState state, out NetworkSampleMode mode);

        Assert.True(ok);
        Assert.Equal(NetworkSampleMode.Extrapolated, mode);
        Assert.Equal(230f, state.X, 3);
    }

    [Fact]
    public void Extrapolation_StopsAtTheLimit_ThenHoldsTheLatestValue()
    {
        var interpolator = CreateInterpolator(delayTicks: 2);
        interpolator.MaxExtrapolationTicks = 2;
        interpolator.Push(10, new TestState(0f));
        interpolator.Push(20, new TestState(100f));
        interpolator.Push(30, new TestState(200f));

        // Exactly at the limit: alpha = 2 ticks -> 200 + 2*10 = 220, still extrapolation.
        interpolator.TrySample(34, out TestState state, out NetworkSampleMode mode);
        Assert.Equal(NetworkSampleMode.Extrapolated, mode);
        Assert.Equal(220f, state.X, 3);

        // Over the limit: hold the last known state instead of guessing off at a
        // tangent.
        interpolator.TrySample(35, out state, out mode);
        Assert.Equal(NetworkSampleMode.Held, mode);
        Assert.Equal(200f, state.X, 3);

        // Far over the limit: still still, never an acceleration off.
        interpolator.TrySample(60, out state, out mode);
        Assert.Equal(NetworkSampleMode.Held, mode);
        Assert.Equal(200f, state.X, 3);
    }

    [Fact]
    public void Extrapolation_RequiresTwoSnapshots()
    {
        // With a single snapshot there is no speed to guess with - then it is
        // better to hold still than to invent a direction.
        var interpolator = CreateInterpolator(delayTicks: 0);
        interpolator.Push(30, new TestState(200f));

        interpolator.TrySample(33, out TestState state, out NetworkSampleMode mode);

        Assert.Equal(NetworkSampleMode.Held, mode);
        Assert.Equal(200f, state.X, 3);
    }

    [Fact]
    public void MissingSnapshot_FallsBackToInterpolation_WhenItShowsUp()
    {
        var interpolator = CreateInterpolator(delayTicks: 2);
        interpolator.Push(10, new TestState(0f));
        interpolator.Push(20, new TestState(100f));

        // Render moment 25, but the newest snapshot is 20: five ticks too far
        // ahead to guess - we hold the last known state.
        interpolator.TrySample(27, out TestState state, out NetworkSampleMode mode);
        Assert.Equal(NetworkSampleMode.Held, mode);
        Assert.Equal(100f, state.X, 3);

        // The next snapshot (tick 30) comes in.
        interpolator.Push(30, new TestState(200f));

        // Now it interpolates again - and the value lands in the middle, not at
        // 200.
        interpolator.TrySample(27, out state, out mode);
        Assert.Equal(NetworkSampleMode.Interpolated, mode);
        Assert.Equal(150f, state.X, 3);
    }

    [Fact]
    public void LostSnapshot_JoiningLineContinuesOverTheGap()
    {
        // 25 is missing entirely (UDP loss).
        var interpolator = CreateInterpolator(delayTicks: 2);
        interpolator.Push(10, new TestState(0f));
        interpolator.Push(20, new TestState(100f));
        interpolator.Push(30, new TestState(200f));

        interpolator.TrySample(27, out TestState state, out NetworkSampleMode mode);

        Assert.Equal(NetworkSampleMode.Interpolated, mode);
        Assert.Equal(150f, state.X, 3);
    }

    // A ring, not a growing list: the buffer has to hold a bounded amount of history, because 60 snapshots/s
    // per remote entity would otherwise be an unbounded allocation per peer.
    [Fact]
    public void Ring_OverwritesTheOldestWhenItBecomesFull()
    {
        var interpolator = CreateInterpolator(delayTicks: 2, capacity: 4);
        for (int tick = 1; tick <= 10; tick++)
            interpolator.Push(tick, new TestState(tick * 100f));

        // The capacity is 4, so only 7..10 are left - never more, never a growing
        // list.
        Assert.Equal(4, interpolator.BufferedSnapshotCount);
        Assert.Equal(6, interpolator.OverwrittenSnapshotCount);
        Assert.False(interpolator.TryGetSnapshot(6, out _), "Tick 6 should have been overwritten.");
        Assert.True(interpolator.TryGetSnapshot(7, out _));

        // And the ring is correct after the wrap: 9.5 should lie halfway between
        // 900 and 1000.
        interpolator.TrySampleTime(9.5, out TestState state, out NetworkSampleMode mode);
        Assert.Equal(NetworkSampleMode.Interpolated, mode);
        Assert.Equal(950f, state.X, 3);
    }

    [Fact]
    public void LateSnapshot_IsIgnoredAndCounted()
    {
        var interpolator = CreateInterpolator(delayTicks: 2);
        interpolator.Push(10, new TestState(100f));
        interpolator.Push(11, new TestState(110f));

        // A UDP packet that arrives out of order must not disturb the buffering.
        interpolator.Push(10, new TestState(999f));

        Assert.Equal(2, interpolator.BufferedSnapshotCount);
        Assert.Equal(1, interpolator.LateSnapshotCount);
        Assert.True(interpolator.TryGetSnapshot(10, out TestState state));
        Assert.Equal(100f, state.X, 3);
    }

    [Fact]
    public void CustomLerpFunction_IsUsedForInterpolation()
    {
        // A game with rotation supplies its own blending. Here we only check that
        // the interpolator actually uses it instead of the standard linear one.
        var interpolator = new NetworkSnapshotInterpolator<TestState>(2);
        int calls = 0;
        interpolator.Lerp = (a, b, t) =>
        {
            calls++;
            return NetworkStateBlend.Lerp(a, b, t);
        };

        interpolator.Push(10, new TestState(0f));
        interpolator.Push(20, new TestState(100f));
        interpolator.TrySampleTime(15, out _);

        Assert.Equal(1, calls);
    }

    // q and -q are the same rotation: without the sign flip slerp would take the long way round, 180 degrees in
    // the wrong direction (see the component-wise blend test below).
    [Fact]
    public void SlerpRotation_TakesTheShortestPathAndNormalizes()
    {
        var identity = Quaternion.Identity;
        var halvVarv = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);

        var medPlus = NetworkStateBlend.SlerpRotation(identity, halvVarv, 0.5f);
        var medMinus = NetworkStateBlend.SlerpRotation(identity, -halvVarv, 0.5f);

        Assert.Equal(1f, medPlus.Length(), 4);
        Assert.Equal(1f, medMinus.Length(), 4);
        Assert.Equal(medPlus.X, medMinus.X, 4);
        Assert.Equal(medPlus.W, medMinus.W, 4);

        // 90 degrees halfway should give 45 degrees.
        var nittio = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var resultat = NetworkStateBlend.SlerpRotation(identity, nittio, 0.5f);
        Assert.Equal(MathF.PI / 4f, 2f * MathF.Atan2(resultat.Y, resultat.W), 4);
    }

    // The proof of why rotation must not be interpolated component-wise: a linear blend of two unit quaternions
    // has length < 1, and a matrix built on such a quaternion gets a scale that is not 1.
    [Fact]
    public void ComponentwiseBlend_OfRotationGivesNonUnitQuaternion()
    {
        var identity = Quaternion.Identity;
        var nittio = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);

        float x = identity.X + ((nittio.X - identity.X) * 0.5f);
        float y = identity.Y + ((nittio.Y - identity.Y) * 0.5f);
        float z = identity.Z + ((nittio.Z - identity.Z) * 0.5f);
        float w = identity.W + ((nittio.W - identity.W) * 0.5f);
        float length = MathF.Sqrt((x * x) + (y * y) + (z * z) + (w * w));

        Assert.True(length < 1f, $"The length should be less than 1, was {length}");
    }

    private static NetworkReconciler<TestState, TestInput> CreateReconciler(
        float minCorrectionDistance = 1f,
        int capacity = NetworkReconciler<TestState, TestInput>.DefaultInputHistoryCapacity)
        => new(
            new VisualSmoothingSettings
            {
                MinCorrectionDistance = minCorrectionDistance,
                MaxCorrectionSeconds = 0.15f,
                MinCorrectionSeconds = 0.05f,
                UseEaseOut = true,
            },
            capacity)
        {
            Step = (state, tick, input) => Step(state, tick, input),
        };

    [Fact]
    public void Rollback_ReplayedInputs_GiveTheSameEndStateAsTheFirstTime()
    {
        var reconciler = CreateReconciler();
        reconciler.Reset(100, new TestState(0f));

        // The client runs five ticks locally with inputs 1, 2, 3, 4, 5.
        int tick = 100;
        for (int i = 1; i <= 5; i++)
            reconciler.Predict(++tick, new TestInput(i));

        float lokaltSlatlage = reconciler.PredictedState.X;
        Assert.Equal(15f, lokaltSlatlage, 3);

        // The server answers: it has processed tick 103 and reached 6 (the client
        // reached 6 too - so it agreed all the way there).
        reconciler.SetAuthoritativeState(104, new TestState(6f));
        int replayed = reconciler.Reconcile(104, 103, Replay);

        Assert.Equal(2, replayed);

        // 6 + 4 + 5 = 15 - the same end state as when the client first ran them.
        // That proves the replay function is pure: same input, same outcome.
        Assert.Equal(lokaltSlatlage, reconciler.PredictedState.X, 4);

        // Ticks 104 and 105 are still unacknowledged - they have to stay, or the
        // next rollback cannot re-run them.
        Assert.Equal(2, reconciler.UnacknowledgedInputCount);
        Assert.Equal(105, reconciler.NewestPendingTick);
    }

    [Fact]
    public void Rollback_RunsAllUnacknowledgedInputs_InTickOrder()
    {
        var reconciler = CreateReconciler();
        reconciler.Reset(0, new TestState(0f));

        for (int i = 1; i <= 4; i++)
            reconciler.Predict(i, new TestInput(i));

        Assert.Equal(4, reconciler.UnacknowledgedInputCount);

        reconciler.SetAuthoritativeState(5, new TestState(0f));
        int replayed = reconciler.Reconcile(5, 2, (authoritative, lastTick, inputs) =>
        {
            // The count is read from PendingReplayCount - otherwise the game would have to compute it itself,
            // which is exactly the glue code the API should save us from.
            Assert.Equal(2, reconciler.PendingReplayCount);
            Assert.Equal(3f, inputs[0].DeltaX);
            Assert.Equal(4f, inputs[1].DeltaX);
            return Replay(authoritative, lastTick, inputs[..reconciler.PendingReplayCount]);
        });

        Assert.Equal(2, replayed);
        Assert.Equal(7f, reconciler.PredictedState.X, 3);
        Assert.Equal(1, reconciler.ReconcileCount);
        Assert.Equal(2, reconciler.TotalReplayedInputs);

        // The acknowledged inputs are gone from the history, the two that remain
        // are still there.
        Assert.Equal(2, reconciler.UnacknowledgedInputCount);
        Assert.Equal(4, reconciler.NewestPendingTick);
    }

    // The threshold is what separates "the player missed a few centimeters" from "the server thinks we are
    // somewhere else entirely": below it the rendered state slides, above it both physics and rendering jump,
    // because sliding a five metre error over 150 ms looks like a shunt in the side.
    [Fact]
    public void CorrectionUnderThreshold_SlidesVisuallyButPhysicsSnapsDirectly()
    {
        var reconciler = CreateReconciler(minCorrectionDistance: 1f);
        reconciler.Reset(100, new TestState(0f));
        reconciler.Predict(101, new TestInput(0.1f));
        float forUtjamning = reconciler.SmoothedState.X;

        // The server says 0.15 instead of the client's guessed 0.1 - 5 cm off.
        reconciler.SetAuthoritativeState(102, new TestState(0.15f));
        reconciler.Reconcile(102, 101, Replay);

        // Physics: straight to the corrected value, no sliding.
        Assert.Equal(0.15f, reconciler.PredictedState.X, 3);

        // Rendering: stays where it was - the smoothing has only just started.
        Assert.Equal(forUtjamning, reconciler.SmoothedState.X, 3);
        Assert.True(reconciler.IsSmoothing);
        Assert.False(reconciler.LastCorrectionWasTeleport);
        Assert.Equal(0.05f, reconciler.LastCorrectionDistance, 3);

        // Halfway into the smoothing it lies between the start state and the
        // target.
        reconciler.UpdateRenderState(0.025f);
        Assert.InRange(reconciler.SmoothedState.X, forUtjamning, 0.15f);

        // And after the time it is exactly on the target (not "almost" - see
        // UpdateRenderState).
        reconciler.UpdateRenderState(1f);
        Assert.Equal(0.15f, reconciler.SmoothedState.X, 3);
        Assert.False(reconciler.IsSmoothing);
    }

    // A unit that keeps moving during a smoothing must keep moving on screen too: the rendered state is the
    // running prediction plus a decaying error, not a slide towards the corrected state as it was at the
    // correction.
    [Fact]
    public void SmoothingFollowsAMovingPrediction()
    {
        var reconciler = CreateReconciler(minCorrectionDistance: 1f);
        reconciler.Reset(100, new TestState(0f));
        int tick = 100;
        for (int i = 0; i < 5; i++)
            reconciler.Predict(++tick, new TestInput(0.5f));    // 0.5 per tick, i.e. 30 units/s at 60 Hz
        Assert.Equal(2.5f, reconciler.SmoothedState.X, 3);

        // The server: 0.2 behind what the client predicted for the same tick.
        reconciler.SetAuthoritativeState(102, new TestState(0.3f));
        reconciler.Reconcile(102, 101, Replay);
        Assert.Equal(2.3f, reconciler.PredictedState.X, 3);
        Assert.Equal(2.5f, reconciler.SmoothedState.X, 3);     // nothing jumps at the correction

        // Halfway through the smoothing, with five more ticks predicted, the rendered state has moved with the
        // prediction and carries part of the 0.2 error - it is not stuck near 2.3.
        for (int i = 0; i < 5; i++)
        {
            reconciler.Predict(++tick, new TestInput(0.5f));
            reconciler.UpdateRenderState(1f / 60f);
        }
        float predicted = reconciler.PredictedState.X;
        Assert.Equal(4.8f, predicted, 3);
        Assert.InRange(reconciler.SmoothedState.X, predicted, predicted + 0.2f);

        reconciler.UpdateRenderState(1f);
        Assert.Equal(predicted, reconciler.SmoothedState.X, 4);
        Assert.False(reconciler.IsSmoothing);
    }

    [Fact]
    public void CorrectionOverThreshold_Teleports()
    {
        var reconciler = CreateReconciler(minCorrectionDistance: 1f);
        reconciler.Reset(100, new TestState(0f));
        reconciler.Predict(101, new TestInput(0.1f));

        // The server says 5.0 - almost five metres off. Sliding there over 150 ms
        // would look like a shunt in the side, so both physics and rendering jump.
        reconciler.SetAuthoritativeState(102, new TestState(5f));
        reconciler.Reconcile(102, 101, Replay);

        Assert.Equal(5f, reconciler.SmoothedState.X, 3);
        Assert.Equal(5f, reconciler.PredictedState.X, 3);
        Assert.False(reconciler.IsSmoothing);
        Assert.True(reconciler.LastCorrectionWasTeleport);
        Assert.Equal(4.9f, reconciler.LastCorrectionDistance, 3);
    }

    [Fact]
    public void RepeatedCorrection_StartsFromTheRenderedState_InsteadOfWobbling()
    {
        var reconciler = CreateReconciler(minCorrectionDistance: 1f);
        reconciler.Reset(100, new TestState(0f));

        // First correction: 0 -> 0.4
        reconciler.SetAuthoritativeState(101, new TestState(0.4f));
        reconciler.Reconcile(101, 100, Replay);
        reconciler.UpdateRenderState(0.05f);
        float renderatEfterForsta = reconciler.SmoothedState.X;

        // Second correction, in the opposite direction, before the first is even
        // done.
        reconciler.SetAuthoritativeState(102, new TestState(0.2f));
        reconciler.Reconcile(102, 101, Replay);

        // Rendering must NOT jump back to 0 or to 0.4 - it smooths on from what it actually showed.
        Assert.Equal(renderatEfterForsta, reconciler.SmoothedState.X, 4);
        Assert.True(reconciler.IsSmoothing);
    }

    [Fact]
    public void ConfirmedWithoutNewError_SmoothingContinuesTowardTheSameTarget()
    {
        var reconciler = CreateReconciler(minCorrectionDistance: 1f);
        reconciler.Reset(100, new TestState(0f));

        reconciler.SetAuthoritativeState(101, new TestState(0.4f));
        reconciler.Reconcile(101, 100, Replay);
        reconciler.UpdateRenderState(0.01f);
        float renderatUnderLopet = reconciler.SmoothedState.X;
        Assert.True(reconciler.IsSmoothing);

        // The server confirms exactly what the client already arrived at (0.4 -> 0.4): no new error, but the
        // rendering is still behind. It should therefore keep sliding - snapping there would be a visible jerk,
        // which is exactly what the smoothing exists for.
        reconciler.SetAuthoritativeState(102, new TestState(0.4f));
        reconciler.Reconcile(102, 101, Replay);

        Assert.Equal(0.4f, reconciler.PredictedState.X, 3);
        Assert.Equal(renderatUnderLopet, reconciler.SmoothedState.X, 4);
        reconciler.UpdateRenderState(1f);
        Assert.Equal(0.4f, reconciler.SmoothedState.X, 3);
        Assert.False(reconciler.IsSmoothing);
    }

    [Fact]
    public void RepeatedServerSequence_DoesNotCauseEndlessReplay()
    {
        var reconciler = CreateReconciler();
        reconciler.Reset(100, new TestState(0f));
        for (int i = 1; i <= 5; i++)
            reconciler.Predict(100 + i, new TestInput(1f));

        // One server packet ...
        reconciler.SetAuthoritativeState(106, new TestState(1f));
        int forsta = reconciler.Reconcile(106, 102, Replay);
        Assert.Equal(3, forsta);

        // ... which the network repeats 100 times (UDP duplicates, a hostile client, or a server that has
        // deadlocked).
        for (int i = 0; i < 100; i++)
        {
            Assert.False(reconciler.SetAuthoritativeState(106, new TestState(1f)));
            Assert.Equal(0, reconciler.Reconcile(106, 102, Replay));
        }

        // Even an old snapshot that arrives late (UDP reordering) is ignored.
        Assert.False(reconciler.SetAuthoritativeState(90, new TestState(999f)));
        Assert.Equal(0, reconciler.Reconcile(90, 102, Replay));

        Assert.Equal(1, reconciler.ReconcileCount);
        Assert.Equal(3, reconciler.TotalReplayedInputs);
        Assert.True(reconciler.IgnoredServerStateCount >= 100, $"Only {reconciler.IgnoredServerStateCount} were ignored.");
        Assert.Equal(4f, reconciler.PredictedState.X, 3);
    }

    [Fact]
    public void SparseServerSequence_BecomesBoundedWork()
    {
        // A client that only replays and never gets an acknowledgement must not make the game grow without
        // bound: the buffer is fixed, and a rollback to a tick the client no longer has counts as a miss and is
        // cut off.
        var reconciler = CreateReconciler(capacity: 8);
        reconciler.Reset(100, new TestState(0f));

        for (int i = 1; i <= 30; i++)
            reconciler.Predict(100 + i, new TestInput(1f));

        Assert.Equal(8, reconciler.UnacknowledgedInputCount);
        Assert.True(reconciler.IsInputBufferFull);
        Assert.Equal(22, reconciler.DroppedInputCount);

        // The server answers about a tick the client no longer has anything saved
        // for.
        reconciler.SetAuthoritativeState(200, new TestState(0f));
        int replayed = reconciler.Reconcile(200, 101, Replay);

        Assert.Equal(0, replayed);
        Assert.Equal(1, reconciler.RollbackMissCount);
        Assert.Equal(0, reconciler.UnacknowledgedInputCount);
        Assert.Equal(0f, reconciler.PredictedState.X, 3);
    }

    [Fact]
    public void InputWithoutStoredState_ThrowsAClearError()
    {
        var reconciler = CreateReconciler();
        reconciler.Reset(100, new TestState(0f));

        Assert.Throws<InvalidOperationException>(() => reconciler.RecordInput(101, new TestInput(1f)));
    }

    [Fact]
    public void FirstServerPacket_AcceptsTheWorldWithoutReplay()
    {
        var reconciler = CreateReconciler();

        // The client has already guessed (typically: a reconnect after having
        // played for a while).
        reconciler.RecordState(100, new TestState(7f));
        reconciler.RecordInput(100, new TestInput(1f));

        reconciler.SetAuthoritativeState(101, new TestState(50f));
        int replayed = reconciler.Reconcile(101, 99, Replay);

        Assert.Equal(0, replayed);
        Assert.Equal(50f, reconciler.PredictedState.X, 3);
        Assert.Equal(50f, reconciler.SmoothedState.X, 3);
        Assert.Equal(0, reconciler.UnacknowledgedInputCount);
    }

    [Fact]
    public void SmoothingDisabled_SnapsImmediately()
    {
        var reconciler = new NetworkReconciler<TestState, TestInput>(VisualSmoothingSettings.Disabled)
        {
            Step = (state, tick, input) => Step(state, tick, input),
        };

        reconciler.Reset(100, new TestState(0f));
        reconciler.Predict(101, new TestInput(0.1f));

        reconciler.SetAuthoritativeState(102, new TestState(0.15f));
        reconciler.Reconcile(102, 101, Replay);

        Assert.Equal(0.15f, reconciler.SmoothedState.X, 3);
        Assert.False(reconciler.IsSmoothing);
    }
}

/// <summary>Tests for the ring buffer itself - the part both the interpolator and the reconciler rest on.</summary>
public class NetworkStateRingTests
{
    private static NetworkStateRing<NetworkStateEntry<int>> CreateRing(int capacity)
        => new(capacity, static entry => entry.Tick);

    [Fact]
    public void FewerThanTwoEntries_IsRejected()
    {
        // A ring with a single entry can never interpolate - better to say so
        // deliberately than to discover it one frame into a match.
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateRing(1));
        Assert.Throws<ArgumentNullException>(() => new NetworkStateRing<NetworkStateEntry<int>>(8, null!));
    }

    [Fact]
    public void OverwritesTheOldestWhenItBecomesFull()
    {
        var ring = CreateRing(3);
        for (int tick = 1; tick <= 5; tick++)
            Assert.True(ring.Push(tick, new NetworkStateEntry<int>(tick, tick * 10)));

        Assert.Equal(3, ring.Count);
        Assert.Equal(3, ring.OldestTick);
        Assert.Equal(5, ring.NewestTick);
        Assert.Equal(30, ring[0].Value);
        Assert.Equal(50, ring[2].Value);
    }

    [Fact]
    public void NonIncreasingTick_IsRejected()
    {
        var ring = CreateRing(4);
        Assert.True(ring.Push(10, new NetworkStateEntry<int>(10, 1)));
        Assert.False(ring.Push(9, new NetworkStateEntry<int>(9, 2)), "An older tick must not disturb the buffer.");
        Assert.False(ring.Push(10, new NetworkStateEntry<int>(10, 3)), "A duplicate must not disturb the buffer.");
        Assert.Equal(1, ring.Count);
    }

    [Fact]
    public void BinarySearch_FindsTheRightEntry()
    {
        var ring = CreateRing(16);
        for (int tick = 100; tick < 110; tick++)
            ring.Push(tick, new NetworkStateEntry<int>(tick, tick));

        Assert.Equal(-1, ring.IndexOfTickAtOrBefore(99));
        Assert.Equal(0, ring.IndexOfTickAtOrBefore(100));
        Assert.Equal(1, ring.IndexOfTickAtOrBefore(101));
        Assert.Equal(5, ring.IndexOfTickAtOrBefore(105));
        Assert.Equal(9, ring.IndexOfTickAtOrBefore(109));
    }

    [Fact]
    public void Removal_KeepsTheNewestAndDropsAcknowledged()
    {
        var ring = CreateRing(8);
        for (int tick = 1; tick <= 6; tick++)
            ring.Push(tick, new NetworkStateEntry<int>(tick, tick));

        int borttagna = ring.RemoveAtOrBefore(3);

        Assert.Equal(3, borttagna);
        Assert.Equal(3, ring.Count);
        Assert.Equal(4, ring.OldestTick);
        Assert.Equal(6, ring.NewestTick);

        // Removing everything leaves an empty but usable ring.
        Assert.Equal(3, ring.RemoveAtOrBefore(99));
        Assert.True(ring.IsEmpty);
        Assert.False(ring.TryGet(0, out _));
        Assert.True(ring.Push(7, new NetworkStateEntry<int>(7, 70)));
        Assert.Equal(7, ring.NewestTick);
    }

    [Fact]
    public void EmptyRing_ThrowsForOldTickAccessors()
    {
        var ring = CreateRing(4);

        Assert.True(ring.IsEmpty);
        Assert.Throws<InvalidOperationException>(() => ring.OldestTick);
        Assert.Throws<InvalidOperationException>(() => ring.NewestTick);
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ring[0]; });
        Assert.False(ring.TryGet(0, out _));
    }

    [Fact]
    public void SetOverwritesWithoutDisturbingOtherEntries()    {
        var ring = CreateRingWithThree();
        ring.Set(1, new NetworkStateEntry<int>(2, 222));

        Assert.Equal(1, ring.OldestTick);
        Assert.Equal(222, ring[1].Value);
        Assert.Equal(3, ring.NewestTick);
    }

    private static NetworkStateRing<NetworkStateEntry<int>> CreateRingWithThree()
    {
        var ring = CreateRing(4);
        ring.Push(1, new NetworkStateEntry<int>(1, 1));
        ring.Push(2, new NetworkStateEntry<int>(2, 2));
        ring.Push(3, new NetworkStateEntry<int>(3, 3));
        return ring;
    }
}