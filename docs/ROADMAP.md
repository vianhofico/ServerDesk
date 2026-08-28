# Roadmap and Milestone Gates

**English** | [Tiếng Việt](ROADMAP.vi.md)

This roadmap is ordered. Later milestones must not be used to bypass unfinished architecture, security, test, or UX requirements in earlier milestones.

## M0 — Foundation

### Goal

Create a buildable Windows desktop foundation and freeze the implementation contracts agents must follow.

### Scope

- .NET 10 WPF shell;
- Domain/Application project boundaries;
- dependency injection composition strategy;
- navigation shell placeholder;
- system/light/dark theme foundation;
- common result/error primitives;
- logging policy;
- local data/secret-store interfaces;
- repository docs and ADR process;
- CI build;
- PR/issue templates;
- test project/test infrastructure plan.

### Exit criteria

- repository builds from a clean Windows runner;
- no secret persistence is implemented incorrectly;
- UI shell launches;
- architecture/security/UX/agent documents exist and agree;
- CI required checks are green;
- M1 interfaces can be implemented without changing Domain's dependency direction.

---

## M1 — Remote Core

### Goal

Make ServerDesk a secure, reliable SSH/SFTP client foundation before adding management features.

### Scope

- server profiles/groups/tags;
- Windows secure credential implementation;
- SSH connection lifecycle;
- password authentication;
- private-key authentication;
- passphrase handling;
- keyboard-interactive/MFA;
- known-host store;
- unknown host trust UI;
- changed fingerprint blocking;
- keepalive/timeouts;
- reconnect state;
- jump/bastion support;
- proxy support where chosen SSH library supports it;
- SFTP abstraction;
- PTY terminal using WebView2 + xterm.js;
- multiple terminal sessions;
- local/remote/dynamic forwarding;
- capability detection baseline;
- connection audit without secrets.

### Required tests

- successful password/key connections;
- wrong password/key;
- encrypted key;
- unknown host;
- changed host key;
- timeout;
- abrupt disconnect;
- reconnect;
- SFTP permission error;
- terminal resize/close lifecycle;
- tunnel open/close;
- secret redaction.

### Exit criteria

A user can securely save a server profile, connect, verify trust, browse SFTP, use a real terminal, create a tunnel, disconnect/reconnect, and receive useful errors without using a raw developer debug view.

---

## M2 — Windows-like Server UI

### Goal

Deliver the core differentiator: routine Linux inspection/management through familiar Windows-like UI.

### Scope

#### Dashboard
- CPU;
- memory/swap;
- load;
- uptime;
- network throughput;
- filesystem summary;
- warnings.

#### Explorer
- complete remote navigation;
- file/folder create/rename/copy/move/delete;
- upload/download;
- drag/drop;
- permissions/owner/group properties;
- large directory virtualization;
- privileged file save transaction.

#### Editor
- syntax highlighting;
- find/replace;
- diff before privileged save;
- validators extension point.

#### Processes
- normalized process list;
- CPU/RAM/user/state;
- terminate/kill with risk distinction.

#### Services
- systemd list/search/details;
- start/stop/restart/reload;
- enable/disable;
- logs link.

#### Storage
- filesystems/mounts;
- block device overview;
- disk usage analyzer;
- low-space warnings.

#### Network
- interfaces;
- addresses;
- rates;
- listening ports;
- process association where possible.

#### Logs
- journald and file log viewer;
- realtime follow;
- pause/filter/search/export.

### Exit criteria

A supported Ubuntu/Debian server can be routinely diagnosed and managed for files, processes, services, logs, storage, and network without requiring the user to manually type commands.

---

## M3 — DevOps

### Goal

Cover the workflows developers most frequently perform after SSHing to an application server.

### Scope

#### Docker
- detect Docker availability/permission;
- containers;
- images;
- volumes;
- networks;
- inspect;
- stats;
- logs;
- terminal/exec;
- start/stop/restart/pause/kill/remove;
- safe sensitive environment display.

#### Docker Compose
- project discovery/configuration;
- services;
- up/down/restart;
- pull/build;
- logs;
- YAML editor;
- validation.

#### Git operational helper
- repository status;
- branch/revision;
- diff;
- fetch;
- ahead/behind;
- explicit safe pull workflow.

