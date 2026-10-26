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

This library parses untrusted network input (handshake, messages, codecs). Reports
about crashes, unbounded allocation, or state corruption caused by crafted packets
are in scope. Bugs in LiteNetLib itself should be reported to that project.