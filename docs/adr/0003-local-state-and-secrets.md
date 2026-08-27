# ADR 0003 — Local state and secret storage

Status: Accepted

## Context

ServerDesk needs local persistence before M1 can create SSH profiles. The desktop app must remember non-secret metadata while ensuring passwords, private-key passphrases, sudo credentials and future database credentials are not stored in plaintext SQLite or JSON files.

## Decision

- Store structured, non-secret application metadata in a versioned SQLite database under `%LOCALAPPDATA%\ServerDesk\data`.
- Store lightweight UI preferences such as System/Light/Dark theme in `%LOCALAPPDATA%\ServerDesk\settings.json` using atomic replacement.
- Represent credentials in domain/application models only through an opaque `SecretReference`.
- Store actual secret values with Windows Credential Manager through `ISecretStore`.
- Keep persistence and Windows implementations behind application ports so future transports and platform implementations do not leak into the domain or WPF view models.
- Record safe operation summaries in SQLite, never raw commands or secret-bearing payloads.

## Consequences

- M1 can add SSH authentication without changing the `ServerProfile` persistence model.
- Copying `serverdesk.db` or `settings.json` does not copy credential values.
- Windows Credential Manager availability is a platform requirement for the Windows client.
- Database migrations are explicit and versioned; a database newer than the client is rejected rather than silently downgraded.
- Secret references are identifiers, not encryption containers, and must never be treated as credential values.
