# Krut.Netcode guide

What the library does, how the pieces fit, and the facts that are not obvious from the source. Read
the source (`src/Krut.Netcode`) for member lists; every type named here exists there.

## 1. Scope

Provided: UDP transport on LiteNetLib 2.1.4 (reliable/unreliable, sequencing), `NetworkServer` /
`NetworkClient` with a handshake (app id, protocol version, name), rejection with a reason and
library-level reconnect, a hand-written pooled message system, four channels, a fixed tick clock,
client lag primitives (time sync, snapshot interpolation, reconciliation), a link simulator and
diagnostics.

Not provided (game side): what a message means, spawn/replication, snapshot format and delta
encoding, headless server executable, matchmaking, lobby, anti-cheat. The library sends bytes, the
game decides the meaning.

Everything runs on the thread that calls `session.Update(deltaSeconds)` (the only mandatory call;
LiteNetLib events are raised inside its `PollEvents`, queued by `NetworkListener` and drained at its start). No locks, no
background threads. `NetworkServer`/`NetworkClient` are not `sealed`: a game subclasses and
overrides the `protected virtual OnTick()` to run once per whole tick.

## 2. Core types

- **`NetworkSession`** (abstract): peers, tick clock, dispatch, `Send`/`Broadcast`, static
  `Rent<T>()`, `ApplySimulation`, `ResetClockTo(serverTick)`, counters
  (`UnknownMessageCount`, `MalformedMessageCount`, `DroppedPacketCount`). Events:
  `PeerConnected`, `PeerDisconnected`, `MessageReceived`, `Error`.
- **`NetworkServer`**: `Start()` (false if the port cannot be bound), `Stop()`,
  `ConnectionApproval` (set before `Start`), `DisconnectPeer`, `DisconnectAll`. Enforces `MaxClients` (only when no `ConnectionApproval` is set; with one, the callback decides).
- **`NetworkClient`**: `Connect(address, port, name)`, `Disconnect`, `Stop`, `SetPlayerName`,
  `State` (`Offline`/`Connecting`/`Connected`/`Reconnecting`), `IsSessionReady`, events
  `SessionReady`, `Closed`. The connection is up long before `Welcome` arrives: replicate only
  after `SessionReady`, not `PeerConnected`.
- **`NetworkPeerState`**: id, sanitized name, `Ping`, `Rtt`, packet counters,
  `PacketLossPercent`, `ReliableQueueLength` (growing = overload), `Tag` for game data.
- **`NetworkEnvelope`**: a received message (`PeerId`, `Message`, `Delivery`). `NetworkError`
  carries a `NetworkErrorCode` (`Socket`, `ConnectionRejected`, `MalformedMessage`,
  `UnknownMessageType`, `PacketTooLarge`, `PeerUnknown`, `NotConnected`, `StartFailed`). The
  library never throws at game logic for network problems and never logs; everything comes via
  `Error`.

Handshake: the client sends `ApplicationId`, `ProtocolVersion` and a name. The server validates all
three plus `MaxClients` and the approval callback, then accepts or rejects with a readable reason.
On accept it sends `Welcome` (peer id, tick rate, server tick); the client resyncs its clock to
that tick.

LiteNetLib's own reconnect is disabled; `NetworkClient` retries itself (`AutoReconnect`,
`ReconnectDelayMs`, `MaxConnectAttempts`) so the game can show "reconnecting".

### `NetworkConfig`

Data-driven; `Validate()` (run by the session constructor) clamps broken values so a bad settings
file cannot stop the layer from starting.

| Property | Default | Controls |
|---|---|---|
| `ApplicationId` | `"krut.netcode"` | Rejects another application on the same port |
| `ProtocolVersion` | `1` | Mismatch is rejected with an explanation |
| `Port` | `25555` | UDP port (server bind / client target) |
| `TickRate` | `30` | Fixed session tick |
| `MaxClients` | `16` | Full lobby is rejected |
| `EnableStatistics` | `true` | Bytes/packets/loss per peer |
| `MtuDiscovery` | `true` | Largest MTU per client |
| `PingIntervalMs` / `DisconnectTimeoutMs` | `1000` / `8000` | Ping cadence / silence before dead |
| `ReconnectDelayMs` / `MaxConnectAttempts` / `AutoReconnect` | `500` / `8` / `false` | Client reconnect: retry delay / retries before giving up (client goes `Offline`, error `NotConnected`) / on-off |
| `IPv6Support` | `true` | Dual-stack |
| `MaxCatchUpTicks` | `5` | Cap on ticks released in one frame |

