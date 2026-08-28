# ServerDesk

[English](README.md) | **Tiếng Việt**

ServerDesk là ứng dụng desktop dành cho Windows giúp quản lý máy chủ Linux thông qua giao diện trực quan, quen thuộc kiểu Windows, đồng thời vẫn sử dụng SSH/SFTP làm lớp điều khiển an toàn.

> Định hướng sản phẩm: **File Explorer + Task Manager + Services + Terminal + Docker Desktop + quản trị máy chủ**, giúp người dùng thực hiện các tác vụ phổ biến mà không cần ghi nhớ nhiều lệnh Linux.

## Trạng thái

Repository hiện đang ở giai đoạn bootstrap/foundation. Product plan, ràng buộc kiến trúc, workflow cho coding agent, mô hình bảo mật, quy tắc UX, support matrix và roadmap theo milestone được xem là các hợp đồng triển khai cần tuân thủ.

## Công nghệ mục tiêu

- Ứng dụng desktop cho Windows 10/11
- .NET 10 + WPF
- Kiến trúc module theo định hướng MVVM
- Ưu tiên SSH/SFTP, không yêu cầu cài agent trên server
- WebView2 + xterm.js cho lớp terminal (được đưa vào từ M1)
- SQLite để lưu metadata local không chứa secret
- Windows Credential Manager / DPAPI để lưu secret
- Có thể bổ sung `serverdesk-agent` chạy qua gRPC tunnel ở milestone sau

## Tài liệu

Nên đọc các tài liệu sau trước khi triển khai feature:

1. [`AGENTS.vi.md`](AGENTS.vi.md) — workflow bắt buộc dành cho coding agent
2. [`docs/PRODUCT_PLAN.vi.md`](docs/PRODUCT_PLAN.vi.md) — phạm vi sản phẩm đầy đủ
3. [`docs/ARCHITECTURE.vi.md`](docs/ARCHITECTURE.vi.md) — kiến trúc và các ranh giới hệ thống
4. [`docs/UI_UX.vi.md`](docs/UI_UX.vi.md) — quy tắc tương tác và thiết kế giao diện
5. [`docs/ROADMAP.vi.md`](docs/ROADMAP.vi.md) — thứ tự milestone và acceptance gate
6. [`docs/SECURITY.vi.md`](docs/SECURITY.vi.md) — yêu cầu bảo mật
7. [`docs/TESTING.vi.md`](docs/TESTING.vi.md) — chiến lược kiểm thử và compatibility gate
8. [`docs/SUPPORT_MATRIX.vi.md`](docs/SUPPORT_MATRIX.vi.md) — các OS/capability được chứng nhận hỗ trợ
9. [`docs/CONNECTION_ROUTING.vi.md`](docs/CONNECTION_ROUTING.vi.md) — định tuyến SSH qua direct/proxy/bastion

## Nguyên tắc cốt lõi

- **Agentless first:** chỉ cần một SSH server thông thường là đủ cho phiên bản ban đầu.
- **Secure by default:** xác minh host key; secret không bao giờ được lưu dạng plaintext.
- **GUI trước, CLI luôn sẵn sàng:** tác vụ phổ biến có giao diện trực quan, thao tác nâng cao vẫn thực hiện được qua terminal thật.
- **Capability based:** không giả định server luôn có Docker, systemd, nginx, sudo hoặc package manager.
- **Machine readable:** ưu tiên output có cấu trúc và system file ổn định thay vì parse nội dung terminal dành cho người đọc.
- **Safe mutations:** với thao tác thay đổi hệ thống, ưu tiên validate, preview, confirm, backup, execute, verify và rollback khi có thể.
- **Không đặt điều kiện distro trong UI:** logic riêng theo distro nằm phía sau các adapter.

## Cấu trúc repository ban đầu

```text
src/
  ServerDesk.App/
  ServerDesk.Domain/
  ServerDesk.Application/

docs/
.github/
```

Các project hạ tầng bổ sung, Linux adapter và feature module chỉ được đưa vào theo roadmap khi thực sự cần tới ranh giới của chúng.

## Build

```powershell
dotnet restore src/ServerDesk.App/ServerDesk.App.csproj
dotnet build src/ServerDesk.App/ServerDesk.App.csproj -c Release
```

CI sử dụng Windows runner vì ứng dụng chính được xây dựng bằng WPF.

## Tiêu chí hoàn thành V1

V1 chỉ được xem là hoàn thành khi support matrix đã chứng nhận vượt qua kiểm thử end-to-end cho các phần: kết nối an toàn, Explorer, editor, terminal, dashboard, processes, services, logs, storage, network/ports, Docker/Compose, Git cơ bản, nginx cơ bản, SSH tunneling, reconnect, credentials, operation history và các cơ chế bảo vệ đối với thao tác phá hủy dữ liệu/hệ thống.
