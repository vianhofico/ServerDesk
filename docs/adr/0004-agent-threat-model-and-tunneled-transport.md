# ADR 0004 — Agent threat model and SSH-tunneled realtime transport

**English** | [Tiếng Việt](0004-agent-threat-model-and-tunneled-transport.vi.md)

- Status: Accepted for M8 implementation
- Date: 2026-09-03
- Parent: #10
- Security gate: #122

## Context

ADR 0002 established that ServerDesk is agentless-first and that a future optional `serverdesk-agent` must implement existing application abstractions instead of creating a second product architecture. M8 introduces that optional agent for high-frequency metrics and event streams.

The agent is a security-sensitive remote service. It observes operating-system state and may interact with sources such as procfs, systemd, Docker and logs. A design mistake could expose a new management port, increase privilege, leak secrets, allow downgrade/version confusion, or turn an observability stream into an unbounded denial-of-service path.

This ADR is the mandatory threat-model and transport decision that must be accepted before agent implementation starts.

## Decision summary

The M8 baseline is:

```text
Windows ServerDesk
    |
    | established SSH connection and host/user authentication
    v
SSH local forward on Windows 127.0.0.1:ephemeral
    |
    | encrypted SSH channel
    v
Linux 127.0.0.1:agent-port
    |
    v
serverdesk-agent
    |
    +--> bounded read-only adapters: metrics / process events / service events / Docker events / logs
```

The agent listener binds loopback only by default. ServerDesk reaches it through an SSH-controlled local forward. M8 does not add a public agent management port.

Any future non-loopback network mode requires a separate ADR and security review that specifies authentication, authorization, transport encryption, certificate/key lifecycle, firewall expectations and revocation. It is not an implicit configuration switch.

## Assets to protect

- SSH host trust and authenticated server identity.
- SSH user identity and authorization boundary.
- Windows credentials, private keys, passphrases and secret references.
- Agent process integrity and executable/update integrity.
- Server operating-system state and privileged interfaces.
- Streamed operational data, including logs that may contain sensitive application content.
- Feature decisions made by the Windows client from agent-provided capability and version data.
- Availability of the server and ServerDesk workspace.

## Trust boundaries

1. **Windows UI/Application boundary** — feature UI and use cases consume Application-layer interfaces and domain/application DTOs. They do not consume generated gRPC messages or clients directly.
2. **SSH trust boundary** — existing SSH host verification and user authentication establish which server/user the tunnel belongs to. Agent RPC does not replace or weaken this boundary.
3. **Local forwarding boundary** — the Windows client opens a local forward bound to Windows loopback and targets Linux loopback. The selected SSH session/profile owns the tunnel lifetime.
4. **Agent listener boundary** — the default listener accepts only loopback connections. It is not configured on `0.0.0.0`, `::`, a LAN address or a public address by default.
5. **Agent process boundary** — the agent parses RPC input, negotiates capabilities and reads OS data. It must treat all client input as untrusted and apply size/rate/concurrency limits.
6. **OS/source boundary** — procfs, systemd, Docker socket/API and log sources are separate capability boundaries. Access to one source must not imply unrestricted shell execution or access to every source.
7. **Update boundary** — downloaded/retrieved agent artifacts are untrusted until an authenticated manifest/signature and artifact digest have been verified.

## Threat model

### Public-port exposure

**Threat:** installation or configuration accidentally exposes a remotely reachable management service.

**Controls:**

- bind `127.0.0.1` and/or `::1` only by default;
- fail startup when the configured baseline listener would resolve to a wildcard/public bind;
- do not create a firewall allow rule for an agent port in the baseline installer;
- client connects through SSH local forwarding, not directly to the server address;
- a non-loopback design requires a new security ADR.

### Tunnel hijack, target confusion or cross-server mix-up

**Threat:** the UI displays data from one server while the tunnel belongs to another profile/session, or another local process races for the forwarded endpoint.

**Controls:**

