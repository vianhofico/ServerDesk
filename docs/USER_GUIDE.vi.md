# Hướng dẫn sử dụng ServerDesk V1

[English](USER_GUIDE.md) | **Tiếng Việt**

Tài liệu này hướng dẫn sử dụng ServerDesk theo từng phân hệ hiện có trong Windows client. Tên control có thể thay đổi nhẹ theo localization/version, nhưng workflow và safety boundary được mô tả theo V1 hiện tại.

> Trước khi dùng feature có mutation (xóa file, restart service, firewall, package, restore...), hãy đọc preview/confirmation. Nếu ServerDesk báo `Ambiguous` hoặc trạng thái không chắc chắn sau timeout/network loss, **refresh/re-observe trước khi retry**.

## 1. Cài và chạy ServerDesk

### Release package

V1.0.0 publish package Windows x64 dạng self-contained ZIP.

1. Vào GitHub **Releases** của repository.
2. Tải `ServerDesk-v1.0.0-win-x64.zip`.
3. Giải nén vào thư mục người dùng có quyền ghi.
4. Chạy `ServerDesk.App.exe`.
5. Nếu terminal/WebView cần WebView2 Runtime mà máy chưa có, cài Microsoft Edge WebView2 Runtime rồi mở lại ứng dụng.

### Build từ source

```powershell
dotnet restore ServerDesk.sln
dotnet build ServerDesk.sln -c Release
```

## 2. Tạo Server Profile và kết nối

Phân hệ profile là điểm bắt đầu của mọi thao tác.

### Tạo profile

1. Từ màn hình server list, chọn tạo server mới.
2. Nhập friendly name, hostname/IP, SSH port (thường 22), username.
3. Chọn authentication phù hợp:
   - password;
   - private key;
   - encrypted private key + passphrase;
   - keyboard-interactive/MFA khi server yêu cầu.
4. Chọn group/tag/environment/favorite nếu cần.
5. Lưu profile.

Secret được lưu qua secure-storage abstraction; không nên đặt password/token vào tên profile, tag hoặc note không được thiết kế cho secret.

### Host-key trust

Khi kết nối host mới, ServerDesk hiển thị host-key trust dialog. Kiểm tra fingerprint trước khi trust. Nếu host key thay đổi, ServerDesk phải chặn kết nối thay vì tự động chấp nhận key mới. Chỉ resolve mismatch khi bạn đã xác minh nguyên nhân chính đáng (reinstall server/key rotation...).

### Connection state

UI phân biệt connecting/connected/disconnected/reconnecting/failure. Khi kết nối lỗi, đọc category thay vì chỉ retry liên tục: authentication failure, host-key mismatch, permission, timeout, unsupported capability...

## 3. Group, tag, favorite và quản lý nhiều profile

Dùng **Profile Organization** để quản lý nhiều server:

- group theo team/project/environment;
- tag theo role (`web`, `db`, `prod`, `staging`...);
- favorite server thường dùng;
- search/filter;
- clone profile khi server có cấu hình gần giống;
- import/organize metadata theo workflow được cung cấp.

Không sao chép secret thủ công qua file metadata. Profile metadata và credential reference được tách biệt.

## 4. Connection Routing — direct, proxy, bastion/jump host

Mở **Connection Route** khi server không thể SSH trực tiếp.

- **Direct:** Windows client kết nối thẳng target.
- **Proxy:** đi qua proxy hỗ trợ bởi transport hiện tại.
- **Bastion/Jump:** kết nối qua server trung gian rồi tới target.

Sau khi cấu hình, kiểm tra route/endpoint identity trước khi thao tác production. Connection history giúp xem lần kết nối gần đây nhưng không nên dùng history như bằng chứng rằng host key hiện tại vẫn đúng.

## 5. Dashboard

**Server Dashboard** dùng để xem nhanh tình trạng server.

Thông tin chính tùy capability:

