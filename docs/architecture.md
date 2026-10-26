# Architecture

For maintainers. [guide.md](guide.md) explains how to use the library; this document explains how it
is built and which rules keep it correct. Diagrams: [diagrams.md](diagrams.md) (Mermaid, renders on
GitHub) and [architecture.drawio](architecture.drawio) (editable in diagrams.net).

## 1. Code map

Diagram: [Overview](diagrams.md#overview).

Everything lives in `src/Krut.Netcode` (namespace `Krut.Netcode` throughout; folders are for
navigation only). Dependencies: LiteNetLib 2.1.4 and `System.Numerics`.

**`Session/`: transport and session**

| File | Role |
|---|---|
| `NetworkSession.cs` | Abstract base of server and client. Owns the `NetManager`, the peer table, the clock, the send and receive paths and the `Update` loop. |
| `NetworkServer.cs` | Listens, validates the handshake, sends `Welcome` and the lobby, enforces `MaxClients`. |
| `NetworkClient.cs` | Dials, sends the handshake, tracks `NetworkClientState`, handles `Welcome` and library-level reconnect. |
| `NetworkListener.cs` | The LiteNetLib event listener. Only copies events into a queue (see section 3). Internal. |
| `NetworkConfig.cs` | Data-driven settings and `Validate()` clamping. |
| `NetworkConnection.cs` | `NetworkConnectionRequest` / `NetworkConnectionResult`: what `ConnectionApproval` sees and answers. |
| `NetworkPeerState.cs` | Per-peer view: id, name, ping, loss, reliable queue length, `Tag`. |
| `NetworkError.cs` | `NetworkError` and `NetworkErrorCode`: how network problems reach the game. |

**`Messages/`: messages and codec**

| File | Role |
|---|---|
| `INetworkMessage.cs` | The message contract: `Type`, `WireType`, `Delivery`, `Write`, `Read`. |
| `NetworkProtocol.cs` | `NetworkDelivery`, `NetworkMessageType` (built-in ids 1-15) and `NetworkChannels`. |
| `NetworkMessages.cs` | Built-in messages (`Welcome`, `SystemChat`, `Chat`, `PlayerInfo`), the game-type registry and the per-type pools. |
| `NetworkCodec.cs` | Quantized position and rotation, vectors, sanitized names and text. |
| `NetworkEnvelope.cs` | A received message with sender id, delivery and time. |

**`Time/`: clock and time sync**

| File | Role |
|---|---|
| `NetworkClock.cs` | Fixed-rate tick accumulator with a catch-up cap. |
| `NetworkTimeSync.cs` | Median-offset estimate of the server tick and the client lead. |

**`Prediction/`: lag primitives** (independent of the session; generic over `unmanaged` state)

| File | Role |
|---|---|
| `NetworkStateRing.cs` | `NetworkStateRing<T>` (tick-stamped ring) and `NetworkStateBlend` (reflection-free lerp, extend, slerp). |
| `NetworkSnapshotInterpolator.cs` | Remote entities: draw in the past, extrapolate briefly, then hold. |
| `NetworkReconciler.cs` | Local entity: predict, rewind to the acknowledged input, replay, ease the visible correction. |

**`Simulation/`: the link simulator**

| File | Role |
|---|---|
| `NetworkSimulation.cs` | The simulator facade and the `KRUT_NET_*` variable names. |
| `NetworkSimulationOptions.cs` | Options record, `NetworkSimulationSide`, `NetworkOutage`. |
| `NetworkSimulationProfile.cs` | Named links (`clean`, `lan`, `gate`, `stress`, `bad`, `blackout`, `thin`) in round-trip terms. |
| `NetworkLinkModel.cs` | Per-link decision model: loss, bursts, reorder, duplicates, bandwidth, outages. Deterministic per seed. |
| `NetworkSimRandom.cs` | Seeded PRNG owned by one link. |
| `NetworkDelayQueue.cs` | Holds outgoing messages until their due time (wall clock). |
| `NetworkDatagramChain.cs`, `NetworkDatagramSimLayer.cs` | Optional datagram-level stage (`KRUT_NET_DATAGRAM`) as LiteNetLib packet layers. |
| `KrutEnv.cs` | The one reader for `KRUT_*` environment variables; never throws. |

**`Diagnostics/`: measurement**

| File | Role |
|---|---|
| `NetworkStatistics.cs` | Byte and packet counters for the session. |
| `NetDatagramProbe.cs` | Packet layer that only measures datagram sizes and totals. |

## 2. One frame, step by step

Diagram: [Frame](diagrams.md#frame).

`NetworkSession.Update(deltaSeconds)` is the only mandatory call. In order:

1. Release outgoing messages whose simulated delay has expired (`NetworkDelayQueue.FlushDue`), then
   `Flush()`.
2. `NetManager.PollEvents()`: LiteNetLib calls `NetworkListener`, which appends to the pending
   event list.
3. `DrainEvents()`: swap the pending and draining lists, then handle each event in arrival order:
   peer connected (`PeerConnected`), peer disconnected (`PeerDisconnected`), socket error
   (`Error`), received packet (decode and `MessageReceived`, section 5).
4. `NetworkClock.Advance(dt, OnTick)`: `OnTick` runs once per whole elapsed tick, at most
   `MaxCatchUpTicks` per frame. This is where a server sends its per-tick snapshot.
5. `Flush()`: if anything was sent, `TriggerUpdate` wakes LiteNetLib's logic thread so packets
   leave now instead of on its next timer.

`NetworkClient.Update` additionally runs the reconnect timer after the base update. Handlers may
send, broadcast and disconnect freely; they must not call `Update` re-entrantly.

## 3. Threading model

All library code that the game touches runs on the thread that calls `Update`: no locks, no
background threads of our own. The session sets `NetManager.UnsyncedEvents = false`, so LiteNetLib
raises its events synchronously inside `PollEvents()` on the caller's thread. `NetworkListener`
therefore runs on the game thread and does only two things: copy the packet bytes into an
`ArrayPool<byte>` buffer and append a `NetworkEvent` to a list; the list is swapped and consumed in
step 3. LiteNetLib still has its own logic and socket threads, and the only library code that sees
them is the datagram layers: `NetworkDatagramSimLayer` guards its state with a lock and
`NetDatagramProbe` uses `Interlocked` counters.

## 4. Wire format

Diagram: [Wire format](diagrams.md#wire-format).

One message is one packet, with no length prefix and no per-packet header of our own:

```
byte 0        message type (the wire byte)
byte 1..n     the message body, exactly what INetworkMessage.Write put out
```

| Wire byte | Meaning |
|---|---|
| 0 | Reserved (`None`); a corrupt header never looks like a valid message |
| 1 | `Welcome` (peer id, tick rate, server tick, player name) |
| 2, 3, 4 | `SystemChat`, `Chat`, `PlayerInfo` |
| 5-15 | Reserved for future built-in messages |
| 16-255 | Game messages, registered with `NetworkMessages.RegisterGameMessage<T>` |

The channel and delivery method come from the message's `Delivery`:
`NetworkChannels.For(delivery)` picks channel 1 (`Snapshot`) for `Unreliable` and channel 2
(`Events`) for everything reliable; `NetworkChannels.ToDeliveryMethod` maps to LiteNetLib. Channels
0 (`Input`) and 3 (`Control`) exist but are not selected automatically. A packet that does not fit
the MTU on an unreliable channel is reported as `PacketTooLarge`.

The connection request carries a separate raw payload written by `NetworkClient.Connect`, read by
`NetworkServer.HandleConnectionRequest` before any session state exists:

```
string  ApplicationId
int     ProtocolVersion
string  player name  (NetworkCodec.WriteName: at most 32 chars)
```

Quantized codec formats: `WritePosition` is three `short` over a world extent (default 32000);
`WriteRotation` is four `short`, normalized on read.

## 5. Life of a message

Send:

1. `NetworkSession.Rent<T>()` takes an instance from the per-type pool (`NetworkMessagePool<T>`).
2. The game fills it and calls `Send(peerId, msg)` or `Broadcast(msg)`.
3. `SendInternal` resets the shared `NetDataWriter`, writes the wire byte, calls `msg.Write`.
4. If a simulation is active the bytes are copied into the `NetworkDelayQueue`; otherwise
   `peer.Send(writer, channel, method)` goes straight to LiteNetLib.
5. The instance is recycled into its pool. The game must not touch it again.
   `Broadcast` recycles once, after the last peer.

Receive:

1. `PollEvents`: `NetworkListener.OnNetworkReceive` copies the bytes to a pooled buffer and
   queues the event.
2. `Update`, step 3: `DecodeAndDeliver` reads the type byte, creates an instance from the registry
   (`NetworkMessages.CreateFromWire`, a pool rent) and calls `Read`.
3. An empty packet or a `Read` that throws is `MalformedMessage` (counted, instance recycled). An
   unregistered byte is `UnknownMessageType` (counted, connection kept).
4. `MessageReceived` is invoked with a `NetworkEnvelope`; when it returns, the instance goes back to
   the pool and the buffer returns to `ArrayPool`. Handlers copy what they keep.

## 6. Handshake and reconnect

Diagram: [Handshake](diagrams.md#handshake).

```
client                                   server
  Connect(addr, port, name)
  connection request + payload  ------>  HandleConnectionRequest
                                           ApplicationId differs -> silent reject
                                           ProtocolVersion differs -> reject + reason
                                           lobby full -> reject + reason (only when no ConnectionApproval is set)
                                           ConnectionApproval refuses -> reject + reason
                                         accept
                                <------  OnPeerConnected: Welcome (reliable), then PlayerInfo rows
  Welcome: set peer id, name,
  ResetClockTo(ServerTick),
  state = Connected, SessionReady
```

`PeerConnected` fires when the transport is up, before `Welcome`; replicate after `SessionReady`.
A rejection arrives as a disconnect whose additional data is the server's text, surfaced through
`Closed(reason, text)`; the server's text wins over the client's generic one, and a rejection never
triggers a retry. For other disconnects, if `AutoReconnect` is on the state becomes `Reconnecting`
and `NetworkClient.Update` calls `Connect` again every `ReconnectDelayMs`, counting
`ReconnectAttempts` (a retry keeps counting the same outage). LiteNetLib's own reconnect is disabled. Separately,
`ConnectRequestAttempts` controls how often LiteNetLib resends one connection request.

After `MaxConnectAttempts` failed retries in a row the client goes `Offline` and reports `NotConnected` ("Gave up reconnecting..."); a rejection never retries. The counter resets when a ready session is lost, so each outage gets a fresh budget.

## 7. Prediction and reconciliation per tick

Diagram: [Prediction](diagrams.md#prediction).

Local entity, each client tick:

1. Read input, `reconciler.Predict(tick, input)` applies `Step` to the current state, stores the
   pre-input state and the input in the ring, and returns the new state.
2. Send the input (typically redundantly, unreliable). Stop sending when `IsInputBufferFull`.

On each server snapshot for this entity:

1. `SetAuthoritativeState(serverTick, state)`; false means duplicate or reordered, drop it.
2. `Reconcile(serverTick, lastProcessedInputTick, replay)` rewinds to the acknowledged input and
   re-applies every later stored input through `Step`. If the rollback target is older than the
   history, the state is accepted and `RollbackMissCount` rises.
3. Physics continues from `PredictedState`; rendering reads `SmoothedState`, advanced by
   `UpdateRenderState(dt)`. Corrections over `MinCorrectionDistance` teleport.

Remote entities: `NetworkSnapshotInterpolator.Push(tick, state)` per snapshot, then each frame
`TrySampleTime(CurrentTick + TickProgress - DelayTicks, ...)`. `NetworkTimeSync` keeps the client
clock ahead of the server by `ClientLeadTicks` so inputs arrive before the server needs them.

## 8. Invariants

These are the rules that make the library correct. Do not break them; tests and CI enforce several.

1. **No reflection, Native-AOT safe.** Serialization is hand-written; the wire byte is a property,
   not derived from the type. CI publishes the demo with `PublishAot=true` and trim/AOT warnings as
   errors. LiteNetLib's `NetSerializer` is never used.
2. **Zero allocation in steady state.** Messages are pooled, packets reuse one writer, receive
   buffers come from `ArrayPool`, lag primitives allocate nothing per frame.
   `NetworkSessionAllocationTests` guards the send path; do not add per-packet or per-frame `new`,
   LINQ, closures or boxing.
3. **`Write` and `Read` list fields in the same order.** There is no framing to catch a mismatch;
   it decodes cleanly into the wrong values. Keep both in one class, side by side.
4. **Wire bytes are forever.** A registered byte and a built-in id never change meaning; add new
   ones last.
5. **Tick rate equals the physics step.** Reconciliation replays inputs and is only as good as
   both sides taking identical steps.
6. **Never forward extrapolated state.** An extrapolated or held state may be drawn; it must not
   be sent back to the network or fed into authoritative logic.
7. **The library never throws or logs at the game for network problems.** Everything is an
   `Error` event with a `NetworkErrorCode`; bad input from a peer (unknown type, short packet,
   oversized name) is counted and dropped. Programmer errors, such as sending an unregistered
   game message, do throw, and `Validate()` clamps configuration instead of refusing it.
8. **Single-threaded game side.** No game callback runs on a LiteNetLib thread; do not add locks
   or background work that touches session state.
9. **Simulation affects only what a session sends**, and never LiteNetLib's own pings and acks
   (except the opt-in datagram stage).
