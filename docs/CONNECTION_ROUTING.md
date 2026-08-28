# Connection routing

**English** | [Tiếng Việt](CONNECTION_ROUTING.vi.md)

ServerDesk keeps route selection separate from SSH authentication. A server profile owns its SSH endpoint and authentication method; an optional route describes how the client reaches that endpoint.

## Supported routes

- **Direct** — connect to the server endpoint directly.
- **HTTP proxy** — SSH.NET native HTTP CONNECT proxy transport.
- **SOCKS4 proxy** — SSH.NET native SOCKS4 proxy transport.
- **SOCKS5 proxy** — SSH.NET native SOCKS5 proxy transport.
- **Single-hop SSH bastion** — connect to a saved bastion profile, create a loopback-only local forward from that SSH session to the target endpoint, then establish a separate SSH session to the target through the forward.

## Security invariants

1. Proxy passwords are stored only through `ISecretStore` (Windows Credential Manager in the desktop app). SQLite stores only an opaque `SecretReference`.
2. The route editor never reads an existing proxy password back into WPF. Blank + unchanged keeps the stored secret; explicit replace/clear is a separate action.
3. Bastion and target host keys are verified independently. The target trust observation always uses the original target host and port even though the local transport socket terminates at a temporary loopback forward.
4. Bastion forwarding binds only to `127.0.0.1` and an automatically allocated local port.
5. Self-bastion, missing bastion profiles, route cycles and nested bastions fail closed in V1.
6. A bastion may itself connect directly or through HTTP/SOCKS proxy, but it may not reference another bastion.
7. Control, command, SFTP, PTY and user-created port-forward channels consume the same route-aware SSH connection plan.
8. Route creation never retries an ambiguous remote mutation. The only route-side remote state is the temporary loopback SSH forward used for a bastion and it is disposed with the connection plan.

## Persistence

SQLite schema v5 adds `server_connection_routes`. Direct routing is represented by absence of a row. Proxy route rows contain endpoint metadata, optional username and an opaque credential reference only. Bastion rows contain only the referenced server profile id.

## Test contract

CI keeps the existing SSH/SFTP/PTY/forwarding regression suite and additionally runs:

- HTTP CONNECT proxy → real OpenSSH command execution.
- SOCKS4 proxy → real OpenSSH command execution.
- SOCKS5 proxy → real OpenSSH command execution.
- single-hop bastion → target command execution with independent bastion/target host-trust observations.
- missing bastion → typed fail-closed result.