- CPU utilization;
- memory/swap;
- load average;
- uptime;
- filesystem usage;
- network overview;
- process/service/container summary;
- cảnh báo hoặc section unavailable khi probe không đủ quyền/capability.

Dashboard là read-oriented. Khi một section `Unavailable/PermissionDenied/Unknown`, không suy diễn rằng toàn server offline; mở module tương ứng để xem lỗi chi tiết.

## 6. Remote Explorer

**Remote Explorer** là File Explorer qua SFTP.

### Tác vụ phổ biến

- duyệt folder bằng breadcrumb/address;
- back/forward/up;
- xem file ẩn;
- sort/filter/search local view;
- tạo file/folder;
- rename;
- copy/move;
- upload từ Windows;
- download về Windows;
- xóa với confirmation phù hợp;
- xem owner/group/mode;
- copy remote path;
- mở editor/terminal tại path khi workflow hỗ trợ.

### File protected

Nếu file không ghi được bằng user SSH hiện tại, dùng privileged-edit workflow thay vì `chmod 777`. ServerDesk stage candidate, validate nếu có validator, thực hiện privileged replace có guard, preserve metadata phù hợp và verify sau save.

### Large directory

Chờ loading/virtualized result; tránh bấm refresh liên tục trong lúc operation đang chạy.

## 7. Remote Editor

Dùng **Remote Editor** cho config/source text.

Workflow an toàn:

1. Load content và metadata hiện tại.
2. Chỉnh nội dung.
3. Nếu file type/module có validator, chạy validation trước apply.
4. Save thường nếu path writable; dùng privileged save nếu thực sự cần.
5. Với workflow guarded, candidate được stage thay vì ghi đè live file ngay lập tức.
6. Sau save, kiểm tra status/validation result.

Không dùng editor như binary editor. Đối với nginx/Compose/env file, ưu tiên mở editor từ module chuyên biệt để nhận thêm validation/safety context.

## 8. Terminal

**Terminal** là SSH PTY tương tác, phù hợp khi GUI chưa bao phủ tác vụ nâng cao.

- mở nhiều terminal session cùng server;
- chạy shell tương tác;
- copy/paste/search/scrollback;
- resize terminal;
- đóng session riêng mà không bắt buộc disconnect toàn workspace.

Terminal là escape hatch mạnh nhất; lệnh gõ trong terminal không nhận được cùng mức preview/guard như các workflow GUI. Người dùng chịu trách nhiệm với lệnh shell tự nhập.

## 9. SSH Port Forwarding

Mở **Port Forwarding** để tạo tunnel.

### Local forwarding

Ví dụ database:

```text
Windows 127.0.0.1:5433 -> SSH -> Linux 127.0.0.1:5432
```

Dùng khi muốn app local kết nối service chỉ mở trên server loopback/private network.

### Remote forwarding

Expose một endpoint phía remote thông qua SSH theo profile đã review. Xác minh bind address/port vì remote forwarding có thể mở reachability ngoài ý muốn.

### Dynamic/SOCKS

Tạo SOCKS proxy qua SSH khi transport/server support.

Luôn xem tunnel state và bound port thực tế trước khi đưa endpoint cho tool khác.

## 10. Processes

**Process Manager** hiển thị process inventory với các field tùy permission như PID, name, user, CPU, memory, state và command metadata.

Actions:

- xem detail;
- terminate;
- force kill với mức cảnh báo cao hơn;
- liên kết tới port/log/terminal khi discoverable.

`Kill` có thể làm mất dữ liệu; thử graceful terminate trước nếu phù hợp.

## 11. Services

**Service Manager** tập trung vào systemd.

Có thể:

- list/search/filter unit;
- xem active/inactive/failed;
- xem enabled/disabled/masked;
- start/stop/restart/reload;
- enable/disable;
- xem unit details/path và recent logs khi khả dụng.

Mutation cần quyền phù hợp, thường qua noninteractive sudo policy. Nếu server không dùng systemd, V1 không giả vờ full support.

## 12. Storage

**Storage** dùng cho quan sát disk/filesystem:

