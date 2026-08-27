# ServerDesk

ServerDesk is a Windows desktop application for managing Linux servers through a visual, Windows-like experience while keeping SSH/SFTP as the secure control plane.

> Product direction: **File Explorer + Task Manager + Services + Terminal + Docker Desktop + server administration**, without requiring users to memorize Linux commands for common operations.

## Status

The repository is in the bootstrap/foundation stage. The product plan, architecture constraints, agent workflow, security model, UX rules, support matrix, and milestone roadmap are treated as implementation contracts.

## Target stack

- Windows 10/11 desktop client
- .NET 10 + WPF
- MVVM-oriented modular architecture
- SSH/SFTP first (agentless)
- WebView2 + xterm.js for the terminal layer (introduced in M1)
- SQLite for non-secret local metadata
- Windows Credential Manager / DPAPI for secrets
- Optional `serverdesk-agent` over tunneled gRPC in a later milestone

## Documentation

Read these before implementing features:

1. [`AGENTS.md`](AGENTS.md) — mandatory workflow for coding agents
2. [`docs/PRODUCT_PLAN.md`](docs/PRODUCT_PLAN.md) — complete product scope
3. [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — architecture and boundaries
4. [`docs/UI_UX.md`](docs/UI_UX.md) — interaction and visual rules
5. [`docs/ROADMAP.md`](docs/ROADMAP.md) — ordered milestones and acceptance gates
6. [`docs/SECURITY.md`](docs/SECURITY.md) — security requirements
7. [`docs/TESTING.md`](docs/TESTING.md) — test strategy and compatibility gates
8. [`docs/SUPPORT_MATRIX.md`](docs/SUPPORT_MATRIX.md) — certified OS/capability targets

## Core principles

- **Agentless first:** a normal SSH server is enough for the initial product.
- **Secure by default:** host keys are verified; secrets are never stored in plaintext.
- **GUI first, CLI always available:** common operations are visual, advanced operations remain possible through a real terminal.
- **Capability based:** never assume Docker, systemd, nginx, sudo, or a package manager exists.
- **Machine readable:** prefer structured command output and stable system files over parsing human-oriented terminal formatting.
- **Safe mutations:** validate, preview, confirm, back up, execute, verify, and roll back where possible.
- **No distro conditionals in UI:** distro-specific behavior lives behind adapters.

## Initial repository layout

```text
src/
  ServerDesk.App/
  ServerDesk.Domain/
  ServerDesk.Application/

docs/
.github/
```

Additional infrastructure, Linux adapter, and feature-module projects are introduced by the roadmap only when their boundaries are needed.

## Build

```powershell
dotnet restore src/ServerDesk.App/ServerDesk.App.csproj
dotnet build src/ServerDesk.App/ServerDesk.App.csproj -c Release
```

The CI workflow uses Windows runners because the primary app is WPF.

## Definition of a successful V1

V1 is not considered complete until the certified support matrix passes end-to-end tests for secure connection, Explorer, editor, terminal, dashboard, processes, services, logs, storage, network/ports, Docker/Compose, Git basics, nginx basics, SSH tunneling, reconnect, credentials, operation history, and destructive-action safeguards.
