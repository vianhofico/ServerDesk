# Kiến trúc

[English](ARCHITECTURE.md) | **Tiếng Việt**

> Bản English là source of truth khi có khác biệt ngoài ý muốn giữa hai bản dịch.

## 1. Mục tiêu kiến trúc

ServerDesk phải dễ bảo trì khi phát triển từ SSH/SFTP client thành desktop application quản trị server. Vì vậy kiến trúc tách riêng UI, use case, remote transport, Linux interpretation, persistence và feature module.

Mục tiêu chính:

- phần lớn logic có thể test mà không cần server thật;
- thích nghi distro/version mà không đưa conditional vào UI;
- xây command an toàn và typed error;
- hỗ trợ nhiều remote channel đồng thời;
- có thể bổ sung agent trong tương lai mà không viết lại feature use case;
- secret không đi qua boundary không phù hợp.

## 2. Topology cấp cao

```text
+----------------------- Windows ------------------------+
|                                                        |
|  ServerDesk.App (WPF)                                  |
|      |                                                 |
|      v                                                 |
|  Application / Feature Use Cases                       |
|      |                                                 |
|      +------------------+-------------------+           |
|      |                  |                   |           |
|      v                  v                   v           |
|  Linux adapters     SSH/SFTP infra     Local storage   |
|      |                  |                   |           |
+------+------------------+-------------------+-----------+
       |                  |
       | SSH/SFTP/PTY     | optional tunneled gRPC later
       v                  v
+---------------------- Linux server ---------------------+
| systemd / proc / Docker / nginx / files / tools         |
| optional serverdesk-agent bound to loopback             |
+---------------------------------------------------------+
```

## 3. Trách nhiệm của từng layer

### ServerDesk.Domain

Chứa các product concept và value object ổn định.

Ví dụ:

- server identity/profile metadata;
- capability model;
- operation risk classification;
- normalized process/service/storage/network model;
- typed operation result/error;
- known-host fingerprint dưới dạng value, không phải persistence implementation.

Không được phụ thuộc WPF, SSH.NET, SQLite, WebView2, distro-specific implementation hoặc network library.

### ServerDesk.Application

Chứa application use case và port/interface.

Ví dụ:

- connect/disconnect server;
- list directory;
- save privileged file;
- restart service;
- list container;
- create tunnel;
- retrieve metrics;
- operation orchestration và rollback policy.

Định nghĩa các abstraction như:

```text
IRemoteCommandExecutor
IRemoteFileSystem
IServerSession
ICapabilityDetector
IServiceManager
IProcessManager
IStorageManager
INetworkManager
ILogManager
IFirewallManager
IPackageManager
ISecretStore
IProfileRepository
IOperationAudit
```

### Infrastructure.Ssh

Chỉ implement transport concern:

- connection lifecycle;
- authentication mechanism;
- host-key event;
- command channel;
- PTY/shell stream;
- SFTP;
- forwarding;
- translation timeout/cancellation;
- map SSH-specific error sang application error.

Nó không biết cách parse nginx, Docker, systemd hoặc distro package output.

### Linux.Common

Chứa Linux-wide command specification/parser khi behavior đủ ổn định giữa các certified distro.

Ví dụ:

- parse `/etc/os-release`;
- `/proc` metrics;
- generic process fact;
- `command -v` probe;
- safe POSIX-ish file metadata operation khi đã chứng minh portable.

### Linux.Debian / Linux.Rhel

Chứa adapter theo distro family.

Ví dụ:

- APT vs DNF;
- lựa chọn/hành vi UFW vs firewalld;
- distro-specific package metadata;
- service/config path chỉ khi không thể capability-discover.

Feature code nên kiểm capability thay vì distro name khi dependency thật sự là một capability.

### Platform.Windows

Chứa Windows-specific implementation:

- Credential Manager / DPAPI secret storage;
- app path;
- OS notification nếu dùng;
- Windows shell/file picker integration;
- secure local IPC khi cần về sau.

### Persistence

Chứa SQLite implementation cho non-secret metadata.

Secret chỉ được tham chiếu gián tiếp qua identifier do `ISecretStore` quản lý.

### Feature modules

Feature module sở hữu UI + application-facing orchestration cho một domain area nhất quán:

```text
Dashboard
Explorer
Terminal
Processes
Services
Docker
Storage
Network
Logs
ScheduledTasks
Git
Nginx
Security
Database
Backup
```

Feature module có thể phụ thuộc Application/Domain abstraction, không bao giờ phụ thuộc concrete SSH class.

## 4. Dependency rule

Hướng dependency được phép:

```text
App/UI ---------> Application ---------> Domain
   |                    ^                  ^
   |                    |                  |
   +-> feature modules -+                  |
                        |                  |
Infrastructure.Ssh -----+------------------+
Linux adapters ---------+------------------+
Persistence ------------+------------------+
Platform.Windows -------+------------------+
```

Infrastructure không bao giờ trở thành business API được ViewModel sử dụng trực tiếp.

## 5. Remote command model

Mọi command execution dùng typed specification, về khái niệm:

```text
RemoteCommandSpec
- executable
- arguments[]
- environment
- workingDirectory (optional)
- locale policy
- privilege requirement
- timeout
- stdin policy
- output parser
- idempotency/retry policy
- operation risk
```

Yêu cầu chính:

- không arbitrary interpolate untrusted input vào shell string;
- argument được validate/escape tại một nơi;
- yêu cầu structured output khi có;
- explicit locale cho parser cần stable text;
- capture stdout/stderr riêng;
- giữ exit code;
- hỗ trợ cancellation;
- command có bounded timeout trừ khi chủ đích streaming.