### `NetworkCodec`

`WritePosition`/`ReadPosition` (three shorts over a world extent, ~2 cm; default extent 32000),
`WriteRotation`/`ReadRotation` (four shorts, normalized on read), `WriteVector`/`ReadVector` (three
floats, rare events), `WriteName`/`SanitizeName` (max 32 chars, control characters stripped),
`WriteText` (max 512). Names and text are sanitized on the way in so a bad client cannot inject
control characters or blow up packet size. The codec uses `System.Numerics` `Vector3`/`Quaternion`;
the library has no dependency on any game engine.

## 3. Messages

Every message implements `INetworkMessage` and is serialized by hand through
`NetDataWriter`/`NetDataReader`. Why hand-written:

- **No reflection.** The library is Native-AOT safe; LiteNetLib's reflection-based `NetSerializer`
  is deliberately never used. The wire type byte is a property on the message, not derived from
  the type at runtime.
- **Computable size.** What goes on the wire is exactly the `Write` body, so bandwidth is
  reasoned about, not discovered.
- **Forward compatibility.** An unknown type byte is counted and skipped instead of kicking an
  older client (`UnknownMessageCount`, `NetworkError.UnknownMessageType`; the connection stays).

Library types occupy bytes 1-15 (`Welcome`, `SystemChat`, `Chat`, `PlayerInfo`).
`NetworkMessageType` is a closed enum. A game registers its own types on bytes **16-255** and edits
no library file:

```csharp
public sealed class MyMessage : INetworkMessage
{
    public NetworkMessageType Type => NetworkMessageType.None;   // game message
    public byte WireType => 16;                                  // stable wire format, add last
    public NetworkDelivery Delivery => NetworkDelivery.Unreliable;
    public int Tick { get; set; }
    public void Write(NetDataWriter w) { w.Put(Tick); }          // field order identical in Read
    public void Read(NetDataReader r)  { Tick = r.GetInt(); }
}
// once at startup (registration is per process):
NetworkMessages.RegisterGameMessage<MyMessage>(16, NetworkDelivery.Unreliable);
```

A parameterless constructor is required (pooling). The registration also gives the return path to
the right pool: without it instances leak instead of being reused. `Delivery` on the message
decides the channel; the registration value is only a default. `ClearGameMessages()` resets
registrations (restart in one process).

**Pool ownership.** A received message is recycled immediately after `MessageReceived` returns:
copy what you keep. `Send`/`Broadcast` reuse the message afterwards: do not write to it again.
Pools are static and shared between sessions (a client and server in one process share them).
Outgoing packets reuse one `NetDataWriter`; steady state allocates nothing per packet (covered by
`NetworkSessionAllocationTests`).

## 4. Channels and delivery

| Channel | Value | Delivery | Use |
|---|---|---|---|
| `Input` | 0 | Unreliable | client input per tick, sent redundantly |
| `Snapshot` | 1 | Unreliable | server state per tick |
| `Events` | 2 | Reliable ordered | discrete game events |
| `Control` | 3 | Reliable | handshake, clock sync, kick |

One LiteNetLib queue each, so a lost input packet never waits behind an event.
`NetworkChannels.For(delivery)` currently routes everything unreliable to `Snapshot` and everything
reliable to `Events`; `Input` and `Control` have no automatic routing.

Choose `Delivery` per message by what it represents: state of this tick -> `Unreliable` (a late
packet is worthless, the next replaces it); something that must arrive -> `ReliableOrdered`;
large blobs where an old piece must not block a new one -> `ReliableUnordered`. Unreliable for a
must-arrive message fails silently; reliable for fast state queues behind any loss
(`ReliableQueueLength` shows it).

## 5. The tick clock

`NetworkClock` (`NetworkSession.CurrentTick`) counts whole ticks at `NetworkConfig.TickRate`
independent of frame rate and releases one `OnTick` per whole elapsed tick. A hitch produces no
packet rather than a burst, bounded by `MaxCatchUpTicks` (an alt-tab or loading screen does not
release hundreds of ticks). `TickProgress` (0-1) and `Time` let rendering interpolate between
ticks: with 30 Hz snapshots and 144 Hz frames use `NetworkSnapshotInterpolator.TrySampleTime`.
`NetworkClock(tickRate, maxCatchUp)` can also be used standalone with `Advance(dt, onTick)`.

**The tick rate must match the physics timestep**, not be chosen freely. Reconciliation replays
inputs and only gives the server's result if both sides take exactly the same steps; a tick that
spans two physics steps on one side makes every rollback introduce a new error.

