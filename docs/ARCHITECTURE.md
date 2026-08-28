# Architecture

**English** | [Tiếng Việt](ARCHITECTURE.vi.md)

## 1. Architectural goals

ServerDesk must remain maintainable as it grows from an SSH/SFTP client into a server-management desktop application. The architecture therefore separates UI, use cases, remote transport, Linux interpretation, persistence, and feature modules.

Primary goals:

- testability without a real server for most logic;
- distro/version adaptation without UI conditionals;
- safe command construction and typed errors;
- multiple simultaneous remote channels;
- optional future agent without rewriting feature use cases;
- no secrets crossing inappropriate boundaries.

## 2. High-level topology

```text
+----------------------- Windows ------------------------+
|                                                        |
|  ServerDesk.App (WPF)                                  |
|      |                                                 |
|      v                                                 |
|  Application / Feature Use Cases                       |
|      |                                                 |
|      +------------------+-------------------+           |
|      |                  |                   |           |
|      v                  v                   v           |
|  Linux adapters     SSH/SFTP infra     Local storage   |
|      |                  |                   |           |
+------+------------------+-------------------+-----------+
       |                  |
       | SSH/SFTP/PTY     | optional tunneled gRPC later
       v                  v
+---------------------- Linux server ---------------------+
| systemd / proc / Docker / nginx / files / tools         |
| optional serverdesk-agent bound to loopback             |
+---------------------------------------------------------+
```

## 3. Layer responsibilities

### ServerDesk.Domain

Contains stable product concepts and value objects.

Examples:

- server identity/profile metadata;
- capability model;
- operation risk classification;
- normalized process/service/storage/network models;
- typed operation results/errors;
- known-host fingerprints as values (not persistence implementation).

Must not depend on WPF, SSH.NET, SQLite, WebView2, distro-specific implementations, or network libraries.

### ServerDesk.Application

Contains application use cases and ports/interfaces.

Examples:

- connect/disconnect server;
- list directory;
- save privileged file;
- restart service;
- list containers;
- create tunnel;
- retrieve metrics;
- operation orchestration and rollback policy.

Defines abstractions such as:

```text
IRemoteCommandExecutor
IRemoteFileSystem
IServerSession
ICapabilityDetector
IServiceManager
IProcessManager
IStorageManager
INetworkManager
ILogManager
IFirewallManager
IPackageManager
ISecretStore
IProfileRepository
IOperationAudit
```

### Infrastructure.Ssh

Implements transport concerns only:

- connection lifecycle;
- authentication mechanisms;
- host-key events;
- command channels;
- PTY/shell streams;
- SFTP;
- forwarding;
- timeout/cancellation translation;
- SSH-specific errors mapped to application errors.

It does not know how to parse nginx, Docker, systemd, or distro package output.

### Linux.Common

Contains Linux-wide command specifications/parsers where behavior is sufficiently stable across certified distros.

Examples:

- `/etc/os-release` parsing;
- `/proc` metrics;
- generic process facts;
- `command -v` probes;
- safe POSIX-ish file metadata operations when proven portable.

### Linux.Debian / Linux.Rhel

Contain distro-family adapters.

Examples:

- APT vs DNF;
- UFW vs firewalld selection/behavior;
- distro-specific package metadata;
- service/config paths only when not capability-discoverable.

Feature code should test capabilities, not distro names, whenever the actual dependency is a capability.

### Platform.Windows

Contains Windows-specific implementations:

- Credential Manager / DPAPI secret storage;
- app paths;
- OS notifications if used;
- Windows shell/file picker integration;
- secure local IPC when needed later.

### Persistence

Contains SQLite implementation for non-secret metadata.

Secrets are referenced indirectly through identifiers managed by `ISecretStore`.

### Feature modules

Feature modules own UI + application-facing orchestration for a coherent domain area:

```text
Dashboard
Explorer
Terminal
Processes
Services
Docker
Storage
Network
Logs
ScheduledTasks
Git
Nginx
Security
Database
Backup
```

A feature module may depend on Application/Domain abstractions, never on concrete SSH classes.

## 4. Dependency rule

Allowed direction:

```text
App/UI ---------> Application ---------> Domain
   |                    ^                  ^
   |                    |                  |
   +-> feature modules -+                  |
                        |                  |
Infrastructure.Ssh -----+------------------+
Linux adapters ---------+------------------+
Persistence ------------+------------------+
Platform.Windows -------+------------------+
```

Infrastructure never becomes the business API used directly by ViewModels.

## 5. Remote command model

All command execution uses a typed specification, conceptually:

```text
RemoteCommandSpec
- executable
- arguments[]
- environment
- workingDirectory (optional)
- locale policy
- privilege requirement
- timeout
- stdin policy
- output parser
- idempotency/retry policy
- operation risk
```

Key requirements:

- no arbitrary interpolation of untrusted input into shell strings;
- arguments validated/escaped in one place;
- structured output requested where available;
- explicit locale for parsers that require stable text;
- stdout/stderr captured separately;
- exit code retained;
- cancellation supported;
- commands have bounded timeouts unless intentionally streaming.

