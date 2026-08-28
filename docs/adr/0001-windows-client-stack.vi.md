# ADR 0001 — Stack Windows client: .NET 10 + WPF

[English](0001-windows-client-stack.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

- Trạng thái: Accepted
- Ngày: 2026-08-27

## Bối cảnh

ServerDesk chủ đích là sản phẩm desktop ưu tiên Windows. Ứng dụng cần tích hợp native với windowing/input/accessibility của Windows, có khả năng networking mạnh từ .NET, vận hành ổn định dưới dạng desktop process chạy lâu dài và có khả năng host WebView2 cho các bề mặt terminal/editor.

## Quyết định

Sử dụng:

- .NET 10;
- WPF cho desktop shell;
- phân tách theo định hướng MVVM;
- chỉ dùng WebView2 cho các component hưởng lợi từ web rendering như xterm.js/advanced editor;
- dùng native WPF control/layout cho UI chính của ứng dụng.

## Hệ quả

Tích cực:

- tích hợp tốt với Windows desktop;
- truy cập trực tiếp các API security/platform của Windows;
- hệ sinh thái data binding/control trưởng thành;
- phân phối đơn giản dưới dạng Windows desktop application;
- WebView2 được cô lập vào terminal/editor thay vì biến toàn bộ sản phẩm thành web wrapper.

Trade-off:

- client không cross-platform mặc định;
- code UI đặc thù WPF phải nằm trong `ServerDesk.App`/UI module;
- CI cho desktop shell sử dụng Windows runner;
- client macOS/Linux trong tương lai sẽ cần một quyết định UI-platform có chủ đích thay vì tái sử dụng WPF.

## Xem xét lại khi

Chỉ xem xét lại nếu ServerDesk chuyển rõ ràng từ Windows-first sang cross-platform desktop product, hoặc nếu một Windows UI platform trong tương lai cải thiện sản phẩm đủ đáng kể để biện minh cho migration có kiểm soát.