- block devices;
- filesystem/mount;
- size/used/free;
- usage warning;
- directory/storage hints tùy capability.

V1 không phải Disk Management đầy đủ: partitioning, formatting và destructive filesystem surgery không thuộc scope mặc định.

## 13. Network và listening ports

**Network** hiển thị:

- interfaces;
- IP/address;
- RX/TX information;
- routes khi adapter có dữ liệu;
- listening ports;
- protocol/bind address;
- PID/process association khi permission cho phép.

Dùng thông tin này để mở Process/Service view hoặc tạo SSH tunnel; không nhầm listening trên `127.0.0.1` với public exposure.

## 14. Logs

**Log Viewer** gom các nguồn được hỗ trợ như journald, file log và container log.

Workflow:

- chọn source/service;
- refresh hoặc follow realtime khi có;
- pause/resume;
- filter severity/text/time khi source hỗ trợ;
- export/bookmark nếu UI cung cấp;
- dừng stream khi không cần để giảm tải.

Log content là dữ liệu server không tin cậy; ServerDesk không nên thực thi ANSI/markup tùy ý từ log text.

## 15. Docker Inventory và Container Actions

Mở **Docker Inventory** khi Docker CLI/daemon được detect và user có permission.

Có thể:

- xem container/image/network/volume/system inventory theo UI hiện có;
- start/stop/restart/pause/kill/remove container theo action được expose;
- inspect;
- xem stats/logs;
- mở container diagnostics;
- mở exec terminal khi được phép.

ServerDesk dùng remote CLI qua SSH ở agentless mode và không expose Docker Unix socket qua network.

## 16. Docker Exec Terminal / Diagnostics

Dùng **Docker Exec Terminal** cho shell/command bên trong container.

- xác minh đúng container trước khi exec;
- không đưa secret vào command line nếu có cơ chế input an toàn hơn;
- hiểu rằng exec bên trong container có thể thay đổi workload mặc dù Docker inventory là read-oriented.

Nếu container restart/disappear, refresh inventory trước khi retry.

## 17. Docker Compose v2

Mở **Docker Compose** từ Docker workflow.

### Project

- discover project identity/config files;
- xem service state;
- xem logs;
- Up;
- Down;
- Restart;
- Pull;
- Build.

Mutation có confirmation và post-action verification. `Down` không tự yêu cầu xóa volumes.

### Raw YAML

V1 giữ raw YAML để không làm mất anchors/extensions/profiles.

Workflow save:

1. Chọn config file đúng project.
2. Chỉnh raw YAML.
3. Validate candidate bằng `docker compose config --quiet` với đúng project directory/file chain.
4. Chỉ apply khi validation pass.
5. Sau apply, refresh project state.

Không dùng legacy `docker-compose` v1 như baseline certification.

## 18. Git Operations

**Git Operations** phục vụ vận hành/deployment, không thay thế IDE Git.

Dùng để:

- discover repository tại path được cấu hình;
- xem branch/revision;
- xem working-tree state;
- ahead/behind khi available;
- fetch;
- pull khi explicit/safe;
- xem diff/status phục vụ deployment.

ServerDesk không tự destructive-reset working tree để “sửa” conflict.

## 19. Scheduled Tasks

**Scheduled Tasks** hợp nhất trải nghiệm cron và systemd timer ở mức task-oriented.

Có thể tùy capability:

- list;
- enable/disable;
- tạo/chỉnh basic schedule;
- xem command/unit;
- last/next execution;
- logs;
- raw view cho syntax nâng cao.

Luôn review command và schedule trước save; task chạy background có thể gây mutation sau khi bạn rời ServerDesk.

## 20. nginx Inventory và Site Editor

### Inventory

**Nginx Inventory** phát hiện nginx/site/config liên quan.

### Site Editor

Dùng simple fields cho trường hợp phổ biến hoặc raw config cho advanced directives.

Mutation chuẩn:

