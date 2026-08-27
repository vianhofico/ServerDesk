# AGENTS.md — ServerDesk Agent Contract

This file is mandatory for every coding agent working in this repository. It is an execution contract, not optional guidance.

## 1. Mission

Build ServerDesk as a production-grade Windows desktop application that makes Linux server administration visual and approachable without hiding security, permissions, or failure states.

The app must not become a collection of buttons that execute arbitrary shell strings. The architecture must remain typed, capability-aware, testable, distro-adaptable, and safe for production servers.

## 2. Source-of-truth priority

When instructions conflict, use this order:

1. Explicit task/issue requirements
2. `AGENTS.md`
3. `docs/ARCHITECTURE.md`
4. `docs/SECURITY.md`
5. `docs/UI_UX.md`
6. `docs/ROADMAP.md`
7. `docs/PRODUCT_PLAN.md`
8. Existing implementation conventions

If a task requires violating items 2–5, stop implementation and document the conflict in the PR instead of silently bypassing the rule.

## 3. Mandatory agent workflow

Every implementation task must follow these steps in order.

### Step 0 — Synchronize context

Before coding:

- Read this file.
- Read the issue/task completely.
- Read the relevant architecture/security/UI/testing docs.
- Inspect existing code in the affected modules.
- Inspect currently open PRs touching the same area when available.
- Do not assume a feature is missing until repository search confirms it.

### Step 1 — Define the change boundary

Write down internally before editing:

- user-visible outcome;
- affected projects/modules;
- capability requirements;
- privilege level: `ReadOnly`, `ElevatedRead`, `Mutating`, or `Destructive`;
- supported distro impact;
- expected error/failure states;
- required tests.

Avoid unrelated cleanup. One task/PR should have one coherent goal.

### Step 2 — Validate architecture placement

Dependencies must point inward:

```text
App/UI -> Application -> Domain
Infrastructure -> Application/Domain
Linux adapters -> Application/Domain
Feature modules -> Application abstractions, never raw SSH implementation
```

Rules:

- UI must never assemble shell commands.
- UI must never parse remote command output.
- Domain must not reference WPF, SSH.NET, SQLite, WebView2, Docker SDKs, or distro-specific libraries.
- Application defines ports/abstractions and use cases.
- Infrastructure implements transport/persistence/platform concerns.
- Distro-specific commands belong in Linux adapters.

### Step 3 — Prefer structured remote data

Use, in order of preference:

1. stable kernel/proc/sys files;
2. explicit JSON output;
3. key/value or property output;
4. fixed machine-oriented format with explicit locale;
5. human text parsing only as a documented last resort.

Never parse colored/table-formatted CLI output intended for humans when a structured alternative exists.

When text parsing is unavoidable:

- force `LC_ALL=C` where appropriate;
- isolate the parser;
- add fixtures from every certified distro affected;
- fail closed with `ParseFailed` rather than guessing.

### Step 4 — Build commands safely

Never concatenate untrusted input into shell snippets.

All remote execution must flow through the command execution abstraction and a typed command specification containing at least:

- executable;
- argument list;
- environment/locale when necessary;
- timeout;
- cancellation;
- privilege requirement;
- output parser.

Do not write code equivalent to:

```csharp
RunCommand($"rm {path}");
```

Paths, names, container IDs, service names, usernames, and user input must be passed through validated/escaped argument handling appropriate to the remote command model.

### Step 5 — Apply safety workflow to mutations

For configuration and destructive changes, use the strongest applicable sequence:

```text
precondition -> preview -> confirmation -> backup/snapshot -> validate -> execute -> verify -> rollback on safe failure -> audit
```

Examples:

- nginx change: back up -> edit temp -> `nginx -t` -> atomic replace/reload -> verify;
- privileged file save: upload temp -> preserve owner/group/mode -> atomic install -> verify;
- destructive Docker volume delete: explicit resource-name confirmation and no automatic retry.

Never solve permissions with `chmod 777`.

### Step 6 — Implement UI states, not only happy path

Every async screen/action must define:

- loading;
- empty;
- success;
- recoverable error;
- permission/sudo required;
- capability unavailable;
- disconnected/reconnecting;
- cancellation.

No operation may freeze the WPF UI thread.

### Step 7 — Add tests before declaring complete

Minimum expectation:

- domain/application behavior: unit tests;
- command builder/parser: fixture tests;
- distro behavior: adapter tests;
- remote feature: integration tests where infrastructure exists;
- security-sensitive changes: negative tests;
- important UI workflow: UI automation when the UI test harness exists.

Do not weaken or delete tests to make CI pass unless the task explicitly changes the requirement and the PR documents why.

### Step 8 — Run quality gates

At minimum, run the repository-equivalent of:

```powershell
dotnet restore
dotnet build -c Release
# dotnet test -c Release   # once test projects are present
```

Also run format/static/security checks configured by the repository.

Warnings introduced by the change must be fixed. Do not blanket-disable analyzers.

### Step 9 — Self-review the diff

Before committing/review completion, inspect the complete diff and verify:

