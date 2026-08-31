# Support Matrix

**English** | [Tiếng Việt](SUPPORT_MATRIX.vi.md)

ServerDesk distinguishes **certified**, **tested**, **experimental/best-effort**, and **unsupported/unknown** behavior. A distro or engine/version capability is certified only after the required automated/manual compatibility gates pass for the listed release.

## 1. Client platform

### Certified target

- Windows 11 x64

### Required compatibility target

- Windows 10 x64 where the selected .NET/WPF/WebView2 stack remains supported by the project dependencies.

ARM64 may be added after explicit build/test coverage.

## 2. Linux server certification order

### V1 primary certification

| Family | Release | Target level |
|---|---|---|
| Ubuntu | 24.04 LTS | Certified |
| Ubuntu | 26.04 LTS | Certified |
| Debian | 13 | Certified |

### V1.x expansion

| Family | Release | Target level |
|---|---|---|
| Rocky Linux | 9 | Certified after adapter matrix passes |
| Rocky Linux | 10 | Certified after adapter matrix passes |
| AlmaLinux | 9 | Certified after adapter matrix passes |
| AlmaLinux | 10 | Certified after adapter matrix passes |

### Future/best-effort until promoted

- Amazon Linux;
- Oracle Linux;
- Fedora;
- openSUSE;
- Arch Linux;
- other systemd Linux distributions.

## 3. Base assumptions

Agentless mode requires:

- SSH reachable from the Windows client;
- an authentication method supported by the client implementation;
- SFTP subsystem for Explorer features;
- basic remote shell/tools needed by the requested capability.

ServerDesk must not assume root access.

## 4. Capability support levels

Each capability is detected independently.

### Core

| Capability | Initial requirement |
|---|---|
| SSH command | Required |
| SFTP | Required for Explorer |
| PTY/shell | Required for Terminal |
| local forwarding | M1 |
| remote forwarding | M1 |
| dynamic forwarding | M1 where library/platform support is reliable |
| `/etc/os-release` | preferred OS detection source |
| `/proc` metrics | Linux dashboard baseline |

### Service management

| Capability | V1 |
|---|---|
| systemd | Certified path |
| SysV init | Not certified initially |

### Containers

| Capability | V1 |
|---|---|
| Docker Engine CLI | Supported when detected and usable |
| Docker Compose v2 (`docker compose`) | Supported |
| legacy `docker-compose` v1 | Best-effort/optional, not required for certification |
| Podman | Future adapter |
| Kubernetes | Out of V1 scope |

### Web server

| Capability | V1 |
|---|---|
| nginx | Supported in deployment milestone |
| Apache | Future/experimental |
| Caddy | Future |

### Firewall

| Family | Adapter |
|---|---|
| Debian/Ubuntu | UFW where present |
| RHEL family | firewalld where present |
| raw nftables | Advanced/future; not directly edited in initial V1 |

### Package management

| Family | Adapter |
|---|---|
| Debian/Ubuntu | APT |
| Rocky/Alma/RHEL family | DNF |

### Databases — certified engine/version matrix

The rows below are tied to exact real-engine fixtures exercised by the OpenSSH CI path. **Certified** means the real engine fixture is exercised for that capability. **Tested** is reserved for useful test evidence that does not include the full real-engine certification path; no unlisted version is silently promoted to Certified. **Unsupported** means ServerDesk fails closed for that capability.

| Engine | Exact fixture version | Runtime / inventory | SSH tunneled connectivity | Diagnostics | Backup | Restore |
|---|---:|---|---|---|---|---|
| PostgreSQL | 18.6 | Certified | Certified | Certified | Certified | Certified |
| MySQL | 8.4.11 | Certified | Certified | Certified | Certified | Certified |
| MariaDB | 11.8.9 | Certified | Certified | Certified | Certified | Certified |
| Redis | 8.10.0 | Certified | Certified | Certified | Unsupported | Unsupported |
| Microsoft SQL Server | 17.0.4075.5 (SQL Server 2025 CU8) | Certified | Certified | Certified | Certified | Certified |

Evidence and boundaries:

- CI runs PostgreSQL `18.6`, MySQL `8.4.11`, MariaDB `11.8.9`, Redis `8.10.0`, and Microsoft SQL Server `17.0.4075.5` through the real OpenSSH integration job.
- PostgreSQL/MySQL/MariaDB backup is marked usable only after deterministic artifact verification; restore requires the exact verified manifest/target identity, fresh preview/confirmation, destructive dispatch handling, and post-restore target verification.
- SQL Server backup uses a native `.bak` artifact and is not marked usable until a bounded file check, SHA-256 verification, and `RESTORE VERIFYONLY ... WITH CHECKSUM` all succeed. Restore is tied to the exact verified manifest/database target, requires a fresh destructive preview/confirmation, preserves Ambiguous/Unknown after uncertain dispatch, and post-verifies the target identity.
- SQL Server runtime inventory distinguishes server package/service discovery from client tooling; `sqlcmd` alone is not treated as a running SQL Server instance. Exact live server version is obtained through authenticated diagnostics before version-gated backup/restore certification.
- SQL Server credentials remain in the secret abstraction. The CI fixture generates and masks its SA password at runtime; credential values are not persisted into profile metadata, history, rendered commands, or uploaded diagnostic artifacts.
- Redis backup/restore is **Unsupported** because deterministic persistence-copy/recovery semantics have not been proven. The UI/application must fail closed before generating a certified backup/restore mutation.
- Any engine version not explicitly listed above is **not Certified** merely because parsing or a client command happens to work. It remains unsupported/unknown for certification purposes until explicit evidence promotes it.
- `Tested` is available for future partial evidence, but the currently listed exact engine/version rows are either Certified for a capability or explicitly Unsupported.
- Arbitrary/basic SQL query execution remains outside the certified database scope; ServerDesk does not provide a SQL query console as part of this matrix.

## 5. Capability state semantics

UI and application code must distinguish:

```text
Available
Unavailable
PermissionDenied
UnsupportedVersion
ProbeFailed/Unknown
```

Examples:

- `docker` not installed != Docker permission denied;
- `systemctl` found but system not booted with systemd != systemd unavailable for all purposes;
- command timeout != command absent.

## 6. Promotion criteria

To promote a distro/release or capability to Certified:

- adapter/parser fixtures pass;
- core SSH/SFTP integration passes;
- applicable feature integration passes;
- security-negative tests pass;
- manual core workflow checklist passes on representative system;
- known limitations documented;
- no required test is permanently skipped for that target.

For a database engine/version capability, promotion additionally requires an explicit matrix row and a real-engine OpenSSH integration fixture for that exact version/capability.

## 7. Removal/deprecation

Certified support may be deprecated when:

- upstream OS is no longer reasonably supportable/security maintained for the product;
- required .NET/SSH/tool compatibility is lost;
- maintenance cost cannot be justified.

Deprecation must be documented before removal and should not silently change an existing server from Certified to unsupported without release notes.
