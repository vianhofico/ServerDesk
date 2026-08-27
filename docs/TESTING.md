# Testing Strategy

## 1. Goal

ServerDesk cannot claim reliability by unit tests alone because it depends on SSH behavior, Linux tooling, permissions, distro output, network failures, and destructive remote state transitions. The test strategy is layered so fast tests protect design and real-environment tests protect compatibility.

## 2. Test pyramid

### Unit tests

Cover:

- domain models/value objects;
- capability state transitions;
- operation risk policy;
- command specifications/builders;
- parsers;
- validation;
- retry decisions;
- rollback orchestration;
- error mapping;
- ViewModel behavior not requiring live WPF integration.

### Fixture/parser tests

Every machine-output parser keeps representative fixtures from affected certified distros/versions.

Fixtures must include:

- normal output;
- empty output;
- malformed/truncated output;
- permission errors when applicable;
- version variations known to differ;
- unusual but valid names/paths.

Parser tests must not “fix” unexpected output by guessing silently.

### Adapter tests

Run Linux adapter behavior against the distro family it claims to support.

Examples:

- `/etc/os-release` detection;
- systemd property queries;
- APT/DNF inventory parsing;
- UFW/firewalld normalization;
- standard file metadata operations.

### SSH integration tests

Use disposable Linux environments/VMs with a real SSH daemon.

Cases:

- password auth;
- key auth;
- encrypted key/passphrase;
- keyboard-interactive where harness supports it;
- unknown host;
- changed host fingerprint;
- permission denied;
- command timeout;
- dropped TCP connection;
- SFTP upload/download/rename/delete;
- PTY open/resize/close;
- port forward lifecycle;
- concurrent command/SFTP/terminal channels.

### Feature integration tests

Run real supported tooling in disposable environments:

- systemd services;
- Docker/Compose;
- nginx validation/reload;
- journald/log streaming;
- firewall adapters;
- package manager read operations;
- database backup/restore test instances.

Destructive tests never target shared or persistent infrastructure.

### UI tests

Automate critical user journeys after the UI automation harness exists:

- create profile;
- connection/trust flow;
- Explorer navigation;
- upload/download;
- edit/save;
- restart service;
- open/filter logs;
- Docker start/stop/logs;
- disconnect/reconnect;
- cancellation/error recovery;
- theme switching and major keyboard navigation.

Visual snapshot testing may supplement but cannot replace behavioral assertions.

## 3. Certified compatibility environments

The support matrix controls required environments. Initial certification targets are Ubuntu/Debian, followed by Rocky/AlmaLinux.

Tests must record exact image/VM version. “Latest Linux” is not a valid certification label.

## 4. CI tiers

### Pull request CI

Fast enough for every PR:

```text
restore
build
format/static analysis
unit tests
parser fixtures
security-oriented unit tests
```

### Extended integration CI

Run on relevant PRs or dedicated workflow:

```text
SSH integration
selected Linux adapter integration
Docker/nginx feature integration
```

### Nightly compatibility CI

Run the complete certified matrix where infrastructure permits:

```text
Ubuntu versions
Debian versions
Rocky versions
AlmaLinux versions
feature fixtures
failure injection
```

Generate a compatibility report rather than hiding skipped environments.

## 5. Failure-injection requirements

Remote software must deliberately test ambiguous failures:

- connection drops before command sent;
- drops after command may have been sent;
- drops while streaming output;
- SFTP interrupted mid-transfer;
- disk full;
- permission changes during operation;
- service command returns failure;
- validator rejects candidate config;
- remote process hangs;
- malformed/unsupported CLI output.

The test should prove that ServerDesk does not incorrectly retry destructive operations or report success without verification.

## 6. File mutation tests

Privileged file-save workflow must test:

- owner/group/mode preserved;
- validation fail leaves original unchanged;
- backup created when policy requires;
- atomic candidate installation where supported;
- temp cleaned on success;
- best-effort cleanup on failure;
- insufficient sudo permissions;
- target changes concurrently (future conflict detection where implemented).

## 7. Security tests

Required categories:

- shell/argument injection strings in paths/names;
- secret redaction in logs/errors/history;
- unknown/changed SSH host key;
- unsafe WebView message payloads;
- destructive confirmation cancellation;
- database/Docker environment secret display policy;
- firewall change that may affect current SSH access;
- path traversal assumptions in local download destinations.

## 8. Performance tests

Measure representative workloads:

- startup;
- server connect/capability scan;
- 10k-file directory listing;
- high-rate logs;
- hundreds/thousands of processes;
- many Docker containers;
- dashboard streaming for multiple minutes;
- multiple terminal tabs;
- concurrent SFTP transfer and metrics.

Primary assertion: the UI remains responsive and memory/connection resources do not grow without bound.

## 9. Manual exploratory checklist before release

On every certified OS family:

- connect from a clean Windows install/user profile;
- trust host key;
- reconnect after app restart;
- Explorer CRUD and permissions;
- terminal interactive tools;
- process/service controls;
- logs;
- storage/network views;
- Docker/Compose if certified;
- nginx if certified;
- tunnel database connection;
- disconnect during a safe operation and a mutation;
- light/dark/system theme;
- keyboard-only pass over core workflow.

## 10. Definition of tested

A feature is not “tested” solely because a mocked happy-path unit test exists.

For remote features, completion requires the combination appropriate to risk:

```text
unit behavior
+ parser fixtures
+ adapter/integration evidence
+ negative/failure cases
+ UI workflow test when harness exists
```
