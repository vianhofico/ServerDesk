# Thực thi và phục hồi vòng đời agent

[English](agent-lifecycle-execution.md) | **Tiếng Việt**

Tài liệu này mô tả biên thực thi M8.9 cho việc cài đặt, cập nhật, kiểm tra trạng thái và clean uninstall `serverdesk-agent` tùy chọn. Nội dung này mở rộng các quy tắc signed-release trong `agent-release-security.vi.md`, không thay thế chúng.

## Điều kiện tiên quyết

Install và update chỉ nhận `VerifiedAgentArtifact` đã được tạo sau khi chữ ký manifest, byte length và SHA-256 của artifact đều được xác thực. Executor kiểm tra lại `AgentLifecyclePlan` trước mọi remote read hoặc mutation. Plan giả mạo service unit, path/resource set, cặp operation/version hoặc byte array của verified artifact đã bị sửa đều fail trước khi bắt đầu lifecycle work từ xa.

Tài khoản Linux dùng cho SFTP staging phải tuân theo cú pháp bảo thủ `[a-z_][a-z0-9_-]{0,31}`. Quy tắc này ngăn account value trở thành owner argument không an toàn. Lifecycle command không nhận executable, shell fragment, destination path, systemd unit hoặc service port do caller cung cấp.

## Remote layout cố định

Lifecycle executor chỉ sở hữu các tài nguyên persistent sau:

- `/opt/serverdesk-agent/serverdesk-agent`
- `/var/lib/serverdesk-agent`
- `/var/cache/serverdesk-agent`
- `/etc/systemd/system/serverdesk-agent.service`
- systemd unit `serverdesk-agent.service`

Agent port cố định là `41371`, nhưng listener của agent vẫn bị khóa cấu trúc ở loopback-only. ServerDesk truy cập nó qua ephemeral SSH local forward hiện có.

Staging bị giới hạn dưới `/var/cache/serverdesk-agent/staging/<plan-id>/`. Cache root và staging root là thư mục root-owned chỉ cho phép traverse (`0711`), còn đúng thư mục của từng operation được tạo `0700` cho SSH account đã validate để SFTP chỉ ghi file của operation đó. Systemd service cố ý không dùng `CacheDirectory=serverdesk-agent`; vì vậy `DynamicUser` của agent không thể thay đổi ownership của lifecycle staging. Runtime state của agent vẫn được cô lập bằng `StateDirectory=serverdesk-agent`.

Update giữ tối đa một rollback copy cố định ở `/var/cache/serverdesk-agent/serverdesk-agent.previous`. Không tạo rollback file theo timestamp hoặc tên do caller cung cấp.

## Biên privileged command có kiểu rõ ràng

Privileged mutation dùng `RemoteCommandSpec` dạng argv với executable `sudo`, argument đầu tiên `-n`, `OperationRisk` rõ ràng và `LC_ALL=C`. Không có `/bin/sh -c`, `bash -c`, generic command input, truyền sudo password tương tác hoặc command lấy từ manifest.

Trước activation, ServerDesk đọc lại staged file bằng các lệnh read-only cố định `stat -c %s -- <fixed-path>` và `sha256sum -- <fixed-path>`, đồng thời yêu cầu byte length và SHA-256 khớp tuyệt đối với giá trị đã xác thực. Binary đã cài và fixed unit cũng được đọc lại sau khi copy.

Service mutation đi qua `IServerServiceManager` hiện có; implementation systemd mặc định dùng typed command `sudo -n systemctl ... -- serverdesk-agent.service` và verify state sau mutation.

## Install

1. Xác nhận fixed unit chưa tồn tại.
2. Xác nhận remote architecture khớp architecture của authenticated artifact.
3. Tạo fixed staging directories và upload binary đã verify cùng unit file do ServerDesk sở hữu qua SFTP.
4. Verify lại length và SHA-256 của staged binary/unit từ xa.
5. Chỉ install vào fixed binary path và fixed unit path.
6. Reload systemd, enable và start `serverdesk-agent.service`.
7. Mở SSH-controlled local tunnel và yêu cầu negotiation compatible, health tốt và đúng authenticated target version.
8. Chỉ xóa per-operation staging directory sau khi verification thành công và trạng thái đã biết chắc.

Healthy nhưng version không khớp vẫn là failure. Trạng thái unreachable/ambiguous sau mutation không được coi là success.

## Update và rollback

Update yêu cầu đúng healthy version đã dùng khi lập plan và authenticated target phải mới hơn nghiêm ngặt. Same-version replacement và downgrade bị từ chối.

Trước mutation, ServerDesk ghi nhận length/SHA-256 của current binary. Sau khi stage target, executor copy current fixed binary sang đúng một fixed rollback path và verify rollback copy khớp tuyệt đối với byte integrity trước update. Sau đó mới activate verified target và restart fixed service.

Rollback chỉ được thử sau deterministic known post-swap failure. Trước khi restart service đã restore, ServerDesk verify restored binary khớp tuyệt đối với captured previous integrity. Rollback thành công còn phải khôi phục đúng previous healthy version qua SSH tunnel.

Nếu restart, transport, command completion, cancellation hoặc health verification trở nên không chắc chắn sau mutation, ServerDesk trả về `Ambiguous`, giữ rollback copy và **không** blind retry hoặc tự rollback. Operator phải refresh status trước khi quyết định bước tiếp theo.

## Status

Status kết hợp fixed systemd state với tunneled agent negotiation và health. Các trạng thái được phân biệt rõ:

- `Absent`: fixed unit chưa được cài;
- `Healthy`: active, enabled, compatible và healthy qua tunnel;
- `Degraded`: unit hoặc agent có thể truy cập nhưng không healthy/đầy đủ;
- `Incompatible`: protocol major version không tương thích;
- `Unreachable`: service đã biết nhưng tunnel/agent không thể truy cập đáng tin cậy;
- `Ambiguous`: ServerDesk không thể chứng minh fixed service state.

Runtime version chấp nhận canonical `major.minor.patch`, hoặc dạng bốn phần của .NET chỉ khi thành phần thứ tư chính xác bằng zero. Health version và negotiated runtime version phải khớp nhau.

## Clean uninstall

Uninstall chỉ stop/disable `serverdesk-agent.service`, chỉ xóa fixed unit, binary, bounded rollback copy, state directory và cache directory, reload systemd rồi verify từng fixed path và unit đã biến mất.

Không được xóa hoặc sửa SSH configuration/key, firewall rule, Docker configuration/data, systemd unit không liên quan, user file, application logs/data hoặc ServerDesk profile.

## Quy tắc operator khi gặp ambiguous state

Kết quả ambiguous là safety gate, không phải gợi ý retry. Phải refresh lifecycle status trước. Không retry install/update/uninstall dựa trên giả định cũ. Nếu rollback copy còn tồn tại sau ambiguous update, giữ nguyên cho tới khi installed binary/service state được re-observe.

## Yêu cầu đối với operator/server

- Linux có systemd và các utility cố định executor sử dụng (`uname`, `stat`, `sha256sum`, `install`, `rm`, `test`, `systemctl`, `sudo`).
- Luồng SSH host-trust/authentication hiện có của ServerDesk phải hoạt động trước.
- SSH account phải được sudoers cho phép thực hiện đúng các lifecycle mutation cố định cần thiết theo non-interactive mode; ServerDesk không bao giờ forward sudo password.
- Không cần firewall rule hay public agent port. Baseline listener vẫn loopback-only và được truy cập qua SSH forwarding.
