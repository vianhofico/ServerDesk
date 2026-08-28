# Ma trận hỗ trợ

[English](SUPPORT_MATRIX.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

ServerDesk phân biệt rõ hành vi **certified**, **experimental/best-effort** và **unsupported/unknown**. Một distro chỉ được coi là certified sau khi các compatibility gate tự động/thủ công bắt buộc vượt qua cho release tương ứng.

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

### Databases

Hỗ trợ server-oriented ban đầu:

- PostgreSQL;
- MySQL/MariaDB;
- Redis.

Các major version cụ thể chỉ được certified khi integration test của M6 định nghĩa và thực thi chúng.

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

## 7. Removal / deprecation

Certified support có thể bị deprecate khi:

- upstream OS không còn hợp lý để hỗ trợ/duy trì bảo mật cho sản phẩm;
- mất compatibility bắt buộc với .NET/SSH/tool;
- chi phí bảo trì không còn hợp lý.

Deprecation phải được ghi tài liệu trước khi loại bỏ và không được âm thầm chuyển một server đang Certified thành unsupported mà không có release note.
