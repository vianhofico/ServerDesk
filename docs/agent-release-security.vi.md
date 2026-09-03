# Bảo mật ký release và vòng đời agent

[English](agent-release-security.md) | **Tiếng Việt**

Tài liệu này hoàn thiện thiết kế metadata release theo ADR 0004 trước khi ServerDesk được phép cài đặt hoặc cập nhật `serverdesk-agent`.

## Mô hình tin cậy phân phối

ServerDesk chỉ chấp nhận một release agent khi toàn bộ kiểm tra sau thành công theo đúng thứ tự:

1. Manifest dùng key id đã được pin trong gói phát hành của ứng dụng ServerDesk.
2. Chữ ký của manifest canonical xác thực thành công bằng ECDSA P-256 và SHA-256.
3. Metadata đã được xác thực thỏa policy về schema, product, protocol, nền tảng Linux, kiến trúc, tên file, kích thước, digest và timestamp.
4. Độ dài byte của artifact khớp tuyệt đối với manifest đã xác thực.
5. SHA-256 của artifact khớp tuyệt đối với manifest đã xác thực.
6. Chỉ sau đó ServerDesk mới được tạo lifecycle plan cài đặt hoặc cập nhật.

Mọi lỗi đều fail-closed. Không được phép chạy mutation cài đặt/cập nhật từ manifest hoặc artifact chưa xác thực.

## Quản lý signing key

Trong gói ứng dụng ServerDesk chỉ được chứa public key ECDSA P-256 ở dạng SubjectPublicKeyInfo. Trust store bất biến ánh xạ key id có giới hạn sang public key đã pin và từ chối key id không biết.

Private signing key tương ứng là bí mật của release engineering. Key phải được sinh và lưu bên ngoài repository này và bên ngoài runtime `serverdesk-agent`, ưu tiên HSM hoặc managed signing service. Tuyệt đối không commit, embed vào ServerDesk, sao chép lên server được quản lý hoặc ghi vào manifest agent.

Rotate key bằng cách phát hành một bản ServerDesk đã chứa public key id mới trước khi các release mới chỉ còn được ký bằng key đó. Việc loại bỏ key cũ là quyết định của bản phát hành client, không phải lệnh do agent cung cấp.

## Manifest canonical được ký

Chữ ký bao phủ các dòng UTF-8 xác định theo đúng thứ tự sau:

```text
schema=<integer>
product=serverdesk-agent
version=<major.minor.patch>
protocol-major=<integer>
protocol-minor=<integer>
platform=linux
architecture=<x64|arm64>
artifact-file=serverdesk-agent-linux-<architecture>
artifact-length=<bytes>
artifact-sha256=<64 ký tự hex chữ thường>
released-unix-seconds=<UTC unix seconds>
```

Giá trị chuỗi chứa CR, LF hoặc NUL bị từ chối trước khi verify chữ ký để một field không thể chèn thêm canonical line. Việc diễn giải semantic của version/platform/digest chỉ diễn ra sau khi chữ ký hợp lệ.

Baseline M8 chỉ chấp nhận version số canonical `major.minor.patch`. Pre-release/build metadata cố ý nằm ngoài contract này.

## Tính toàn vẹn artifact

Manifest đã xác thực ràng buộc cả byte length lẫn SHA-256. Chữ ký hợp lệ chưa đủ để artifact trở thành trusted: ServerDesk phải hash đúng byte artifact thực tế và so sánh cả hai giá trị trước khi lập kế hoạch install/update.

Giới hạn artifact baseline là 256 MiB. Đây là safety bound, không phải kích thước package mục tiêu.

## Biên sở hữu cố định

Lifecycle planning không nhận path, service unit hay command từ signed manifest. Các tài nguyên duy nhất thuộc quyền sở hữu của luồng cài agent ServerDesk là:

- `/opt/serverdesk-agent/serverdesk-agent`
- `/var/lib/serverdesk-agent`
- `/var/cache/serverdesk-agent`
- `/etc/systemd/system/serverdesk-agent.service`
- systemd unit `serverdesk-agent.service`

Clean uninstall chỉ được xóa các tài nguyên agent-owned này. Không được xóa hoặc sửa SSH configuration, firewall rules, Docker configuration/data, systemd unit không liên quan, application logs/data, file người dùng hoặc ServerDesk server profiles.

## Lifecycle planning

M8.8 tạo plan để review nhưng cố ý không thực hiện remote mutation.

Install plan stage artifact đã verify, verify lại remote staged digest/length, cài binary/unit cố định, reload systemd, enable/start service cố định rồi yêu cầu tunneled health/version verification.

Update plan yêu cầu target version đã xác thực phải mới hơn nghiêm ngặt so với version đang cài. Same-version replacement và downgrade bị từ chối. Slice execution có thể giữ một previous-binary rollback copy có giới hạn, nhưng rollback không bao giờ được activate artifact chưa xác thực.

Uninstall plan stop/disable fixed unit, chỉ xóa fixed owned resources, reload systemd rồi verify unit/resource đã biến mất.

Mỗi step mang `OperationRisk` rõ ràng cùng yêu cầu post-action verification. M8.9 phải giữ nguyên các gate này và coi mất kết nối/timeout trong mutation là ambiguous state, buộc re-observe trước khi retry.

## Yêu cầu tạo release

Pipeline phát hành `serverdesk-agent` sau này phải:

1. build Linux artifact cho đúng architecture khai báo;
2. tính byte length chính xác và SHA-256;
3. tạo canonical manifest từ các giá trị đó;
4. ký canonical bytes bằng external ECDSA P-256 release key;
5. publish artifact, manifest, key id và DER signature cùng nhau;
6. tuyệt đối không publish signing private key.

Executor install/update ở slice sau có thể download hoặc nhận các file này, nhưng bắt buộc phải gọi verifier trước mọi server mutation.