#### Scheduled tasks
- cron;
- systemd timers;
- normalized list;
- basic editor;
- logs/history where available.

### Exit criteria

A developer can diagnose/restart/redeploy the normal containerized application stack through ServerDesk without exposing Docker socket or database ports publicly.

---

## M4 — Deployment

### Goal

Provide safe application exposure/deployment workflows.

### Scope

- nginx discovery and configuration inventory;
- simple reverse-proxy/site editor;
- raw advanced editor;
- `nginx -t` validation gate;
- backup/atomic apply/reload/verify/rollback;
- SSL certificate inventory;
- Certbot integration when detected;
- expiration warnings;
- environment-file guarded editing;
- deployment workflow orchestration;
- restart and health verification hooks;
- explicit rollback strategy for supported deployment type.

### Exit criteria

A user can configure a common reverse proxy/HTTPS deployment without ServerDesk ever replacing a valid live nginx config with a known-invalid candidate.

---

## M5 — Administration

### Goal

Cover routine host administration with stronger safety controls.

### Scope

- firewall abstraction;
- UFW adapter;
- firewalld adapter;
- users/groups;
- authorized SSH keys;
- account lock/unlock;
- group/sudo membership visibility;
- package manager abstraction;
- APT adapter;
- DNF adapter;
- update inventory;
- explicit package operations;
- backup/restore framework;
- operation audit UI.

### Exit criteria

Routine administration is possible without casual lockout/root-enablement hazards, and dangerous operations have explicit safety UX and test coverage.

---

## M6 — Databases

### Goal

Add server-oriented database administration without trying to become a full database IDE.

### Scope

- PostgreSQL status/version/size/log basics;
- MySQL/MariaDB status/version/size/log basics;
- Redis status/version/memory/log basics;
- SSH-tunneled connection profiles;
- backup;
- restore with preview/confirmation;
- optional basic query console after backup/restore is stable.

### Exit criteria

Users can inspect and safely back up/restore supported databases without exposing their ports publicly.

---

## M7 — Multi-server

### Goal

Scale proven single-server workflows to multiple environments without making bulk operations unsafe.

### Scope

- global server dashboard;
- groups/tags/favorites;
- health/warning summary;
- global search/navigation;
- safe comparison of selected facts;
- carefully approved bulk read operations;
- narrowly scoped bulk mutations with separate risk review;
- import/export of non-secret profile metadata;
- profile secret references remain local/secure.

### Exit criteria

Users can manage many servers while every operation still makes target server identity obvious and bulk destructive actions cannot occur accidentally.

---

## M8 — Optional ServerDesk Agent

### Goal

Improve realtime performance and normalize complex metrics/events without changing the product's secure connection model.

### Scope

- `serverdesk-agent` Linux service;
- gRPC/Protobuf contract;
- loopback-only default listener;
- SSH tunnel bootstrap;
- version/capability negotiation;
- metrics streaming;
- process/service events;
- Docker events;
- log streaming;
- transport fallback to agentless mode;
- secure upgrade/uninstall plan.

### Exit criteria

Agent mode improves realtime behavior but every supported core operation still degrades gracefully to the documented agentless implementation where applicable.

---

# Cross-milestone release gates

Every milestone must satisfy applicable items:

- clean build;
- tests green;
- no new high-severity security finding;
- no secrets in repository/log fixtures;
- no host-key bypass;
- typed errors for new infrastructure failures;
- loading/empty/error/disconnect/cancel UI states;
- dark/light/system themes reviewed;
- keyboard-accessible primary workflow;
- support matrix updated;
- architecture docs/ADR updated when contracts change;
- no unrelated known failing required CI.

# Issue sizing rule

A milestone is an epic, not a single coding PR.

Preferred issue size: one reviewable vertical slice. Examples:

```text
M1: known-host storage and trust workflow
M1: password/key authentication
M1: PTY terminal foundation
M2: /proc CPU+memory data service and dashboard cards
M2: Explorer directory navigation
M2: privileged file save transaction
M3: Docker container inventory
M3: container logs stream
```

If an issue requires touching more than a few architectural areas or cannot be tested independently, split it before implementation.
