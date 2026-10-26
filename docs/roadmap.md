# Roadmap

Known limitations and sensible next steps. Each limitation below was checked against the source;
file names are given so the claim can be re-checked. Out-of-scope items are out of scope on purpose.

## Known limitations

- **Measured RTT in the demo is above the configured one.** The demo runs the server and both
  clients in one process on one shared polling loop. The `gate` profile (100 ms RTT) measures
  124-156 ms there. Delayed packets are released and the socket is read only inside `Update`, so
  the loop cadence can add to the simulated delay on every hop. This is believed to be a harness
  effect; runs with separate processes have not been measured.
- **The simulator's delay queue is wall-clock.** `Simulation/NetworkDelayQueue.cs` takes its time
  from `Stopwatch.GetTimestamp`, so a simulated run cannot go faster than real time and a test of a
  10 s outage takes 10 s. Delay is applied per message rather than per UDP packet, and releases
  happen inside `Update`, so the effective resolution is the update cadence
  (`Simulation/NetworkSimulation.cs` class summary).
- **Only two of the four channels are routed.** `NetworkChannels.For` in
  `Messages/NetworkProtocol.cs` maps unreliable to `Snapshot` and everything reliable to `Events`.
  `Input` (0) and `Control` (3) are defined but nothing selects them, so today the separation is
  two LiteNetLib queues, not four.
- **Leftover wording in the source.** Several XML comments still refer to "the engine", requirement
  ids (R2, R6) or a "car"; the code is generic.
- **Built-in chat and lobby messages.** `SystemChat`, `Chat` and `PlayerInfo` are built into the
  protocol (`Messages/NetworkMessages.cs`), and the server sends a `PlayerInfo` row per peer after
  `Welcome`. Applications that do not want a lobby still receive them.
- **No automatic congestion handling.** The reliable queue length is exposed
  (`NetworkPeerState.ReliableQueueLength` in `Session/NetworkPeerState.cs`) but nothing throttles
  on it.

## By design: the game's job

- Snapshot format, delta encoding and interest management. The library carries bytes and gives
  you tick-stamped state primitives; it does not know what an entity is.
- Encryption and authentication. The transport is plain UDP over LiteNetLib; an application that
  needs confidentiality or account auth has to add it above or below.
- Anti-cheat, matchmaking, lobby UI, NAT traversal and relay.
- Physics determinism. The reconciler is only as good as the simulation it replays.

## Next steps

1. Route `Input` and `Control` in `NetworkChannels.For` (for example a per-message channel
   override on `INetworkMessage`), keeping the default stable for existing wire behaviour.
2. Give the demo separate processes (or separate polling loops) so the measured RTT matches the
   configured one, and add a `--seed` flag for repeatable runs.
3. A virtual-clock mode for the delay queue so simulation tests run faster than real time and
   become deterministic.
4. Make the built-in chat and lobby messages optional, or move them into a sample.
5. Optional snapshot helpers: a delta-encoding example and a per-client send budget, kept in a
   separate package or sample so the core stays small.
6. Publish the package to nuget.org (the release workflow is in place; it needs the
   `NUGET_API_KEY` secret).
7. Back the zero-allocation claim with numbers in the repo: run the benchmark project in CI and
   record per-packet allocations and send/receive throughput.
