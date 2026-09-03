# ADR 0004 — Threat model cho agent và realtime transport qua SSH tunnel

[English](0004-agent-threat-model-and-tunneled-transport.md) | **Tiếng Việt**

- Trạng thái: Chấp nhận cho triển khai M8
- Ngày: 2026-09-03
- Parent: #10
- Security gate: #122

## Bối cảnh

ADR 0002 đã chốt ServerDesk theo hướng agentless-first và `serverdesk-agent` tùy chọn trong tương lai phải implement các abstraction hiện có của application thay vì tạo một kiến trúc sản phẩm thứ hai. M8 đưa agent tùy chọn này vào để cải thiện metrics và event stream tần suất cao.

Agent là một remote service nhạy cảm về bảo mật. Nó quan sát trạng thái hệ điều hành và có thể tương tác với các nguồn như procfs, systemd, Docker và log. Một sai sót thiết kế có thể làm lộ management port mới, tăng privilege, rò rỉ secret, cho phép downgrade/version confusion hoặc biến observability stream thành đường denial-of-service không giới hạn.

ADR này là threat-model và quyết định transport bắt buộc phải được chấp nhận trước khi bắt đầu implement agent.

## Tóm tắt quyết định

Baseline M8 là:

```text
Windows ServerDesk
    |
    | kết nối SSH đã thiết lập + xác thực host/user
    v
SSH local forward trên Windows 127.0.0.1:ephemeral
    |
    | kênh SSH được mã hóa
    v
Linux 127.0.0.1:agent-port
    |
    v
serverdesk-agent
    |
    +--> adapter read-only có giới hạn: metrics / process events / service events / Docker events / logs
```

Listener của agent chỉ bind loopback theo mặc định. ServerDesk truy cập agent qua SSH-controlled local forward. M8 không bổ sung public agent management port.

Bất kỳ network mode non-loopback nào trong tương lai đều cần ADR và security review riêng, mô tả authentication, authorization, transport encryption, vòng đời certificate/key, yêu cầu firewall và revocation. Nó không được là một configuration switch ngầm định.

## Tài sản cần bảo vệ

- SSH host trust và danh tính server đã được xác thực.
- SSH user identity và authorization boundary.
- Credential, private key, passphrase và secret reference phía Windows.
- Tính toàn vẹn của agent process và binary/update artifact.
- Trạng thái hệ điều hành server và các privileged interface.
- Operational data được stream, bao gồm log có thể chứa nội dung ứng dụng nhạy cảm.
- Các quyết định feature của Windows client dựa trên capability/version do agent cung cấp.
- Availability của server và ServerDesk workspace.

## Trust boundary

1. **Windows UI/Application boundary** — feature UI và use case dùng Application-layer interface cùng domain/application DTO. Không dùng trực tiếp generated gRPC message/client.
2. **SSH trust boundary** — cơ chế SSH host verification và user authentication hiện có xác lập server/user mà tunnel thuộc về. Agent RPC không thay thế hoặc làm yếu boundary này.
3. **Local forwarding boundary** — Windows client mở local forward bind Windows loopback và target Linux loopback. SSH session/profile đã chọn sở hữu vòng đời tunnel.
4. **Agent listener boundary** — listener mặc định chỉ nhận kết nối loopback. Không cấu hình mặc định trên `0.0.0.0`, `::`, địa chỉ LAN hoặc public.
5. **Agent process boundary** — agent parse RPC input, negotiate capability và đọc OS data. Mọi client input phải được coi là không tin cậy và có giới hạn size/rate/concurrency.
6. **OS/source boundary** — procfs, systemd, Docker socket/API và log source là các capability boundary riêng. Quyền truy cập một source không đồng nghĩa với unrestricted shell execution hoặc quyền với mọi source khác.
7. **Update boundary** — agent artifact tải/nhận về được coi là không tin cậy cho tới khi authenticated manifest/signature và artifact digest được verify.

## Threat model

### Lộ public port

**Nguy cơ:** quá trình cài đặt hoặc cấu hình vô tình mở một management service có thể truy cập từ xa.

**Control:**

- mặc định chỉ bind `127.0.0.1` và/hoặc `::1`;
- fail startup nếu listener baseline được cấu hình thành wildcard/public bind;
- baseline installer không tạo firewall allow rule cho agent port;
- client kết nối qua SSH local forwarding, không kết nối trực tiếp tới server address;
- thiết kế non-loopback cần security ADR mới.

### Tunnel hijack, nhầm target hoặc cross-server mix-up

**Nguy cơ:** UI hiển thị dữ liệu của server này nhưng tunnel thuộc profile/session khác, hoặc local process khác chiếm/race forwarded endpoint.

**Control:**

