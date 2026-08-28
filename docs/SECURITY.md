# Security Requirements

**English** | [Tiếng Việt](SECURITY.vi.md)

## 1. Security objectives

ServerDesk will routinely operate against production servers. Security failures can cause credential compromise, remote code execution, data loss, server lockout, or unnoticed connection to an attacker-controlled host.

Security therefore is a product requirement, not a later hardening phase.

Primary objectives:

- authenticate the intended server;
- protect credentials and private keys;
- minimize privilege;
- prevent command injection;
- make destructive intent explicit;
- avoid unnecessary public network exposure;
- avoid leaking sensitive remote data locally;
- preserve a useful audit trail without secrets;
- fail closed when trust/parse/state is ambiguous.

## 2. Threat model

Relevant threats include:

- SSH man-in-the-middle attacks;
- stolen local credential database;
- malicious/compromised remote server returning hostile data;
- command injection through path/service/container/user input;
- accidental destructive click;
- retrying an operation whose completion state is unknown;
- broad sudo misuse;
- logging secrets/environment values;
- malicious filenames/ANSI output affecting UI/terminal;
- unsafe temporary files;
- exposed Docker/database/agent management ports;
- supply-chain compromise of dependencies/updates;
- privilege escalation from running the desktop app as Administrator.

## 3. SSH host verification

Unknown host:

- show host, port, algorithm, fingerprint;
- require explicit trust decision;
- allow trust-once and trust-and-save where product UX supports both;
- save trusted fingerprint in the known-host implementation.

Changed host key:

- connection is blocked by default;
- show old and new fingerprints;
- never automatically overwrite known-host data;
- require a deliberate resolution workflow.

No `AcceptAllHostKeys`-style behavior is permitted in production code.

## 4. Credential storage

Do not persist these directly in SQLite or plaintext configuration:

- passwords;
- sudo passwords;
- private-key contents;
- key passphrases;
- database passwords;
- API tokens/certificate private keys introduced later.

Use a Windows secure-secret implementation behind `ISecretStore`, based on an approved Windows credential mechanism/DPAPI design.

SQLite stores only opaque references and non-secret metadata.

Private key file paths may be stored as metadata, but key file contents are not copied into ordinary application data unless a separately designed encrypted key vault is introduced.

## 5. Secret handling in memory and logs

- Minimize lifetime of sensitive strings/arrays where practical.
- Never write secrets to structured logs, exception messages, operation history, analytics, or test snapshots.
- Remote command logging must redact secret-bearing arguments/environment.
- Database/Container environment viewers must treat common secret-looking values as sensitive and require deliberate reveal.
- Clipboard copy of secrets is explicit, not automatic.

## 6. Local application privilege

ServerDesk should run as a normal Windows user.

Do not request Windows Administrator rights globally merely because remote operations need Linux sudo.

Windows elevation, if ever needed for a specific local operation, must be separately justified and scoped.

## 7. Remote privilege / sudo

Operations declare their privilege/risk needs before execution.

Rules:

- prefer normal user permissions;
- use sudo only for commands/files that need it;
- do not launch a persistent root shell as the normal implementation strategy;
- do not cache sudo password in SQLite/logs;
- do not solve write failures via global permission changes;
- preserve original file owner/group/mode during privileged replacement;
- show the user when an action needs elevated rights.

## 8. Command injection defense

All variable remote input is considered untrusted, including data originally returned by the server.

Potentially dangerous values:

- paths;
- filenames;
- service names;
- usernames;
- container/image/volume/network IDs and names;
- Git branches;
- nginx domains/paths;
- firewall addresses/ports;
- database names;
- user-entered command parameters.

Requirements:

- use typed command specifications;
- validate IDs/names to the grammar required by the target CLI where possible;
- centralize safe argument encoding;
- avoid `sh -c` unless a reviewed operation truly needs shell semantics;
- never interpolate a path/name into a compound shell command without strict quoting/validation;
- parser output is data, never executable script text.

## 9. Temporary files

Remote temp files:

- use unpredictable names;
- use a location with appropriate ownership/permissions;
- set restrictive permissions for sensitive config candidates;
- clean up on success and best-effort on failure;
- never place credential plaintext in broadly readable temp files.

Local temp files containing sensitive remote content should be avoided; prefer memory. If unavoidable, use restricted application storage and reliable cleanup.

## 10. Destructive operations

Destructive operations include, but are not limited to:

- recursive delete;
- Docker volume deletion;
- database restore overwrite/drop;
- user deletion;
- firewall changes risking SSH access;
- package removal of critical components;
- overwriting protected config without a safe backup path.

Requirements:

- explicit target display;
- consequence warning;
- stronger confirmation for high-impact resources;
- typed-name confirmation where appropriate;
- never automatic retry on ambiguous network failure;
- audit result.

## 11. Network exposure

Agentless design uses existing SSH exposure.

Do not require:

- public Docker socket;
- public PostgreSQL/MySQL/Redis ports;
- a public ServerDesk agent port.

Use SSH local forwarding for databases/internal admin services.

Future `serverdesk-agent` binds loopback by default and is reached through SSH tunneling unless a separately reviewed authenticated network design is introduced.

## 12. Firewall lockout protection

When editing firewall rules:

- identify the current SSH path/port where possible;
- warn if a candidate change may remove current access;
- validate syntax before apply when tooling allows;
- avoid applying a broad default-deny transition without explicit user awareness;
- never promise lockout prevention when network topology is unknown.

## 13. Configuration mutation safety

Critical config change pattern:

```text
read current
-> create candidate
-> backup where appropriate
-> validate candidate
-> atomic apply
-> reload/restart
-> verify health/state
-> rollback if safe and deterministic
-> audit
```

Examples include nginx and system configuration files.

If validation fails, live config remains unchanged.

If connection drops after apply and final state is unknown, surface `AmbiguousState`/equivalent rather than automatically repeating the mutation.

## 14. Terminal safety

Terminal is intentionally powerful and executes user commands; it is not restricted like GUI operations.

Still:

- sanitize terminal output only as required to prevent host WebView/script boundary escape;
- xterm data stays in the terminal renderer, not interpreted as HTML;
- do not inject terminal text into WebView DOM unsafely;
- WebView2 messaging accepts only validated message schemas;
- avoid enabling unnecessary WebView permissions/navigation.

## 15. Editor safety

Remote file content is untrusted.

- syntax highlighter/editor must not execute file content;
- preview rendered HTML/Markdown, if added, must be sandboxed/sanitized;
- external links require deliberate policy;
- diff/validator output is displayed as text, not interpreted markup.

## 16. Update and dependency security

When automatic update is introduced:

- packages/installers must be signed;
- update metadata must be authenticated;
- rollback/recovery plan required;
- do not execute unsigned downloaded binaries.

Dependencies:

- pin/manage versions;
- monitor security advisories;
- keep dependency footprint small;
- review licenses before distribution.

## 17. Telemetry and privacy

V1 should work without a cloud account.

If telemetry is later introduced:

- document exactly what is collected;
- never collect terminal contents, credentials, file contents, or remote environment secrets;
- provide appropriate consent/control;
- keep local-only mode functional.

## 18. Security testing gate

Security-sensitive features require negative tests covering relevant cases:

- malicious path/name quoting;
- host-key mismatch;
- secret redaction;
- sudo denied;
- permission denied;
- unexpected/malformed command output;
- dropped connection after request sent;
- dangerous confirmation cancellation;
- WebView message validation;
- invalid firewall/nginx candidate.

A security failure must not be converted into a silent fallback that weakens protection.