## 6. Client lag primitives

All standalone from `NetworkSession`: generic over the game's `unmanaged` state structs, blind to
peers/messages/channels, zero allocation per frame, no reflection (AOT-safe), testable without a
network. The sample in `samples/PredictionDemo` wires them to a session.

Per frame: the local entity goes through `NetworkReconciler` (predict, rewind, replay), other
entities through `NetworkSnapshotInterpolator` (drawn in the past). They are different problems.

- **`NetworkTimeSync`** (client). NTP-like: the client sends its tick, the server answers with
  its own, the client keeps the last 16 samples (`RecordRequest`/`RecordResponse`). Offset =
  **median** of `serverTick - clientTick - rtt/2` over samples that pass a minimum-RTT filter
  (samples above `MaxRttRatio` 2.0 x the best are dropped: a high RTT measures a queue).
  `LatencyTicks` comes from the minimum RTT. A plain mean lets one 200 ms packet move the
  estimate permanently; `NaiveAverageOffsetTicks` exists only for comparison, do not use it.
  `ClientLeadTicks` = `LatencyTicks + ExtraLeadTicks`; `ClientTickAheadOf(serverTick)` is the tick
  the clock should be set to (feed `ResetClockTo`). The library sends no time-sync message: the
  game defines its own.
- **`NetworkStateRing<TEntry>`**. Fixed-size ring of tick-stamped entries, strictly increasing
  ticks (late/duplicate `Push` returns false). Bounded memory; substrate for the two below.
- **`NetworkStateBlend`**. Reflection-free blending of `unmanaged` states, assuming 4-byte
  components (`float`/`int`/`uint`; not `bool`/enum: set the interpolator's `Lerp` /
  `Extrapolate` delegates instead). Never blend rotations component-wise (the midpoint of unit
  quaternions is not unit; `q` and `-q` are the same rotation): use `SlerpRotation`.
- **`NetworkSnapshotInterpolator<TState>`** (remote entities). Draws `DelayTicks` (2-3) in the
  past because interpolation needs two samples and a single lost packet must not be visible.
  `Push(tick, state)` on each received snapshot, `TrySampleTime(renderTick, ...)` per frame with
  `CurrentTick + TickProgress - DelayTicks`. Short of data it extrapolates up to
  `MaxExtrapolationTicks` (3) then **holds**; an extrapolated state may be shown, never forwarded.
  `LastSampleMode` (`Exact`/`Interpolated`/`Extrapolated`/`Held`), `LateSnapshotCount`,
  `OverwrittenSnapshotCount` feed a debug overlay.
- **`NetworkReconciler<TState, TInput>`** (local entity). The server snapshot is the truth: rewind
  to the last acknowledged input tick, replay every later input. Stores pre-input state and input
  per tick (128-entry ring by default), so replay is the same computation as the first time.
  `Step` `(state, tick, input) -> state` drives both `Predict` and replay and must be pure.
  `SetAuthoritativeState` returns false for a duplicate/reordered snapshot; `Reconcile(serverTick,
  lastProcessedInputTick, replay)` returns the replayed count. Physics reads `PredictedState`
  (corrected directly); rendering reads `SmoothedState` (eased by `UpdateRenderState(dt)`). A
  correction larger than `VisualSmoothingSettings.MinCorrectionDistance` (1.0 unit) is teleported,
  not eased. Defaults: `MaxCorrectionSeconds` 0.15, `MinCorrectionSeconds` 0.05, `UseEaseOut`
  true. `Reconcile` bails (state accepted, history cleared, `RollbackMissCount++`) when the
  rollback target is older than the retained history. `IsInputBufferFull` /
  `UnacknowledgedInputCount` tell the client to stop sending.

## 7. Diagnostics and the link simulator

`NetworkSimulation` applies delay, jitter, loss, bursts, reorder, duplicates, a bandwidth cap and
scheduled outages to a real session in every build variant (LiteNetLib's built-in simulation is
stripped from the Release NuGet package, so the library delays and drops itself in
`NetworkSession`). It affects one direction per session: the packets that session sends (client =
uplink, server = downlink); apply one to both sessions to degrade both directions. Decisions are
made above LiteNetLib when a message is handed to `Send`, so a reliable message that "drops" is
delivered late instead of vanishing. Unset variables mean no simulation and the production code
path. Each link has a seeded PRNG stream: the same seed and the same send sequence give the same
verdicts.

