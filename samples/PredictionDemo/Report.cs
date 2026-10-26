using System.Numerics;
using Krut.Netcode;

namespace PredictionDemo;

/// <summary>Compares what each client believed with what the server actually computed, then prints a table.</summary>
public sealed class Report
{
    private readonly DemoServer _server;
    private readonly DemoClient[] _clients;
    private readonly double _playSeconds;
    private readonly double _totalSeconds;
    private readonly List<string> _rows = new();

    public string? Failure { get; private set; }

    private Report(DemoServer server, DemoClient[] clients, double playSeconds, double totalSeconds)
    {
        _server = server;
        _clients = clients;
        _playSeconds = playSeconds;
        _totalSeconds = totalSeconds;
    }

    public static Report Build(DemoServer server, DemoClient[] clients, double playSeconds, double totalSeconds)
    {
        var report = new Report(server, clients, playSeconds, totalSeconds);
        report.Fill();
        return report;
    }

    public void Print()
    {
        Console.WriteLine();
        Console.WriteLine($"{"",-44}{"client 0",12}{"client 1",12}");
        foreach (var row in _rows)
            Console.WriteLine(row);
    }

    private void Row(string label, Func<int, string> cell) =>
        _rows.Add($"{label,-44}{cell(0),12}{cell(1),12}");

    private void Fill()
    {
        var truthTicks = new List<(float open, float predicted)>[2];
        var remoteErrors = new List<float>[2];
        for (int i = 0; i < 2; i++)
        {
            truthTicks[i] = PredictionErrors(i);
            remoteErrors[i] = RemoteErrors(i);
        }

        Row("measured RTT, min / max (ms)", i =>
            $"{_clients[i].Sync.MinRoundTripSeconds * 1000:0}/{_clients[i].Sync.MaxRoundTripSeconds * 1000:0}");
        Row("clock lead (ticks)", i => _clients[i].Sync.ClientLeadTicks.ToString());
        Row("packets dropped by link, up / down (%)", i =>
            $"{LossPercent(_clients[i].Net.SimulationStats):0.0}/{LossPercent(_server.Net.SimulationStats):0.0}");
        Row("inputs repeated / late at server", i => $"{_server.Slots[i].Repeated}/{_server.Slots[i].Late}");
        _rows.Add("");
        Row("error vs server, no reconcile: mean (mm)", i => $"{Mean(truthTicks[i], t => t.open):0.0}");
        Row("error vs server, no reconcile: max (mm)", i => $"{Max(truthTicks[i], t => t.open):0.0}");
        Row("error vs server, reconciled: mean (mm)", i => $"{Mean(truthTicks[i], t => t.predicted):0.0}");
        Row("error vs server, reconciled: max (mm)", i => $"{Max(truthTicks[i], t => t.predicted):0.0}");
        Row("error at ack, before reconcile: mean (mm)", i => $"{Mean(_clients[i].ErrorBeforeReconcile):0.0}");
        Row("error at ack, before reconcile: max (mm)", i => $"{Max(_clients[i].ErrorBeforeReconcile):0.0}");
        Row("correction applied per snapshot: mean (mm)", i => $"{Mean(_clients[i].CorrectionSizes):0.0}");
        _rows.Add("");
        Row("snapshots reconciled (rollbacks)", i => _clients[i].Reconciler.ReconcileCount.ToString());
        Row("inputs replayed, total / per rollback", i =>
        {
            var r = _clients[i].Reconciler;
            return $"{r.TotalReplayedInputs}/{(r.ReconcileCount == 0 ? 0 : r.TotalReplayedInputs / r.ReconcileCount)}";
        });
        Row("rollback misses / ignored snapshots", i =>
            $"{_clients[i].Reconciler.RollbackMissCount}/{_clients[i].Reconciler.IgnoredServerStateCount}");
        _rows.Add("");
        foreach (var mode in Enum.GetValues<NetworkSampleMode>())
        {
            if (mode != NetworkSampleMode.Empty)
                Row($"remote unit samples: {mode}", i => _clients[i].SampleModes[(int)mode].ToString());
        }

        Row("remote unit error vs server: mean (mm)", i => $"{Mean(remoteErrors[i]):0.0}");
        Row("remote unit error vs server: max (mm)", i => $"{Max(remoteErrors[i]):0.0}");
        Row("remote view delay (ms)", i => $"{(_clients[i].Sync.LatencyTicks + DemoClient.InterpolationDelayTicks) * 1000 / UnitSim.TickRate}");
        _rows.Add("");
        Row("bytes/s sent (client)", i => $"{_clients[i].Net.Statistics.BytesSent / _totalSeconds:0}");
        Row("bytes/s received (client)", i => $"{_clients[i].Net.Statistics.BytesReceived / _totalSeconds:0}");
        _rows.Add($"{"server bytes/s sent / received",-44}{_server.Net.Statistics.BytesSent / _totalSeconds,12:0}{_server.Net.Statistics.BytesReceived / _totalSeconds,12:0}");
        _rows.Add($"{"play time (s)",-44}{_playSeconds,12:0.0}");

        Failure = Verdict(truthTicks, remoteErrors);
        _rows.Add("");
        _rows.Add(Failure is null ? "PASS: prediction stays close to the server, replay corrects the rest." : "FAIL: " + Failure);
    }

