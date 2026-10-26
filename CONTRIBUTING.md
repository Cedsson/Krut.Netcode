# Contributing

Thanks for looking at Krut.Netcode. Read [docs/architecture.md](docs/architecture.md) first: it
lists the invariants every change has to respect. [docs/guide.md](docs/guide.md) covers usage and
[docs/roadmap.md](docs/roadmap.md) lists known gaps and sensible next work.

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

## Build, test, run

Requires the .NET 9 SDK.

```
dotnet build Krut.Netcode.slnx -c Release -warnaserror
dotnet test tests/Krut.Netcode.Tests -c Release       # or: dotnet test Krut.Netcode.slnx
dotnet run --project samples/PredictionDemo -c Release   # --profile gate|stress|bad|lan|clean --seconds 10
```

The demo exits with code 0 on PASS and 1 on failure, so it doubles as an end-to-end check. To check
Native AOT locally (needs clang and zlib on Linux, the C++ build tools on Windows):

```
dotnet publish samples/PredictionDemo -c Release -r linux-x64 -p:PublishAot=true -p:TreatWarningsAsErrors=true
```

Run the benchmarks before and after any change to a hot path (codec, pool, reconciler,
interpolator):

```
dotnet run -c Release --project benchmarks/Krut.Netcode.Benchmarks -- --filter "*"   # add --job short for a quick run
```

Hot paths must stay allocation-free (`MemoryDiagnoser` shows `Allocated` as `-`). Package versions
live in `Directory.Packages.props`; edit them only there. Code style is enforced through
`.editorconfig` (4 spaces, block-scoped namespaces, braces on new lines, `_camelCase` private
fields).

## Code style

- English and ASCII only, in code, comments and docs.
- Comment only what the code cannot say: a non-obvious why, an invariant, a unit or range, or a
  wire-format or determinism constraint. One line by default. No history ("used to"), no restating
  the code, no banners.
- Nullable is on and CI builds with `-warnaserror`. The library is marked `IsAotCompatible`: no
  reflection, no `Activator`, no runtime type discovery.
- Hot paths (send, receive, tick, the lag primitives) must not allocate in steady state: no LINQ,
  no closures capturing locals, no boxing, no `new` per packet or per frame.
- Network problems are reported through `Error` with a `NetworkErrorCode`; do not throw or log at
  the game for them.
- Keep classes small. A file near 1000 lines wants a cohesive extraction, not a `partial` split.

## Adding a message

**A game message** (what an application would do; no library change needed):

1. Implement `INetworkMessage` with a parameterless constructor, `Type => NetworkMessageType.None`,
   a `WireType` in 16-255, a `Delivery`, and `Write` / `Read` with identical field order.
2. Call `NetworkMessages.RegisterGameMessage<T>(wireType, delivery)` once at startup, on both sides.
3. Never reuse or renumber a wire byte; add new ones last.

**A built-in message** (shared by every application, so the bar is high):

1. Add a value to `NetworkMessageType` in `Messages/NetworkProtocol.cs` using the next free id in 1-15.
2. Add the class in `Messages/NetworkMessages.cs`, `Type` set to the new value.
3. Add it to the switch in `NetworkMessages.Create` (rent) and to the recycle switch beside it,
   otherwise the instance leaks instead of returning to its pool.
4. Add a round-trip test (write, read, compare) and a test that the instance is recycled.

## Adding a simulation profile

Profiles are defined in round-trip terms in the `All` array of `Simulation/NetworkSimulationProfile.cs`:

```csharp
new("name", rttMs, rttJitterMs, lossChance) { BurstMeanLength = 4, ... },
```

`Names` and `TryGet` derive from the array, so `KRUT_NET_PROFILE=name` works immediately. Then
update `AllSevenProfilesExist_AndAreCaseInsensitive` in `NetworkSimulatorTests.cs` (the count and
any profile-specific assertions) and the profile list in `docs/guide.md` section 7.

## Adding a test

Tests are xunit, in `tests/Krut.Netcode.Tests`, and see internals (`InternalsVisibleTo`).

- Anything that opens sockets, reads or writes `KRUT_NET_*` variables, or touches static state
  (the message registry, the pools) belongs in the serial collection:
  `[Collection(NetworkTestCollection.Name)]`. It disables parallelization so tests do not trample
  each other's ports, environment or pools.
- Use `NetworkTestSupport`: `FreeUdpPort()` and `Config(port)` for an isolated server,
  `Pump(seconds, sessions...)` to run `Update` on every session at about 120 Hz,
  `PumpUntil(seconds, condition, sessions...)` to wait for an event, `WaitUntilReady` and
  `WaitUntilAllReady` for a completed handshake, and `Require(condition, what, detail)` for
  assertions with a readable message. Nothing arrives unless some session's `Update` runs.
- Prefer a pure test (clock, codec, time sync, reconciler, interpolator) over a socket test when
  the logic does not need a network.
- Clean up: dispose or `Stop()` sessions, and call `NetworkMessages.ClearGameMessages()` if you
  registered types.

Timing-sensitive categories, which use real sockets and wall-clock time and can flake on a loaded
CI machine: handshake (`NetworkHandshakeTests`), lifecycle and reconnect (`NetworkLifecycleTests`),
the link simulator and its datagram stage (`NetworkSimulationTests`, `NetworkSimulatorTests`,
`NetworkDatagramSimTests`), and the allocation test. Give these generous time budgets, assert on
outcomes instead of exact durations, and never lower a timeout to make a test faster.

## Pull request checklist

- [ ] CI is green on Linux and Windows, including the Native AOT job
- [ ] `-warnaserror` build is clean
- [ ] Tests added or updated; `NetworkSessionAllocationTests` still passes (zero new allocations
      on the send, receive and tick paths)
- [ ] The demo still prints PASS
- [ ] Invariants in architecture.md section 8 hold (no reflection, `Write`/`Read` order, wire bytes
      unchanged, tick rate story intact)
- [ ] Docs updated: guide for behaviour, architecture for structure, roadmap if a gap is closed or
      found
- [ ] `CHANGELOG.md` updated under "Unreleased", and the description follows `.github/pull_request_template.md`
- [ ] English and ASCII only, comments only where needed

## Releasing

See the Releasing section of [docs/guide.md](docs/guide.md): tag `vX.Y.Z` on `main` and the Release
workflow builds, tests, packs and publishes.

## Conduct and security

Participation is covered by the [Code of Conduct](CODE_OF_CONDUCT.md). Report vulnerabilities
privately as described in [SECURITY.md](SECURITY.md), not in a public issue.