Với operation vốn cần shell pipeline/compound script, isolate script trong command object riêng đã được review và quote chặt chẽ input đã validate. Không biến shell snippet thành execution model mặc định.

## 6. Parse dữ liệu

Nguồn remote data ưu tiên:

1. stable file/interface (`/proc`, `/etc/os-release`, v.v.);
2. JSON;
3. explicit property/key-value output;
4. fixed delimiter format;
5. human prose chỉ là last resort.

Parser tạo domain/application model và không đẩy trách nhiệm raw line splitting lên UI.

Parser failure trả `ParseFailed` với diagnostic detail phù hợp cho log nhưng không chứa secret.

## 7. Server session model

Một logical server workspace sở hữu một `ServerSession`, nhưng session không được serialize mọi công việc qua một SSH channel duy nhất.

Resource về khái niệm:

```text
ServerSession
- command connection/pool
- dedicated SFTP client
- terminal shell #1..N
- log stream #1..N
- port forwarding sessions
- cancellation scope
- capability snapshot
- connection/reconnect state
```

Terminal bị block hoặc log stream dài không được ngăn dashboard refresh hoặc file operation.

## 8. Connection lifecycle

Các state:

```text
Disconnected
Connecting
AwaitingHostTrust
Authenticating
Connected
Degraded
Reconnecting
Disconnecting
Failed
```

Feature subscribe normalized connection state và phải chịu được reconnect/disconnect.

Automatic reconnect được phép cho safe channel. Mutating/destructive operation không được replay mù quáng sau ambiguous network failure vì remote operation có thể đã hoàn thành.

## 9. Capability architecture

`ServerCapabilities` ghi observed fact cùng confidence/status, không chỉ boolean.

Về khái niệm:

```text
Capability<T>
- state: Available | Unavailable | PermissionDenied | Unsupported | Unknown
- value/version/details
- detectedAt
- diagnostic reason
```

Điều này ngăn UI coi “probe failed” là “software absent”.

Capability được cache local để cải thiện startup UX nhưng refresh sau connection và sau thay đổi package/tool.

## 10. Privilege model

Remote operation khai báo một trong:

```text
ReadOnly
ElevatedRead
Mutating
Destructive
```

Privilege escalation phải scope theo từng operation. Desktop process không chạy elevated chỉ vì remote operation cần sudo.

Sudo behavior được biểu diễn như capability/policy:

- không có;
- user không được phép;
- passwordless được phép cho command;
- cần password;
- có thể tồn tại cached remote sudo ticket.

Sudo password được xử lý qua secure transient memory/UI flow và không bao giờ persist trong metadata/log thông thường.

## 11. Kiến trúc file mutation

File user có quyền write bình thường dùng SFTP operation.

Privileged save dùng transaction-like workflow:

```text
read metadata
-> create remote temporary candidate under controlled path
-> upload candidate
-> optional validator
-> optional backup
-> privileged atomic install/rename
-> restore/preserve owner, group, mode
-> verify hash/metadata/content as appropriate
-> cleanup temp
-> audit
```

Nếu rollback an toàn và failure xảy ra sau replacement, restore backup. Nếu state mơ hồ, dừng và surface ambiguity thay vì đoán.

## 12. Retry policy

Automatic retry chỉ được phép khi action chứng minh là safe/idempotent.

Ví dụ:

- retry read-only capability probe: thường an toàn;
- retry directory listing: an toàn;
- retry service restart sau connection loss: không tự động an toàn nếu chưa reconcile remote state;
- retry volume deletion: cấm tự động.

Retry policy thuộc operation metadata/infrastructure policy, không phải ad-hoc loop trong ViewModel.

## 13. Local persistence

SQLite chứa non-secret product state như profile, group, UI preference, history và cached capability.

Dùng migration/versioning ngay từ khi persistent schema xuất hiện.

`CredentialReference` chỉ lưu opaque reference tới Windows secret store.

## 14. Audit model

Remote state change quan trọng tạo operation record:

- timestamp;
- server profile ID;
- operation type;
- target resource identity;
- result;
- duration;
- non-secret diagnostic summary;
- optional correlation ID.

Không ghi password, token, private key, full sensitive environment value hoặc sensitive file content.

## 15. UI threading và async rule

- Không block WPF UI thread bằng network/process I/O.
- Mọi operation dài hỗ trợ cancellation khi hợp lý.
- Collection với remote dataset lớn dùng virtualization/batching.
- Streaming update được throttle/coalesce trước UI binding.
- Dispose remote stream/session resource deterministically.

## 16. Extensibility

Hỗ trợ distro mới bằng adapter/fixture/test thay vì conditional khắp app.

Remote transport mới (future agent) implement existing application port khi có thể.

Ví dụ:

```text
IProcessManager
  |- SshProcessManager
  `- AgentProcessManager
```

Feature ViewModel không cần biết transport nào tạo normalized process model.

## 17. Tiến hóa project

Bootstrap chủ đích bắt đầu nhỏ:

```text
ServerDesk.App
ServerDesk.Application
ServerDesk.Domain
```

Project chỉ được tách khi implementation bắt đầu thực sự cần boundary, tránh hàng chục empty project ngay ngày đầu. Target structure được mô tả trong product plan và roadmap.

## 18. Architecture Decision Record

Quyết định không tầm thường và khó đảo ngược cần ADR dưới `docs/adr/`.

ADR nên ghi:

- context;
- options;
- decision;
- consequences;
- migration/reversal consideration.

Thay đổi cần ADR gồm thay WPF, đổi remote control plane, đổi secret storage, đưa mandatory server daemon vào, đổi persistence technology hoặc bypass application abstraction boundary.
