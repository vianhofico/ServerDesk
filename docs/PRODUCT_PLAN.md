# ServerDesk Product Plan

**English** | [Tiếng Việt](PRODUCT_PLAN.vi.md)

## 1. Product definition

ServerDesk is a Windows desktop application that provides a visual, Windows-like control surface for Linux servers while using standard secure remote administration mechanisms underneath.

The product goal is not to turn Linux into a Windows desktop and not to hide the existence of Linux. The goal is to make the most common server operations discoverable, visual, safe, and reversible where possible.

The expected mental model is:

```text
Windows File Explorer   -> Remote Explorer
Windows Task Manager    -> Processes + Performance
services.msc            -> Services
Event Viewer            -> Logs
Task Scheduler          -> Cron + systemd timers
Docker Desktop          -> Remote Docker Manager
CMD/PowerShell          -> SSH Terminal
Disk Management         -> Storage
Windows Firewall UI     -> UFW/firewalld abstraction
IIS Manager-like forms  -> nginx management (with raw config escape hatch)
```

## 2. Primary users

### Developer

Needs to connect to development/staging/production hosts, inspect files and logs, restart services, manage containers, create tunnels, and deploy without remembering every Linux command.

### DevOps / Sysadmin-lite

Needs process/service/storage/network/firewall/user/package visibility and safe routine administration.

### Learner

Needs a GUI that reveals the underlying Linux concept rather than replacing it with unexplained magic. Advanced/raw views must remain available.

## 3. Non-goals for V1

ServerDesk V1 is not:

- a Kubernetes IDE;
- an AWS/Azure/GCP console;
- a full Navicat/DataGrip replacement;
- a full Git IDE;
- a SaaS monitoring platform;
- a team collaboration service;
- a remote desktop protocol for rendering a Linux graphical desktop;
- an AI agent product;
- a mandatory server daemon.

These areas may gain integrations later without compromising the local-first desktop architecture.

## 4. Product principles

### 4.1 Agentless first

The initial product requires only a reachable SSH service and valid credentials. This keeps installation simple and avoids opening a proprietary management port.

### 4.2 GUI first, terminal always available

Common workflows must be achievable through visual controls. Complex or unsupported workflows remain possible through a real interactive terminal and raw configuration editors.

### 4.3 Capability-driven behavior

The client detects what the remote server can actually do. A feature is displayed as available only when its required capability is present and supported.

Examples:

- Docker UI requires a usable Docker CLI/daemon and permission.
- Services UI prefers systemd and must not pretend SysV-only systems are fully supported.
- Firewall module selects UFW/firewalld adapters only when detected.
- nginx module appears only when nginx tooling/config can be identified.

### 4.4 Secure by default

Known-host verification, least privilege, secret separation, safe tunneling, and destructive confirmation are product behavior, not optional settings.

### 4.5 Safe mutations

Remote mutations are classified by risk and follow a validation/verification workflow. Config edits should be atomic where possible. Destructive operations are never silently retried.

### 4.6 Honest compatibility

ServerDesk certifies only tested OS/version/feature combinations. Unknown systems may run in best-effort mode but are never advertised as fully supported.

## 5. Technical baseline

### Desktop

- Windows 10/11
- .NET 10
- WPF
- MVVM-oriented design
- Fluent/Windows 11 visual language
- WebView2 for embedded web-based terminal/editor surfaces where appropriate

### Remote transport

- SSH for commands
- SFTP for file operations
- SCP only where specifically useful
- SSH local/remote/dynamic forwarding
- SSH interactive shell/PTY

### Local state

- SQLite for profiles/preferences/history/capability cache
- Windows Credential Manager and/or DPAPI-protected storage for secrets
- secrets referenced by ID from SQLite, never persisted there directly

### Later optional agent

- `serverdesk-agent`
- gRPC + Protobuf
- bound to loopback by default
- reached through an SSH tunnel so no public management port is required

## 6. Core user experience

### 6.1 Home / server list

Users can group, tag, favorite, search, add, edit, clone, import, and remove server profiles.

Each server card shows:

- friendly name;
- hostname/IP;
- environment color/tag;
- last known OS;
- online/offline/connecting/reconnecting state;
- recent latency or last connection time;
- favorite status.

