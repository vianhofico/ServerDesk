# Yêu cầu bảo mật

[English](https://github.com/vianhofico/ServerDesk/blob/main/docs/SECURITY.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

## 1. Mục tiêu bảo mật

ServerDesk thường xuyên thao tác với production server. Lỗi bảo mật có thể dẫn tới lộ credential, remote code execution, mất dữ liệu, khóa quyền truy cập server hoặc kết nối âm thầm tới host do attacker kiểm soát.

Vì vậy bảo mật là yêu cầu sản phẩm ngay từ đầu, không phải giai đoạn hardening về sau.

Mục tiêu chính:

- xác thực đúng server dự kiến;
- bảo vệ credential và private key;
- giảm thiểu privilege;
- ngăn command injection;
- làm rõ ý định destructive;
- tránh public network exposure không cần thiết;
- tránh rò rỉ remote data nhạy cảm xuống local;
- giữ audit trail hữu ích nhưng không chứa secret;
- fail closed khi trust/parse/state mơ hồ.

## 2. Threat model

Các threat liên quan gồm:

- SSH man-in-the-middle attack;
- local credential database bị đánh cắp;
- remote server độc hại/bị compromise trả về dữ liệu nguy hiểm;
- command injection thông qua path/service/container/user input;
- click destructive ngoài ý muốn;
- retry operation khi không biết nó đã hoàn tất hay chưa;
- lạm dụng sudo phạm vi rộng;
- log secret/environment value;
- filename/ANSI output độc hại ảnh hưởng UI/terminal;
- temporary file không an toàn;
- Docker/database/agent management port bị expose;
- supply-chain compromise của dependency/update;
- privilege escalation do chạy desktop app bằng Administrator.

## 3. Xác minh SSH host

Unknown host:

- hiển thị host, port, algorithm, fingerprint;
- yêu cầu quyết định trust rõ ràng;
- cho phép trust-once và trust-and-save khi UX sản phẩm hỗ trợ cả hai;
- lưu fingerprint đã trust trong known-host implementation.

Changed host key:

- mặc định chặn kết nối;
- hiển thị fingerprint cũ và mới;
- không bao giờ tự động overwrite known-host data;
- yêu cầu workflow xử lý có chủ đích.

Không được phép có hành vi kiểu `AcceptAllHostKeys` trong production code.

## 4. Lưu trữ credential

Không lưu trực tiếp các dữ liệu sau trong SQLite hoặc plaintext configuration:

- password;
- sudo password;
- private-key content;
- key passphrase;
- database password;
- API token/certificate private key được bổ sung về sau.

Sử dụng Windows secure-secret implementation phía sau `ISecretStore`, dựa trên cơ chế Windows credential/DPAPI được phê duyệt.

SQLite chỉ lưu opaque reference và non-secret metadata.

Có thể lưu private key file path như metadata, nhưng không copy nội dung key file vào application data thông thường trừ khi sau này có encrypted key vault được thiết kế riêng.

## 5. Xử lý secret trong memory và log

- Giảm thời gian sống của sensitive string/array khi khả thi.
- Không bao giờ ghi secret vào structured log, exception message, operation history, analytics hoặc test snapshot.
- Remote command logging phải redact argument/environment chứa secret.
- Database/Container environment viewer phải coi các value có dấu hiệu là secret là nhạy cảm và yêu cầu thao tác reveal có chủ đích.
- Copy secret vào clipboard phải là hành động rõ ràng, không tự động.

## 6. Privilege của local application

ServerDesk nên chạy dưới normal Windows user.

Không yêu cầu Windows Administrator toàn cục chỉ vì remote operation cần Linux sudo.

Nếu một local operation cụ thể trong tương lai cần Windows elevation, phải có lý do riêng và scope rõ ràng.

## 7. Remote privilege / sudo

Operation phải khai báo privilege/risk requirement trước khi execute.

Quy tắc:

- ưu tiên normal user permission;
- chỉ dùng sudo cho command/file thực sự cần;
- không dùng persistent root shell như implementation strategy thông thường;
- không cache sudo password trong SQLite/log;
- không xử lý write failure bằng thay đổi permission toàn cục;
- giữ nguyên owner/group/mode gốc khi privileged replacement;
- cho user biết khi action cần elevated rights.

## 8. Phòng chống command injection

Mọi variable remote input đều được coi là untrusted, kể cả dữ liệu ban đầu do server trả về.

Các value có thể nguy hiểm:

- path;
- filename;
- service name;
- username;
- container/image/volume/network ID và name;
- Git branch;
- nginx domain/path;
- firewall address/port;
- database name;
- command parameter do user nhập.

Yêu cầu:

- dùng typed command specification;
- validate ID/name theo grammar mà target CLI yêu cầu khi có thể;
- centralize safe argument encoding;
- tránh `sh -c` trừ khi operation đã review thật sự cần shell semantic;
- không interpolate path/name vào compound shell command nếu chưa quote/validate nghiêm ngặt;
- parser output chỉ là data, không bao giờ là executable script text.

## 9. Temporary file

Remote temp file:

- dùng tên khó đoán;
- dùng location có ownership/permission phù hợp;
- đặt permission hạn chế cho sensitive config candidate;
- cleanup khi thành công và best-effort khi thất bại;
- không đặt credential plaintext trong temp file có thể đọc rộng rãi.

Nên tránh local temp file chứa remote content nhạy cảm; ưu tiên memory. Nếu bắt buộc, dùng restricted application storage và cleanup đáng tin cậy.

## 10. Destructive operation

Destructive operation bao gồm nhưng không giới hạn:

- recursive delete;
- xóa Docker volume;
- database restore overwrite/drop;
- xóa user;
- firewall change có nguy cơ làm mất SSH access;
- remove package thành phần quan trọng;
- overwrite protected config mà không có safe backup path.

Yêu cầu:

- hiển thị target rõ ràng;
- cảnh báo hậu quả;
- confirmation mạnh hơn cho resource có impact cao;
- typed-name confirmation khi phù hợp;
- không bao giờ automatic retry khi network failure tạo trạng thái mơ hồ;
- audit kết quả.

## 11. Network exposure

Agentless design sử dụng SSH exposure hiện có.

Không yêu cầu:

- public Docker socket;
- public PostgreSQL/MySQL/Redis port;
- public ServerDesk agent port.

Dùng SSH local forwarding cho database/internal admin service.

`serverdesk-agent` trong tương lai bind loopback theo mặc định và được truy cập qua SSH tunneling, trừ khi có một authenticated network design riêng được review.

## 12. Bảo vệ khỏi firewall lockout

Khi chỉnh firewall rule:

- xác định SSH path/port hiện tại khi có thể;
- cảnh báo nếu candidate change có thể loại bỏ quyền truy cập hiện tại;
- validate syntax trước apply khi tooling cho phép;
- tránh apply broad default-deny transition nếu user chưa nhận thức rõ;
- không hứa chắc chắn ngăn lockout khi topology mạng không xác định.

## 13. An toàn khi thay đổi configuration

Pattern cho critical config change:

```text
read current
-> create candidate
-> backup where appropriate
-> validate candidate
-> atomic apply
-> reload/restart
-> verify health/state
-> rollback if safe and deterministic
-> audit
```

Ví dụ gồm nginx và system configuration file.

Nếu validation fail, live config phải giữ nguyên.

Nếu connection drop sau apply và final state không rõ, surface `AmbiguousState`/equivalent thay vì tự động lặp lại mutation.

## 14. Terminal safety

Terminal chủ đích mạnh và thực thi user command; nó không bị giới hạn như GUI operation.

Tuy vậy:

- chỉ sanitize terminal output ở mức cần thiết để ngăn thoát khỏi boundary WebView/script của host;
- xterm data nằm trong terminal renderer, không được interpret như HTML;
- không inject terminal text vào WebView DOM theo cách không an toàn;
- WebView2 messaging chỉ nhận message schema đã validate;
- tránh enable permission/navigation không cần thiết trong WebView.

## 15. Editor safety

Remote file content là untrusted.

- syntax highlighter/editor không được execute file content;
- preview HTML/Markdown render, nếu bổ sung, phải sandbox/sanitize;
- external link cần policy rõ ràng;
- diff/validator output hiển thị dạng text, không interpret như markup.

## 16. Update và dependency security

Khi automatic update được bổ sung:

- package/installer phải được sign;
- update metadata phải được authenticate;
- cần rollback/recovery plan;
- không execute unsigned binary đã download.

Dependency:

- pin/manage version;
- theo dõi security advisory;
- giữ dependency footprint nhỏ;
- review license trước distribution.

## 17. Telemetry và privacy

V1 phải hoạt động không cần cloud account.

Nếu telemetry được bổ sung về sau:

- tài liệu hóa chính xác dữ liệu được thu thập;
- không bao giờ thu terminal content, credential, file content hoặc remote environment secret;
- cung cấp consent/control phù hợp;
- local-only mode vẫn phải hoạt động.

## 18. Security testing gate

Feature nhạy cảm về bảo mật cần negative test cho các trường hợp liên quan:

- malicious path/name quoting;
- host-key mismatch;
- secret redaction;
- sudo denied;
- permission denied;
- command output bất ngờ/malformed;
- connection drop sau khi request đã gửi;
- hủy dangerous confirmation;
- WebView message validation;
- invalid firewall/nginx candidate.

Security failure không được chuyển thành silent fallback làm yếu protection.
