# Architecture diagrams

These are the same five diagrams as the pages of [architecture.drawio](architecture.drawio), written as Mermaid so GitHub renders them directly. The editable source is `architecture.drawio` (open it with [diagrams.net](https://app.diagrams.net) or the VS Code Draw.io extension); keep both in step when you change the code.

The groups follow the folders under `src/Krut.Netcode`: `Session/`, `Messages/`, `Time/`, `Prediction/`, `Simulation/`, `Diagnostics/`.

- [Overview](#overview)
- [Frame](#frame)
- [Prediction](#prediction)
- [Handshake](#handshake)
- [Wire format](#wire-format)

## Overview

Arrows mean "uses / calls / creates". A hollow arrowhead means "derives from". Dotted arrows are configuration or callbacks. Only the main dependencies are drawn (`NetworkError` and `NetworkSimRandom` are left out).

```mermaid
flowchart TB
    Game["Game code<br/>owns a NetworkServer or NetworkClient,<br/>calls Update(dt), registers and handles messages"]
    Game2["Game code (prediction loop)<br/>Predict, Reconcile, Push,<br/>TrySampleTime, RecordResponse"]
    LNL["LiteNetLib<br/>NetManager, INetEventListener,<br/>PacketLayerBase, ConnectionRequest"]

    subgraph Session["Session/ - transport and session"]
        direction TB
        NetworkServer["NetworkServer<br/>lobby, approval, Welcome + PlayerInfo"]
        NetworkClient["NetworkClient<br/>state machine, reconnect, SessionReady"]
        NetworkConnection["NetworkConnectionRequest<br/>NetworkConnectionResult"]
        NetworkSession["NetworkSession<br/>update loop, send/receive, peer table, events"]
        NetworkListener["NetworkListener<br/>internal INetEventListener,<br/>queues NetworkEvent"]
        NetworkPeerState["NetworkPeerState<br/>one per connected peer"]
        NetworkConfig["NetworkConfig<br/>tick rate, ports, timeouts, DatagramLayer"]
    end

    subgraph Messages["Messages/ - messages and wire"]
        direction TB
        NetworkMessages["NetworkMessages<br/>type registry + NetworkMessagePool#lt;T#gt;"]
        INetworkMessage["INetworkMessage<br/>Type, WireType, Delivery, Write, Read"]
        NetworkProtocol["NetworkProtocol.cs<br/>NetworkDelivery, NetworkChannels,<br/>NetworkMessageType"]
        NetworkCodec["NetworkCodec<br/>name, text, vector, quaternion encoding"]
        NetworkEnvelope["NetworkEnvelope<br/>PeerId, Message, Delivery, Time"]
    end

    subgraph Time["Time/"]
        direction TB
        NetworkClock["NetworkClock<br/>fixed tick accumulator"]
        NetworkTimeSync["NetworkTimeSync<br/>offset, RTT, client lead"]
    end

    subgraph Prediction["Prediction/ - lag primitives (game-driven)"]
        direction TB
        NetworkReconciler["NetworkReconciler#lt;TState,TInput#gt;<br/>rewind + replay, visual smoothing"]
        NetworkSnapshotInterpolator["NetworkSnapshotInterpolator#lt;TState#gt;<br/>render in the past"]
        NetworkStateRing["NetworkStateRing#lt;TEntry#gt;<br/>NetworkStateBlend, NetworkStateEntry#lt;T#gt;"]
    end

    subgraph Simulation["Simulation/ - link simulation"]
        direction TB
        NetworkSimulationProfile["NetworkSimulationProfile<br/>named presets"]
        NetworkSimulationOptions["NetworkSimulationOptions<br/>NetworkOutage"]
        NetworkSimulation["NetworkSimulation<br/>latency, jitter, loss, outages"]
        KrutEnv["KrutEnv<br/>KRUT_* environment variables"]
        NetworkSimulationStats["NetworkSimulationStats"]
        NetworkDelayQueue["NetworkDelayQueue<br/>internal, delays Send"]
        NetworkLinkModel["NetworkLinkModel<br/>per-link verdict"]
        NetworkDatagramChain["NetworkDatagramChain<br/>ordered PacketLayerBase list"]
        NetworkDatagramSimLayer["NetworkDatagramSimLayer<br/>datagram-level loss"]
    end

    subgraph Diagnostics["Diagnostics/"]
        direction TB
        NetworkStatistics["NetworkStatistics<br/>NetManager counters"]
        NetDatagramProbe["NetDatagramProbe<br/>byte / datagram counters (a PacketLayerBase)"]
    end

    NetworkServer -- derives --> NetworkSession
    NetworkClient -- derives --> NetworkSession
    NetworkServer -- approval --> NetworkConnection
    NetworkServer -- SanitizeName --> NetworkCodec
    NetworkSession <-- owns / receives events --> NetworkListener
    NetworkSession -- peer table --> NetworkPeerState
    NetworkSession -- reads --> NetworkConfig
    NetworkSession <-- NetManager --> LNL
    LNL -. callbacks .-> NetworkListener

    NetworkSession -- CreateFromWire, Recycle --> NetworkMessages
    NetworkSession -- raises MessageReceived --> NetworkEnvelope
    NetworkMessages -- creates, pools --> INetworkMessage
    NetworkMessages -- built-ins use --> NetworkCodec
    INetworkMessage -- Type, Delivery --> NetworkProtocol
    NetworkEnvelope -- wraps --> INetworkMessage

    NetworkSession -- Advance, ResetClockTo --> NetworkClock
    NetworkSession -- owns --> NetworkDelayQueue
    NetworkSession -- ApplySimulation --> NetworkSimulation
    NetworkSession -- DatagramChain --> NetworkDatagramChain
    NetworkSession -- Statistics --> NetworkStatistics
    NetworkConfig -. DatagramLayer .-> NetworkDatagramChain

    NetworkSimulationProfile -- ToOptions --> NetworkSimulationOptions
    NetworkSimulationOptions -- Create --> NetworkSimulation
    KrutEnv -- FromEnvironment --> NetworkSimulation
    NetworkSimulation -- configures --> NetworkLinkModel
    NetworkDelayQueue -- Submit --> NetworkLinkModel
    NetworkDelayQueue -- counts --> NetworkSimulationStats
    NetworkDatagramChain -- hosts --> NetworkDatagramSimLayer
    NetworkDatagramSimLayer -- Submit --> NetworkLinkModel

    NetworkSnapshotInterpolator -- buffer --> NetworkStateRing
    NetworkReconciler -- history --> NetworkStateRing

    Game -- Update, Send --> NetworkServer
    Game -- Update, Send --> NetworkClient
    Game -- RegisterGameMessage --> NetworkMessages
    Game2 --> NetworkReconciler
    Game2 --> NetworkSnapshotInterpolator
    Game2 --> NetworkTimeSync

    classDef session fill:#dae8fc,stroke:#6c8ebf,color:#000;
    classDef msg fill:#d5e8d4,stroke:#82b366,color:#000;
    classDef time fill:#fff2cc,stroke:#d6b656,color:#000;
    classDef lag fill:#ffe6cc,stroke:#d79b00,color:#000;
    classDef sim fill:#e1d5e7,stroke:#9673a6,color:#000;
    classDef ext fill:#f5f5f5,stroke:#666,color:#000;
    class NetworkServer,NetworkClient,NetworkConnection,NetworkSession,NetworkListener,NetworkPeerState,NetworkConfig session;
    class NetworkMessages,INetworkMessage,NetworkProtocol,NetworkCodec,NetworkEnvelope msg;
    class NetworkClock,NetworkTimeSync time;
    class NetworkReconciler,NetworkSnapshotInterpolator,NetworkStateRing lag;
    class NetworkSimulationProfile,NetworkSimulationOptions,NetworkSimulation,KrutEnv,NetworkSimulationStats,NetworkDelayQueue,NetworkLinkModel,NetworkDatagramChain,NetworkDatagramSimLayer,NetworkStatistics,NetDatagramProbe sim;
    class Game,Game2,LNL ext;
```

Colours: blue `Session/`, green `Messages/`, yellow `Time/`, orange `Prediction/`, purple `Simulation/` and `Diagnostics/`, grey external code.

## Frame

What `NetworkSession.Update(dt)` does, in order. `NetworkClient.Update` calls it first and then runs the reconnect timer.

```mermaid
sequenceDiagram
    autonumber
    participant Game
    participant S as NetworkSession (Server or Client)
    participant DQ as NetworkDelayQueue
    participant NM as LiteNetLib NetManager
    participant L as NetworkListener
    participant M as NetworkMessages
    participant C as NetworkClock

    Game->>S: Update(dt)
    Note over S,NM: 1  Release delayed sends
    S->>DQ: FlushDue()
    DQ-->>NM: peer.Send(data, channel, method) for every packet whose delay expired
    S->>NM: Flush(): TriggerUpdate() if _unflushed
    Note over S,NM: 2  Poll the socket (events run on the caller thread, UnsyncedEvents = false)
    S->>NM: PollEvents()
    NM->>L: OnPeerConnected / OnPeerDisconnected / OnNetworkReceive / OnNetworkError / OnConnectionRequest
    L-->>S: append NetworkEvent to _pending (receive: bytes copied into an ArrayPool buffer, connection request: HandleConnectionRequest at once)
    Note over S: 3  DrainEvents(): swap _pending and _draining
    Note over S: PeerConnected: AddPeer if new, OnPeerConnectedEvent, PeerConnected event.<br/>PeerDisconnected: RemovePeer, OnPeerDisconnectedEvent, PeerDisconnected event.<br/>SocketError: Error event.
    S->>M: Received: CreateFromWire(typeByte), then message.Read(reader)
    S->>Game: MessageReceived(NetworkEnvelope) (unknown type or short body: Error event instead)
    S->>M: Recycle(message, typeByte), buffer back to ArrayPool
    Note over S,C: 4  Advance the fixed-tick clock
    S->>C: Advance(dt, onTick)
    C-->>S: OnTick() once per whole tick (at most Config.MaxCatchUpTicks)
    Note over S,NM: 5  Flush
    S->>NM: Flush(): TriggerUpdate() if anything was sent
    Note over S: NetworkClient.Update only: then the reconnect timer (state Reconnecting: Connect() every ReconnectDelayMs)
```

## Prediction

The client and server loop per tick. Message bytes 16-19 below are the ones the `PredictionDemo` sample registers; a game registers its own.

```mermaid
sequenceDiagram
    autonumber
    participant G as Client game loop
    participant TS as NetworkTimeSync
    participant RC as NetworkReconciler#lt;TState,TInput#gt;
    participant IT as NetworkSnapshotInterpolator#lt;TState#gt;
    participant CL as NetworkClient
    participant SV as NetworkServer
    participant SG as Server game loop

    rect rgb(255, 242, 204)
    Note over G,SG: A  Time sync: how far the client runs ahead (about every 8 ticks, every 2 before the first sample)
    G->>TS: ShouldProbe(tick)
    G->>CL: Send(SyncRequest: ClientTick, Stamp), unreliable
    CL-->>SG: datagram
    SG-->>G: SyncResponse: ClientTick, ServerTick, Stamp (server replies at once)
    G->>TS: RecordResponse(clientTick, serverTick, rtt)
    Note over TS: min-RTT filter + median gives TickOffset, LatencyTicks,<br/>ClientLeadTicks = LatencyTicks + ExtraLeadTicks
    TS-->>G: ClientTickAheadOf(serverNow)
    G->>CL: ResetClockTo(target), once, before play starts
    end

    rect rgb(255, 230, 204)
    Note over G,SG: B  Every client tick
    G->>RC: Predict(tick, input): records state and input, runs Step
    G->>CL: Send(Input: tick, input), unreliable
    CL-->>SG: datagram
    Note over SG: on its tick: apply the stored input (repeat the last one if late), Step, record the truth
    SG->>SV: Send(Snapshot: ServerTick, LastProcessedInput, states) every SnapshotEveryTicks
    SV-->>G: datagram, raised through MessageReceived
    end

    rect rgb(255, 230, 204)
    Note over G,SG: C  Own unit: reconcile (rewind + replay)
    G->>RC: SetAuthoritativeState(serverTick, state)
    G->>RC: Reconcile(serverTick, lastProcessedInputTick, replay)
    Note over RC: drops acknowledged inputs, starts from the server state,<br/>replays the unacknowledged inputs through the replay callback
    RC-->>G: PredictedState corrected, SmoothedState / RenderState blend the error
    G->>RC: UpdateRenderState(dt) every frame
    end

    rect rgb(255, 230, 204)
    Note over G,SG: D  Other units: interpolate in the past
    G->>IT: Push(serverTick, otherState)
    G->>IT: TrySampleTime(ServerTickFor(tick) - DelayTicks) or TrySample(currentTick)
    IT-->>G: state + NetworkSampleMode (Interpolated, Extrapolated, Held ...)
    end
```

## Handshake

Connect, approval, `Welcome`, `SessionReady`, reject and reconnect.

```mermaid
sequenceDiagram
    autonumber
    participant CG as Client game
    participant CC as NetworkClient
    participant NM as LiteNetLib (network)
    participant SS as NetworkServer
    participant SG as Server game (ConnectionApproval)

    Note over CG,SG: 1  Connect
    CG->>CC: Connect(address, port, playerName)
    Note over CC: StartListener, state = Connecting.<br/>Payload: ApplicationId (string), ProtocolVersion (int), name (string)
    CC->>NM: ConnectTo(endPoint, payload)
    NM->>SS: connection request (resent every ConnectRequestIntervalMs, up to ConnectRequestAttempts)

    Note over CG,SG: 2  Server decides inside OnConnectionRequest
    Note over SS: HandleConnectionRequest:<br/>ApplicationId differs: Reject() silently.<br/>ProtocolVersion differs: Reject(reason).
    SS->>SG: ConnectionApproval(NetworkConnectionRequest) if set, else ClientCount >= MaxClients: Reject
    SG-->>SS: NetworkConnectionResult.Accept() or Reject(reason)

    alt 3a  Accepted
        Note over SS: request.Accept(), AddPeer, DescribePeer(name, JoinTick)
        SS->>NM: connected
        NM-->>CC: PeerConnected (state still Connecting)
        Note over SS: next Update: DrainEvents, OnPeerConnectedEvent
        SS->>CC: Welcome (reliable): PeerId, TickRate, ServerTick, PlayerName
        SS->>CC: PlayerInfo for every peer incl. the new one, PlayerInfo of the new peer to the others, SystemChat "name joined."
        Note over CC: HandleMessage(Welcome): SetLocalPeerId, ResetClockTo(ServerTick), state = Connected
        CC-->>CG: SessionReady event
    else 3b  Rejected
        SS->>CC: disconnect: ConnectionRejected + reason text
        Note over CC: state = Offline (never retried), the server's reason text wins
        CC-->>CG: Closed(DisconnectReason, text)
    end

    Note over CG,SG: 4  Connection lost later (timeout, server closed, ...)
    Note over CC: OnPeerDisconnectedEvent: SetLocalPeerId(-1).<br/>Was Connected: attempts = 0.<br/>AutoReconnect on: state = Reconnecting. Off, or rejected: state = Offline.
    CC-->>CG: Closed(DisconnectReason, text)
    loop while state = Reconnecting
        Note over CC: Update: after ReconnectDelayMs, StopListener, Connect(address, port), attempts + 1
        CC->>NM: ConnectTo(endPoint, payload), repeat from step 1
    end
    Note over CG,SG: 5  Give up after Config.MaxConnectAttempts failed retries in a row
    Note over CC: next disconnect with attempts >= MaxConnectAttempts: state = Offline, no more retries
    CC-->>CG: Error: NotConnected (Gave up reconnecting to address:port ...), then Closed(DisconnectReason, text)
```

## Wire format

One message is one LiteNetLib packet payload: a type byte followed by the message body.

```mermaid
flowchart LR
    subgraph Packet["Packet payload  (NetworkSession.SendInternal)"]
        direction LR
        H["LiteNetLib packet header<br/>added by LiteNetLib"] --> T["type byte<br/>1 byte"] --> B["message body<br/>INetworkMessage.Write(NetDataWriter)<br/>no length prefix"]
    end

    subgraph Types["Type byte"]
        direction LR
        T0["0 None<br/>not a valid wire value"] ~~~ T1["1 Welcome<br/>2 SystemChat<br/>3 Chat<br/>4 PlayerInfo"] ~~~ T5["5 - 15<br/>reserved"] ~~~ T16["16 - 255<br/>game messages<br/>RegisterGameMessage#lt;T#gt;(byte, delivery)"]
    end

    T -.-> Types

    classDef bad fill:#f8cecc,stroke:#b85450,color:#000;
    classDef built fill:#dae8fc,stroke:#6c8ebf,color:#000;
    classDef game fill:#d5e8d4,stroke:#82b366,color:#000;
    classDef ext fill:#f5f5f5,stroke:#666,color:#000;
    classDef res fill:#fff,stroke:#999,color:#000;
    class H ext;
    class T,T1 built;
    class B,T16 game;
    class T0 bad;
    class T5 res;
```

The receiver calls `NetworkMessages.CreateFromWire(typeByte)`, then `Read(reader)`. An unknown byte raises an `UnknownMessageType` error and keeps the connection; a body that is too short raises `MalformedMessage`. An unreliable message larger than the MTU raises `PacketTooLarge`.

### Message bodies

Fields appear in `Write` order. `int` and `float` are 4 bytes, `long` is 8 bytes, `string` is length-prefixed UTF-8 (names at most 32 characters, text at most 512).

| Byte | Message | Delivery | Body |
|---|---|---|---|
| 1 | `WelcomeMessage` | ReliableOrdered | `int PeerId`, `int TickRate`, `int ServerTick`, `string PlayerName` |
| 2 | `SystemChatMessage` | ReliableOrdered | `string Text` |
| 3 | `ChatMessage` | ReliableOrdered | `string Text` |
| 4 | `PlayerInfoMessage` | ReliableOrdered | `int PeerId`, `int JoinTick`, `string PlayerName` |
| 16 | PredictionDemo `InputMessage` | Unreliable | `int Tick`, `float Ax`, `float Ay` |
| 17 | PredictionDemo `SnapshotMessage` | Unreliable | `int ServerTick`, `int LastProcessedInput`, `UnitState Own`, `UnitState Other` (4 floats each) |
| 18 | PredictionDemo `SyncRequestMessage` | Unreliable | `int ClientTick`, `long Stamp` |
| 19 | PredictionDemo `SyncResponseMessage` | Unreliable | `int ClientTick`, `int ServerTick`, `long Stamp` |

The connection request carries a raw payload that is not a message: `string ApplicationId`, `int ProtocolVersion`, `string name`. It is read in `NetworkServer.HandleConnectionRequest`.

### Channels

`NetManager.ChannelsCount` is `NetworkChannels.Count` (4). `NetworkChannels.For` only ever returns channel 1 or 2, so channels 0 and 3 are reserved constants today. Delayed sends from `NetworkDelayQueue` use the same channel and method.

| Channel | Name | `NetworkDelivery` routed here | LiteNetLib `DeliveryMethod` | Used for |
|---|---|---|---|---|
| 0 | Input | none (constant only) | - | reserved |
| 1 | Snapshot | Unreliable | Unreliable | every unreliable message (input, snapshots, time sync in the sample) |
| 2 | Events | ReliableOrdered, ReliableUnordered | ReliableOrdered / ReliableUnordered | built-in messages and reliable game messages |
| 3 | Control | none (constant only) | - | reserved |