### 6.2 Connection profile

Required profile capabilities:

- hostname/IPv4/IPv6;
- port;
- username;
- password auth;
- private-key auth;
- encrypted private-key passphrase;
- SSH agent where implementation permits;
- keyboard-interactive/MFA;
- proxy;
- jump/bastion host;
- keepalive;
- timeout;
- startup directory;
- environment/group/tags.

Unknown host keys require explicit trust. Changed host keys block the connection until the user explicitly resolves the mismatch.

### 6.3 Server workspace

A connected server opens a persistent workspace with server identity and connection state always visible.

Primary navigation:

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
Scheduled Tasks
Git
Nginx
Security
Database
Backups
Settings
```

Items unavailable on the current server are hidden or disabled with an explanation depending on discoverability needs.

## 7. Capability detection

On connection, collect a normalized `ServerCapabilities` snapshot using safe read-only probes.

Expected information:

- OS ID/version/name from `/etc/os-release`;
- kernel and architecture;
- current user and groups;
- sudo availability and mode;
- systemd availability;
- Docker and Compose availability;
- nginx/Apache presence;
- Git;
- UFW/firewalld;
- PostgreSQL/MySQL/MariaDB/Redis tooling;
- common system tools required by adapters.

Capability detection must distinguish:

- executable not installed;
- installed but service unavailable;
- installed but permission denied;
- installed but unsupported version/format;
- unknown due to command failure.

## 8. Remote Explorer

Remote Explorer is a flagship feature and should feel familiar to Windows users.

Required V1 capabilities:

- directory navigation;
- breadcrumbs and address bar;
- back/forward/up;
- hidden files toggle;
- sorting and filtering;
- multi-select;
- create file/folder;
- rename;
- copy/move;
- upload/download;
- drag/drop from Windows;
- delete with risk-appropriate confirmation;
- owner/group/mode view;
- chmod/chown through safe privileged operations;
- symlink identification;
- file properties;
- copy remote path;
- open terminal at path;
- checksum when needed.

Large directories must stream/paginate/virtualize rather than freezing the UI.

## 9. Remote editor

The editor supports common server configuration and source formats with syntax highlighting, search/replace, diff, formatting where safe, and undo/redo.

Important privileged save flow:

```text
read original metadata
-> download/read content
-> edit locally/in memory
-> upload temporary file to writable remote temp location
-> validate if file type has a validator
-> backup original when policy requires
-> privileged atomic install/replace
-> preserve owner/group/mode
-> verify
-> remove temp
-> audit
```

Never work around permissions by broadening remote file permissions.

## 10. Terminal

The terminal must be a real PTY-backed SSH experience, not a textbox that runs one command at a time.

Required:

- ANSI/VT behavior;
- resize;
- scrollback;
- copy/paste;
- multiple tabs;
- multiple concurrent sessions to one server;
- search;
- configurable fonts;
- reconnect/closed-session state;
- keyboard-first usability.

Planned implementation: xterm.js hosted in WebView2 connected to an SSH shell stream.

## 11. Dashboard / performance

Show:

- CPU;
- memory and swap;
- load average;
- uptime;
- filesystem usage;
- network throughput;
- process summary;
- service summary;
- container summary;
- available updates when package capability exists;
- warnings such as low disk space.

Prefer Linux kernel/system sources and structured commands over parsing `top`.

Expected sources include `/proc/stat`, `/proc/meminfo`, `/proc/loadavg`, `/proc/net/dev`, structured `lsblk`, and filesystem queries.

## 12. Process manager

Task-Manager-like table:

- PID;
- process name;
- user;
- CPU;
- memory;
- command line;
- start time where available;
- state;
- parent process.

Actions:

- view details;
- terminate;
- force kill with stronger confirmation;
- inspect related listening ports;
- open related logs/terminal when discoverable.

## 13. Services

Systemd-first service management:

- list/search/filter;
- active/inactive/failed;
- enabled/disabled/masked;
- start/stop/restart/reload;
- enable/disable;
- details and unit file paths;
- recent logs;
- explicit sudo/permission state.

Use machine-oriented `systemctl` properties rather than parsing decorated `status` output.

## 14. Logs

Unified viewer over:

- journald;
- regular log files;
- Docker logs;
- later database/application integrations.

Required:

- realtime follow;
- pause/resume;
- time range;
- service/source selection;
- severity filters when source supports severity;
- text search;
- highlight;
- export;
- bookmark;
- safe handling of very high-volume streams.

## 15. Docker

Docker is a major V1 differentiator.

Navigation:

```text
Containers
Images
Compose
Volumes
Networks
System
```

Container features:

- list/status;
- start/stop/restart/pause/kill;
- remove;
- inspect;
- stats;
- logs;
- terminal/exec;
- environment display with sensitive-value treatment;
- ports;
- mounts;
- networks.

Compose:

- discover projects;
- up/down/restart/pull/build;
- service list/status;
- logs;
- compose file editor;
- config validation before apply.

Do not expose the Docker Unix socket over the network. Use remote CLI through SSH for agentless mode.

## 16. Storage

Disk-Management-like experience:

- block devices;
- filesystems;
- mounts;
- size/used/free;
- inode warnings where useful;
- disk usage analyzer by directory;
- Docker/log/database storage hints;
- read-only hardware/filesystem facts where supported.

High-risk partitioning/filesystem mutation is not part of initial V1 unless separately designed and tested.

## 17. Network and ports

Show:

- interfaces;
- addresses;
- RX/TX rates;
- routes when useful;
- listening ports;
- protocol;
- bound address;
- associated PID/process when permissions permit.

Actions can link a port to process/service views and to SSH tunnel creation.

## 18. SSH tunnel manager

Support reusable profiles for:

- local forwarding;
- remote forwarding;
- dynamic/SOCKS forwarding.

Typical database workflow:

```text
Windows localhost:5433 -> encrypted SSH tunnel -> server 127.0.0.1:5432
```

The UI must display tunnel state and make it clear which local port is exposed on the Windows machine.

## 19. Scheduled tasks

Unify cron and systemd timers into a task-oriented experience while preserving the underlying type.

Features:

- list;
- enable/disable where applicable;
- create/edit basic schedules;
- last/next execution where available;
- command/unit details;
- logs;
- validation;
- raw view for advanced syntax.

## 20. Git deployment helper

Git support is operational, not a replacement for a full Git client.

Required scope:

- repository discovery at configured paths;
- branch and revision;
- working-tree changes;
- ahead/behind;
- fetch;
- pull when safe/explicit;
- diff viewer;
- links into deployment workflow.

No automatic destructive reset of working trees.

## 21. nginx

Use a hybrid simple/advanced model.

Simple mode covers common server blocks:

- domain/server name;
- listening ports;
- reverse proxy target;
- WebSocket proxy options;
- static root;
- redirects;
- certificate association.

Advanced mode exposes raw configuration editing.

Mutation workflow:

```text
prepare candidate
-> back up relevant config
-> validate with `nginx -t`
-> reject and preserve current live config on failure
-> install/activate
-> reload
-> verify service state
-> audit
```

Do not attempt to model every nginx directive as a form.

## 22. SSL

Later deployment milestone functionality:

- certificate inventory;
- expiration warnings;
- association with nginx sites;
- Certbot detection;
- explicit issue/renew actions;
- renewal logs;
- no assumption that every certificate is Let's Encrypt managed.

## 23. Firewall

Application abstraction: `IFirewallManager`.

Initial adapters:

- UFW;
- firewalld.

Features:

- current state;
- normalized rules;
- add/delete rule;
- source/port/protocol/action;
- preview generated change;
- protection against obviously locking out the current SSH session without explicit override.

## 24. Users and groups

Administration milestone:

- users/groups;
- shell/home;
- locked/unlocked;
- group membership;
- SSH authorized keys management;
- sudo access visibility;
- create/lock/unlock/change groups through guarded privileged workflows.

Do not provide a casual one-click root-enablement path.

## 25. Package updates

Abstraction: `IPackageManager`.

Initial implementations:

- APT family;
- DNF family.

Features:

- refresh metadata explicitly;
- list updates;
- distinguish security updates where the distro exposes reliable metadata;
- selected update/install/remove actions;
- output/logs;
- never auto-update production servers by default.

## 26. Databases

V1.x scope deliberately stays smaller than a database IDE.

Initial engines:

- PostgreSQL;
- MySQL/MariaDB;
- Redis.

Capabilities:

- service/status;
- version;
- data size overview where reliable;
- connection profile through SSH tunnel;
- backup/restore workflows;
- logs;
- later query console.

## 27. Backup and restore

Backup targets can include:

- configuration files;
- application directories;
- database dumps;
- selected Docker volumes through explicit strategies.

A backup job defines:

- source;
- type;
- destination;
- schedule;
- retention;
- optional encryption;
- verification strategy.

Restore must preview target and overwrite implications before execution.

## 28. Multi-server management

After single-server workflows are stable:

- global server dashboard;
- groups/tags;
- search;
- favorites;
- warnings;
- compare selected configuration facts;
- carefully scoped bulk operations.

Bulk destructive operations require additional safeguards and are not inherited automatically from single-server actions.

## 29. Optional server agent

Agent mode exists to improve performance/realtime behavior, not to replace secure SSH administration.

Expected topology:

```text
ServerDesk Windows client
  |-- SSH/SFTP/PTY for standard operations
  `-- SSH local tunnel -> serverdesk-agent on 127.0.0.1
```

