# ADR 0002 — Ưu tiên SSH/SFTP không cần agent

[English](0002-agentless-first.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

- Trạng thái: Accepted
- Ngày: 2026-08-27

## Bối cảnh

Sản phẩm cần hoạt động với các Linux server thông thường với setup tối thiểu và không đưa thêm một management service có thể truy cập công khai. Các thao tác quản trị phổ biến có thể được triển khai thông qua SSH command, SFTP, PTY session và SSH forwarding.

Một custom agent về sau có thể cải thiện hiệu năng realtime và khả năng chuẩn hóa dữ liệu, nhưng bắt buộc cài agent ngay từ đầu sẽ làm tăng độ phức tạp về cài đặt, bảo mật, cập nhật, compatibility và trust trước khi các core workflow được chứng minh.

## Quyết định

ServerDesk V1 bắt đầu theo mô hình agentless:

```text
Windows ServerDesk -> SSH / SFTP / PTY / forwarding -> Linux server
```

Kiến trúc ứng dụng sử dụng ports/interfaces để `serverdesk-agent` trong tương lai có thể implement một số data/streaming service mà không cần thay đổi feature UI/use case.

Agent trong tương lai phải bind vào loopback theo mặc định và nên được truy cập qua SSH tunnel, trừ khi một secure network mode riêng được thiết kế và review.

## Hệ quả

Tích cực:

- không cần cài thêm phần mềm trên server cho giai đoạn đầu;
- tái sử dụng cơ chế kiểm soát truy cập SSH đã tồn tại;
- không mở thêm public management port;
- dễ áp dụng cho VPS/server đang có;
- SSH vẫn là escape hatch cho các thao tác chưa được hỗ trợ.

Trade-off:

- có overhead khi khởi động remote CLI và parse output;
- metrics/events realtime cần polling hoặc command stream chạy lâu;
- compatibility test phải bao phủ output theo distro/tool;
- một số operation khó chuẩn hóa an toàn hơn khi không có agent.

## Xem xét lại khi

Chỉ đưa optional agent mode vào sau khi interface của M1–M7 đủ ổn định để agent có thể implement các abstraction hiện có thay vì trở thành một kiến trúc sản phẩm thứ hai.
