# ServerDesk V1 User Guide

**English** | [Tiếng Việt](USER_GUIDE.vi.md)

This guide explains the delivered Windows client module by module. Labels may vary slightly with localization/version, but the workflows and safety boundaries reflect the current V1 implementation.

> For mutations such as delete, restart, firewall, package changes or restore, review the preview/confirmation. If ServerDesk reports an `Ambiguous` result after timeout/network loss, **refresh/re-observe before retrying**.

## 1. Install and run

The V1.0.0 release publishes a self-contained Windows x64 ZIP.

1. Open the repository's GitHub **Releases** page.
2. Download `ServerDesk-v1.0.0-win-x64.zip`.
3. Extract it to a writable user directory.
4. Run `ServerDesk.App.exe`.
5. If a WebView-backed terminal requires WebView2 Runtime and it is not present, install Microsoft Edge WebView2 Runtime and reopen ServerDesk.

Build from source:

```powershell
dotnet restore ServerDesk.sln
dotnet build ServerDesk.sln -c Release
```

## 2. Create a server profile and connect

1. From the server list, create a new profile.
2. Enter friendly name, host/IP, SSH port and username.
3. Select authentication: password, private key, encrypted private key/passphrase, or keyboard-interactive/MFA where required.
4. Optionally assign group, tags, environment and favorite state.
5. Save and connect.

Secrets belong in the secure credential abstraction, not profile names/tags/notes.

### Host-key trust

For a new host, verify the fingerprint before trusting it. A changed host key must block normal connection until the mismatch is explicitly verified/resolved; do not accept a replacement key merely to make the warning disappear.

## 3. Organize profiles

Use Profile Organization for groups, tags, favorites, search/filter, cloning and supported metadata import/organization workflows. Credential references remain separate from ordinary profile metadata.

## 4. Connection routing

Use Connection Route when the target is not directly reachable:

- **Direct** — connect straight to the target.
- **Proxy** — use a supported proxy route.
- **Bastion/Jump** — route through an intermediate SSH host.

Verify the final target identity before production mutations. Connection history is useful context, not a substitute for host-key verification.

## 5. Dashboard

Server Dashboard provides normalized CPU, memory/swap, load, uptime, filesystem, network and summary information as remote capabilities allow. A single unavailable section does not mean the whole server is offline; open the related module for detailed state/permission information.

## 6. Remote Explorer

Remote Explorer provides SFTP-based file management:

- navigate with breadcrumbs/address/back/forward/up;
- hidden-file toggle and local filtering/sorting;
- create file/folder;
- rename/copy/move;
- upload/download;
- delete with risk-appropriate confirmation;
- inspect owner/group/mode and path metadata;
- open supported editor/terminal workflows at a path.

For protected files, prefer the guarded privileged-edit path instead of broadening permissions such as `chmod 777`.

## 7. Remote Editor

Typical safe workflow:

1. Load current content/metadata.
2. Edit locally in the UI.
3. Run a validator when the module/file type provides one.
4. Save normally when writable, or use the guarded privileged save only when necessary.
5. Let the workflow stage/replace/verify rather than manually weakening permissions.

For nginx, Compose and environment files, entering the editor from the dedicated module provides additional context and validation.

## 8. Terminal

Terminal is a real interactive SSH PTY with concurrent sessions, scrollback, copy/paste, search and resize behavior. It is the advanced escape hatch; commands typed manually do not receive the same preview/confirmation guarantees as structured GUI workflows.

## 9. SSH port forwarding

### Local forwarding

Example:

```text
Windows 127.0.0.1:5433 -> SSH -> Linux 127.0.0.1:5432
```

Use this for local tools connecting to services that remain private/loopback on the Linux host.

### Remote forwarding

Review remote bind address/port carefully because it can change reachability on the remote side.

### Dynamic/SOCKS

Creates a SOCKS tunnel where the transport/server supports it. Always verify actual tunnel state and bound port.

## 10. Processes

Process Manager shows process inventory/details subject to permission. Supported actions include graceful termination and stronger force-kill paths with higher risk. Prefer graceful termination when possible and refresh state after uncertain completion.

