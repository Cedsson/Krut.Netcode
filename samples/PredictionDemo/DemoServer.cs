using Krut.Netcode;

namespace PredictionDemo;

/// <summary>Authoritative side: steps every unit once per tick with the input that arrived for that tick.</summary>
public sealed class DemoServer
{
    public const int SnapshotEveryTicks = 2;

    public sealed class Slot
    {
        public int PeerId;
        public int Index;
        public UnitState State;
        public UnitInput Last;
        public bool Started;
        public readonly Dictionary<int, UnitInput> Pending = new();
        public readonly Dictionary<int, UnitState> Truth = new();
        public int Applied, Repeated, Late;
    }

    public readonly NetworkServer Net;
    public readonly List<Slot> Slots = new();
    private int _lastTick;

    public DemoServer(NetworkConfig config)
    {
        Net = new NetworkServer(config);
        Net.PeerConnected += peer =>
            Slots.Add(new Slot { PeerId = peer.Id, Index = Slots.Count, State = UnitSim.Spawn(Slots.Count) });
        Net.MessageReceived += OnMessage;
    }

    public bool Start() => Net.Start();

    public void Update(float dt)
    {
        Net.Update(dt);
        while (_lastTick < Net.CurrentTick)
            Tick(++_lastTick);
    }

    private void OnMessage(NetworkEnvelope e)
    {
        var slot = Slots.Find(s => s.PeerId == e.PeerId);
        if (slot is null)
            return;

        switch (e.Message)
        {
            case InputMessage input:
                // A tick already simulated cannot be applied any more; the server repeated the last input instead.
                if (input.Tick <= _lastTick)
                    slot.Late++;
                else
                    slot.Pending[input.Tick] = input.Input;
                break;
            case SyncRequestMessage probe:
                var reply = NetworkSession.Rent<SyncResponseMessage>();
                reply.ClientTick = probe.ClientTick;
                reply.ServerTick = Net.CurrentTick;
                reply.Stamp = probe.Stamp;
                Net.Send(e.PeerId, reply);
                break;
        }
    }

    private void Tick(int tick)
    {
        foreach (var slot in Slots)
        {
            if (slot.Pending.Remove(tick, out var input))
            {
                slot.Last = input;
                slot.Started = true;
                slot.Applied++;
            }
            else if (slot.Started)
            {
                slot.Repeated++;
            }

            slot.State = UnitSim.Step(slot.State, tick, slot.Last);
            slot.Truth[tick] = slot.State;
        }

        if (tick % SnapshotEveryTicks != 0 || Slots.Count < 2)
            return;

        foreach (var slot in Slots)
        {
            var snapshot = NetworkSession.Rent<SnapshotMessage>();
            snapshot.ServerTick = tick;
            snapshot.LastProcessedInput = tick;
            snapshot.Own = slot.State;
            snapshot.Other = Slots[1 - slot.Index].State;
            Net.Send(slot.PeerId, snapshot);
        }
    }
}
