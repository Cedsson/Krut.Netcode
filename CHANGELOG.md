# Changelog

All notable changes to Krut.Netcode are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0](https://github.com/Cedsson/Krut.Netcode/compare/v1.0.2...v1.1.0) (2026-11-20)


### Features

* **nat:** add NativeAOT-safe binary NAT punchthrough and rendezvous server ([347d188](https://github.com/Cedsson/Krut.Netcode/commit/347d188fdb0ab5b4df71f8532b49d9caeec13c1a))
* **security:** add AES-256-GCM datagram and ephemeral ECDH session encryption ([8baed80](https://github.com/Cedsson/Krut.Netcode/commit/8baed809c7acb05db055d7c244594ba2ab761d4b))


### Bug Fixes

* **messages:** harden codec quantization, finite reads, and thread-safe registry ([17aca3c](https://github.com/Cedsson/Krut.Netcode/commit/17aca3ca565dab6295c8d43dbd42ece37d3e0b8e))
* **prediction:** fix slerp flip, tick-span math, and reconciler rollback edge cases ([de9325f](https://github.com/Cedsson/Krut.Netcode/commit/de9325fc7f97e4801c223a0a4982698e886b9484))
* **session:** bound inbound queues, isolate callbacks, and harden socket lifecycle ([fcb4809](https://github.com/Cedsson/Krut.Netcode/commit/fcb48091e3b3c0d5ea62f108087f9e1635446e04))
* **simulation:** bound delay queue, synchronize link stats, and guard invalid inputs ([5f26cfb](https://github.com/Cedsson/Krut.Netcode/commit/5f26cfb0ffca9426692e4b72fb1c134f9750bfcb))
* **time:** add clock clamp telemetry and harden time sync bounds ([0bf6b90](https://github.com/Cedsson/Krut.Netcode/commit/0bf6b90dc51d1015ecec6158eb22fca43ccb7717))

### [1.0.2](https://github.com/Cedsson/Krut.Netcode/compare/v1.0.1...v1.0.2) (2026-11-20)


### Bug Fixes

* **session:** enforce MaxClients with approval, reject spoofed SystemChat and trailing bytes, and allow restart after Stop ([e2ab76f](https://github.com/Cedsson/Krut.Netcode/commit/e2ab76f8dc0e157990f1ea4d4034ccde2780b099))


### Build and Dependencies

* **deps:** bump the actions-dependencies group with 2 updates ([#10](https://github.com/Cedsson/Krut.Netcode/issues/10)) ([f702052](https://github.com/Cedsson/Krut.Netcode/commit/f7020521ddbeef4e3f73c87002a503efa2cfb678))
* **deps:** bump the nuget-dependencies group with 2 updates ([d30ebd2](https://github.com/Cedsson/Krut.Netcode/commit/d30ebd262c2cf1ddaf3260fcf516faab4ef7fe46))


### Performance

* **netcode:** add fast-slot message pooling and SIMD-accelerate NetworkStateBlend ([f7a53a8](https://github.com/Cedsson/Krut.Netcode/commit/f7a53a8b5708a3567f1b62f36429791f79ec75d9))

### [1.0.1](https://github.com/Cedsson/Krut.Netcode/compare/v1.0.0...v1.0.1) (2026-11-20)


### Bug Fixes

* **messages:** harden codec sanitization, add pool Reset and per-message Channel ([4740f59](https://github.com/Cedsson/Krut.Netcode/commit/4740f5911d30b8e3a3eceb82584ed6ca3b6167bb))
* **prediction:** clamp smoothing on assignment, clear replay scratch and cache ring ticks ([d39ca79](https://github.com/Cedsson/Krut.Netcode/commit/d39ca79f06015679624f7eebe72604786e8a6654))
* **session:** harden handshake and message validation, apply Welcome tick rate and pool delay buffers ([eadc5f5](https://github.com/Cedsson/Krut.Netcode/commit/eadc5f541931863536cd551bcd616fc6333bcf78))
* **time:** allow runtime tick-rate update and recover ShouldProbe after lost packet ([bf00178](https://github.com/Cedsson/Krut.Netcode/commit/bf00178b6314c74635e18acfc8878914fd58aeb0))


### Build and Dependencies

* **deps:** bump dependencies and actions, enable NuGetAudit and group Dependabot ([5fa3025](https://github.com/Cedsson/Krut.Netcode/commit/5fa3025444767119d3ab5d6bb78184804e6a8b77))

## 1.0.0 (2026-11-20)


### Features

* **release:** release 1.0.0 with automated semantic versioning and NuGet trusted publishing ([581706e](https://github.com/Cedsson/Krut.Netcode/commit/581706ea2114b2b4efeff395c8881b2e6acb643b))
