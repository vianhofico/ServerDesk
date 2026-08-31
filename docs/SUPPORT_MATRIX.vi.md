# Ma trận hỗ trợ

[English](SUPPORT_MATRIX.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

ServerDesk phân biệt rõ hành vi **certified**, **tested**, **experimental/best-effort** và **unsupported/unknown**. Một distro hoặc capability theo engine/version chỉ được coi là certified sau khi các compatibility gate tự động/thủ công bắt buộc vượt qua cho release tương ứng.

## 1. Nền tảng client

### Mục tiêu certified

- Windows 11 x64

### Mục tiêu tương thích bắt buộc

- Windows 10 x64 khi stack .NET/WPF/WebView2 được chọn vẫn còn được các dependency của dự án hỗ trợ.

ARM64 có thể được bổ sung sau khi có build/test coverage rõ ràng.

## 2. Thứ tự chứng nhận Linux server

### Chứng nhận chính cho V1

| Family | Release | Mức mục tiêu |
|---|---|---|
| Ubuntu | 24.04 LTS | Certified |
| Ubuntu | 26.04 LTS | Certified |
| Debian | 13 | Certified |

### Mở rộng V1.x

| Family | Release | Mức mục tiêu |
|---|---|---|
| Rocky Linux | 9 | Certified sau khi adapter matrix pass |
| Rocky Linux | 10 | Certified sau khi adapter matrix pass |
| AlmaLinux | 9 | Certified sau khi adapter matrix pass |
| AlmaLinux | 10 | Certified sau khi adapter matrix pass |

### Tương lai / best-effort cho tới khi được promote

- Amazon Linux;
- Oracle Linux;
- Fedora;
- openSUSE;
- Arch Linux;
- các distro Linux dùng systemd khác.

## 3. Giả định nền tảng

Agentless mode yêu cầu:

- SSH có thể truy cập được từ Windows client;
- một phương thức xác thực được client implementation hỗ trợ;
- SFTP subsystem cho các tính năng Explorer;
- các shell/tool remote cơ bản cần thiết cho capability được yêu cầu.

ServerDesk không được giả định có quyền root.

## 4. Mức hỗ trợ capability

Mỗi capability được phát hiện độc lập.

### Core

| Capability | Yêu cầu ban đầu |
|---|---|
| SSH command | Bắt buộc |
| SFTP | Bắt buộc cho Explorer |
| PTY/shell | Bắt buộc cho Terminal |
| local forwarding | M1 |
| remote forwarding | M1 |
| dynamic forwarding | M1 khi library/platform support đủ tin cậy |
| `/etc/os-release` | nguồn ưu tiên để phát hiện OS |
| `/proc` metrics | baseline cho Linux dashboard |

### Quản lý service

| Capability | V1 |
|---|---|
| systemd | Luồng certified |
| SysV init | Chưa certified ban đầu |

### Containers

| Capability | V1 |
|---|---|
| Docker Engine CLI | Hỗ trợ khi được phát hiện và có thể sử dụng |
| Docker Compose v2 (`docker compose`) | Hỗ trợ |
| legacy `docker-compose` v1 | Best-effort/tùy chọn, không bắt buộc để certification |
| Podman | Adapter tương lai |
| Kubernetes | Ngoài phạm vi V1 |

### Web server

| Capability | V1 |
|---|---|
| nginx | Hỗ trợ trong deployment milestone |
| Apache | Tương lai/experimental |
| Caddy | Tương lai |

### Firewall

| Family | Adapter |
|---|---|
| Debian/Ubuntu | UFW khi có |
| RHEL family | firewalld khi có |
| raw nftables | Advanced/tương lai; không chỉnh trực tiếp trong V1 ban đầu |

### Package management

| Family | Adapter |
|---|---|
| Debian/Ubuntu | APT |
| Rocky/Alma/RHEL family | DNF |

### Databases — ma trận chứng nhận engine/version

Các dòng dưới đây gắn với đúng fixture engine thật được thực thi trong luồng OpenSSH CI. **Certified** nghĩa là capability đó đã chạy với engine fixture thật. **Tested** dành cho bằng chứng test hữu ích nhưng chưa có đầy đủ đường certification với engine thật; version chưa liệt kê không được tự động nâng lên Certified. **Unsupported** nghĩa là ServerDesk fail-closed cho capability đó.

| Engine | Version fixture chính xác | Runtime / inventory | Kết nối tunnel SSH | Diagnostics | Backup | Restore |
|---|---:|---|---|---|---|---|
| PostgreSQL | 18.6 | Certified | Certified | Certified | Certified | Certified |
| MySQL | 8.4.11 | Certified | Certified | Certified | Certified | Certified |
| MariaDB | 11.8.9 | Certified | Certified | Certified | Certified | Certified |
| Redis | 8.10.0 | Certified | Certified | Certified | Unsupported | Unsupported |
| Microsoft SQL Server | 17.0.4075.5 (SQL Server 2025 CU8) | Certified | Certified | Certified | Certified | Certified |

Bằng chứng và ranh giới:

- CI chạy fixture PostgreSQL `18.6`, MySQL `8.4.11`, MariaDB `11.8.9`, Redis `8.10.0` và Microsoft SQL Server `17.0.4075.5` qua job OpenSSH integration thật.
- Backup PostgreSQL/MySQL/MariaDB chỉ được đánh dấu có thể sử dụng sau xác minh artifact xác định; restore yêu cầu đúng verified manifest/định danh target, preview/xác nhận mới, xử lý destructive dispatch và xác minh target sau restore.
- Backup SQL Server dùng artifact `.bak` native và chỉ được đánh dấu usable sau khi kiểm tra file có giới hạn, xác minh SHA-256 và `RESTORE VERIFYONLY ... WITH CHECKSUM` đều thành công. Restore được khóa vào đúng manifest/database target đã verify, yêu cầu preview/xác nhận destructive mới, giữ trạng thái Ambiguous/Unknown khi dispatch không chắc chắn và post-verify danh tính target.
- Runtime inventory SQL Server phân biệt việc phát hiện package/service của server với client tooling; chỉ có `sqlcmd` không được coi là SQL Server đang chạy. Exact live server version được lấy qua authenticated diagnostics trước khi áp dụng version gate cho backup/restore.
- Credential SQL Server tiếp tục nằm trong secret abstraction. CI tạo và mask mật khẩu SA tại runtime; giá trị credential không được persist vào profile metadata, history, rendered command hay diagnostic artifact tải lên.
- Backup/restore Redis là **Unsupported** vì chưa chứng minh được semantics sao chép/khôi phục persistence một cách xác định. UI/application phải fail-closed trước khi tạo mutation backup/restore được chứng nhận.
- Mọi engine version không được liệt kê rõ ở trên đều **không được Certified** chỉ vì parser hay client command tình cờ hoạt động. Chúng vẫn là unsupported/unknown cho mục đích certification cho đến khi có bằng chứng rõ ràng để promote.
- `Tested` dành cho bằng chứng từng phần trong tương lai; các exact engine/version row hiện tại hoặc Certified cho capability, hoặc được ghi rõ Unsupported.
- Thực thi SQL query tùy ý/basic query console vẫn nằm ngoài phạm vi database được certified; ServerDesk không cung cấp SQL query console như một phần của matrix này.

## 5. Ý nghĩa trạng thái capability

UI và application code phải phân biệt:

```text
Available
Unavailable
PermissionDenied
UnsupportedVersion
ProbeFailed/Unknown
```

Ví dụ:

- `docker` chưa cài != Docker permission denied;
- tìm thấy `systemctl` nhưng hệ thống không boot bằng systemd != systemd unavailable cho mọi mục đích;
- command timeout != command không tồn tại.

## 6. Tiêu chí promote

Để promote một distro/release hoặc capability lên Certified:

- adapter/parser fixture pass;
- core SSH/SFTP integration pass;
- feature integration tương ứng pass;
- security-negative test pass;
- manual core workflow checklist pass trên hệ thống đại diện;
- known limitation được ghi tài liệu;
- không có required test nào bị skip vĩnh viễn cho target đó.

Với capability theo database engine/version, promotion còn yêu cầu một dòng matrix rõ ràng và fixture OpenSSH integration với engine thật cho đúng version/capability đó.

## 7. Removal / deprecation

Certified support có thể bị deprecate khi:

- upstream OS không còn hợp lý để hỗ trợ/duy trì bảo mật cho sản phẩm;
- mất compatibility bắt buộc với .NET/SSH/tool;
- chi phí bảo trì không còn hợp lý.

Deprecation phải được ghi tài liệu trước khi loại bỏ và không được âm thầm chuyển một server đang Certified thành unsupported mà không có release note.