- tunnel lifetime is scoped to the established SSH session and selected profile id;
- use an ephemeral Windows loopback port rather than a global fixed local port;
- associate the agent connection with the exact session/profile that created the forward;
- negotiation returns non-secret server/agent identity metadata suitable for consistency checks;
- discard a transport when its owning SSH session disconnects or changes.

### Privilege escalation and confused deputy

**Threat:** a read/stream RPC becomes a general privileged command mechanism, or Docker/systemd access expands the agent's authority unexpectedly.

**Controls:**

- run the agent with least privilege sufficient for the enabled read-only capabilities;
- do not expose arbitrary shell, arbitrary file read, arbitrary systemd action, Docker mutation or generic command RPCs in the M8 realtime contract;
- model capabilities explicitly and allow-list them;
- privileged mutation/helper designs require separate review before implementation;
- source adapters validate identifiers and input lengths before touching OS APIs.

### Compromised or malicious agent

**Threat:** the agent reports fabricated data or attempts to make the client perform unsafe actions.

**Controls:**

- agent data is observational input, never implicit authorization for a destructive client action;
- existing review/safety gates remain authoritative for mutations;
- UI/use cases consume normalized application abstractions with explicit availability/source state;
- agent disconnect or invalid data degrades/falls back instead of switching to raw execution paths.

### Replay, downgrade and version confusion

**Threat:** an incompatible agent/client pair silently interprets fields or capabilities incorrectly.

**Controls:**

- first RPC is explicit protocol/version/capability negotiation;
- negotiation includes protocol major/minor, agent version and a capability allow-list;
- incompatible protocol major is rejected with an explicit `Incompatible` state;
- optional capabilities are used only when both sides advertise support;
- missing capability is `Unsupported`, not guessed from version numbers;
- no silent fallback from an incompatible agent transport to a different privileged agent RPC.

### Update tampering

**Threat:** an attacker replaces an agent binary/package during install or update.

**Controls:**

- release/update flow must authenticate a signed manifest or equivalent signed release metadata before activation;
- manifest binds version, platform/architecture and cryptographic digest of the artifact;
- artifact digest is verified after download and before install/swap;
- signing private keys are never embedded in ServerDesk or `serverdesk-agent`;
- a failed or unverifiable update leaves the existing installation unchanged and reports failure explicitly;
- rollback policy must not permit an unauthenticated downgrade.

The exact signing/distribution implementation may be selected in a later M8 slice, but authenticated release metadata plus digest verification is a non-optional acceptance requirement.

### Secret leakage and unsafe logging

**Threat:** RPC errors, logs or diagnostics expose credentials, key material, passphrases, tokens or sensitive command payloads.

**Controls:**

- no credential, private-key content, passphrase or secret-reference field is part of the agent realtime contract;
- exceptions crossing the transport are mapped to bounded error codes/categories rather than raw stack traces or arbitrary exception messages;
- agent/client logs must sanitize endpoints/identifiers when necessary and must never log secret values;
- log-stream content is treated as user/server data and is displayed/retained only according to explicit product behavior, not copied into diagnostic telemetry by default.

### Denial of service and backpressure failure

**Threat:** high-frequency events, log floods or slow consumers exhaust memory/CPU/network resources.

**Controls:**

- every stream has bounded server-side buffering;
- define maximum message size, subscription count and concurrent streams per tunneled client;
- use cancellation/deadlines and stop producing promptly after disconnect;
- apply sampling/coalescing for metrics where exact every-event delivery is unnecessary;
- event streams surface dropped/coalesced counters when fidelity is reduced;
- never accumulate unbounded historical events waiting for a slow Windows consumer.

## Authentication and authorization model

For the M8 baseline, trust rides through the existing SSH-controlled tunnel:

- SSH host verification authenticates the remote host according to ServerDesk's existing policy;
- SSH user authentication establishes the remote user/session;
- Linux loopback reachability plus ownership of that SSH tunnel is the transport access boundary;
- the agent does not introduce a reusable bearer token or password stored in profile metadata;
- the design does not claim that loopback alone is sufficient for a future public/LAN listener.

If future deployment scenarios require defense against untrusted processes already running as the same server user, that requires an additional local peer-authentication design and review. M8 must not solve that by exposing a public credential-bearing endpoint.

