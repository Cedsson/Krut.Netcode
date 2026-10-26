using Krut.Netcode;
using LiteNetLib.Utils;

namespace PredictionDemo;

/// <summary>Game wire types start at NetworkMessages.GameTypeBase (16); the byte is part of the wire format.</summary>
public static class Wire
{
    public const byte Input = 16;
    public const byte Snapshot = 17;
    public const byte SyncRequest = 18;
    public const byte SyncResponse = 19;

    public static void Register()
    {
        NetworkMessages.RegisterGameMessage<InputMessage>(Input, NetworkDelivery.Unreliable);
        NetworkMessages.RegisterGameMessage<SnapshotMessage>(Snapshot, NetworkDelivery.Unreliable);
        NetworkMessages.RegisterGameMessage<SyncRequestMessage>(SyncRequest, NetworkDelivery.Unreliable);
        NetworkMessages.RegisterGameMessage<SyncResponseMessage>(SyncResponse, NetworkDelivery.Unreliable);
    }

    internal static void Put(NetDataWriter w, in UnitState s)
    {
        w.Put(s.X);
        w.Put(s.Y);
        w.Put(s.Vx);
        w.Put(s.Vy);
    }

    internal static UnitState Get(NetDataReader r) => new() { X = r.GetFloat(), Y = r.GetFloat(), Vx = r.GetFloat(), Vy = r.GetFloat() };
}

/// <summary>Client to server: the input for one tick.</summary>
public sealed class InputMessage : INetworkMessage
{
    public NetworkMessageType Type => NetworkMessageType.None;
    public byte WireType => Wire.Input;
    public NetworkDelivery Delivery => NetworkDelivery.Unreliable;

    public int Tick;
    public UnitInput Input;

    public void Write(NetDataWriter w)
    {
        w.Put(Tick);
        w.Put(Input.Ax);
        w.Put(Input.Ay);
    }

    public void Read(NetDataReader r)
    {
        Tick = r.GetInt();
        Input = new UnitInput { Ax = r.GetFloat(), Ay = r.GetFloat() };
    }
}

/// <summary>Server to client: the client's own unit, the other unit, and the last input tick the server applied.</summary>
public sealed class SnapshotMessage : INetworkMessage
{
    public NetworkMessageType Type => NetworkMessageType.None;
    public byte WireType => Wire.Snapshot;
    public NetworkDelivery Delivery => NetworkDelivery.Unreliable;

    public int ServerTick;
    public int LastProcessedInput;
    public UnitState Own;
    public UnitState Other;

    public void Write(NetDataWriter w)
    {
        w.Put(ServerTick);
        w.Put(LastProcessedInput);
        Wire.Put(w, Own);
        Wire.Put(w, Other);
    }

    public void Read(NetDataReader r)
    {
        ServerTick = r.GetInt();
        LastProcessedInput = r.GetInt();
        Own = Wire.Get(r);
        Other = Wire.Get(r);
    }
}

/// <summary>The engine has no time sync message; the game carries the probe itself.</summary>
public sealed class SyncRequestMessage : INetworkMessage
{
    public NetworkMessageType Type => NetworkMessageType.None;
    public byte WireType => Wire.SyncRequest;
    public NetworkDelivery Delivery => NetworkDelivery.Unreliable;

    public int ClientTick;
    public long Stamp;

    public void Write(NetDataWriter w)
    {
        w.Put(ClientTick);
        w.Put(Stamp);
    }

    public void Read(NetDataReader r)
    {
        ClientTick = r.GetInt();
        Stamp = r.GetLong();
    }
}

public sealed class SyncResponseMessage : INetworkMessage
{
    public NetworkMessageType Type => NetworkMessageType.None;
    public byte WireType => Wire.SyncResponse;
    public NetworkDelivery Delivery => NetworkDelivery.Unreliable;

    public int ClientTick;
    public int ServerTick;
    public long Stamp;

    public void Write(NetDataWriter w)
    {
        w.Put(ClientTick);
        w.Put(ServerTick);
        w.Put(Stamp);
    }

    public void Read(NetDataReader r)
    {
        ClientTick = r.GetInt();
        ServerTick = r.GetInt();
        Stamp = r.GetLong();
    }
}
