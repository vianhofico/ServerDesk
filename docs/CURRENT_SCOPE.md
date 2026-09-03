# Current Scope of ServerDesk V1

**English** | [Tiếng Việt](CURRENT_SCOPE.vi.md)

This document describes the **delivered V1 scope**: what ServerDesk can do now, which capabilities are conditional on the remote server, and what remains unsupported or outside the certified boundary.

> `PRODUCT_PLAN` and `ROADMAP` preserve product intent and milestone contracts. This document together with `SUPPORT_MATRIX` is the primary reference for current release capability.

## 1. V1 in one paragraph

ServerDesk is a Windows desktop client for administering **Linux servers**. SSH/SFTP/PTY remains the primary control plane and normal agentless operation does not require a proprietary daemon. The optional `serverdesk-agent` adds realtime transport while remaining loopback-only and reachable through an SSH tunnel.

The M0–M8 roadmap is implemented/certified in the repository. V1 is not a web control panel, Linux RDP product, cloud console or management SaaS.

## 2. Certified client/server targets

### Client

- **Certified:** Windows 11 x64.
- **Compatibility target:** Windows 10 x64 while selected .NET/WPF/WebView2 dependencies remain supported.
- **Not certified:** Windows ARM64.
- No Linux/macOS desktop client in V1.

### Linux servers

Primary certified distribution targets:

- Ubuntu 24.04 LTS;
- Ubuntu 26.04 LTS;
- Debian 13.

Rocky/Alma 9/10 are expansion targets and are not implicitly Certified until their gates pass. Other systemd distributions are best-effort/unknown until promoted.

## 3. Delivered modules

| Module | V1 state | Main scope |
|---|---|---|
| Server profiles & organization | Delivered | add/edit/clone/remove, groups/tags/favorites/search, metadata organization/import, connection history |
| SSH security & routing | Delivered | password/key/encrypted-key, keyboard-interactive/MFA, host-key trust, reconnect, direct/proxy/bastion routes |
| Remote Explorer | Delivered | SFTP browsing, file/folder operations, upload/download, metadata and guarded privileged workflows |
| Remote Editor | Delivered | raw/config editing, staged replacement, permission-aware privileged save, validators where available |
| Terminal | Delivered | real SSH PTY and concurrent interactive sessions |
| SSH tunnels | Delivered | local, remote and dynamic/SOCKS forwarding |
| Dashboard | Delivered | CPU, memory, load, uptime, filesystem/network and normalized overview |
| Processes | Delivered | inventory/details plus guarded terminate/kill workflows |
| Services | Delivered for systemd | status and lifecycle/enablement operations with logs where available |
| Storage | Delivered, read-oriented | block/filesystem/mount/usage visibility; no general destructive partition editor |
| Network & ports | Delivered | interfaces, addresses, traffic/listeners/routes/process association when available |
| Logs | Delivered | journald/files/container-related views and bounded realtime paths where implemented |
| Docker Engine | Delivered when usable | inventory, lifecycle, inspect/stats/logs and exec diagnostics |
| Docker Compose v2 | Delivered | project discovery, up/down/restart/pull/build, logs, raw YAML + validation |
| Git operations | Delivered | operational repo status/fetch/pull/diff workflows; not a full Git IDE |
| Scheduled tasks | Delivered | cron/systemd-timer oriented management with raw escape hatch |
| nginx | Delivered | inventory/site configuration, guarded edits and validation/reload |
| TLS/Certbot | Delivered when detected | certificate inventory and supported certificate operations |
| Environment files | Delivered | guarded environment/config file editing |
| Deployment | Delivered | reviewed deployment workflows built on existing remote primitives |
| Firewall | Delivered for adapters | UFW/firewalld; raw nftables editing is not V1 scope |
| Users/groups/SSH keys | Delivered | account/group visibility and guarded administration/authorized-key workflows |
| Packages | Delivered for adapters | APT/DNF inventory/update operations with safety gates |
| Backup/restore | Delivered for certified targets | verified artifacts, target preview, destructive confirmation, post-verification |
| Databases | Delivered with exact matrix | runtime/inventory, SSH tunnel, diagnostics; backup/restore only where certified |
| Multi-server | Delivered | global dashboard, comparison and guarded metadata/bulk workflows |
| Operation history/audit | Delivered | records reviewed mutations without persisting secret payloads |
| Optional agent | Backend/lifecycle delivered & certified | loopback gRPC over SSH, realtime streams, signed artifact/lifecycle backend; no dedicated standalone Agent Management window is claimed |

## 4. Exact database boundary

The database module is **not a Navicat/DataGrip replacement**. It focuses on runtime/inventory, authenticated diagnostics, SSH-tunneled connectivity and verified backup/restore.