## 11. Services

Service Manager is systemd-first. It supports list/search/status plus guarded start/stop/restart/reload/enable/disable workflows and related details/logs where available. V1 does not claim full SysV-init certification.

## 12. Storage

Storage shows block devices, filesystems, mounts, size/used/free and related usage information. V1 is intentionally read-oriented here; it is not a general destructive disk partition/format/filesystem editor.

## 13. Network and listening ports

Network shows interfaces, addresses, traffic information, routes where available, listeners/protocol/bind address and process association when permissions permit. Distinguish loopback listeners from public exposure.

## 14. Logs

Log Viewer supports the available journal/file/container sources, with realtime follow and filtering controls where the source supports them. Log text is untrusted server data; ServerDesk does not treat arbitrary log markup as executable UI content.

## 15. Docker Inventory and container actions

When Docker CLI/daemon and permissions are usable, Docker Inventory provides container/image/network/volume/system views plus supported lifecycle actions, inspect, stats, logs, diagnostics and exec workflows. Agentless mode uses remote CLI over SSH and does not expose the Docker Unix socket over the network.

## 16. Docker Exec Terminal / Diagnostics

Verify the selected container before exec. An exec session can mutate a workload even if the surrounding inventory view is read-oriented. If the container disappears/restarts, refresh inventory before retrying.

## 17. Docker Compose v2

Compose supports project discovery, service state, logs, Up/Down/Restart/Pull/Build and raw YAML editing. `Down` does not silently add volume deletion.

For raw YAML:

1. Select the correct project/config file.
2. Edit raw text; advanced anchors/extensions/profiles are preserved rather than silently reserialized.
3. Validate using the project's Compose context (`docker compose config --quiet`).
4. Apply only after validation passes.
5. Refresh project state after the mutation.

Legacy `docker-compose` v1 is not a certification requirement.

## 18. Git Operations

Git Operations is an operational/deployment helper: repository discovery, branch/revision, working-tree state, ahead/behind where available, fetch, explicit/safe pull and diff/status workflows. It is not a full Git IDE and does not automatically destructive-reset a working tree.

## 19. Scheduled Tasks

Scheduled Tasks provides a task-oriented view over cron/systemd timers where supported: list, enable/disable, basic schedule editing, command/unit details, last/next execution, logs and raw escape hatch. Review commands carefully because scheduled tasks continue running after ServerDesk closes.

## 20. nginx

Nginx Inventory discovers relevant sites/configuration. Nginx Site Editor supports simple common settings plus raw configuration for advanced directives.

Safe mutation flow:

1. Prepare candidate.
2. Preserve/backup context as required.
3. Validate with `nginx -t`.
4. Reject invalid configuration without activating it.
5. Install/activate.
6. Reload nginx.
7. Verify service state.

## 21. TLS / Certbot

TLS Certificate views expose certificate inventory/expiration/association where detectable. Certbot-backed actions are available only when that capability is present; ServerDesk does not assume every certificate is Let's Encrypt managed.

## 22. Environment Files

Use Environment File workflows for guarded config/env editing. Secret values must not be copied into ordinary audit/profile metadata. Use privileged save only when required and review any service reload/restart impact separately.

## 23. Deployment

Before deployment, verify server/environment, repository/branch/revision and every previewed step/risk. After deployment, inspect verification results, service/nginx/health/log state. A timeout after mutation can be Ambiguous; re-observe before retrying.

## 24. Firewall

Firewall selects the supported adapter when detected:

- UFW for Debian/Ubuntu where present;
- firewalld for supported RHEL-family systems where present.

Review source/port/protocol/action and protect the current SSH access path before applying a rule. Raw nftables visual editing is outside V1.

## 25. User Administration

Provides users/groups, shell/home, lock state, memberships, authorized keys and sudo visibility plus supported guarded changes. There is no casual one-click root enablement. Preserve a valid SSH access path when editing authorized keys.

## 26. Package Administration

APT and DNF adapters support explicit metadata refresh, update inventory and guarded package actions as capability permits. ServerDesk does not silently auto-update production servers by default.