- no secrets/credentials/private keys;
- no debug-only bypasses;
- no host-key auto-accept;
- no shell injection path;
- no destructive automatic retry;
- no UI-to-SSH coupling;
- no distro logic leaking into UI;
- no unsupported feature silently shown as supported;
- no unrelated files.

### Step 10 — Produce a completion report

The PR/task report must state:

- what changed;
- architecture decisions;
- user-visible behavior;
- tests run and results;
- certified distro impact;
- security/safety considerations;
- known limitations/follow-ups.

## 4. Git workflow

Default branch: `main`.

Never develop directly on `main`.

Branch naming:

```text
feat/<issue>-short-name
fix/<issue>-short-name
refactor/<issue>-short-name
test/<issue>-short-name
docs/<issue>-short-name
chore/<issue>-short-name
```

For bootstrap work without an issue, a descriptive `chore/...` branch is acceptable.

Commit style:

```text
feat(explorer): add remote directory listing
fix(ssh): reject changed host fingerprint
test(docker): cover malformed inspect output
docs(agent): clarify destructive action policy
```

Prefer small, coherent commits. Never force-push shared branches unless explicitly requested.

PRs must target `main`, remain focused, and pass CI before merge. Prefer squash merge for normal feature work unless commit history itself is intentionally meaningful.

## 5. Issue execution rules

Roadmap milestones are ordered. An agent should select work from the earliest milestone whose prerequisites are complete.

For each issue:

1. verify prerequisites;
2. identify acceptance criteria;
3. split into sub-issues if it cannot be reviewed coherently;
4. implement one vertical slice at a time;
5. link follow-ups instead of silently expanding scope.

Do not start a later milestone to avoid finishing tests/security work in the current milestone.

## 6. Dependency policy

Before adding a package:

- verify it is actively maintained and compatible with the repository target framework;
- prefer BCL/Microsoft-supported capabilities when sufficient;
- document why the dependency is needed;
- avoid packages that duplicate an existing abstraction;
- pin/centrally manage versions once central package management is introduced;
- check license compatibility before product distribution.

Never introduce a cloud/backend dependency for a feature that can remain local-only unless the product plan explicitly requires it.

## 7. Security invariants

These rules are non-negotiable:

- never store passwords, sudo passwords, passphrases, or private-key contents in SQLite;
- never log secrets;
- never automatically trust unknown or changed SSH host keys;
- never expose Docker socket remotely to make the UI easier;
- never require public database ports when SSH tunneling is sufficient;
- never use plaintext temporary files for credentials;
- never run the entire application elevated by default;
- request privilege only for the remote operation that needs it;
- destructive actions require explicit user intent and resource identity.

Read `docs/SECURITY.md` before touching connection, credential, sudo, file mutation, Docker, firewall, user, package, SSL, backup, or update code.

## 8. UX invariants

ServerDesk is GUI-first but must remain honest about Linux state.

- Use Windows 11/Fluent interaction patterns.
- Keep connection and server identity visible.
- Explain permission/capability failures in user language and provide technical details separately.
- Avoid modal dialogs for normal information; use modal confirmation for genuinely risky decisions.
- Do not hide errors behind generic “Something went wrong”.
- Preserve advanced access through terminal/raw config views where forms cannot represent the full underlying system.
- A disabled feature must explain why it is unavailable.

See `docs/UI_UX.md`.

## 9. Compatibility rules

Certified support is defined only by `docs/SUPPORT_MATRIX.md` and passing automated/manual compatibility gates.

If code works on an unlisted distro, describe it as best-effort/experimental, not certified.

Feature code must query capabilities instead of checking distro names when the capability itself is what matters.

## 10. Definition of Done

A feature is not done when “the button works”. It is done only when all applicable items are satisfied:

- architecture boundary respected;
- UI complete for required states;
- dark/light/system theme behavior acceptable;
- keyboard-accessible primary workflow;
- capability detection handled;
- permissions/sudo handled;
- cancellation/timeout handled;
- disconnect/reconnect handled;
- errors mapped to typed/user-safe errors;
- no secrets logged/stored incorrectly;
- tests added and passing;
- certified distro fixtures/integration updated;
- docs changed when behavior/contracts changed;
- CI green;
- self-review complete.

## 11. What agents must never do

- Implement the entire roadmap in one giant PR.
- Skip a required prerequisite because a later feature is more interesting.
- Replace architecture abstractions with direct SSH calls in ViewModels.
- Introduce broad `sudo sh -c` usage as a shortcut.
- “Fix” permissions globally.
- Auto-delete/auto-prune user data.
- Auto-update production packages/services without explicit action.
- Auto-retry destructive operations where the first request may already have succeeded.
- Claim universal Linux support without tests.
- merge a PR with known failing required checks.

## 12. Product milestone order

The canonical sequence is:

```text
M0 Foundation
M1 Remote Core
M2 Windows-like Server UI
M3 DevOps
M4 Deployment
M5 Administration
M6 Databases
M7 Multi-server
M8 Optional ServerDesk Agent
```

See `docs/ROADMAP.md` for exact acceptance gates.
