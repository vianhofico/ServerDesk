# Contract thiết kế UI / UX

[English](UI_UX.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

## 1. Mục tiêu UX

ServerDesk phải tạo cảm giác như một workspace quản trị Windows native, hiện đại thay vì một web dashboard nhúng trong desktop shell. User phải có thể nhận ra và khám phá các thao tác Linux phổ biến bằng giao diện quen thuộc, trong khi advanced/raw view vẫn sẵn sàng khi cần.

## 2. Ngôn ngữ thiết kế

- Phân cấp giao diện lấy cảm hứng từ Windows 11 / Fluent.
- Hỗ trợ theme `System`, `Light`, `Dark`.
- Surface trung tính; accent color chỉ dành cho selection, focus, primary action, progress và nhấn mạnh status.
- Tránh gradient/glow trang trí làm giảm information density.
- Dùng spacing rhythm nhất quán theo đơn vị 8px.
- Ưu tiên Segoe UI/system typography.
- Icon phải nhất quán và dễ nhận biết; tránh trộn nhiều style icon.
- Table, tree, split pane, breadcrumb, tab, command bar và context menu là pattern chính.

## 3. Application shell

Desktop layout:

```text
+--------------------------------------------------------------+
| ServerDesk | server/environment | connection | search/actions|
+----------------+---------------------------------------------+
| Servers        | Breadcrumb / page command bar              |
| Production     +---------------------------------------------+
| Staging        |                                             |
|                | Current feature                             |
| Navigation     |                                             |
| Dashboard      |                                             |
| Explorer       |                                             |
| Terminal       |                                             |
| Processes      |                                             |
| Services       |                                             |
| Docker         |                                             |
| ...            |                                             |
+----------------+---------------------------------------------+
| optional status/activity strip                               |
+--------------------------------------------------------------+
```

Identity của server hiện tại và trạng thái connection phải luôn hiển thị khi đang thao tác trên server.

Environment identity có thể dùng label tiết chế như `PROD`, `STAGING`, `DEV`; destructive action trên production có thể áp dụng confirmation policy mạnh hơn.

## 4. Navigation

Thứ tự primary navigation:

1. Dashboard
2. Explorer
3. Terminal
4. Processes
5. Services
6. Docker
7. Storage
8. Network
9. Logs
10. Scheduled Tasks
11. Git
12. Nginx
13. Security
14. Databases
15. Backups
16. Server Settings

Navigation phải capability-aware. Với feature không khả dụng:

- ẩn nếu nó không liên quan và giá trị discovery thấp; hoặc
- disable và giải thích lý do khi discovery/installation guidance hữu ích.

Không bao giờ navigate tới một blank page rồi đơn giản để command fail.

## 5. State model cho mọi screen

Mọi async feature phải chủ động thiết kế:

- initial/loading;
- loaded;
- empty;
- partial/degraded;
- permission required;
- capability unavailable;
- disconnected;
- reconnecting;
- recoverable error;
- fatal/unsupported error.

Dùng skeleton/progress state cho loading thông thường. Không freeze window hoặc hiển thị spinner vô hạn không có cancellation cho operation dài.

## 6. Feedback

### Toast

Dùng cho kết quả ngắn, non-blocking:

- copied path;
- upload finished;
- service restarted;
- tunnel started.

### Inline status

Dùng cho error/warning gắn với panel/resource cụ thể.

### Modal dialog

Chỉ dùng khi:

- credential/trust cần focused input;
- destructive action cần explicit confirmation;
- user phải giải quyết ambiguity trước khi tiếp tục.

Thông tin bình thường không nên bắt user dismiss modal.

## 7. Thiết kế action theo risk

### Read-only

Thực hiện ngay, không confirmation.

### Mutating nhưng thường quy

Ví dụ: restart service, start container.

- action hiển thị rõ;
- chỉ confirmation khi impact đáng kể;
- hiển thị progress và final state;
- cung cấp technical detail khi failure.

### Destructive

Ví dụ: xóa Docker volume, recursive delete protected directory, destructive restore.

Yêu cầu có thể gồm:

- warning callout;
- target identity;
- consequence summary;
- typed target-name confirmation cho case irreversible/high-impact;
- destructive button khác biệt rõ về visual;
- destructive button không nhận default keyboard focus.

## 8. Explorer UX

Dùng mental model của Windows Explorer:

- tree/quick location ở pane trái tùy chọn;
- breadcrumb path;
- address entry;
- file table/grid;
- multi-select;
- right-click context menu;
- drag/drop upload;
- keyboard shortcut;
- transfer/activity panel hiển thị rõ cho operation dài.

Shortcut quan trọng:

- `Ctrl+C`, `Ctrl+X`, `Ctrl+V` khi semantic an toàn;
- `F2` rename;
- `Delete` với confirmation theo policy;
- `Ctrl+L` focus remote path;
- `Alt+Left/Right` history;
- `Alt+Up` parent.

Không giả lập behavior local-Windows không được remote Linux filesystem hỗ trợ; phải hiển thị symlink, ownership và permission rõ ràng.

## 9. Terminal UX

Terminal thiên về keyboard và không nên nhận app-wide shortcut khi terminal đang focus, trừ khi được thiết kế rõ ràng.

Visual feature bắt buộc:

- tabs;
- control new/close/reconnect session;
- server/path title;
- search scrollback;
- disconnected indicator rõ ràng;
- copy/paste tương thích expectation của terminal;
- configurable font size/family từ danh sách monospace đã phê duyệt.

## 10. Tables

Processes, Services, Containers, Ports và Logs dùng table pattern nhất quán:

- column sorting;
- filtering/search;
- column resizing;
- row selection;
- keyboard navigation;
- context menu;
- details pane thay vì mở window không cần thiết;
- virtualization cho dataset lớn.

Không bao giờ chỉ dựa vào màu để biểu diễn state; phải dùng icon/text kết hợp màu.

## 11. Dashboard

Dashboard phải trả lời nhanh:

- Server có khỏe không?
- Storage có gần đầy không?
- CPU/memory có chịu áp lực không?
- Service/container quan trọng có đang fail không?
- Connection có degraded không?

Tránh dashboard gồm hàng chục card có trọng số ngang nhau. Ưu tiên performance summary rõ ràng, khu vực warning, service/container summary và recent activity.

## 12. Forms

- Mọi input đều có label.
- Giải thích field nâng cao như jump host/proxy bằng help text ngắn.
- Validate inline trước submit.
- Không bao giờ xóa form user đã nhập chỉ vì connection validation fail.
- Password field mặc định không reveal; show/reveal phải explicit.
- Secret value không tự động copy vào clipboard.

## 13. Errors

Pattern user-facing:

```text
Unable to reload nginx
Configuration validation failed, so the live configuration was not replaced.

[View validation output] [Open config]
```

Không phải:

```text
System.Exception: command returned 1
```

Technical detail vẫn truy cập được trong vùng expandable và có thể gồm safe command/exit information, nhưng phải redact argument/environment chứa secret.

## 14. Accessibility

- Primary workflow truy cập được bằng keyboard.
- Focus indication phải rõ.
- Contrast đủ trong mọi theme.
- Actionable control có label thân thiện với screen reader.
- Không biểu diễn Running/Stopped/Error chỉ bằng xanh/đỏ.
- Touch không phải target chính, nhưng control không nên nhỏ quá mức cần thiết.
- Tôn trọng Windows text scaling khi thực tế cho phép.

## 15. Window behavior

- Ghi nhớ safe layout preference như window size, splitter position và selected theme.
- Không ghi nhớ secret trong UI state.
- Restore tab thận trọng; không tự động reconnect production rồi execute command khi launch.
- Nhiều server workspace về sau có thể dùng tabs/documents, nhưng resource ownership phải luôn rõ.

## 16. Search và command palette

Command palette dài hạn (`Ctrl+P` hoặc shortcut được chọn có chủ đích) có thể expose:

- switch server;
- navigate feature;
- open remote path;
- run saved safe command;
- open terminal;
- search service/container.

Destructive operation không được chỉ cách một lần Enter vô tình từ search result của command palette.

## 17. Empty/capability state

Tốt:

```text
Docker isn't available on this server.
ServerDesk could not find a supported Docker CLI/daemon for the current user.
[View detection details]
```

Nếu có installation guidance, nó phải aware distro/version và không auto-install nếu chưa có explicit consent.

## 18. Design review gate

Một user-facing feature chưa hoàn thành cho tới khi được review về:

- visual hierarchy;
- loading/error/empty state;
- keyboard behavior;
- dark/light theme;
- risk treatment;
- responsiveness khi resize window;
- long name/path/value;
- accessibility label;
- visibility của production-server identity.