| Engine fixture | Runtime/Inventory | SSH tunnel | Diagnostics | Backup | Restore |
|---|---|---|---|---|---|
| PostgreSQL 18.6 | Certified | Certified | Certified | Certified | Certified |
| MySQL 8.4.11 | Certified | Certified | Certified | Certified | Certified |
| MariaDB 11.8.9 | Certified | Certified | Certified | Certified | Certified |
| Redis 8.10.0 | Certified | Certified | Certified | **Unsupported** | **Unsupported** |
| Microsoft SQL Server 17.0.4075.5 / SQL Server 2025 CU8 | Certified | Certified | Certified | Certified | Certified |
| MongoDB 8.0.29 standalone | Certified | Certified | Certified | Certified | Certified |

Mandatory boundaries:

- Unlisted versions are **not automatically Certified** just because a client command connects.
- Redis backup/restore fails closed because deterministic persistence-copy/recovery semantics are not proven.
- MongoDB backup/restore is certified only for the listed **standalone topology**; replica-set/mongos backup/restore remains unsupported until separately certified.
- MongoDB diagnostics do not read/display document contents.
- Arbitrary SQL execution, Mongo shell execution and general query consoles are outside the certified database scope.
- Database secrets remain behind the secret abstraction instead of ordinary profile/URI persistence.

See [`SUPPORT_MATRIX.md`](SUPPORT_MATRIX.md) for exact evidence and versions.

## 5. Container boundary

V1 supports Docker Engine CLI when usable and Docker Compose v2, including inventory, lifecycle actions, inspect/stats/logs, exec diagnostics, Compose project/service state, up/down/restart/pull/build, logs and validated raw YAML editing.

Not certified/in scope: Kubernetes, Podman adapter, Docker Swarm management console, exposing/forwarding the Docker Unix socket, or treating legacy `docker-compose` v1 as a certification requirement.

## 6. Deployment/web boundary

Delivered: nginx inventory/site management, raw configuration escape hatch, validation before activation/reload, TLS inventory/Certbot integration when detected, environment-file workflows and reviewed deployment workflows.

Outside V1: full Apache/Caddy management, cloud-provider load balancer/DNS consoles and Kubernetes deployment.

## 7. System-administration boundary

Delivered: systemd-first services, UFW/firewalld, users/groups/authorized keys/account state/sudo visibility when detectable, APT/DNF, verified backup/restore and operation audit/history.

Not certified/in V1: full SysV init management, raw nftables visual editing, one-click root enablement, destructive disk partitioning/formatting/filesystem surgery, or unattended production updates by default.

## 8. Multi-server boundary

V1 includes global overview, organization/search, selected-server comparison and guarded metadata/bulk workflows. Multi-server support does **not** automatically permit broadcasting every single-server mutation. Destructive bulk operations require separate safety design.

## 9. Optional serverdesk-agent

Implemented/certified backend capabilities include:

- Linux agent host with structurally loopback-only listener;
- fixed Linux agent port `41371`;
- ephemeral Windows loopback SSH local forwarding to Linux loopback;
- Protobuf/gRPC negotiation and health;
- explicit protocol/capability compatibility;
- metrics streaming;
- process/service event streaming;
- normalized Docker events;
- bounded/redacted journald streaming;
- agentless fallback/degradation;
- ECDSA P-256/SHA-256 signed release metadata;
- exact artifact length/SHA-256 verification;
- fixed-surface install/update/status/uninstall backend;
- `sudo -n`, fixed paths, bounded rollback and explicit Ambiguous state for uncertain mutation completion.

Boundaries:

- No public/LAN agent listener in V1.
- No generic remote-command RPC.
- Agent transport never replaces SSH authentication/host trust.
- The repository does not prove a **dedicated end-user Agent Management WPF window**, so V1 does not advertise agent lifecycle as a standalone GUI module.
- Installable agent distribution must follow the signed-manifest/external signing-key process; signing private keys do not live in this repository.

## 10. Explicit non-goals for V1

- Kubernetes IDE/control plane;
- AWS/Azure/GCP console;
- full Navicat/DataGrip replacement;
- full Git IDE;
- SaaS monitoring/team collaboration platform;
- Linux graphical remote desktop;
- arbitrary root-shell automation engine;
- public proprietary server management port.

## 11. Capability-state semantics

A feature may exist in code/UI but be unavailable on a particular server. ServerDesk distinguishes states such as Available, Unavailable, PermissionDenied, UnsupportedVersion, ProbeFailed/Unknown and Ambiguous after uncertain mutations.

Examples: a `docker` executable does not prove daemon availability/permission; `sqlcmd` does not prove a SQL Server service exists; `mongosh` does not prove `mongod` is running.

## 12. Source-of-truth order

When documents appear to conflict, use:

1. [`SUPPORT_MATRIX.md`](SUPPORT_MATRIX.md) for exact platform/version/certification boundaries;
2. this `CURRENT_SCOPE` document for delivered/out-of-scope product capability;
3. dedicated security/ADR documents for security invariants;
4. `PRODUCT_PLAN`/`ROADMAP` for product intent and milestone history.