    private string? Verdict(List<(float open, float predicted)>[] truth, List<float>[] remote)
    {
        for (int i = 0; i < 2; i++)
        {
            if (_clients[i].Reconciler.ReconcileCount == 0)
                return $"client {i} never reconciled";
            if (truth[i].Count == 0 || remote[i].Count == 0)
                return $"client {i} produced no samples";
            if (Mean(truth[i], t => t.predicted) > Mean(truth[i], t => t.open) + 1f)
                return $"client {i}: reconciling did not beat open-loop prediction";
            if (_clients[i].Net.UnknownMessageCount + _clients[i].Net.MalformedMessageCount > 0)
                return $"client {i} received messages it could not decode";
        }

        return _server.Net.UnknownMessageCount + _server.Net.MalformedMessageCount > 0
            ? "the server received messages it could not decode"
            : null;
    }

    // Per predicted tick: distance from the server's real state at that tick, with and without reconciliation.
    private List<(float, float)> PredictionErrors(int i)
    {
        var result = new List<(float, float)>();
        var truth = _server.Slots[i].Truth;
        foreach (var (tick, predicted) in _clients[i].Predicted)
        {
            if (truth.TryGetValue(tick, out var real))
                result.Add((UnitSim.Distance(_clients[i].OpenLoop[tick], real), UnitSim.Distance(predicted, real)));
        }

        return result;
    }

    // Where the interpolator drew the other unit versus where it really was at that render tick.
    private List<float> RemoteErrors(int i)
    {
        var result = new List<float>();
        var truth = _server.Slots[1 - i].Truth;
        foreach (var (renderTick, pos) in _clients[i].RemoteSamples)
        {
            int lower = (int)Math.Floor(renderTick);
            if (!truth.TryGetValue(lower, out var a) || !truth.TryGetValue(lower + 1, out var b))
                continue;

            var real = Vector2.Lerp(a.Pos, b.Pos, (float)(renderTick - lower));
            result.Add(Vector2.Distance(pos, real));
        }

        return result;
    }

    private static double LossPercent(NetworkSimulationStats stats) =>
        stats.Submitted == 0 ? 0 : 100.0 * stats.Dropped / stats.Submitted;

    private static float Mean(List<float> values) => values.Count == 0 ? 0 : values.Average() * 1000;

    private static float Max(List<float> values) => values.Count == 0 ? 0 : values.Max() * 1000;

    private static float Mean<T>(List<T> values, Func<T, float> pick) => values.Count == 0 ? 0 : values.Average(pick) * 1000;

    private static float Max<T>(List<T> values, Func<T, float> pick) => values.Count == 0 ? 0 : values.Max(pick) * 1000;
}
