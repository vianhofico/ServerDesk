# Scope hiện tại của ServerDesk V1

[English](CURRENT_SCOPE.md) | **Tiếng Việt**

Tài liệu này là bản mô tả **scope thực tế đã triển khai tại V1**, dùng để trả lời rõ ba câu hỏi: ServerDesk đang làm được gì, capability nào chỉ hoạt động khi server đáp ứng điều kiện, và phần nào chưa được hỗ trợ/certify.

> `PRODUCT_PLAN` và `ROADMAP` mô tả định hướng/contract phát triển. Tài liệu này cùng `SUPPORT_MATRIX` là nguồn tham chiếu chính để xác định capability hiện có của release.

## 1. Tổng quan V1

ServerDesk là Windows desktop client quản trị **Linux server**. Đường điều khiển chính là SSH/SFTP/PTY; không cần cài daemon proprietary để dùng các chức năng agentless. Optional `serverdesk-agent` bổ sung realtime transport nhưng vẫn chạy loopback-only và được truy cập qua SSH tunnel.

Roadmap M0–M8 hiện đã được triển khai/certify trong repository. V1 không phải một control panel web, không phải RDP Linux desktop và không phải cloud management SaaS.

## 2. Client và server được certify

### Client

- **Certified:** Windows 11 x64.
- **Compatibility target:** Windows 10 x64 khi .NET/WPF/WebView2 dependency còn hỗ trợ.
- **Chưa certify:** Windows ARM64.
- Không có Linux/macOS desktop client trong V1.

### Linux server

Primary certified distro targets:

- Ubuntu 24.04 LTS;
- Ubuntu 26.04 LTS;
- Debian 13.

Rocky/Alma 9/10 là expansion target theo adapter matrix, không được ngầm coi là certified nếu chưa qua gate tương ứng. Các distro systemd khác là best-effort/unknown cho tới khi được promote.

## 3. Trạng thái theo phân hệ

| Phân hệ | Trạng thái V1 | Scope chính |
|---|---|---|
| Server profiles & organization | Delivered | add/edit/clone/remove, group/tag/favorite/search, metadata organization/import, connection history |
| SSH security & routing | Delivered | password/key/encrypted key, keyboard-interactive/MFA, host-key trust, reconnect, direct/proxy/bastion routes |
| Remote Explorer | Delivered | SFTP browsing, file/folder operations, upload/download, metadata and guarded privileged file workflows |
| Remote Editor | Delivered | raw/config editing, safe staged replacement, permission-aware privileged save, validators where available |
| Terminal | Delivered | real SSH PTY, concurrent sessions/tabs, interactive shell behavior |
| SSH tunnels | Delivered | local, remote and dynamic/SOCKS forwarding with explicit state |
| Dashboard | Delivered | CPU, memory, load, uptime, filesystem/network and normalized server overview |
| Processes | Delivered | inventory/details and guarded terminate/kill workflows |
| Services | Delivered for systemd | list/status/start/stop/restart/reload/enable/disable and logs where available |
| Storage | Delivered read-oriented | block/filesystem/mount/usage visibility; no general destructive partition editor |
| Network & ports | Delivered | interfaces, addresses, traffic/listeners/routes/process association where available |
| Logs | Delivered | journald/files/container-related viewing; bounded/realtime paths where implemented |
| Docker Engine | Delivered when usable | inventory, lifecycle actions, inspect/stats/logs/exec diagnostics |
| Docker Compose v2 | Delivered | project discovery, up/down/restart/pull/build, logs, raw YAML editing + validation |
| Git operations | Delivered | repository operational status, fetch/pull/diff-oriented workflows; not a full Git IDE |
| Scheduled tasks | Delivered | cron/systemd-timer oriented management with raw escape hatch |
| nginx | Delivered | inventory/site config, guarded edits, validation/reload workflow |
| TLS/Certbot | Delivered when detected | certificate inventory and supported certificate operations |
| Environment files | Delivered | guarded environment/config file editing |
| Deployment | Delivered | reviewed deployment workflow using existing remote primitives |
| Firewall | Delivered for adapters | UFW/firewalld normalized management; raw nftables editor is not V1 scope |
| Users/groups/SSH keys | Delivered | account/group visibility and guarded administration/authorized-key workflows |
| Packages | Delivered for adapters | APT/DNF inventory/update operations with confirmation/safety gates |
| Backup/restore | Delivered for certified targets | verified artifacts, target preview, destructive confirmation and post-action verification |
| Databases | Delivered with exact matrix | runtime/inventory, SSH tunnel, diagnostics; backup/restore only where capability is certified |
| Multi-server | Delivered | global dashboard, comparison and guarded metadata/bulk workflows |
| Operation history/audit | Delivered | records reviewed remote mutations without persisting secret payloads |
| Optional agent | Backend/lifecycle delivered & certified | loopback gRPC over SSH, realtime metrics/events/logs, signed artifact/lifecycle backend; no dedicated standalone Agent Management window is claimed in V1 |

## 4. Database scope chính xác

Database module **không phải Navicat/DataGrip replacement**. Nó tập trung vào runtime/inventory, authenticated diagnostics, SSH-tunneled connectivity và backup/restore có kiểm chứng.

| Engine | Runtime/Inventory | SSH tunnel | Diagnostics | Backup | Restore |
|---|---|---|---|---|---|
| PostgreSQL 18.6 fixture | Certified | Certified | Certified | Certified | Certified |
| MySQL 8.4.11 fixture | Certified | Certified | Certified | Certified | Certified |
| MariaDB 11.8.9 fixture | Certified | Certified | Certified | Certified | Certified |
| Redis 8.10.0 fixture | Certified | Certified | Certified | **Unsupported** | **Unsupported** |
| Microsoft SQL Server 17.0.4075.5 / SQL Server 2025 CU8 fixture | Certified | Certified | Certified | Certified | Certified |
| MongoDB 8.0.29 standalone fixture | Certified | Certified | Certified | Certified | Certified |