1. Prepare candidate.
2. Backup/context theo workflow.
3. Validate bằng `nginx -t`.
4. Nếu fail: không activate config lỗi.
5. Install/activate.
6. Reload nginx.
7. Verify service state.

Không cố ép mọi nginx directive vào form GUI.

## 21. TLS Certificates / Certbot

**TLS Certificate** module hiển thị certificate inventory/expiration/association khi detect được.

Khi Certbot capability hiện diện, workflow được expose có thể hỗ trợ issue/renew theo scope. Không giả định mọi cert là Let's Encrypt-managed. Xác minh domain, nginx site và renewal result trước khi coi tác vụ hoàn tất.

## 22. Environment Files

**Environment File** dùng để chỉnh file cấu hình/env theo guarded editor pattern.

- không log/persist secret value vào operation history;
- preview path/target;
- validate/guard nếu workflow có;
- privileged save chỉ khi cần;
- reload/restart application/service chỉ khi deployment workflow yêu cầu và đã review impact.

## 23. Deployment

**Deployment** kết hợp các primitive Git/config/service/nginx theo workflow đã review.

Trước deploy:

- xác minh server/environment;
- kiểm tra repository/branch/revision;
- đọc preview step và risk;
- đảm bảo backup/rollback prerequisite nếu workflow yêu cầu.

Sau deploy:

- đọc verification result;
- kiểm tra service/nginx/health/log;
- nếu network timeout xảy ra sau mutation và state là Ambiguous, re-observe trước retry.

## 24. Firewall

**Firewall Inventory/Mutation** chọn adapter theo server:

- Ubuntu/Debian: UFW khi present;
- RHEL-family: firewalld khi present.

Có thể xem state/rule và thực hiện add/delete rule theo workflow support.

Trước mutation:

1. Xác minh source/port/protocol/action.
2. Đặc biệt bảo vệ port SSH hiện tại.
3. Review preview.
4. Apply.
5. Verify rule/state.

Raw nftables visual editing không thuộc V1.

## 25. User Administration

**User Administration** cung cấp visibility và guarded workflow cho:

- users/groups;
- shell/home;
- lock state;
- group membership;
- authorized keys;
- sudo visibility;
- supported create/lock/unlock/group changes.

Không có casual one-click root enablement. Khi sửa authorized keys, xác minh còn đường SSH hợp lệ trước khi đóng session hiện tại.

## 26. Package Administration

**Package Administration** dùng adapter:

- APT cho Debian/Ubuntu;
- DNF cho RHEL-family khi được support/detect.

Workflow phổ biến:

1. Refresh metadata explicit.
2. Xem available updates.
3. Chọn package/action.
4. Review impact.
5. Execute.
6. Kiểm tra result/reboot/service impact nếu có.

ServerDesk không tự update production server mặc định.

## 27. Backup & Restore

**Backup/Restore** là destructive-sensitive module.

### Backup

- chọn target/type;
- xác minh destination;
- chạy backup;
- artifact chỉ được coi usable khi verification policy pass (size/hash/native verification tùy target).

### Restore

1. Chọn đúng verified artifact.
2. Preview target identity/overwrite impact.
3. Confirm destructive operation.
4. Execute một lần.
5. Post-verify target.
6. Nếu dispatch completion không chắc chắn: giữ `Ambiguous/Unknown`, không blind retry.

Database restore có thêm engine-specific constraint ở mục tiếp theo.

## 28. Database Profiles

**Database Profiles** quản lý thông tin kết nối không-secret và credential reference.

Engine hiện có adapter V1:

- PostgreSQL;
- MySQL;
- MariaDB;
- Redis;
- Microsoft SQL Server;
- MongoDB.

Kết nối remote database nên dùng SSH local tunnel để database có thể tiếp tục bind loopback/private endpoint thay vì mở public port.

## 29. Database Runtime & Diagnostics

**Database Runtime** phát hiện server engine/service/tooling và mở authenticated diagnostics theo engine.

Lưu ý:

