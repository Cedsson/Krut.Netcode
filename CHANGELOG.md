# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - Not yet released

Initial release, extracted from the Krut.Racing game.

### Added

- Authoritative-server sessions on LiteNetLib: `NetworkServer`, `NetworkClient`, handshake with protocol version check, welcome message, peer ids, reconnect loop and clean disconnect handling.
- Typed, pooled message system (`INetworkMessage`, `NetworkMessages`, `NetworkMessagePool<T>`) with per-message delivery method and registration of game messages.
- Wire codec helpers for names, text, vectors, quantized positions and rotations (`NetworkCodec`).
- Fixed-tick clock (`NetworkClock`) and server time synchronization (`NetworkTimeSync`).
- Client-side prediction and server reconciliation (`NetworkReconciler<TState, TInput>`) with input history, replay and visual error smoothing.
- Snapshot interpolation and extrapolation (`NetworkSnapshotInterpolator<TState>`) over a fixed-capacity state ring.
- Link simulator for latency, jitter, loss, duplication and reordering (`NetworkSimulation`, `NetworkDatagramChain`) and named profiles.
- Diagnostics: per-session statistics and a datagram probe.
- `PredictionDemo` sample, xUnit test suite, Native AOT compatibility (`IsAotCompatible`), XML documentation and SourceLink.

### Fixed during extraction

- `NetworkEnvelope.Is<T>` returns false instead of throwing when the envelope carries a different message type.
- `NetworkClient` auto-reconnect dials again after a lost connection; it used to stay in `Reconnecting` without ever sending a new request.
- `NetworkClient` enforces `MaxConnectAttempts`: after that many failed retries the client goes offline and reports `NotConnected`. The counter resets when a ready session is lost.

### Removed

- Game-specific telemetry (`NetStats`) and all MonoGame types; vectors and rotations use `System.Numerics`.

[Unreleased]: https://github.com/Cedsson/Krut.Netcode/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Cedsson/Krut.Netcode/releases/tag/v0.1.0