## 27. Backup & Restore

### Backup

Select target/type/destination and run the workflow. An artifact is considered usable only after its required verification policy succeeds.

### Restore

1. Select the exact verified artifact.
2. Review target identity and overwrite impact.
3. Confirm the destructive operation.
4. Dispatch once.
5. Post-verify the target.
6. If completion is uncertain, retain Ambiguous/Unknown instead of blind retry.

## 28. Database Profiles

V1 adapters exist for PostgreSQL, MySQL, MariaDB, Redis, Microsoft SQL Server and MongoDB. Store non-secret connection metadata plus credential references. Prefer SSH local tunnels so database services can remain on loopback/private endpoints.

## 29. Database Runtime & Diagnostics

Runtime discovery distinguishes server engine/service from client tooling. `sqlcmd` alone is not a SQL Server runtime; `mongosh`/Database Tools alone do not prove `mongod`/`mongos` is running. Diagnostics are version/capability gated. MongoDB V1 diagnostics expose bounded topology/database/collection metadata and do not read document contents.

## 30. Database Backup

Use only when the support matrix marks the capability Certified:

- PostgreSQL/MySQL/MariaDB — engine-specific dump plus verification.
- SQL Server — native `.bak`, bounded file check, SHA-256 and `RESTORE VERIFYONLY ... WITH CHECKSUM` before the artifact is usable.
- MongoDB standalone — gzip archive, SHA-256 and dry-run verification with the certified Database Tools path.
- Redis — **Backup Unsupported in V1**.

## 31. Database Restore

Restore is bound to verified artifact/manifest and exact target identity.

- PostgreSQL/MySQL/MariaDB — fresh preview/confirmation/post-verification.
- SQL Server — exact database target and verified `.bak` with post-verification.
- MongoDB — V1 only certifies the listed standalone topology with namespace/target guards and post-verification.
- Redis — **Restore Unsupported**.
- MongoDB replica-set/mongos backup/restore — **Unsupported until separately certified**.

General arbitrary SQL/Mongo shell query consoles are outside the certified V1 database module.

## 32. Global Dashboard and multi-server tools

Global Dashboard provides a normalized multi-server overview. Server Comparison compares selected facts. Bulk Metadata Mutation/Profile Metadata Import support guarded organization/metadata workflows. Multi-server support does not imply every destructive single-server action has a safe bulk equivalent.

## 33. Operation History / Audit

Operation History records reviewed mutations with operation/risk/target/result/time context while avoiding password, private-key, token and raw secret payload persistence.

## 34. Optional serverdesk-agent

V1 implements/certifies the backend: Linux loopback-only host, SSH-tunneled gRPC, negotiation/health, realtime metrics, process/service/Docker events, redacted journald streaming, signed artifact verification and fixed-surface install/update/status/uninstall with bounded rollback/Ambiguous-state safety.

The repository currently does not prove a **dedicated Agent Management WPF window**, so this is not documented as a normal standalone GUI menu. Agentless operation remains the primary user path. Agent distribution must follow the signed-manifest and external private-signing-key process.

## 35. When a feature is Disabled/Unsupported

Check in order:

1. Is SSH still connected?
2. Does the required tool/service/capability exist?
3. Does the SSH user have permission?
4. Is the version/topology inside the certification matrix?
5. Is noninteractive sudo required/available?
6. Is a previous mutation still Ambiguous?

Do not solve capability failures by opening public ports, disabling host-key checks, broadening permissions, persisting plaintext secrets, or blindly retrying destructive operations.

## 36. Further reading

- [`CURRENT_SCOPE.md`](CURRENT_SCOPE.md) — delivered and unsupported scope.
- [`SUPPORT_MATRIX.md`](SUPPORT_MATRIX.md) — exact certified versions/platforms.
- [`SECURITY_RULES.md`](SECURITY_RULES.md) — security rules.
- [`agent-lifecycle-execution.md`](agent-lifecycle-execution.md) — agent lifecycle/recovery.
- [`agent-release-security.md`](agent-release-security.md) — signed agent release trust.