Agent responsibilities may include:

- streaming metrics;
- process/service event streams;
- Docker events;
- log streaming;
- normalized storage/network data;
- fewer repetitive SSH process launches.

The agent must not require a publicly reachable management listener by default.

## 30. Local data model

Planned non-secret entities:

- `ServerProfile`;
- `ServerGroup`;
- `KnownHost`;
- `CredentialReference`;
- `FavoritePath`;
- `RecentConnection`;
- `TerminalProfile`;
- `PortForwardProfile`;
- `SavedCommand`;
- `UiSettings`;
- `CapabilityCache`;
- `OperationHistory`.

Sensitive secrets are externalized to secure OS storage.

## 31. Operation risk model

Every remote operation is classified:

- `ReadOnly` — normal inspection;
- `ElevatedRead` — needs privilege to inspect protected data;
- `Mutating` — changes state but is normally reversible/recoverable;
- `Destructive` — may irreversibly delete data or access.

Risk classification influences confirmation, audit, retry behavior, and UI prominence.

## 32. Error model

Normalize infrastructure failures into application-level errors such as:

- `ConnectionFailed`;
- `AuthenticationFailed`;
- `HostKeyUnknown`;
- `HostKeyMismatch`;
- `PermissionDenied`;
- `SudoRequired`;
- `CommandNotFound`;
- `CapabilityUnavailable`;
- `CommandTimeout`;
- `CommandFailed`;
- `ParseFailed`;
- `NetworkInterrupted`;
- `OperationCancelled`.

