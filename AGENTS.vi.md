# AGENTS.md — Contract cho Agent của ServerDesk

[English](AGENTS.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

File này là bắt buộc đối với mọi coding agent làm việc trong repository. Đây là execution contract, không phải optional guidance.

## 1. Mission

Xây dựng ServerDesk thành Windows desktop application production-grade giúp việc quản trị Linux server trở nên trực quan và dễ tiếp cận mà không che giấu security, permission hoặc failure state.

Ứng dụng không được trở thành một tập hợp button chạy shell string tùy ý. Kiến trúc phải luôn typed, capability-aware, testable, có thể thích nghi theo distro và an toàn cho production server.

## 2. Thứ tự source of truth

Khi instruction xung đột, dùng thứ tự sau:

1. Yêu cầu task/issue rõ ràng
2. `AGENTS.md`
3. `docs/ARCHITECTURE.md`
4. `docs/SECURITY.md`
5. `docs/UI_UX.md`
6. `docs/ROADMAP.md`
7. `docs/PRODUCT_PLAN.md`
8. Convention implementation hiện có

Nếu task yêu cầu vi phạm các mục 2–5, dừng implementation và ghi rõ conflict trong PR thay vì âm thầm bypass rule.

## 3. Workflow bắt buộc cho agent

Mọi implementation task phải theo các bước này đúng thứ tự.

### Step 0 — Đồng bộ context

Trước khi code:

- Đọc file này.
- Đọc đầy đủ issue/task.
- Đọc architecture/security/UI/testing doc liên quan.
- Inspect code hiện có trong module bị ảnh hưởng.
- Inspect open PR đang chạm cùng khu vực khi có.
- Không giả định feature bị thiếu cho tới khi repository search xác nhận.

### Step 1 — Xác định boundary của thay đổi

Trước khi edit, agent phải tự xác định:

- user-visible outcome;
- project/module bị ảnh hưởng;
- capability requirement;
- privilege level: `ReadOnly`, `ElevatedRead`, `Mutating` hoặc `Destructive`;
- tác động tới supported distro;
- error/failure state dự kiến;
- test bắt buộc.

Tránh cleanup không liên quan. Một task/PR nên có một mục tiêu nhất quán.

### Step 2 — Validate vị trí kiến trúc

Dependency phải hướng vào trong:

```text
App/UI -> Application -> Domain
Infrastructure -> Application/Domain
Linux adapters -> Application/Domain
Feature modules -> Application abstractions, never raw SSH implementation
```

Quy tắc:

- UI không bao giờ tự assemble shell command.
- UI không bao giờ parse remote command output.
- Domain không được reference WPF, SSH.NET, SQLite, WebView2, Docker SDK hoặc distro-specific library.
- Application định nghĩa port/abstraction và use case.
- Infrastructure implement transport/persistence/platform concern.
- Distro-specific command thuộc Linux adapter.

### Step 3 — Ưu tiên structured remote data

Dùng theo thứ tự ưu tiên:

1. stable kernel/proc/sys file;
2. explicit JSON output;
3. key/value hoặc property output;
4. fixed machine-oriented format với explicit locale;
5. parse human text chỉ như last resort đã được tài liệu hóa.

Không parse colored/table-formatted CLI output dành cho người đọc khi có structured alternative.

Khi text parsing là bắt buộc:

- force `LC_ALL=C` khi phù hợp;
- isolate parser;
- thêm fixture từ mọi certified distro bị ảnh hưởng;
- fail closed bằng `ParseFailed` thay vì đoán.

### Step 4 — Xây command an toàn

Không concatenate untrusted input vào shell snippet.

Mọi remote execution phải đi qua command execution abstraction và typed command specification chứa ít nhất:

- executable;
- argument list;
- environment/locale khi cần;
- timeout;
- cancellation;
- privilege requirement;
- output parser.

Không viết code tương đương:

```csharp
RunCommand($"rm {path}");
```

Path, name, container ID, service name, username và user input phải đi qua cơ chế validate/escape argument phù hợp với remote command model.

### Step 5 — Áp dụng safety workflow cho mutation

Với configuration change và destructive change, dùng sequence mạnh nhất có thể áp dụng:

```text
precondition -> preview -> confirmation -> backup/snapshot -> validate -> execute -> verify -> rollback on safe failure -> audit
```

Ví dụ:

- nginx change: backup -> edit temp -> `nginx -t` -> atomic replace/reload -> verify;
- privileged file save: upload temp -> preserve owner/group/mode -> atomic install -> verify;
- destructive Docker volume delete: explicit resource-name confirmation và không automatic retry.

Không bao giờ xử lý permission bằng `chmod 777`.

### Step 6 — Implement UI state, không chỉ happy path

Mọi async screen/action phải định nghĩa:

- loading;
- empty;
- success;
- recoverable error;
- permission/sudo required;
- capability unavailable;
- disconnected/reconnecting;
- cancellation.

Không operation nào được freeze WPF UI thread.

### Step 7 — Thêm test trước khi tuyên bố complete

Mức tối thiểu:

- domain/application behavior: unit test;
- command builder/parser: fixture test;
- distro behavior: adapter test;
- remote feature: integration test khi infrastructure tồn tại;
- security-sensitive change: negative test;
- important UI workflow: UI automation khi UI test harness tồn tại.

Không làm yếu hoặc xóa test chỉ để CI pass trừ khi task explicit thay đổi requirement và PR ghi rõ lý do.

### Step 8 — Chạy quality gate

Ít nhất chạy repository-equivalent của:

```powershell
dotnet restore
dotnet build -c Release
# dotnet test -c Release   # once test projects are present
```

Đồng thời chạy format/static/security check đã cấu hình trong repository.

Warning do thay đổi mới tạo ra phải được fix. Không blanket-disable analyzer.

### Step 9 — Self-review diff

Trước commit/review completion, inspect complete diff và verify:

- không secret/credential/private key;
- không debug-only bypass;
- không host-key auto-accept;
- không shell injection path;
- không destructive automatic retry;
- không UI-to-SSH coupling;
- không distro logic leak vào UI;
- không feature unsupported bị âm thầm hiển thị như supported;
- không file không liên quan.

### Step 10 — Tạo completion report

PR/task report phải nêu:

- thay đổi gì;
- architecture decision;
- user-visible behavior;
- test đã chạy và kết quả;
- tác động tới certified distro;
- security/safety consideration;
- known limitation/follow-up.

## 4. Git workflow

Default branch: `main`.

Không develop trực tiếp trên `main`.

Branch naming:

```text
feat/<issue>-short-name
fix/<issue>-short-name
refactor/<issue>-short-name
test/<issue>-short-name
docs/<issue>-short-name
chore/<issue>-short-name
```

Với bootstrap work không có issue, descriptive `chore/...` branch là chấp nhận được.

Commit style:

```text
feat(explorer): add remote directory listing
fix(ssh): reject changed host fingerprint
test(docker): cover malformed inspect output
docs(agent): clarify destructive action policy
```

Ưu tiên commit nhỏ, nhất quán. Không force-push shared branch trừ khi được yêu cầu rõ.

PR phải target `main`, giữ scope tập trung và pass CI trước merge. Ưu tiên squash merge cho normal feature work trừ khi commit history có ý nghĩa chủ đích.

## 5. Quy tắc thực thi issue

Roadmap milestone có thứ tự. Agent nên chọn work từ milestone sớm nhất có prerequisite đã complete.

Với mỗi issue:

1. verify prerequisite;
2. xác định acceptance criteria;
3. split thành sub-issue nếu không thể review mạch lạc;
4. implement từng vertical slice;
5. link follow-up thay vì âm thầm mở rộng scope.

Không bắt đầu milestone sau để né test/security work của milestone hiện tại.

## 6. Dependency policy

Trước khi thêm package:

- verify package được maintain tích cực và compatible với target framework của repository;
- ưu tiên BCL/Microsoft-supported capability khi đủ;
- document lý do dependency cần thiết;
- tránh package duplicate abstraction đã có;
- pin/centrally manage version khi central package management được đưa vào;
- check license compatibility trước product distribution.

Không đưa cloud/backend dependency vào cho feature có thể local-only trừ khi product plan yêu cầu rõ.

## 7. Security invariant

Các rule sau không thương lượng:

- không lưu password, sudo password, passphrase hoặc private-key content trong SQLite;
- không log secret;
- không tự động trust unknown hoặc changed SSH host key;
- không expose Docker socket remotely để làm UI dễ hơn;
- không yêu cầu public database port khi SSH tunneling là đủ;
- không dùng plaintext temp file cho credential;
- không chạy toàn app elevated mặc định;
- chỉ request privilege cho remote operation thực sự cần;
- destructive action yêu cầu explicit user intent và resource identity.

Đọc `docs/SECURITY.vi.md` trước khi chạm code connection, credential, sudo, file mutation, Docker, firewall, user, package, SSL, backup hoặc update.

## 8. UX invariant

ServerDesk là GUI-first nhưng phải trung thực với Linux state.

- Dùng interaction pattern Windows 11/Fluent.
- Giữ connection và server identity luôn visible.
- Giải thích permission/capability failure bằng ngôn ngữ user và tách technical detail riêng.
- Tránh modal cho thông tin bình thường; dùng modal confirmation cho quyết định thật sự risk.
- Không giấu error sau generic “Something went wrong”.
- Giữ advanced access qua terminal/raw config view khi form không biểu diễn được toàn bộ underlying system.
- Feature bị disable phải giải thích vì sao unavailable.

Xem `docs/UI_UX.vi.md`.

## 9. Compatibility rule

Certified support chỉ được định nghĩa bởi `docs/SUPPORT_MATRIX.vi.md` và automated/manual compatibility gate đã pass.

Nếu code hoạt động trên distro chưa liệt kê, mô tả là best-effort/experimental, không phải certified.

Feature code phải query capability thay vì check distro name khi capability mới là dependency thực.

## 10. Definition of Done

Feature chưa hoàn thành khi “button chạy được”. Chỉ complete khi mọi mục áp dụng được đều đạt:

- architecture boundary được tôn trọng;
- UI hoàn chỉnh cho required state;
- dark/light/system theme behavior chấp nhận được;
- primary workflow accessible bằng keyboard;
- capability detection được xử lý;
- permission/sudo được xử lý;
- cancellation/timeout được xử lý;
- disconnect/reconnect được xử lý;
- error được map thành typed/user-safe error;
- không secret bị log/store sai;
- test được thêm và pass;
- certified distro fixture/integration được cập nhật;
- docs được cập nhật khi behavior/contract đổi;
- CI green;
- self-review complete.

## 11. Những việc agent không bao giờ được làm

- Implement toàn bộ roadmap trong một giant PR.
- Skip required prerequisite vì feature milestone sau thú vị hơn.
- Thay architecture abstraction bằng direct SSH call trong ViewModel.
- Dùng rộng rãi `sudo sh -c` như shortcut.
- “Fix” permission toàn cục.
- Auto-delete/auto-prune user data.
- Auto-update production package/service khi chưa có explicit action.
- Auto-retry destructive operation khi request đầu có thể đã thành công.
- Claim universal Linux support khi chưa test.
- Merge PR có known failing required check.

## 12. Thứ tự product milestone

Canonical sequence:

```text
M0 Foundation
M1 Remote Core
M2 Windows-like Server UI
M3 DevOps
M4 Deployment
M5 Administration
M6 Databases
M7 Multi-server
M8 Optional ServerDesk Agent
```

Xem `docs/ROADMAP.vi.md` để biết acceptance gate chính xác.
