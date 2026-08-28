# ADR 0003 — Local state và lưu trữ secret

[English](0003-local-state-and-secrets.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

Trạng thái: Accepted

## Bối cảnh

ServerDesk cần persistence local trước khi M1 có thể tạo SSH profile. Desktop app phải ghi nhớ metadata không nhạy cảm, đồng thời đảm bảo password, private-key passphrase, sudo credential và database credential trong tương lai không được lưu plaintext trong SQLite hoặc JSON file.

## Quyết định

- Lưu structured application metadata không chứa secret trong một SQLite database có version tại `%LOCALAPPDATA%\ServerDesk\data`.
- Lưu các UI preference nhẹ như theme System/Light/Dark tại `%LOCALAPPDATA%\ServerDesk\settings.json` bằng atomic replacement.
- Chỉ biểu diễn credential trong domain/application model thông qua opaque `SecretReference`.
- Lưu secret value thực tế bằng Windows Credential Manager thông qua `ISecretStore`.
- Giữ persistence implementation và Windows implementation phía sau application port để transport/platform implementation tương lai không rò rỉ vào domain hoặc WPF ViewModel.
- Ghi các operation summary an toàn vào SQLite, không bao giờ ghi raw command hoặc payload chứa secret.

## Hệ quả

- M1 có thể bổ sung SSH authentication mà không cần thay đổi persistence model của `ServerProfile`.
- Copy `serverdesk.db` hoặc `settings.json` không đồng nghĩa với copy credential value.
- Windows Credential Manager availability là một platform requirement của Windows client.
- Database migration phải rõ ràng và có version; database mới hơn client sẽ bị từ chối thay vì âm thầm downgrade.
- Secret reference chỉ là identifier, không phải encryption container và không bao giờ được coi như credential value.
