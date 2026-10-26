using System.Diagnostics;
using System.Numerics;
using Krut.Netcode;

namespace PredictionDemo;

/// <summary>
/// One player: predicts its own unit with NetworkReconciler, shows the other unit through
/// NetworkSnapshotInterpolator, and locks its clock to the server with NetworkTimeSync.
/// </summary>
public sealed class DemoClient
{
    public const int InterpolationDelayTicks = 8;
    private const int SamplesToStart = 6;
    private const int SamplesToRender = 3;

    public readonly NetworkClient Net;
    public readonly int Slot;
    // Extra lead on top of half the RTT: the in-process loop adds scheduling jitter, and an input that misses its tick is repeated.
    public readonly NetworkTimeSync Sync = new(UnitSim.TickRate, extraLeadTicks: 4);
    public readonly NetworkReconciler<UnitState, UnitInput> Reconciler = new(VisualSmoothingSettings.Default);
    public readonly NetworkSnapshotInterpolator<UnitState> Remote = new(InterpolationDelayTicks);

    // Everything below exists only to measure; a real game would not keep it.
    public readonly Dictionary<int, UnitState> Predicted = new();
    public readonly Dictionary<int, UnitState> OpenLoop = new();
    public readonly List<float> ErrorBeforeReconcile = new();
    public readonly List<float> CorrectionSizes = new();
    public readonly List<(double RenderTick, Vector2 Pos)> RemoteSamples = new();
    public readonly int[] SampleModes = new int[Enum.GetValues<NetworkSampleMode>().Length];
    public int FirstTick;

    private UnitState _openLoop;
    private int _lastTick = -1;
    private bool _started;

    public DemoClient(NetworkConfig config, int slot)
    {
        Slot = slot;
        Net = new NetworkClient(config);
        Net.MessageReceived += OnMessage;
        Reconciler.Step = UnitSim.Step;
        Reconciler.DistanceMetric = UnitSim.Distance;
    }

    public void Update(float dt)
    {
        Net.Update(dt);
        Reconciler.UpdateRenderState(dt);
        if (!Net.IsSessionReady)
            return;

        if (_lastTick < 0)
            _lastTick = Net.CurrentTick;
        while (_lastTick < Net.CurrentTick)
            Tick(++_lastTick);
    }

    private void Tick(int tick)
    {
        if (Sync.ShouldProbe(tick))
        {
            var probe = NetworkSession.Rent<SyncRequestMessage>();
            probe.ClientTick = tick;
            probe.Stamp = Stopwatch.GetTimestamp();
            Net.Send(Net.ServerPeerId, probe);
        }

        if (!_started)
        {
            if (Sync.SampleCount >= SamplesToStart)
                StartPlaying();
            return;
        }

        var input = UnitSim.Script(Slot, tick);
        Reconciler.Predict(tick, input);
        _openLoop = UnitSim.Step(_openLoop, tick, input);
        Predicted[tick] = Reconciler.PredictedState;
        OpenLoop[tick] = _openLoop;

        var message = NetworkSession.Rent<InputMessage>();
        message.Tick = tick;
        message.Input = input;
        Net.Send(Net.ServerPeerId, message);

        if (Sync.SampleCount >= SamplesToRender)
            SampleRemote(tick);
    }

    // Jump the clock to "server now + lead" so inputs arrive just before the server needs them.
    private void StartPlaying()
    {
        int serverNow = Sync.ServerTickFor(Net.CurrentTick);
        int target = Sync.ClientTickAheadOf(serverNow);
        Net.ResetClockTo(target);
        Sync.Reset();
        _lastTick = target;
        FirstTick = target + 1;
        _openLoop = UnitSim.Spawn(Slot);
        Reconciler.Reset(serverNow, _openLoop);
        _started = true;
    }

    private void SampleRemote(int tick)
    {
        double renderTick = Sync.ServerTickFor(tick) - InterpolationDelayTicks;
        if (!Remote.TrySampleTime(renderTick, out var state, out var mode))
            return;

        SampleModes[(int)mode]++;
        RemoteSamples.Add((renderTick, state.Pos));
    }

    private void OnMessage(NetworkEnvelope e)
    {
        switch (e.Message)
        {
            case SyncResponseMessage reply:
                // Probes sent before the clock jump carry the old tick numbering.
                if (_started && reply.ClientTick < FirstTick)
                    break;
                double rtt = (Stopwatch.GetTimestamp() - reply.Stamp) / (double)Stopwatch.Frequency;
                Sync.RecordResponse(reply.ClientTick, reply.ServerTick, rtt);
                break;
            case SnapshotMessage snapshot:
                Remote.Push(snapshot.ServerTick, snapshot.Other);
                if (_started && snapshot.ServerTick >= FirstTick)
                    Reconcile(snapshot);
                break;
        }
    }

    private void Reconcile(SnapshotMessage snapshot)
    {
        // State after the acknowledged input is the state recorded before the next one.
        if (Reconciler.TryGetRecordedState(snapshot.LastProcessedInput + 1, out var predictedThen))
            ErrorBeforeReconcile.Add(UnitSim.Distance(predictedThen, snapshot.Own));

        Reconciler.SetAuthoritativeState(snapshot.ServerTick, snapshot.Own);
        int replayed = Reconciler.Reconcile(snapshot.ServerTick, snapshot.LastProcessedInput, Replay);
        if (replayed > 0)
            CorrectionSizes.Add(Reconciler.LastCorrectionDistance);
    }

    // Same Step as the live prediction, applied to the unacknowledged inputs on top of the server state.
    private UnitState Replay(UnitState authoritative, int lastProcessedTick, UnitInput[] inputs)
    {
        var state = authoritative;
        int count = Reconciler.PendingReplayCount;
        for (int i = 0; i < count; i++)
            state = UnitSim.Step(state, lastProcessedTick + 1 + i, inputs[i]);
        return state;
    }
}