## Application abstraction boundary

Generated Protobuf/gRPC types belong to the transport/infrastructure edge. Feature UI and use cases must continue to depend on Application-layer ports/interfaces.

Examples of stable abstractions may include normalized realtime metrics and typed process/service/Docker/log event streams, but their contracts must express application concepts such as:

- source: `Agent` or `Agentless`;
- state: `Available`, `Unsupported`, `Disconnected`, `Incompatible` or `Failed`;
- capability set;
- cancellation and stream completion semantics.

This allows agentless implementations to remain valid and keeps gRPC replaceable/testable.

## Negotiation and compatibility

Before any realtime subscription, the client performs a bounded negotiation request.

Required negotiation output:

- protocol major/minor;
- agent product version;
- capability identifiers from a documented allow-list;
- non-secret runtime/platform metadata needed to select compatible behavior.

Rules:

- protocol major mismatch => reject agent transport as `Incompatible`;
- supported major with older/newer minor => use only mutually supported capabilities;
- unknown capabilities are ignored, not executed dynamically;
- capability absence => explicit `Unsupported`;
- negotiation timeout/failure => `Disconnected` or `Failed`, then agentless fallback where supported.

## Disconnect and fallback behavior

The optional agent must never become a hidden dependency for proven core operations.

- When an equivalent agentless implementation exists, transport disconnect/failure falls back explicitly to that implementation.
- The UI should expose the data source/degraded state when the difference matters, especially for realtime frequency/fidelity.
- When no agentless equivalent exists, show `Unavailable`/`Unsupported` rather than breaking the entire server workspace.
- Reconnection must create/revalidate the SSH tunnel and repeat negotiation; stale streams are not resumed blindly.

## Installation, privilege and uninstall constraints

- Install/update actions remain explicit reviewed remote mutations and must use existing safety/review conventions.
- Service account/permissions use least privilege for enabled capabilities.
- Agent-owned state has a dedicated location with no embedded SSH/client secrets.
- Clean uninstall removes only the agent service unit, agent binaries/packages and agent-owned state/cache created by ServerDesk's agent installation flow.
- Uninstall must not remove Docker, systemd configuration unrelated to the agent, SSH configuration, firewall rules not owned by the agent, user application logs or ServerDesk server profiles.

## Security invariants for later M8 slices

The following are release-blocking invariants unless superseded by a separately accepted ADR:

1. Default listener is loopback-only.
2. Baseline client access is through an established SSH-controlled tunnel.
3. No feature UI/use case directly depends on generated gRPC types.
4. No arbitrary command or generic privileged mutation RPC is added to the realtime contract.
5. Protocol/capability negotiation precedes streaming.
6. Streams are bounded and cancellable.
7. Disconnect/incompatibility degrades or falls back explicitly.
8. Diagnostic paths do not expose secret material.
9. Update artifacts are authenticated and digest-verified before activation.
10. Agentless core operation remains supported where already proven.

## Consequences

Positive:

- no new public management port in the baseline design;
- reuses established SSH trust and routing behavior;
- realtime transport remains optional and replaceable behind existing abstractions;
- compatibility and failure states are explicit;
- update authenticity and stream backpressure are requirements before release rather than afterthoughts.

Trade-offs:

- agent use requires an SSH connection/tunnel bootstrap;
- a local compromised process on the server remains inside the loopback trust environment and may require stronger peer authentication in a later design;
- least-privilege access may make some event sources unavailable without explicit setup;
- signed update infrastructure adds release engineering work;
- agent/agentless implementations must be tested against the same application contracts.

## Follow-up implementation slices

After this ADR is certified, M8 may proceed in small vertical slices:

1. transport-neutral application contracts plus Protobuf negotiation/health contract;
2. loopback-only Linux agent host and SSH tunnel bootstrap;
3. metrics streaming with measurable polling/latency benefit and fallback;
4. process/service/Docker event streams;
5. log streaming with bounded backpressure;
6. signed install/update/uninstall flow and final security certification.