- client tool tồn tại không đồng nghĩa server engine đang chạy;
- SQL Server `sqlcmd` alone không chứng minh instance runtime;
- MongoDB `mongosh`/tools alone không chứng minh `mongod`/`mongos` runtime;
- diagnostics có version/capability gate;
- MongoDB V1 chỉ đọc bounded topology/database/collection metadata, không đọc document content.

## 30. Database Backup

Chỉ dùng khi matrix đánh dấu capability Certified.

- PostgreSQL/MySQL/MariaDB: dump artifact + deterministic verification theo adapter.
- SQL Server: native `.bak`, bounded file check, SHA-256 và `RESTORE VERIFYONLY ... WITH CHECKSUM` trước khi usable.
- MongoDB standalone: gzip archive, SHA-256 và dry-run verification theo pinned Database Tools.
- Redis: **Backup Unsupported trong V1**.

Nếu UI báo Unsupported, không bypass bằng cách coi một file tự tạo bên ngoài là certified ServerDesk backup.

## 31. Database Restore

Restore luôn gắn với verified manifest/artifact và target identity.

- PostgreSQL/MySQL/MariaDB: fresh preview, confirmation, target verification.
- SQL Server: exact database target + verified `.bak`; post-verify identity.
- MongoDB: V1 chỉ standalone, exact profile/database identity, destructive preview, namespace boundary và post-verify.
- Redis: **Restore Unsupported**.
- MongoDB replica set/mongos backup/restore: **Unsupported cho tới khi có certification topology riêng**.

Không có arbitrary SQL/Mongo shell query console trong certified V1 database module.

## 32. Global Dashboard và Multi-server

**Global Dashboard** cho phép nhìn nhiều server cùng lúc theo normalized status/warning.

**Server Comparison** dùng để so sánh selected facts giữa các server.

**Bulk Metadata Mutation/Profile Metadata Import** phục vụ organization/config metadata có guard. Không suy diễn rằng mọi destructive action của một server đều có bulk equivalent.

## 33. Operation History / Audit

**Operation History** giúp review các remote mutation đã chạy:

- operation kind/risk;
- target identity ở mức an toàn;
- completion/result;
- timestamp/context phù hợp.

Audit không nên chứa password, private key, token, raw secret env value hoặc full sensitive command payload.

## 34. Optional serverdesk-agent

V1 đã implement/certify agent backend:

- Linux loopback-only listener;
- SSH-tunneled gRPC;
- negotiation/health;
- realtime metrics;
- process/service/Docker events;
- redacted journald streaming;
- signed artifact verification;
- fixed-surface install/update/status/uninstall backend;
- bounded rollback và Ambiguous-state safety.

Tuy nhiên repository hiện không chứng minh một **Agent Management WPF window riêng**, vì vậy đây chưa được hướng dẫn như một menu GUI thông thường. Agentless vẫn là đường sử dụng chính. Việc phân phối/cài agent phải tuân theo signed release manifest và external private signing-key process.

## 35. Khi feature bị Disabled/Unsupported

Kiểm tra theo thứ tự:

1. Server còn SSH connected không?
2. Capability/tool/service có tồn tại không?
3. User SSH có permission không?
4. Version/topology có nằm trong certification matrix không?
5. Có cần sudo noninteractive không?
6. Operation trước đó có đang Ambiguous không?

Không “fix” bằng cách mở public port, tắt host-key check, chmod rộng, lưu plaintext secret hoặc blind retry destructive operation.

## 36. Đọc thêm

- [`CURRENT_SCOPE.vi.md`](CURRENT_SCOPE.vi.md) — đã làm/chưa làm.
- [`SUPPORT_MATRIX.vi.md`](SUPPORT_MATRIX.vi.md) — exact platform/version support.
- [`SECURITY_RULES.vi.md`](SECURITY_RULES.vi.md) — security rules.
- [`agent-lifecycle-execution.vi.md`](agent-lifecycle-execution.vi.md) — agent lifecycle/recovery.
- [`agent-release-security.vi.md`](agent-release-security.vi.md) — agent signing/update trust.