For operations that inherently require a shell pipeline/compound script, isolate the script in a dedicated, reviewed command object and strictly quote validated input. Do not normalize shell snippets as the default execution model.

## 6. Data parsing

Preferred remote data sources:

1. stable files/interfaces (`/proc`, `/etc/os-release`, etc.);
2. JSON;
3. explicit properties/key-value output;
4. fixed delimiter formats;
5. human prose only as last resort.

Parsers produce domain/application models and never leak raw line-splitting responsibilities to UI.

Parser failures return `ParseFailed` with diagnostic detail suitable for logs but without secrets.

## 7. Server session model

One logical server workspace owns a `ServerSession`, but the session must not serialize all work through a single SSH channel.

Conceptual resources:

```text
ServerSession
- command connection/pool
- dedicated SFTP client
- terminal shell #1..N
- log stream #1..N
- port forwarding sessions
- cancellation scope
- capability snapshot
- connection/reconnect state
```

A blocked terminal or long log stream must not prevent dashboard refresh or file operations.

## 8. Connection lifecycle

States:

```text
Disconnected
Connecting
AwaitingHostTrust
Authenticating
Connected
Degraded
Reconnecting
Disconnecting
Failed
```

Features subscribe to normalized connection state and must tolerate reconnect/disconnect.

Automatic reconnect is allowed for safe channels. Mutating/destructive operations must not be blindly replayed after an ambiguous network failure because the remote operation may already have completed.

## 9. Capability architecture

`ServerCapabilities` records observed facts plus confidence/status, not just booleans.

Conceptually:

```text
Capability<T>
- state: Available | Unavailable | PermissionDenied | Unsupported | Unknown
- value/version/details
- detectedAt
- diagnostic reason
```

This prevents the UI from treating “probe failed” as “software absent”.

Capabilities are cached locally for startup UX but refreshed after connection and after package/tool changes.

## 10. Privilege model

Remote operations declare one of:

```text
ReadOnly
ElevatedRead
Mutating
Destructive
```

Privilege escalation must be scoped to individual operations. The desktop process itself does not run elevated just because the remote operation needs sudo.

Sudo behavior is represented as a capability/policy:

- not available;
- user not allowed;
- passwordless allowed for command;
- password required;
- cached remote sudo ticket may exist.

Sudo passwords are handled through secure transient memory/UI flow and are never persisted in normal metadata/logs.

## 11. File mutation architecture

Normal user-writable files use SFTP operations.

Privileged save uses a transaction-like workflow:

```text
read metadata
-> create remote temporary candidate under controlled path
-> upload candidate
-> optional validator
-> optional backup
-> privileged atomic install/rename
-> restore/preserve owner, group, mode
-> verify hash/metadata/content as appropriate
-> cleanup temp
-> audit
```

If rollback is safe and failure occurs after replacement, restore the backup. If state is ambiguous, stop and surface the ambiguity instead of guessing.

## 12. Retry policy

Automatic retry is allowed only when the action is demonstrably safe/idempotent.

Examples:

- retry a read-only capability probe: generally safe;
- retry directory listing: safe;
- retry service restart after connection loss: not automatically safe without remote state reconciliation;
- retry volume deletion: forbidden automatically.

Retry policy belongs to operation metadata/infrastructure policy, not ad hoc loops in ViewModels.

## 13. Local persistence

SQLite contains non-secret product state such as profiles, groups, UI preferences, history, and cached capabilities.

Use migrations/versioning from the moment persistent schema is introduced.

A `CredentialReference` stores only an opaque reference to the Windows secret store.

## 14. Audit model

Important remote state changes create an operation record:

- timestamp;
- server profile ID;
- operation type;
- target resource identity;
- result;
- duration;
- non-secret diagnostic summary;
- optional correlation ID.

Do not record passwords, tokens, private keys, full sensitive environment values, or sensitive file contents.

## 15. UI threading and async rules

- Never block the WPF UI thread on network/process I/O.
- All long operations support cancellation where meaningful.
- Collections with large remote datasets use virtualization/batching.
- Streaming updates are throttled/coalesced before UI binding.
- Dispose remote streams/session resources deterministically.

## 16. Extensibility

New distro support adds adapters/fixtures/tests rather than conditionals throughout the app.

New remote transport (future agent) implements existing application ports where possible.

Example:

```text
IProcessManager
  |- SshProcessManager
  `- AgentProcessManager
```

Feature ViewModels should not care which transport produced the normalized process model.

## 17. Project evolution

Bootstrap starts intentionally small:

```text
ServerDesk.App
ServerDesk.Application
ServerDesk.Domain
```

Projects are split when implementation begins to need a boundary, avoiding dozens of empty projects on day one. The target structure is described in the product plan and roadmap.

## 18. Architecture decision records

Non-trivial irreversible decisions require ADRs under `docs/adr/`.

An ADR should document:

- context;
- options;
- decision;
- consequences;
- migration/reversal considerations.

Changes that require an ADR include replacing WPF, changing the remote control plane, changing secret storage, introducing a mandatory server daemon, changing persistence technology, or bypassing the application abstraction boundaries.
