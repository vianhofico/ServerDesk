# Cài ServerDesk trên Windows

[English](INSTALL.md) | **Tiếng Việt**

ServerDesk cung cấp hai dạng package Windows x64.

## Khuyến nghị — Windows installer

Tải `ServerDesk-v1.0.3-win-x64-setup.exe` từ GitHub Release và chạy một lần.

Installer cài theo user hiện tại và không yêu cầu quyền Administrator với đường dẫn cài đặt mặc định. Sau khi cài, installer sẽ:

- cài ServerDesk vào `%LOCALAPPDATA%\Programs\ServerDesk`;
- tạo shortcut **ServerDesk** ngoài Desktop của user hiện tại;
- tạo shortcut **ServerDesk** trong Start Menu;
- dùng đúng icon/avatar chính thức của ServerDesk cho app, setup và shortcut;
- đăng ký ServerDesk trong **Settings → Apps → Installed apps** của Windows để có thể uninstall bình thường.

Sau khi cài xong, chỉ cần mở ServerDesk từ Desktop hoặc Start Menu như các app Windows khác. Không cần mỗi lần đều mở thư mục rồi tìm `ServerDesk.App.exe` nữa.

Để gỡ cài đặt, vào **Settings → Apps → Installed apps → ServerDesk → Uninstall**, hoặc chạy uninstaller trong thư mục ServerDesk đã cài.

> Build public hiện tại chưa được Authenticode/code-sign nên Windows SmartScreen có thể hiện cảnh báo unknown publisher. Khi môi trường yêu cầu, hãy kiểm tra checksum của release trước khi chạy installer.

## ZIP portable

`ServerDesk-v1.0.3-win-x64.zip` vẫn được giữ cho trường hợp chủ động muốn dùng bản portable.

1. Giải nén ZIP vào một thư mục.
2. Chạy `ServerDesk.App.exe` trong thư mục đó.

Bản portable **không** tự cài app, không tự tạo shortcut Desktop/Start Menu và không đăng ký uninstaller.

## Kiểm tra SHA-256

Tải `SHA256SUMS.txt` trong cùng GitHub Release. Chạy PowerShell:

```powershell
Get-FileHash .\ServerDesk-v1.0.3-win-x64-setup.exe -Algorithm SHA256
Get-FileHash .\ServerDesk-v1.0.3-win-x64.zip -Algorithm SHA256
```

Đối chiếu kết quả với đúng hai dòng tương ứng trong `SHA256SUMS.txt`.
