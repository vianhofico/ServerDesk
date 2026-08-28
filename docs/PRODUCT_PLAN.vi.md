# Kế hoạch sản phẩm ServerDesk

[English](PRODUCT_PLAN.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

## 1. Định nghĩa sản phẩm

ServerDesk là ứng dụng desktop Windows cung cấp bề mặt điều khiển trực quan, quen thuộc kiểu Windows cho Linux server, trong khi bên dưới vẫn sử dụng các cơ chế remote administration tiêu chuẩn và an toàn.

Mục tiêu không phải biến Linux thành Windows desktop và cũng không che giấu việc hệ thống đang là Linux. Mục tiêu là giúp các thao tác server phổ biến nhất trở nên dễ khám phá, trực quan, an toàn và có thể đảo ngược khi có thể.

Mental model kỳ vọng:

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

## 2. Người dùng chính

### Developer

Cần kết nối tới development/staging/production host, kiểm tra file và log, restart service, quản lý container, tạo tunnel và deploy mà không phải nhớ mọi Linux command.

### DevOps / Sysadmin-lite

Cần visibility về process/service/storage/network/firewall/user/package và routine administration an toàn.

### Learner

Cần GUI giúp nhìn thấy concept Linux bên dưới thay vì thay thế bằng “phép thuật” khó hiểu. Advanced/raw view vẫn phải luôn có.

## 3. Những gì V1 không hướng tới

ServerDesk V1 không phải:

- Kubernetes IDE;
- AWS/Azure/GCP console;
- bản thay thế đầy đủ cho Navicat/DataGrip;
- full Git IDE;
- SaaS monitoring platform;
- team collaboration service;
- remote desktop protocol để render Linux graphical desktop;
- AI agent product;
- mandatory server daemon.

Các khu vực này có thể được tích hợp về sau nhưng không được làm phá vỡ kiến trúc desktop local-first.

## 4. Nguyên tắc sản phẩm

### 4.1 Agentless first

Sản phẩm ban đầu chỉ yêu cầu SSH service có thể truy cập và credential hợp lệ. Điều này giúp setup đơn giản và tránh mở proprietary management port.

### 4.2 GUI trước, terminal luôn sẵn sàng

Workflow phổ biến phải thực hiện được bằng visual control. Workflow phức tạp hoặc chưa hỗ trợ vẫn có thể thực hiện qua interactive terminal thật và raw configuration editor.

### 4.3 Hành vi dựa trên capability

Client phát hiện remote server thực sự có thể làm gì. Feature chỉ hiển thị available khi capability bắt buộc tồn tại và được hỗ trợ.

Ví dụ:

- Docker UI yêu cầu Docker CLI/daemon usable và permission phù hợp.
- Services UI ưu tiên systemd và không được giả vờ system chỉ có SysV là fully supported.
- Firewall module chỉ chọn UFW/firewalld adapter khi phát hiện.
- nginx module chỉ xuất hiện khi nhận diện được nginx tooling/config.

### 4.4 Secure by default

Known-host verification, least privilege, secret separation, safe tunneling và destructive confirmation là hành vi mặc định của sản phẩm, không phải optional setting.

### 4.5 Mutation an toàn

Remote mutation được phân loại risk và theo workflow validation/verification. Config edit nên atomic khi có thể. Destructive operation không bao giờ được retry âm thầm.

### 4.6 Compatibility trung thực

ServerDesk chỉ certified các tổ hợp OS/version/feature đã test. Unknown system có thể chạy best-effort nhưng không bao giờ được quảng bá là fully supported.

## 5. Technical baseline

### Desktop

- Windows 10/11
- .NET 10
- WPF
- thiết kế theo định hướng MVVM
- visual language Fluent/Windows 11
- WebView2 cho terminal/editor surface dạng web nhúng khi phù hợp

### Remote transport

- SSH cho command
- SFTP cho file operation
- SCP chỉ khi có use case cụ thể
- SSH local/remote/dynamic forwarding
- SSH interactive shell/PTY

### Local state

- SQLite cho profile/preference/history/capability cache
- Windows Credential Manager và/hoặc storage được DPAPI bảo vệ cho secret
- secret được tham chiếu bằng ID từ SQLite, không bao giờ persist trực tiếp ở đó

### Optional agent về sau

- `serverdesk-agent`
- gRPC + Protobuf
- bind loopback theo mặc định
- truy cập qua SSH tunnel để không cần public management port

## 6. Core user experience

### 6.1 Home / danh sách server

User có thể group, tag, favorite, search, add, edit, clone, import và remove server profile.

Mỗi server card hiển thị:

- friendly name;
- hostname/IP;
- environment color/tag;
- last known OS;
- state online/offline/connecting/reconnecting;
- recent latency hoặc last connection time;
- favorite status.

### 6.2 Connection profile

Capability profile bắt buộc:

- hostname/IPv4/IPv6;
- port;
- username;
- password auth;
- private-key auth;
- encrypted private-key passphrase;
- SSH agent khi implementation cho phép;
- keyboard-interactive/MFA;
- proxy;
- jump/bastion host;
- keepalive;
- timeout;
- startup directory;
- environment/group/tags.

Unknown host key yêu cầu explicit trust. Changed host key chặn connection cho tới khi user chủ động giải quyết mismatch.

### 6.3 Server workspace

Server đã kết nối mở một persistent workspace với server identity và connection state luôn hiển thị.

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

Item không available trên server hiện tại được hide hoặc disable kèm giải thích tùy nhu cầu discoverability.

## 7. Capability detection

Khi connect, thu thập normalized `ServerCapabilities` snapshot bằng safe read-only probe.

Thông tin dự kiến:

- OS ID/version/name từ `/etc/os-release`;
- kernel và architecture;
- current user và groups;
- sudo availability và mode;
- systemd availability;
- Docker và Compose availability;
- nginx/Apache presence;
- Git;
- UFW/firewalld;
- PostgreSQL/MySQL/MariaDB/Redis tooling;
- common system tool cần bởi adapter.

Capability detection phải phân biệt:

- executable chưa cài;
- đã cài nhưng service unavailable;
- đã cài nhưng permission denied;
- đã cài nhưng version/format unsupported;
- unknown do command failure.

## 8. Remote Explorer

Remote Explorer là flagship feature và nên tạo cảm giác quen thuộc cho Windows user.

Capability V1 bắt buộc:

- directory navigation;
- breadcrumb và address bar;
- back/forward/up;
- hidden files toggle;
- sorting và filtering;
- multi-select;
- tạo file/folder;
- rename;
- copy/move;
- upload/download;
- drag/drop từ Windows;
- delete với confirmation phù hợp risk;
- view owner/group/mode;
- chmod/chown qua safe privileged operation;
- nhận diện symlink;
- file properties;
- copy remote path;
- open terminal tại path;
- checksum khi cần.

Directory lớn phải stream/paginate/virtualize thay vì freeze UI.

## 9. Remote editor

Editor hỗ trợ các format config/source phổ biến trên server với syntax highlighting, search/replace, diff, formatting khi an toàn và undo/redo.

Privileged save flow quan trọng:

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

Không bao giờ workaround permission bằng cách nới rộng permission của remote file.

## 10. Terminal

Terminal phải là trải nghiệm SSH thật dựa trên PTY, không phải textbox chạy từng command.

Bắt buộc:

- ANSI/VT behavior;
- resize;
- scrollback;
- copy/paste;
- nhiều tab;
- nhiều concurrent session tới cùng server;
- search;
- configurable font;
- reconnect/closed-session state;
- keyboard-first usability.

Implementation dự kiến: xterm.js host trong WebView2 kết nối tới SSH shell stream.

## 11. Dashboard / performance

Hiển thị:

- CPU;
- memory và swap;
- load average;
- uptime;
- filesystem usage;
- network throughput;
- process summary;
- service summary;
- container summary;
- available update khi package capability tồn tại;
- warning như low disk space.

Ưu tiên Linux kernel/system source và structured command thay vì parse `top`.

Nguồn dự kiến gồm `/proc/stat`, `/proc/meminfo`, `/proc/loadavg`, `/proc/net/dev`, structured `lsblk` và filesystem query.

## 12. Process manager

Table kiểu Task Manager:

- PID;
- process name;
- user;
- CPU;
- memory;
- command line;
- start time khi có;
- state;
- parent process.

Action:

- xem detail;
- terminate;
- force kill với confirmation mạnh hơn;
- inspect listening port liên quan;
- mở related log/terminal khi discoverable.

## 13. Services

Quản lý service ưu tiên systemd:

- list/search/filter;
- active/inactive/failed;
- enabled/disabled/masked;
- start/stop/restart/reload;
- enable/disable;
- detail và unit file path;
- recent log;
- sudo/permission state rõ ràng.

Dùng machine-oriented `systemctl` property thay vì parse decorated `status` output.

## 14. Logs

Unified viewer trên:

- journald;
- regular log file;
- Docker log;
- database/application integration về sau.

Bắt buộc:

- realtime follow;
- pause/resume;
- time range;
- chọn service/source;
- severity filter khi source hỗ trợ;
- text search;
- highlight;
- export;
- bookmark;
- xử lý an toàn stream volume rất cao.

## 15. Docker

Docker là major differentiator của V1.

Navigation:

```text
Containers
Images
Compose
Volumes
Networks
System
```

Container feature:

- list/status;
- start/stop/restart/pause/kill;
- remove;
- inspect;
- stats;
- logs;
- terminal/exec;
- hiển thị environment với xử lý value nhạy cảm;
- ports;
- mounts;
- networks.

Compose:

- discover project;
- up/down/restart/pull/build;
- service list/status;
- logs;
- compose file editor;
- config validation trước apply.

Không expose Docker Unix socket qua network. Agentless mode dùng remote CLI thông qua SSH.

## 16. Storage

Trải nghiệm kiểu Disk Management:

- block devices;
- filesystems;
- mounts;
- size/used/free;
- inode warning khi hữu ích;
- disk usage analyzer theo directory;
- hint storage cho Docker/log/database;
- read-only hardware/filesystem fact khi hỗ trợ.

High-risk partitioning/filesystem mutation không nằm trong initial V1 trừ khi được thiết kế và test riêng.

## 17. Network và ports

Hiển thị:

- interfaces;
- addresses;
- RX/TX rates;
- routes khi hữu ích;
- listening ports;
- protocol;
- bound address;
- associated PID/process khi permission cho phép.

Action có thể link port tới process/service view và SSH tunnel creation.

## 18. SSH tunnel manager

Hỗ trợ reusable profile cho:

- local forwarding;
- remote forwarding;
- dynamic/SOCKS forwarding.

Database workflow điển hình:

```text
Windows localhost:5433 -> encrypted SSH tunnel -> server 127.0.0.1:5432
```

UI phải hiển thị tunnel state và làm rõ local port nào đang được expose trên Windows machine.

## 19. Scheduled tasks

Unify cron và systemd timer thành trải nghiệm hướng task nhưng vẫn giữ underlying type.

Feature:

- list;
- enable/disable khi áp dụng;
- create/edit basic schedule;
- last/next execution khi có;
- command/unit detail;
- logs;
- validation;
- raw view cho advanced syntax.

## 20. Git deployment helper

Git support phục vụ vận hành, không thay thế full Git client.

Scope bắt buộc:

- repository discovery tại configured path;
- branch và revision;
- working-tree changes;
- ahead/behind;
- fetch;
- pull khi safe/explicit;
- diff viewer;
- link vào deployment workflow.

Không automatic destructive reset working tree.

## 21. nginx

Dùng mô hình hybrid simple/advanced.

Simple mode bao phủ common server block:

- domain/server name;
- listening ports;
- reverse proxy target;
- WebSocket proxy option;
- static root;
- redirects;
- certificate association.

Advanced mode expose raw configuration editing.

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

Không cố model mọi nginx directive thành form.

## 22. SSL

Functionality thuộc deployment milestone về sau:

- certificate inventory;
- expiration warning;
- association với nginx site;
- Certbot detection;
- explicit issue/renew action;
- renewal log;
- không giả định mọi certificate đều do Let's Encrypt quản lý.

## 23. Firewall

Application abstraction: `IFirewallManager`.

Adapter ban đầu:

- UFW;
- firewalld.

Feature:

- current state;
- normalized rule;
- add/delete rule;
- source/port/protocol/action;
- preview generated change;
- bảo vệ khỏi việc rõ ràng lock out SSH session hiện tại nếu chưa có explicit override.

## 24. Users và groups

Administration milestone:

- users/groups;
- shell/home;
- locked/unlocked;
- group membership;
- quản lý SSH authorized keys;
- visibility sudo access;
- create/lock/unlock/change group qua guarded privileged workflow.

Không cung cấp đường one-click casual để enable root.

## 25. Package updates

Abstraction: `IPackageManager`.

Implementation ban đầu:

- APT family;
- DNF family.

Feature:

- explicit refresh metadata;
- list update;
- phân biệt security update khi distro expose metadata đáng tin cậy;
- selected update/install/remove action;
- output/log;
- không bao giờ auto-update production server mặc định.

## 26. Databases

Scope V1.x chủ đích nhỏ hơn database IDE.

Engine ban đầu:

- PostgreSQL;
- MySQL/MariaDB;
- Redis.

Capability:

- service/status;
- version;
- data size overview khi tin cậy;
- connection profile qua SSH tunnel;
- backup/restore workflow;
- logs;
- query console về sau.

## 27. Backup và restore

Backup target có thể gồm:

- configuration file;
- application directory;
- database dump;
- Docker volume được chọn theo explicit strategy.

Một backup job định nghĩa:

- source;
- type;
- destination;
- schedule;
- retention;
- optional encryption;
- verification strategy.

Restore phải preview target và ảnh hưởng overwrite trước execution.

## 28. Multi-server management

Sau khi single-server workflow ổn định:

- global server dashboard;
- groups/tags;
- search;
- favorites;
- warnings;
- compare selected configuration fact;
- bulk operation được scope cẩn thận.

Bulk destructive operation cần safeguard bổ sung và không tự động kế thừa từ single-server action.

## 29. Optional server agent

Agent mode tồn tại để cải thiện performance/realtime behavior, không thay thế secure SSH administration.

Topology kỳ vọng:

```text
ServerDesk Windows client
  |-- SSH/SFTP/PTY for standard operations
  `-- SSH local tunnel -> serverdesk-agent on 127.0.0.1
```

Trách nhiệm agent có thể gồm:

- streaming metrics;
- process/service event stream;
- Docker event;
- log streaming;
- normalized storage/network data;
- giảm số lần launch SSH process lặp lại.

Agent không được yêu cầu publicly reachable management listener theo mặc định.

## 30. Local data model

Các non-secret entity dự kiến:

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

Sensitive secret được externalize sang secure OS storage.

## 31. Operation risk model

Mọi remote operation được phân loại:

- `ReadOnly` — inspection thông thường;
- `ElevatedRead` — cần privilege để inspect protected data;
- `Mutating` — thay đổi state nhưng thường reversible/recoverable;
- `Destructive` — có thể xóa dữ liệu hoặc quyền truy cập không thể đảo ngược.

Risk classification ảnh hưởng confirmation, audit, retry behavior và prominence trong UI.

## 32. Error model

Chuẩn hóa infrastructure failure thành application-level error như:

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

User-facing UI hiển thị explanation/action ngắn gọn. Technical detail vẫn expandable để debug.

## 33. Mục tiêu hiệu năng

Target ban đầu, đo trên hardware/network đại diện:

- desktop cold start: target khoảng <= 2 s;
- typical LAN SSH connection: target khoảng <= 3 s;
- dashboard có first useful data: target <= 2 s sau authentication;
- normal Explorer directory load: <= 500 ms khi server/network cho phép;
- active CPU/network metric cadence: khoảng 1 s;
- Docker stats cadence: khoảng 2 s;
- UI luôn responsive.

Đây là engineering target, không phải guarantee chống remote latency.

## 34. Tiêu chí hoàn thành V1

Một V1 release yêu cầu workflow end-to-end có tài liệu và test cho:

- secure connection và known hosts;
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
- Docker và Compose;
- basic Git operation;
- basic nginx management;
- capability detection;
- sudo handling;
- reconnect/offline state;
- operation history;
- dark/light/system theme;
- crash-safe local state;
- certified support matrix gate.

V1 không được tuyên bố complete chỉ vì mọi navigation item đã tồn tại.

## 35. Delivery sequence

Thứ tự milestone bắt buộc được định nghĩa trong `ROADMAP.vi.md`:

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

Mỗi milestone có acceptance gate và phải để repository ở trạng thái releasable/dễ hiểu.
