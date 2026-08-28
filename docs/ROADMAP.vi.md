# Roadmap và Milestone Gate

[English](ROADMAP.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

Roadmap này có thứ tự bắt buộc. Không được dùng milestone sau để né các yêu cầu kiến trúc, bảo mật, kiểm thử hoặc UX còn dang dở ở milestone trước.

## M0 — Foundation

### Mục tiêu

Tạo nền tảng Windows desktop có thể build được và cố định các implementation contract mà agent phải tuân thủ.

### Phạm vi

- .NET 10 WPF shell;
- ranh giới project Domain/Application;
- chiến lược composition dependency injection;
- placeholder cho navigation shell;
- nền tảng theme system/light/dark;
- các primitive result/error dùng chung;
- logging policy;
- interface cho local data/secret store;
- repository docs và ADR process;
- CI build;
- PR/issue template;
- test project/test infrastructure plan.

### Tiêu chí hoàn thành

- repository build được trên Windows runner sạch;
- không có secret persistence được triển khai sai;
- UI shell khởi chạy được;
- tài liệu architecture/security/UX/agent tồn tại và thống nhất với nhau;
- required check của CI đều green;
- interface M1 có thể được implement mà không làm thay đổi dependency direction của Domain.

---

## M1 — Remote Core

### Mục tiêu

Biến ServerDesk thành nền tảng SSH/SFTP client an toàn và ổn định trước khi bổ sung các tính năng quản trị.

### Phạm vi

- server profiles/groups/tags;
- Windows secure credential implementation;
- SSH connection lifecycle;
- password authentication;
- private-key authentication;
- passphrase handling;
- keyboard-interactive/MFA;
- known-host store;
- unknown host trust UI;
- chặn changed fingerprint;
- keepalive/timeouts;
- reconnect state;
- jump/bastion support;
- proxy support khi SSH library được chọn hỗ trợ;
- SFTP abstraction;
- PTY terminal dùng WebView2 + xterm.js;
- nhiều terminal session;
- local/remote/dynamic forwarding;
- baseline capability detection;
- connection audit không chứa secret.

### Kiểm thử bắt buộc

- kết nối password/key thành công;
- sai password/key;
- encrypted key;
- unknown host;
- changed host key;
- timeout;
- disconnect đột ngột;
- reconnect;
- SFTP permission error;
- terminal resize/close lifecycle;
- tunnel open/close;
- secret redaction.

### Tiêu chí hoàn thành

User có thể lưu server profile an toàn, kết nối, xác minh trust, duyệt SFTP, dùng terminal thật, tạo tunnel, disconnect/reconnect và nhận lỗi hữu ích mà không cần dùng raw developer debug view.

---

## M2 — Windows-like Server UI

### Mục tiêu

Cung cấp khác biệt cốt lõi của sản phẩm: kiểm tra/quản lý Linux thường ngày bằng UI quen thuộc kiểu Windows.

### Phạm vi

#### Dashboard
- CPU;
- memory/swap;
- load;
- uptime;
- network throughput;
- filesystem summary;
- warnings.

#### Explorer
- remote navigation đầy đủ;
- tạo/đổi tên/copy/move/delete file/folder;
- upload/download;
- drag/drop;
- thuộc tính permission/owner/group;
- virtualization cho directory lớn;
- transaction lưu privileged file.

#### Editor
- syntax highlighting;
- find/replace;
- diff trước khi privileged save;
- extension point cho validator.

#### Processes
- normalized process list;
- CPU/RAM/user/state;
- terminate/kill với phân biệt mức rủi ro.

#### Services
- systemd list/search/details;
- start/stop/restart/reload;
- enable/disable;
- link tới logs.

#### Storage
- filesystems/mounts;
- block device overview;
- disk usage analyzer;
- cảnh báo dung lượng thấp.

#### Network
- interfaces;
- addresses;
- rates;
- listening ports;
- process association khi có thể.

#### Logs
- journald và file log viewer;
- realtime follow;
- pause/filter/search/export.

### Tiêu chí hoàn thành

Một Ubuntu/Debian server được hỗ trợ có thể được chẩn đoán và quản lý thường ngày về files, processes, services, logs, storage và network mà user không cần tự gõ command.

---

## M3 — DevOps

### Mục tiêu

Bao phủ các workflow mà developer thường thực hiện nhất sau khi SSH vào application server.

### Phạm vi

#### Docker
- phát hiện Docker availability/permission;
- containers;
- images;
- volumes;
- networks;
- inspect;
- stats;
- logs;
- terminal/exec;
- start/stop/restart/pause/kill/remove;
- hiển thị environment nhạy cảm an toàn.

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
- logs/history khi có.

### Tiêu chí hoàn thành

Developer có thể chẩn đoán/restart/redeploy application stack containerized thông thường qua ServerDesk mà không expose Docker socket hoặc database port công khai.

---

## M4 — Deployment

### Mục tiêu

Cung cấp workflow expose/deploy ứng dụng an toàn.

### Phạm vi

- nginx discovery và configuration inventory;
- simple reverse-proxy/site editor;
- raw advanced editor;
- validation gate bằng `nginx -t`;
- backup/atomic apply/reload/verify/rollback;
- SSL certificate inventory;
- Certbot integration khi phát hiện;
- expiration warnings;
- guarded editing cho environment file;
- deployment workflow orchestration;
- hook restart và health verification;
- chiến lược rollback rõ ràng cho deployment type được hỗ trợ.

### Tiêu chí hoàn thành

User có thể cấu hình common reverse proxy/HTTPS deployment mà ServerDesk không bao giờ thay live nginx config hợp lệ bằng một candidate đã biết là invalid.

---

## M5 — Administration

### Mục tiêu

Bao phủ routine host administration với safety control mạnh hơn.

### Phạm vi

- firewall abstraction;
- UFW adapter;
- firewalld adapter;
- users/groups;
- authorized SSH keys;
- account lock/unlock;
- hiển thị group/sudo membership;
- package manager abstraction;
- APT adapter;
- DNF adapter;
- update inventory;
- explicit package operations;
- backup/restore framework;
- operation audit UI.

### Tiêu chí hoàn thành

Routine administration có thể thực hiện mà không tạo nguy cơ lockout/root-enablement một cách dễ dãi, và operation nguy hiểm có safety UX rõ ràng cùng test coverage.

---

## M6 — Databases

### Mục tiêu

Bổ sung quản trị database theo hướng server-oriented mà không cố trở thành full database IDE.

### Phạm vi

- PostgreSQL status/version/size/log basics;
- MySQL/MariaDB status/version/size/log basics;
- Redis status/version/memory/log basics;
- connection profile qua SSH tunnel;
- backup;
- restore với preview/confirmation;
- optional basic query console sau khi backup/restore ổn định.

### Tiêu chí hoàn thành

User có thể inspect và backup/restore an toàn các database được hỗ trợ mà không expose port của chúng công khai.

---

## M7 — Multi-server

### Mục tiêu

Mở rộng workflow single-server đã được chứng minh sang nhiều environment mà không làm bulk operation trở nên nguy hiểm.

### Phạm vi

- global server dashboard;
- groups/tags/favorites;
- health/warning summary;
- global search/navigation;
- so sánh an toàn các fact được chọn;
- bulk read operation được phê duyệt cẩn thận;
- bulk mutation phạm vi hẹp với risk review riêng;
- import/export non-secret profile metadata;
- profile secret reference vẫn local/secure.

### Tiêu chí hoàn thành

User có thể quản lý nhiều server trong khi mọi operation vẫn làm target server identity rõ ràng và bulk destructive action không thể xảy ra do vô tình.

---

## M8 — Optional ServerDesk Agent

### Mục tiêu

Cải thiện hiệu năng realtime và chuẩn hóa metrics/events phức tạp mà không thay đổi secure connection model của sản phẩm.

### Phạm vi

- Linux service `serverdesk-agent`;
- gRPC/Protobuf contract;
- listener mặc định chỉ bind loopback;
- SSH tunnel bootstrap;
- version/capability negotiation;
- metrics streaming;
- process/service events;
- Docker events;
- log streaming;
- transport fallback về agentless mode;
- secure upgrade/uninstall plan.

### Tiêu chí hoàn thành

Agent mode cải thiện realtime behavior nhưng mọi core operation được hỗ trợ vẫn degrade gracefully về implementation agentless đã được tài liệu hóa khi phù hợp.

---

# Release gate xuyên milestone

Mỗi milestone phải đáp ứng các mục áp dụng được:

- clean build;
- tests green;
- không có high-severity security finding mới;
- không có secret trong repository/log fixture;
- không host-key bypass;
- typed error cho infrastructure failure mới;
- UI state cho loading/empty/error/disconnect/cancel;
- review dark/light/system theme;
- primary workflow sử dụng được bằng keyboard;
- support matrix được cập nhật;
- architecture doc/ADR được cập nhật khi contract thay đổi;
- không có required CI failure không liên quan bị bỏ qua.

# Quy tắc kích thước issue

Một milestone là epic, không phải một coding PR duy nhất.

Kích thước issue ưu tiên: một vertical slice có thể review độc lập. Ví dụ:

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

Nếu một issue phải chạm quá nhiều khu vực kiến trúc hoặc không thể test độc lập, phải tách trước khi implement.