User-facing UI shows a concise explanation/action. Technical details remain expandable for debugging.

## 33. Performance goals

Initial targets, measured on representative hardware/network:

- desktop cold start: approximately <= 2 s target;
- typical LAN SSH connection: approximately <= 3 s target;
- dashboard first useful data: <= 2 s after authentication target;
- normal Explorer directory load: <= 500 ms when server/network permit;
- active CPU/network metric cadence: about 1 s;
- Docker stats cadence: about 2 s;
- UI remains responsive at all times.

These are engineering targets, not guarantees against remote latency.

## 34. V1 exit criteria

A V1 release requires end-to-end, documented, tested workflows for:

- secure connection and known hosts;
- profile/credential handling;
- SFTP Explorer;
- privileged file editing;
- interactive terminal;
- dashboard/performance;
- processes;
- services;
- logs;
- storage;
- network/ports;
- SSH tunnels;
- Docker and Compose;
- basic Git operations;
- basic nginx management;
- capability detection;
- sudo handling;
- reconnect/offline states;
- operation history;
- dark/light/system theme;
- crash-safe local state;
- certified support matrix gates.

V1 is not declared complete merely because every navigation item exists.

## 35. Delivery sequence

The required milestone order is defined in `docs/ROADMAP.md`:

```text
M0 Foundation
M1 Remote Core
M2 Windows-like Server UI
M3 DevOps
M4 Deployment
M5 Administration
M6 Databases
M7 Multi-server
M8 Optional Agent
```

Each milestone has acceptance gates and must leave the repository in a releasable/understandable state.
