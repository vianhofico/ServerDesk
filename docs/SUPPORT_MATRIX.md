# Support Matrix

ServerDesk distinguishes **certified**, **experimental/best-effort**, and **unsupported/unknown** behavior. A distro is certified only after the required automated/manual compatibility gates pass for the listed release.

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

### Databases

Initial server-oriented support:

- PostgreSQL;
- MySQL/MariaDB;
- Redis.

Exact major versions become certified only when M6 integration tests define and exercise them.

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

## 7. Removal/deprecation

Certified support may be deprecated when:

- upstream OS is no longer reasonably supportable/security maintained for the product;
- required .NET/SSH/tool compatibility is lost;
- maintenance cost cannot be justified.

Deprecation must be documented before removal and should not silently change an existing server from Certified to unsupported without release notes.
