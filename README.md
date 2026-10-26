# Krut.Netcode

[![CI](https://github.com/Cedsson/Krut.Netcode/actions/workflows/ci.yml/badge.svg)](https://github.com/Cedsson/Krut.Netcode/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Krut.Netcode.svg)](https://www.nuget.org/packages/Krut.Netcode)

Authoritative-server netcode for .NET on LiteNetLib: handshake, fixed tick clock, time sync,
client prediction with reconciliation, snapshot interpolation, and a link simulator to prove it
works on a bad network.

## Features

- Handshake with application id, protocol version and name; the server rejects with a readable reason
- Fixed tick clock, independent of frame rate, with a catch-up cap
- NTP-style time sync using the median offset of the lowest-RTT samples
- Client prediction and reconciliation: rewind to the acknowledged input, replay the rest, ease the visible correction
- Snapshot interpolation with bounded extrapolation, then hold
- Pooled messages with hand-written serialization; steady state allocates nothing per packet
- Four channels defined (input, snapshot, events, control); unreliable messages are routed to Snapshot and reliable ones to Events today, so state never queues behind an event. Input and Control routing is on the roadmap
- Link simulator: delay, jitter, loss, loss bursts, reorder, duplicates, outages and a bandwidth cap, switched on with `KRUT_NET_*` environment variables, repeatable with a seed
- Native-AOT safe: no reflection; the only dependencies are LiteNetLib 2.1.4 and `System.Numerics`

## How it fits together

```
 server (headless, authoritative)                      client
 ------------------------------                        ------
 NetworkServer.OnTick (fixed tick)   <-- input ------  NetworkReconciler (own entity)
   apply inputs, step simulation         (unreliable)     predict now, rewind + replay on snapshot
   broadcast snapshot                 -- snapshot --->  NetworkSnapshotInterpolator (other entities)
                                         (unreliable)     draw DelayTicks in the past
   reliable events                    -- events ----->  NetworkTimeSync
                                         (reliable)       median offset -> ResetClockTo(tick)
        ^                                                       |
        +-------- NetworkSimulation (loss, lag, jitter, ...) ---+
```

## Quick start

```csharp
using Krut.Netcode;
using LiteNetLib.Utils;

// A game message: a stable wire byte in 16-255, hand-written Write/Read.
public sealed class InputMessage : INetworkMessage
{
    public NetworkMessageType Type => NetworkMessageType.None;
    public byte WireType => 16;
    public NetworkDelivery Delivery => NetworkDelivery.Unreliable;
    public int Tick; public float Throttle;
    public void Write(NetDataWriter w) { w.Put(Tick); w.Put(Throttle); }
    public void Read(NetDataReader r)  { Tick = r.GetInt(); Throttle = r.GetFloat(); }
}

NetworkMessages.RegisterGameMessage<InputMessage>(16, NetworkDelivery.Unreliable);

var config = new NetworkConfig { Port = 25555, TickRate = 60 };   // tick rate = physics step

// Server
var server = new NetworkServer(config);
server.MessageReceived += env => { if (env.Message is InputMessage m) { /* apply m for env.PeerId */ } };
if (!server.Start()) return;

// Client
var client = new NetworkClient(config);
client.SessionReady += () => Console.WriteLine("connected");
client.Closed += (reason, text) => Console.WriteLine($"closed: {text}");
client.Connect("127.0.0.1", config.Port, "Alice");

// Both sides, every frame. Update is the only mandatory call.
while (true)
{
    server.Update(dt);
    client.Update(dt);

    if (client.IsSessionReady)
    {
        var msg = NetworkSession.Rent<InputMessage>();
        msg.Tick = client.CurrentTick; msg.Throttle = 1f;
        client.Send(client.ServerPeerId, msg);   // msg is pooled: do not touch it after Send
    }
}
```

The guide covers prediction and interpolation wiring (`NetworkReconciler<TState, TInput>`,
`NetworkSnapshotInterpolator<TState>`, `NetworkTimeSync`) in sections 5 and 6.

## Demo

One process, headless: a server and two clients over localhost UDP, a simulated 100 ms RTT / 2 % loss link, scripted input, 10 s of play.

```
dotnet run --project samples/PredictionDemo -c Release
# other links: --profile stress|bad|lan|clean  --seconds 20
```

```text
Link: gate, 100 ms RTT +/-10, 2 % loss per direction
Running 10 s of play ...

                                                client 0    client 1
measured RTT, min / max (ms)                     124/156     124/156
clock lead (ticks)                                     8           8
packets dropped by link, up / down (%)           1.4/1.5     2.2/1.5
inputs repeated / late at server                    10/0        11/0

error vs server, no reconcile: mean (mm)            16.4        11.1
error vs server, no reconcile: max (mm)             30.4        18.0
error vs server, reconciled: mean (mm)               0.1         0.1
error vs server, reconciled: max (mm)                1.4         2.3
error at ack, before reconcile: mean (mm)            0.1         0.1
error at ack, before reconcile: max (mm)             1.1         2.0
correction applied per snapshot: mean (mm)           0.0         0.0

snapshots reconciled (rollbacks)                     299         290
inputs replayed, total / per rollback             2808/9     3048/10
rollback misses / ignored snapshots                  0/0         0/0

remote unit samples: Exact                           298         289
remote unit samples: Interpolated                    303         297
remote unit samples: Extrapolated                      0           0
remote unit samples: Held                              0           0
remote unit error vs server: mean (mm)               1.2         1.2
remote unit error vs server: max (mm)               12.2        11.8
remote view delay (ms)                               200         200

bytes/s sent (client)                                973         947
bytes/s received (client)                           1404        1400
server bytes/s sent / received                      2804        1920
play time (s)                                       10.0

PASS: prediction stays close to the server, replay corrects the rest.
```

Without reconciliation the client drifts from the server by tens of millimetres after a lost input; with it the error stays near zero. The measured RTT is above the configured 100 ms because both clients and the server share one polling loop. Exit code 0 on PASS, 1 on failure.

## Repository layout

```
src/Krut.Netcode/      the library (namespace Krut.Netcode)
  Session/             server, client, session base, config, peers, errors
  Messages/            message contract, built-in messages, codec, channels
  Time/                tick clock, time sync
  Prediction/          state ring, snapshot interpolator, reconciler
  Simulation/          link simulator, profiles, delay queue, datagram stage
  Diagnostics/         statistics and datagram probe
tests/Krut.Netcode.Tests/   xunit tests
benchmarks/Krut.Netcode.Benchmarks/   allocation and throughput benchmarks
samples/PredictionDemo/     headless server + 2 clients demo
docs/                  guide, architecture, diagrams, roadmap
.github/                CI and release workflows, issue and PR templates
CHANGELOG.md, SECURITY.md, CODE_OF_CONDUCT.md, CONTRIBUTING.md
```

## Documentation

- [docs/guide.md](docs/guide.md): usage, configuration, messages, lag primitives, simulator, troubleshooting, releasing
- [docs/architecture.md](docs/architecture.md): code map, frame loop, wire format, handshake, invariants
- [docs/diagrams.md](docs/diagrams.md) and [docs/architecture.drawio](docs/architecture.drawio): Mermaid and editable diagrams
- [CONTRIBUTING.md](CONTRIBUTING.md): build, test, style, pull request checklist
- [docs/roadmap.md](docs/roadmap.md): known limitations and next steps
- [CHANGELOG.md](CHANGELOG.md), [SECURITY.md](SECURITY.md), [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)

## Benchmarks

`benchmarks/Krut.Netcode.Benchmarks` (BenchmarkDotNet) measures the hot paths and their allocations:

```
dotnet run -c Release --project benchmarks/Krut.Netcode.Benchmarks -- --filter "*"
```

Indicative numbers from one short, noisy run (`--job short`, Windows 11, .NET 9): codec write
position + rotation 29 ns and read 23 ns; reconcile predict + replay of 6 / 18 / 60 inputs 277 ns /
866 ns / 3.2 us; interpolator sample 28 ns; all with 0 B allocated. Treat them as a sanity check,
not a published benchmark.

## Testing

```
dotnet test
```

The suite is xunit and covers the handshake and rejection paths, the tick clock, the codec, message
pooling and per-packet allocation, time sync, extrapolation, reconnects and the link simulator.

## Design notes

**Hand-written serialization.** Every message writes and reads its own fields through
`NetDataWriter`/`NetDataReader`. There is no reflection (so Native AOT works), the packet size is
exactly what `Write` puts out, and an unknown type byte is counted and skipped rather than
disconnecting an older client. The cost is that `Write` and `Read` must list fields in the same
order; the troubleshooting table in the guide shows how a mismatch surfaces.

**Median time sync.** The client estimates the server tick from request/response samples. A mean
lets a single delayed packet move the estimate for good, and a high RTT sample mostly measures a
queue. The offset is the median over the last 16 samples after dropping any sample whose RTT is
more than 2x the best. `NaiveAverageOffsetTicks` stays only so tests can show the difference.

**Tick rate equals the physics step.** Reconciliation replays stored inputs and expects the same
result the server got. If one tick spans a different number of physics steps on either side, each
rollback adds a new error instead of removing one.

More detail: [docs/guide.md](docs/guide.md).

## Origin

Extracted from the engine of Krut.Racing, an in-development multiplayer racing game with a
headless authoritative dedicated server. The library has no game-specific code; the game owns its
messages, snapshot format and physics.

## License

MIT, see [LICENSE](LICENSE). Author: William Eklund.