- vòng đời tunnel gắn với SSH session đã thiết lập và selected profile id;
- dùng Windows loopback port tạm thời thay vì một fixed local port dùng chung;
- agent connection phải gắn với đúng session/profile tạo forward;
- negotiation trả về non-secret server/agent identity metadata đủ để consistency-check;
- hủy transport khi SSH session sở hữu nó disconnect hoặc thay đổi.

### Privilege escalation và confused deputy

**Nguy cơ:** một read/stream RPC trở thành generic privileged command hoặc quyền Docker/systemd làm agent có authority lớn hơn dự kiến.

**Control:**

- agent chạy với least privilege đủ cho các read-only capability được bật;
- realtime contract M8 không expose arbitrary shell, arbitrary file read, arbitrary systemd action, Docker mutation hoặc generic command RPC;
- capability được model rõ và allow-list;
- privileged mutation/helper cần review riêng trước khi implement;
- source adapter validate identifier và input length trước khi gọi OS API.

### Agent bị compromise hoặc độc hại

**Nguy cơ:** agent báo dữ liệu giả hoặc cố làm client thực hiện hành động không an toàn.

**Control:**

- dữ liệu agent chỉ là observational input, không bao giờ tự trở thành authorization cho destructive client action;
- review/safety gate hiện có vẫn là nguồn quyết định cho mutation;
- UI/use case dùng normalized application abstraction có availability/source state rõ ràng;
- agent disconnect hoặc dữ liệu invalid phải degrade/fallback, không chuyển sang raw execution path.

### Replay, downgrade và version confusion

**Nguy cơ:** cặp agent/client không tương thích nhưng vẫn âm thầm diễn giải field hoặc capability sai.

**Control:**

- RPC đầu tiên là explicit protocol/version/capability negotiation;
- negotiation gồm protocol major/minor, agent version và capability allow-list;
- protocol major không tương thích bị reject với state `Incompatible` rõ ràng;
- capability tùy chọn chỉ dùng khi cả hai phía cùng advertise support;
- thiếu capability => `Unsupported`, không suy đoán từ version number;
- không silent fallback từ incompatible agent transport sang một privileged agent RPC khác.

### Update bị can thiệp

**Nguy cơ:** attacker thay binary/package agent trong quá trình install/update.

**Control:**

- release/update flow phải authenticate signed manifest hoặc signed release metadata tương đương trước activation;
- manifest ràng buộc version, platform/architecture và cryptographic digest của artifact;
- verify artifact digest sau download và trước install/swap;
- signing private key không bao giờ được nhúng trong ServerDesk hoặc `serverdesk-agent`;
- update fail hoặc không verify được phải giữ nguyên installation hiện tại và báo lỗi rõ;
- rollback policy không được cho phép unauthenticated downgrade.

Chi tiết signing/distribution có thể được chọn ở slice M8 sau, nhưng authenticated release metadata cùng digest verification là acceptance requirement bắt buộc.

### Rò rỉ secret và logging không an toàn

**Nguy cơ:** RPC error, log hoặc diagnostic làm lộ credential, key material, passphrase, token hoặc command payload nhạy cảm.

**Control:**

- realtime contract của agent không có credential, private-key content, passphrase hoặc secret-reference field;
- exception đi qua transport được map thành error code/category có giới hạn thay vì raw stack trace hoặc arbitrary exception message;
- log agent/client phải sanitize endpoint/identifier khi cần và tuyệt đối không log secret value;
- nội dung log stream được coi là dữ liệu user/server và chỉ display/retain theo product behavior rõ ràng, không mặc định copy vào diagnostic telemetry.

### Denial of service và lỗi backpressure

**Nguy cơ:** event tần suất cao, log flood hoặc slow consumer làm cạn memory/CPU/network.

**Control:**

- mọi stream có server-side buffer giới hạn;
- định nghĩa maximum message size, subscription count và concurrent stream cho mỗi tunneled client;
- dùng cancellation/deadline và dừng produce nhanh khi disconnect;
- metrics có thể sampling/coalescing khi không cần delivery từng event chính xác;
- event stream phải expose dropped/coalesced counter khi fidelity bị giảm;
- không tích lũy lịch sử event vô hạn để chờ Windows consumer chậm.

## Mô hình authentication và authorization

Trong baseline M8, trust đi qua SSH-controlled tunnel hiện có:

- SSH host verification xác thực remote host theo policy hiện có của ServerDesk;
- SSH user authentication xác lập remote user/session;
- Linux loopback reachability cùng quyền sở hữu SSH tunnel đó là transport access boundary;
- agent không đưa thêm reusable bearer token hoặc password được lưu trong profile metadata;
- thiết kế không coi loopback là đủ cho public/LAN listener trong tương lai.

Nếu deployment tương lai cần phòng thủ trước process không tin cậy đang chạy cùng server user, cần local peer-authentication design và review bổ sung. M8 không giải quyết việc đó bằng cách mở public endpoint mang credential.