| Variable | Meaning |
|---|---|
| `KRUT_NET_PROFILE` | named link, round-trip terms: `clean`, `lan`, `gate` (100 ms, 2 %), `stress` (180 ms, 2 %), `bad`, `blackout`, `thin` |
| `KRUT_NET_LAG_MS` | delay in ms, round trip; each side gets half. Overrides the profile |
| `KRUT_NET_JITTER_MS` | latency varies by +/- this (round trip) |
| `KRUT_NET_LOSS` | loss fraction 0.0-1.0, e.g. `0.02` |
| `KRUT_NET_BURST` | mean loss burst length in messages |
| `KRUT_NET_REORDER` / `KRUT_NET_DUP` | reorder / duplicate fraction |
| `KRUT_NET_BW_KBIT` | cap on the server's sends in kbit/s |
| `KRUT_NET_OUTAGES` | `start:duration` seconds pairs, e.g. `15:1,30:5` |
| `KRUT_NET_SEED` | PRNG seed (a default is used, so unseeded runs repeat) |
| `KRUT_NET_DATAGRAM` | `1` moves loss, bursts and outages to the datagram stage |

`NetworkSimulation.FromEnvironment(NetworkSimulationSide)` reads the variables (round-trip values,
half per side) and clamps unreadable ones instead of throwing. `Create(lag, jitter, loss)` builds
one in code from one-way values, `session.ApplySimulation(simulation)` applies it.
`NetworkSimulationProfile` holds the named links.

The tests (`tests/Krut.Netcode.Tests`) are xunit; run them with `dotnet test`.

### Troubleshooting

| Symptom | Likely cause | Action |
|---|---|---|
| Up but no messages | Waiting on `PeerConnected` | Wait for `SessionReady` |
| Messages disappear | Unreliable for must-arrive data | `ReliableOrdered` |
| Stuck in `Connecting` | Port, app id, version, firewall | Check the server; try `127.0.0.1` |
| `Offline` immediately | Rejected | Read the `Closed` text (server's explanation) |
| `N unknown` rises | Newer client or unregistered type | Register the type; type byte last |
| `N malformed` rises | `Write` and `Read` field order differ | Make them identical |
| `PacketTooLarge` | Message too big for unreliable | Split or use reliable |
| Clock drifts from server | No/rare time sync, or mean used | Use `NetworkTimeSync` median |
| Things jump instead of easing | `MinCorrectionDistance` too low/high | Tune (~0.5-1.5 units for a car) |
| `LastSampleMode == Held` | Network slower than render delay | Raise `DelayTicks`/`MaxExtrapolationTicks` |
| `RollbackMissCount` rises | `lastProcessedInputTick` older than oldest state | Raise history capacity; do not run too far ahead |
| Nothing ever arrives in a test | `session.Update` never called | Events are delivered only from `Update` |
| Server does not start | Port taken | `Start()` false, `Error` = `StartFailed` |
| Inputs arrive late on the server | `ExtraLeadTicks` too low for the link | Raise it (the sample uses about 4 at 100 ms RTT / 2 % loss; default is 2) |

## 8. Headless and determinism

Nothing in the library needs a window, a graphics device or a game engine: the session, server,
client, clock, config, messages and simulation are plain .NET, and the codec, ring, blend,
interpolator and reconciler need only `System.Numerics` types. A dedicated server is a console
loop that calls `Update`.

Physics determinism is the game's responsibility and decides whether the primitives above are
worth anything: only tick numbers and a fixed `dt`, no `DateTime`/`Stopwatch`/frame time, no
unseeded `Random`, fixed iteration order (no `Dictionary`/`HashSet` iteration), no hidden static
state, same build on server and client. Bit-exact is not required for reconciliation (small errors
get corrected) but fewer divergences mean fewer visible corrections. The library has no state hash,
snapshot format or delta encoding; the game owns those.

Dependency: LiteNetLib 2.1.4 (MIT); only the AOT-safe core (`NetDataWriter`/`NetDataReader`) is
used, never the reflection-based `NetSerializer`.

## Releasing

1. Set the version in your head: `vX.Y.Z` (for example `v0.2.0`; a suffix such as `v0.2.0-beta.1` makes a prerelease).
2. Tag the commit on `main` and push the tag: `git tag vX.Y.Z && git push origin vX.Y.Z`.
3. The Release workflow builds, tests, packs with the version from the tag, creates a GitHub Release with the `.nupkg` attached, and pushes to nuget.org.

The nuget.org push needs the repository secret `NUGET_API_KEY` (Settings, Secrets and variables, Actions). Without it the push is skipped with a notice and the GitHub Release is still created. Re-running a tag is safe: `--skip-duplicate` ignores a version that is already published.
