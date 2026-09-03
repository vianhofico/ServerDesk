<p align="center">
  <img src="docs/assets/serverdesk-logo.png" alt="ServerDesk" width="140" />
</p>

<h1 align="center">ServerDesk</h1>

<p align="center">Visual Linux Server Administration for Windows</p>

<p align="center">
  <strong>English</strong> |
  <a href="README.vi.md">Tiếng Việt</a>
</p>

<p align="center">
  <a href="https://github.com/vianhofico/ServerDesk/actions/workflows/ci.yml"><img src="https://github.com/vianhofico/ServerDesk/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
</p>

ServerDesk is a Windows desktop application for administering Linux servers through a visual, Windows-like control surface while keeping SSH/SFTP as the primary secure control plane.

> Product model: **File Explorer + Task Manager + Services + Terminal + Docker Desktop + deployment/server administration**, with an optional SSH-tunneled realtime agent.

## Status — V1 delivered

The M0–M8 roadmap has been implemented and certified in the repository. V1 includes the Windows client, agentless SSH administration, DevOps/deployment/administration/database/multi-server modules, and the optional loopback-only `serverdesk-agent` transport/lifecycle backend.

This does **not** mean every Linux distribution, database version, topology, or administration workflow is supported. ServerDesk intentionally fails closed for unproven or high-risk capabilities. See:

- [Current scope — what is implemented and what is not](docs/CURRENT_SCOPE.md)
- [Detailed user guide by module](docs/USER_GUIDE.md)
- [Certified support matrix](docs/SUPPORT_MATRIX.md)
- [V1.0.0 release notes](docs/releases/v1.0.0.md)

## V1 highlights

- Secure SSH profiles with host-key trust, password/key/keyboard-interactive authentication, reconnect, proxy/bastion routing and connection history.
- SFTP Remote Explorer and guarded remote editor.
- Real PTY terminal and local/remote/dynamic SSH forwarding.
- Dashboard, processes, systemd services, storage, network/ports and logs.
- Docker Engine management, exec diagnostics and Docker Compose v2 workflows with YAML validation.
- Git operational helper and scheduled tasks.
- nginx, TLS/Certbot, environment-file and deployment workflows.
- UFW/firewalld, users/groups/authorized keys, APT/DNF and audited backup/restore workflows.
- Database runtime/diagnostics and SSH-tunneled workflows for PostgreSQL, MySQL, MariaDB, Redis, Microsoft SQL Server and MongoDB, with capability-specific certification boundaries.
- Global dashboard, server comparison and guarded multi-server metadata operations.
- Optional `serverdesk-agent`: loopback-only gRPC through an SSH local tunnel, realtime metrics/process/service/Docker/journal streams, signed artifact verification, and reviewed install/update/status/uninstall lifecycle backend.

## Important V1 boundaries

Notable items outside the certified V1 scope include Kubernetes, Podman, a Linux graphical remote desktop, cloud-provider consoles, full database IDE/query consoles, raw nftables editing, destructive disk partition/filesystem management, SysV-init certification, and a public/non-SSH agent management listener.

Database-specific boundaries are important: Redis backup/restore is unsupported; MongoDB backup/restore is certified only for the listed standalone fixture/topology; arbitrary SQL/Mongo shell execution is outside the certified database scope. Exact certified versions are listed in [`docs/SUPPORT_MATRIX.md`](docs/SUPPORT_MATRIX.md).

## Target stack

- Windows 11 x64 certified client target; Windows 10 x64 remains a compatibility target where dependencies permit.
- .NET 10 + WPF.
- Modular Domain/Application/Infrastructure architecture.
- SSH/SFTP/PTY first; optional agent never replaces the SSH trust boundary.
- SQLite for non-secret local metadata.
- Windows secure storage abstractions for credentials/secrets.
- `serverdesk-agent` on Linux, bound to loopback and reached through SSH tunneling.

## Repository layout

```text
src/
  ServerDesk.App/                       # WPF client
  ServerDesk.Domain/                    # domain contracts/models
  ServerDesk.Application/               # use cases and transport-neutral ports
  ServerDesk.Infrastructure/            # local persistence/administration infrastructure
  ServerDesk.Infrastructure.Ssh/        # SSH/SFTP/PTY/routing/agent transport
  ServerDesk.Infrastructure.Databases/  # certified database adapters
  ServerDesk.Agent.Contracts/           # Protobuf contracts
  ServerDesk.Agent/                     # optional Linux agent host

tests/
  ServerDesk.Tests/
  ServerDesk.Ssh.IntegrationTests/

docs/
.github/
```

## Documentation

For users:

1. [`docs/USER_GUIDE.md`](docs/USER_GUIDE.md) — how to use each module.
2. [`docs/CURRENT_SCOPE.md`](docs/CURRENT_SCOPE.md) — delivered, conditional, unsupported and out-of-scope capabilities.
3. [`docs/SUPPORT_MATRIX.md`](docs/SUPPORT_MATRIX.md) — exact certified platforms/engines.
4. [`docs/releases/v1.0.0.md`](docs/releases/v1.0.0.md) — first V1 release notes.

For contributors/agents:

1. [`AGENTS.md`](AGENTS.md) — mandatory development workflow.
2. [`docs/PRODUCT_PLAN.md`](docs/PRODUCT_PLAN.md) — product intent and UX model.
3. [`docs/ROADMAP.md`](docs/ROADMAP.md) — M0–M8 milestone contracts/history.
4. [`docs/SECURITY_RULES.md`](docs/SECURITY_RULES.md) — security constraints.
5. [`docs/TEST_STRATEGY.md`](docs/TEST_STRATEGY.md) — test/certification strategy.
6. [`docs/UX_RULES.md`](docs/UX_RULES.md) — UI/UX rules.

## Build from source

```powershell
dotnet restore ServerDesk.sln
dotnet build ServerDesk.sln -c Release
```

The primary WPF build runs on Windows. The release workflow publishes a self-contained `win-x64` package after the exact `main` CI run succeeds.

## Security model in one sentence

ServerDesk prefers standard SSH trust, keeps secrets outside ordinary profile metadata, treats remote capabilities as untrusted/conditional, and requires preview/confirmation/verification for risky mutations instead of silently retrying uncertain operations.