## Application abstraction boundary

Generated Protobuf/gRPC type thuộc transport/infrastructure edge. Feature UI và use case tiếp tục phụ thuộc Application-layer port/interface.

Các abstraction ổn định có thể gồm normalized realtime metrics và typed process/service/Docker/log event stream, nhưng contract phải biểu diễn khái niệm application như:

- source: `Agent` hoặc `Agentless`;
- state: `Available`, `Unsupported`, `Disconnected`, `Incompatible` hoặc `Failed`;
- capability set;
- semantics cho cancellation và stream completion.

Điều này giữ agentless implementation hợp lệ và giúp gRPC có thể thay thế/test độc lập.

## Negotiation và compatibility

Trước mọi realtime subscription, client thực hiện bounded negotiation request.

Negotiation output bắt buộc:

- protocol major/minor;
- agent product version;
- capability identifier từ documented allow-list;
- non-secret runtime/platform metadata cần để chọn behavior tương thích.

Quy tắc:

- protocol major mismatch => reject agent transport thành `Incompatible`;
- major được hỗ trợ nhưng minor cũ/mới hơn => chỉ dùng capability hai phía cùng hỗ trợ;
- unknown capability bị ignore, không execute động;
- thiếu capability => `Unsupported` rõ ràng;
- negotiation timeout/failure => `Disconnected` hoặc `Failed`, sau đó fallback agentless nếu được hỗ trợ.

## Hành vi khi disconnect và fallback

Agent tùy chọn không được trở thành hidden dependency cho core operation đã chứng minh.

- Khi có agentless implementation tương đương, transport disconnect/failure phải fallback rõ ràng sang implementation đó.
- UI nên expose data source/degraded state khi khác biệt có ý nghĩa, nhất là realtime frequency/fidelity.
- Khi không có agentless equivalent, hiển thị `Unavailable`/`Unsupported` thay vì làm hỏng toàn server workspace.
- Reconnect phải tạo/verify lại SSH tunnel và negotiate lại; không resume stale stream một cách mù quáng.

## Ràng buộc install, privilege và uninstall

- Install/update là explicit reviewed remote mutation và phải dùng safety/review convention hiện có.
- Service account/permission dùng least privilege cho capability được bật.
- Agent-owned state có location riêng và không chứa SSH/client secret.
- Clean uninstall chỉ xóa service unit, binary/package và agent-owned state/cache do flow cài agent của ServerDesk tạo ra.
- Uninstall không được xóa Docker, systemd config không thuộc agent, SSH config, firewall rule không thuộc agent, user application log hoặc ServerDesk server profile.

## Security invariant cho các slice M8 sau

Các invariant sau là release-blocking trừ khi được supersede bằng ADR khác đã accepted:

1. Listener mặc định chỉ bind loopback.
2. Baseline client access đi qua SSH-controlled tunnel đã thiết lập.
3. Không feature UI/use case nào phụ thuộc trực tiếp generated gRPC type.
4. Không thêm arbitrary command hoặc generic privileged mutation RPC vào realtime contract.
5. Protocol/capability negotiation diễn ra trước streaming.
6. Stream có giới hạn và cancellable.
7. Disconnect/incompatibility phải degrade hoặc fallback rõ ràng.
8. Diagnostic path không làm lộ secret material.
9. Update artifact phải được authenticate và digest-verify trước activation.
10. Agentless core operation vẫn được hỗ trợ ở nơi đã chứng minh.

## Hệ quả

Tích cực:

- baseline không thêm public management port;
- tái sử dụng SSH trust và routing behavior đã có;
- realtime transport vẫn tùy chọn và có thể thay thế phía sau abstraction hiện có;
- compatibility/failure state rõ ràng;
- update authenticity và stream backpressure trở thành requirement trước release.

Đánh đổi:

- dùng agent cần SSH connection/tunnel bootstrap;
- local process bị compromise trên server vẫn nằm trong loopback trust environment và có thể cần peer authentication mạnh hơn ở thiết kế sau;
- least-privilege access có thể khiến một số event source không khả dụng nếu chưa setup rõ;
- signed update infrastructure làm tăng release engineering work;
- agent và agentless implementation phải được test với cùng application contract.

## Các slice triển khai tiếp theo

Sau khi ADR này được certify, M8 có thể đi theo các vertical slice nhỏ:

1. transport-neutral application contract cùng Protobuf negotiation/health contract;
2. Linux agent host loopback-only và SSH tunnel bootstrap;
3. metrics streaming với lợi ích polling/latency đo được cùng fallback;
4. process/service/Docker event stream;
5. log streaming có bounded backpressure;
6. signed install/update/uninstall flow và final security certification.
