# Changelog

All notable changes to Krut.Netcode are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

# Changelog

All notable changes to Krut.Netcode are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/), and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