Giới hạn bắt buộc:

- Version không nằm trong matrix **không tự động trở thành Certified** chỉ vì command/client có thể kết nối.
- Redis backup/restore fail-closed vì chưa có persistence-copy/recovery semantics đủ deterministic.
- MongoDB backup/restore V1 chỉ certify **standalone topology**; replica set/mongos có thể được detect nhưng backup/restore chưa được promote.
- MongoDB diagnostics không đọc/hiển thị document content.
- Arbitrary SQL execution, Mongo shell execution và query console tổng quát nằm ngoài certified database scope.
- Secret database không được persist trong connection URI/profile metadata thông thường.

Xem version/evidence chi tiết tại [`SUPPORT_MATRIX.vi.md`](SUPPORT_MATRIX.vi.md).

## 5. Docker/container scope

### Có

- Docker Engine CLI khi được detect và user có quyền sử dụng;
- container inventory và lifecycle actions;
- inspect/stats/logs;
- exec terminal/diagnostics;
- Docker Compose v2 `docker compose`;
- project/service state, logs;
- up/down/restart/pull/build;
- raw YAML escape hatch;
- `docker compose config --quiet` validation trước apply;
- không tự thêm `--volumes` vào `down`.

### Chưa/không certify

- Kubernetes;
- Podman adapter;
- Docker Swarm management console;
- expose/forward Docker Unix socket qua network;
- legacy `docker-compose` v1 không phải certification requirement.

## 6. Deployment/web scope

### Có

- nginx inventory và site management;
- raw configuration path cho trường hợp nâng cao;
- validation trước activation/reload;
- TLS certificate inventory và Certbot integration khi capability hiện diện;
- environment-file workflows;
- deployment workflow dựa trên các remote primitive đã review.

### Chưa/ngoài V1

- Apache management đầy đủ;
- Caddy management;
- cloud load balancer/DNS/provider console;
- Kubernetes deployment.

## 7. System administration scope

### Có

- systemd-first service management;
- UFW/firewalld;
- users/groups/authorized keys/account state/sudo visibility theo capability;
- APT/DNF;
- backup/restore có preview/verification;
- operation audit/history.

### Chưa/không certify

- SysV init đầy đủ;
- raw nftables visual editor;
- one-click root enablement;
- destructive disk partitioning/format/filesystem surgery;
- tự động update production server không cần confirmation.

## 8. Multi-server scope

V1 có global dashboard, search/group/tag/favorite organization, selected-server comparison và các thao tác metadata/bulk đã được guard. Multi-server **không** đồng nghĩa mọi mutation đơn-server được phép broadcast hàng loạt. Destructive bulk operations cần thiết kế/safety gate riêng và không được suy diễn tự động.

## 9. Optional serverdesk-agent

Agent là **tùy chọn**, không phải dependency bắt buộc của ServerDesk.

Đã implement/certify:

- Linux `serverdesk-agent` host;
- listener cấu trúc loopback-only;
- fixed agent port `41371` phía Linux;
- Windows mở local forward ephemeral qua SSH đến Linux loopback;
- Protobuf/gRPC negotiation + health;
- explicit protocol/capability compatibility;
- metrics streaming;
- process/service event streaming;
- normalized Docker events;
- bounded/redacted journald streaming;
- agentless fallback/degradation khi capability không có;
- signed release metadata bằng ECDSA P-256/SHA-256;
- exact artifact length/SHA-256 verification;
- fixed-surface install/update/status/uninstall backend;
- `sudo -n`, fixed paths, bounded rollback và explicit Ambiguous state khi mutation completion không chắc chắn.

Giới hạn:

- Không có public/LAN agent listener trong V1.
- Không có generic remote-command RPC.
- Agent không thay thế SSH authentication/host trust.
- Repository hiện không chứng minh một **dedicated end-user Agent Management WPF window**, nên tài liệu không quảng bá đây là GUI module độc lập.
- Việc phân phối agent installable phải tuân theo signed-manifest/release-key process; private signing key không nằm trong repository.

## 10. Những thứ ServerDesk V1 không cố gắng trở thành

- Kubernetes IDE/control plane;
- AWS/Azure/GCP console;
- full Navicat/DataGrip;
- full Git IDE;
- SaaS monitoring/team collaboration platform;
- Linux GUI remote desktop/RDP replacement;
- arbitrary root shell automation engine;
- public proprietary server management port.

## 11. Quy tắc đọc trạng thái capability

Một feature có thể có code/UI nhưng vẫn không khả dụng trên server cụ thể. UI/application phải phân biệt các trạng thái như:

- Available;
- Unavailable;
- PermissionDenied;
- UnsupportedVersion;
- ProbeFailed/Unknown;
- Ambiguous sau mutation không chắc chắn.

Ví dụ: có `docker` executable không đồng nghĩa daemon đang chạy hoặc user có permission; `sqlcmd` không đồng nghĩa SQL Server service tồn tại; `mongosh` không đồng nghĩa `mongod` đang chạy.

## 12. Nguồn sự thật khi có mâu thuẫn tài liệu

Ưu tiên theo thứ tự:

1. [`SUPPORT_MATRIX.vi.md`](SUPPORT_MATRIX.vi.md) cho exact platform/version/certification boundary;
2. tài liệu `CURRENT_SCOPE` này cho delivered/out-of-scope product capability;
3. security/ADR chuyên biệt cho invariant bảo mật;
4. `PRODUCT_PLAN`/`ROADMAP` cho intent và lịch sử contract.
