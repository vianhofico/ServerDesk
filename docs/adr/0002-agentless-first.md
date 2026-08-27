# ADR 0002 — Agentless SSH/SFTP first

- Status: Accepted
- Date: 2026-08-27

## Context

The product should work against ordinary Linux servers with minimal setup and without introducing another publicly reachable management service. Common management operations can be implemented through SSH commands, SFTP, PTY sessions, and SSH forwarding.

A custom agent can later improve realtime performance and normalization, but making it mandatory at the start would increase installation, security, update, compatibility, and trust complexity before core workflows are proven.

## Decision

ServerDesk V1 starts agentless:

```text
Windows ServerDesk -> SSH / SFTP / PTY / forwarding -> Linux server
```

The application architecture uses ports/interfaces so a future `serverdesk-agent` can implement selected data/streaming services without changing feature UI/use cases.

The future agent must bind to loopback by default and should be reached through an SSH tunnel unless a separately reviewed secure network mode is designed.

## Consequences

Positive:

- no extra server installation for initial use;
- reuses established SSH access controls;
- no additional public management port;
- easy adoption on existing VPS/servers;
- SSH remains an escape hatch for unsupported operations.

Trade-offs:

- remote CLI startup/parsing overhead;
- realtime metrics/events require polling or long-lived command streams;
- compatibility tests must cover distro/tool output;
- some operations are harder to normalize safely without an agent.

## Revisit when

Introduce optional agent mode only after M1–M7 interfaces are stable enough that the agent can implement existing abstractions instead of becoming a second product architecture.
