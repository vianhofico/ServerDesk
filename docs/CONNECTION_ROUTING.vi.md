# Định tuyến kết nối

[English](CONNECTION_ROUTING.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

ServerDesk tách việc lựa chọn tuyến kết nối khỏi cơ chế xác thực SSH. Một server profile sở hữu SSH endpoint và phương thức xác thực; một route tùy chọn mô tả cách client đi tới endpoint đó.

## Các route được hỗ trợ

- **Direct** — kết nối trực tiếp tới endpoint của server.
- **HTTP proxy** — dùng cơ chế HTTP CONNECT proxy native của SSH.NET.
- **SOCKS4 proxy** — dùng SOCKS4 proxy transport native của SSH.NET.
- **SOCKS5 proxy** — dùng SOCKS5 proxy transport native của SSH.NET.
- **SSH bastion một hop** — kết nối tới một bastion profile đã lưu, tạo local forward chỉ bind loopback từ SSH session đó tới endpoint đích, sau đó thiết lập một SSH session riêng tới server đích thông qua forward này.

## Các bất biến bảo mật

1. Mật khẩu proxy chỉ được lưu thông qua `ISecretStore` (Windows Credential Manager trong desktop app). SQLite chỉ lưu một `SecretReference` dạng opaque.
2. Route editor không bao giờ đọc mật khẩu proxy hiện có ngược trở lại WPF. Để trống và không thay đổi sẽ giữ secret đã lưu; replace/clear phải là hành động riêng và rõ ràng.
3. Host key của bastion và target được xác minh độc lập. Trust observation của target luôn dùng host và port gốc của target, dù transport socket local kết thúc tại một loopback forward tạm thời.
4. Bastion forwarding chỉ bind tới `127.0.0.1` và một local port được cấp tự động.
5. Self-bastion, bastion profile bị thiếu, route cycle và nested bastion đều fail closed trong V1.
6. Một bastion có thể tự kết nối trực tiếp hoặc qua HTTP/SOCKS proxy, nhưng không được tham chiếu tới một bastion khác.
7. Các channel Control, Command, SFTP, PTY và port-forward do user tạo đều sử dụng cùng route-aware SSH connection plan.
8. Việc tạo route không bao giờ retry một remote mutation có trạng thái mơ hồ. Remote state duy nhất ở phía route là loopback SSH forward tạm thời dùng cho bastion và nó được dispose cùng connection plan.

## Persistence

SQLite schema v5 bổ sung `server_connection_routes`. Direct routing được biểu diễn bằng việc không có row. Proxy route row chứa endpoint metadata, username tùy chọn và opaque credential reference. Bastion row chỉ chứa server profile id được tham chiếu.

## Test contract

CI giữ nguyên regression suite SSH/SFTP/PTY/forwarding hiện có và bổ sung:

- HTTP CONNECT proxy → thực thi command qua OpenSSH thật.
- SOCKS4 proxy → thực thi command qua OpenSSH thật.
- SOCKS5 proxy → thực thi command qua OpenSSH thật.
- single-hop bastion → thực thi command trên target với host-trust observation độc lập cho bastion và target.
- missing bastion → trả về typed fail-closed result.
