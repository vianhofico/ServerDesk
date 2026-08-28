# Kiến trúc và Migration Localization

[English](LOCALIZATION.md) | **Tiếng Việt**

Bản English là technical source of truth nếu hai tài liệu vô tình lệch nhau.

## 1. Phạm vi

Localization V1 của ServerDesk chạy local và deterministic. Các lựa chọn ngôn ngữ được hỗ trợ:

- `System`
- `English` (`en`)
- `Vietnamese` (`vi`)

`System` ánh xạ Windows UI culture tiếng Việt (`vi`, `vi-*`) sang Tiếng Việt. English và mọi system culture chưa hỗ trợ đều fallback về English.

V1 không có cloud translation service, translation database, CMS hoặc auto-translation runtime.

## 2. Dependency boundaries

Localization tuân theo dependency direction hiện tại:

```text
Domain
  không chứa presentation localization

Application.Settings
  enum language preference
  rule resolve language
  contract persistence settings

Platform.Windows
  detector Windows UI culture
  JSON persistence adapter

ServerDesk.App
  WPF localization service
  ResourceDictionary English/Vietnamese
  runtime resource switching
  presentation strings đã localization
```

Domain và infrastructure tiếp tục trả typed error. Presentation ánh xạ state/message người dùng sang localized resources thay vì đưa chuỗi English/Vietnamese vào Domain.

## 3. Resource policy

English là fallback resource set. Khi chạy, ServerDesk luôn load English resources và khi Vietnamese active thì load Vietnamese resources làm override.

Resource key phải tồn tại ở cả hai ngôn ngữ. Lookup bị thiếu fallback an toàn về chính resource key thay vì làm UI crash.

Message có parameter dùng format string hoàn chỉnh, ví dụ:

```text
Unable to connect to {0}.
Không thể kết nối tới {0}.
```

Không ghép từng mảnh câu đã dịch nếu có thể dùng một format resource hoàn chỉnh.

Các technical identifier như `SSH`, `SFTP`, `Docker`, `systemctl`, path, executable name, protocol name, API/type identifier, raw terminal output và raw server log không được dịch nếu việc dịch làm thay đổi nghĩa kỹ thuật.

## 4. Persist language preference

Language preference được lưu bằng giá trị cấu hình ổn định:

- `system`
- `en`
- `vi`

Không bao giờ lưu display string đã localization như `Tiếng Việt` làm giá trị cấu hình.

Settings file cũ có trước localization và chỉ chứa theme preference vẫn hợp lệ; thiếu language sẽ resolve về `System`.

## 5. Runtime switching

WPF localization service đổi effective culture và thay localization ResourceDictionary ngay lúc chạy. Đổi ngôn ngữ không tạo lại remote session, không reconnect SSH và không làm mất server workspace hiện tại.

UI dùng `DynamicResource` cập nhật ngay. Presentation model có localized display choices refresh các lựa chọn đó khi nhận `LanguageChanged`.

## 6. Migration policy

Localization được migrate dần, không làm một giant translation rewrite.

1. Phase 1: foundation, resources, preference, fallback, startup resolution, selector, tests.
2. Phase 2: shell/navigation/common dialogs và shared states.
3. Phase 3: khi feature được chạm tới, migrate user-facing text của feature trong chính thay đổi đó.
4. Phase 4: scan và loại bỏ hard-coded user-facing string còn lại sau khi các milestone lớn ổn định.

Sau khi Phase 1 được merge, mọi user-facing UI text mới phải đi qua localization resources. Text hard-code cũ có thể còn lại cho đến migration slice của nó, nhưng code mới không được tiếp tục tăng technical debt này nếu không có lý do kỹ thuật được document.

## 7. Yêu cầu UI

Text tiếng Việt thường dài hơn English. UI localization mới nên ưu tiên layout co giãn, wrapping khi phù hợp, dialog resize được và control không cắt translated label ở Windows text scaling thông thường.

Không dùng fixed width chỉ đủ cho English nếu có thể dùng layout linh hoạt.

## 8. Testing gate

Thay đổi localization cần các test phù hợp với slice được chạm tới, bao gồm:

- resolve English và Vietnamese explicit;
- resolve `System` culture;
- unsupported culture fallback về English;
- persistence/backward compatibility của language preference;
- key parity giữa English/Vietnamese resources;
- parameterized resource formatting;
- safe fallback khi thiếu resource;
- runtime switching khi có presentation state liên quan;
- review layout với label tiếng Việt dài hơn đáng kể.

Các gate build, format, unit test và integration bình thường của ServerDesk vẫn bắt buộc. Localization không được dùng để bypass hoặc làm yếu prerequisite của milestone.
