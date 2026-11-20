# Security Policy

## Supported versions

Only the latest released minor version receives security fixes.

## Reporting a vulnerability

Please do not open a public issue for security problems.

Report privately through GitHub: open the repository's **Security** tab and choose
**Report a vulnerability** (private vulnerability reporting). Include the affected
version, a description of the issue, and a minimal reproduction if you have one.

You can expect an acknowledgement within 7 days. Confirmed issues are fixed in a
new release and credited in the changelog unless you prefer to stay anonymous.

## Scope

This library parses untrusted network input (handshake, messages, codecs, NAT rendezvous/punch datagrams), bounds inbound event
buffering (`MaxPendingEvents`, `MaxPendingEventBytes`, `MaxReceivedPacketBytes`,
`MaxEventsPerPeerPerPoll`, `MaxUnconnectedEventsPerPoll`), provides opt-in transport and session
encryption (`NetworkEncryptionConfig`: pre-shared key `AES-256-GCM` datagram encryption via
`NetworkEncryptionLayer` and/or per-peer ephemeral `ECDH P-256` with optional `ECDsa` server identity
pinning), and implements authenticated NAT punchthrough (`"KNAT"` v1 with 128-bit `HostSecret`,
`RequestNonce`, and `PunchNonce`, bounded room leases and per-IP/per-room rate limits in
`NetworkNatRendezvousServer`, plus optional `RendezvousPreSharedKey` `AES-256-GCM` datagram
protection). Reports about crashes, unbounded allocation, buffer/pool corruption, cryptographic flaws in
our encryption layers, NAT spoofing/hijack flaws, or state corruption caused by crafted packets in the
library are in scope. Bugs in LiteNetLib itself should be reported to that project.

**Out of library scope (application responsibility):** Key provisioning/match-token distribution,
user account authentication, and game-specific anti-cheat. Your server remains responsible for
validating entity ownership, input rates, tick windows, movement/physics limits, cooldowns, and legal
gameplay state transitions even after a message decrypts and decodes cleanly.