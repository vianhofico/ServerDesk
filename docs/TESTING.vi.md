# Chiến lược kiểm thử

[English](TESTING.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

## 1. Mục tiêu

ServerDesk không thể tuyên bố reliability chỉ dựa vào unit test vì sản phẩm phụ thuộc vào hành vi SSH, Linux tooling, permission, output theo distro, network failure và các remote state transition có tính destructive. Test strategy được phân lớp để test nhanh bảo vệ thiết kế, còn test trên môi trường thật bảo vệ compatibility.

## 2. Test pyramid

### Unit tests

Bao phủ:

- domain model/value object;
- capability state transition;
- operation risk policy;
- command specification/builder;
- parser;
- validation;
- retry decision;
- rollback orchestration;
- error mapping;
- hành vi ViewModel không cần live WPF integration.

### Fixture/parser tests

Mọi parser cho machine output phải giữ fixture đại diện từ các distro/version certified bị ảnh hưởng.

Fixture phải gồm:

- output bình thường;
- output rỗng;
- output malformed/truncated;
- permission error khi áp dụng;
- variation theo version đã biết khác nhau;
- tên/path bất thường nhưng hợp lệ.

Parser test không được “sửa” output bất ngờ bằng cách âm thầm đoán.

### Adapter tests

Chạy hành vi Linux adapter trên distro family mà adapter tuyên bố hỗ trợ.

Ví dụ:

- phát hiện `/etc/os-release`;
- systemd property query;
- parse inventory APT/DNF;
- chuẩn hóa UFW/firewalld;
- thao tác file metadata chuẩn.

### SSH integration tests

Sử dụng Linux environment/VM disposable với SSH daemon thật.

Các case:

- password auth;
- key auth;
- encrypted key/passphrase;
- keyboard-interactive khi harness hỗ trợ;
- unknown host;
- changed host fingerprint;
- permission denied;
- command timeout;
- dropped TCP connection;
- SFTP upload/download/rename/delete;
- PTY open/resize/close;
- port forward lifecycle;
- concurrent command/SFTP/terminal channel.

### Feature integration tests

Chạy tooling thật được hỗ trợ trong environment disposable:

- systemd services;
- Docker/Compose;
- nginx validation/reload;
- journald/log streaming;
- firewall adapter;
- package manager read operation;
- database backup/restore test instance.

Destructive test không bao giờ target shared hoặc persistent infrastructure.

### UI tests

Tự động hóa critical user journey sau khi có UI automation harness:

- tạo profile;
- connection/trust flow;
- Explorer navigation;
- upload/download;
- edit/save;
- restart service;
- mở/filter log;
- Docker start/stop/logs;
- disconnect/reconnect;
- cancellation/error recovery;
- theme switching và major keyboard navigation.

Visual snapshot testing có thể bổ sung nhưng không thay thế behavioral assertion.

## 3. Certified compatibility environment

Support matrix kiểm soát environment bắt buộc. Target certification ban đầu là Ubuntu/Debian, tiếp theo Rocky/AlmaLinux.

Test phải ghi chính xác image/VM version. “Latest Linux” không phải certification label hợp lệ.

## 4. Các tier CI

### Pull request CI

Đủ nhanh để chạy trên mọi PR:

```text
restore
build
format/static analysis
unit tests
parser fixtures
security-oriented unit tests
```

### Extended integration CI

Chạy trên PR liên quan hoặc dedicated workflow:

```text
SSH integration
selected Linux adapter integration
Docker/nginx feature integration
```

### Nightly compatibility CI

Chạy complete certified matrix khi infrastructure cho phép:

```text
Ubuntu versions
Debian versions
Rocky versions
AlmaLinux versions
feature fixtures
failure injection
```

Tạo compatibility report thay vì che giấu environment bị skip.

## 5. Yêu cầu failure injection

Remote software phải cố ý test các failure mơ hồ:

- connection drop trước khi command được gửi;
- drop sau khi command có thể đã được gửi;
- drop trong khi stream output;
- SFTP bị gián đoạn giữa transfer;
- disk full;
- permission thay đổi giữa operation;
- service command trả failure;
- validator reject candidate config;
- remote process bị hang;
- CLI output malformed/unsupported.

Test phải chứng minh ServerDesk không retry sai destructive operation hoặc báo success khi chưa verify.

## 6. File mutation tests

Privileged file-save workflow phải test:

- owner/group/mode được giữ nguyên;
- validation fail để original không đổi;
- backup được tạo khi policy yêu cầu;
- atomic candidate installation khi hỗ trợ;
- temp được cleanup khi success;
- best-effort cleanup khi failure;
- sudo permission không đủ;
- target thay đổi đồng thời (future conflict detection khi được implement).

## 7. Security tests

Các category bắt buộc:

- shell/argument injection string trong path/name;
- secret redaction trong log/error/history;
- unknown/changed SSH host key;
- unsafe WebView message payload;
- hủy destructive confirmation;
- policy hiển thị secret trong database/Docker environment;
- firewall change có thể ảnh hưởng SSH access hiện tại;
- giả định path traversal trong local download destination.

## 8. Performance tests

Đo workload đại diện:

- startup;
- server connect/capability scan;
- directory 10k file;
- log rate cao;
- hàng trăm/hàng nghìn process;
- nhiều Docker container;
- dashboard streaming trong nhiều phút;
- nhiều terminal tab;
- concurrent SFTP transfer và metrics.

Assertion chính: UI vẫn responsive và memory/connection resource không tăng vô hạn.

## 9. Manual exploratory checklist trước release

Trên mọi certified OS family:

- connect từ Windows install/user profile sạch;
- trust host key;
- reconnect sau app restart;
- Explorer CRUD và permission;
- terminal interactive tool;
- process/service control;
- logs;
- storage/network view;
- Docker/Compose nếu certified;
- nginx nếu certified;
- tunnel database connection;
- disconnect trong safe operation và mutation;
- light/dark/system theme;
- keyboard-only pass qua core workflow.

### M1.5 Windows PTY smoke checklist

Chạy checklist này trên Windows đã cài WebView2 Runtime và một Linux SSH target disposable:

1. Build ServerDesk từ clean checkout và xác minh `TerminalFrontend/dist/index.html`, `terminal.js`, `terminal.css` được copy cạnh app output. Sau build, ngắt Internet của Windows machine; mở terminal vẫn phải hoạt động vì runtime asset nằm local.
2. Add/select server profile và click **Terminal**. Xác minh host-trust và credential prompt hoạt động giống SSH connection bình thường và shell prompt xuất hiện.
3. Chạy `printf '\033[31mred\033[0m\n'` và xác minh ANSI color render đúng. Chạy `top`, `less /etc/services` và, nếu có, `vim`; full-screen repaint/input phải vẫn usable.
4. Resize terminal window nhiều lần, sau đó chạy `stty size`; rows/columns phải theo vùng xterm đang hiển thị.
5. Mở ít nhất ba tab, gồm hai tab tới cùng server. Chạy `sleep 20` ở một tab và tiếp tục gõ command ở tab khác; các tab phải độc lập.
6. Trong khi terminal tab bận, thực hiện SFTP operation qua integration harness/current file transport. Terminal không được block independent SFTP channel.
7. Chọn terminal text và nhấn `Ctrl+Shift+C`; paste bằng `Ctrl+Shift+V`. Nhấn `Ctrl+C` thường trong khi `sleep 20` chạy và xác minh SIGINT tới remote shell.
8. Tạo nhiều screen output, nhấn `Ctrl+Shift+F`, search backward/forward, sau đó đóng search và xác minh terminal focus/input hoạt động lại.
9. Đóng một terminal tab đang bận. Xác nhận tab biến mất nhanh và không nhận thêm remote input cho disposed session. Đóng toàn bộ Terminal window khi nhiều tab đang mở và xác minh mọi PTY được cleanup.
10. Drop SSH connection khi tab đang mở. Tab/header phải hiển thị disconnected/faulted thay vì báo success, và mở tab mới phải tạo SSH session mới.

## 10. Định nghĩa “đã được test”

Một feature không được coi là “tested” chỉ vì tồn tại mocked happy-path unit test.

Với remote feature, completion yêu cầu tổ hợp phù hợp với risk:

```text
unit behavior
+ parser fixtures
+ adapter/integration evidence
+ negative/failure cases
+ UI workflow test when harness exists
```
