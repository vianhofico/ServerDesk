# Agent lifecycle execution and recovery

**English** | [Tiếng Việt](agent-lifecycle-execution.vi.md)

This document describes the M8.9 execution boundary for installing, updating, inspecting and cleanly uninstalling the optional `serverdesk-agent`. It extends the signed-release rules in `agent-release-security.md`; it does not replace them.

## Preconditions

Install and update accept only a `VerifiedAgentArtifact` produced after the signed manifest, artifact length and SHA-256 have already been authenticated. The executor validates the `AgentLifecyclePlan` again before any remote read or mutation. A forged service unit, path/resource set, operation/version pair or modified verified-artifact byte array fails before remote lifecycle work begins.

The remote Linux account used for SFTP staging must use the conservative account syntax `[a-z_][a-z0-9_-]{0,31}`. This prevents an account value from becoming an unsafe owner argument. Lifecycle commands never accept a caller-provided executable, shell fragment, destination path, systemd unit or service port.

## Fixed remote layout

The lifecycle executor owns only these persistent resources:

- `/opt/serverdesk-agent/serverdesk-agent`
- `/var/lib/serverdesk-agent`
- `/var/cache/serverdesk-agent`
- `/etc/systemd/system/serverdesk-agent.service`
- systemd unit `serverdesk-agent.service`

The fixed agent port is `41371`, but the agent listener itself remains structurally loopback-only. ServerDesk reaches it through the existing ephemeral SSH local forward.

Staging is bounded beneath `/var/cache/serverdesk-agent/staging/<plan-id>/`. The cache and staging roots are root-owned traverse-only directories (`0711`), while the exact per-operation directory is created as `0700` for the validated SSH account so SFTP can write only that operation's files. The systemd service intentionally does not claim `CacheDirectory=serverdesk-agent`; its `DynamicUser` therefore cannot change lifecycle-staging ownership. Agent runtime state remains isolated through `StateDirectory=serverdesk-agent`.

Update keeps at most one fixed rollback copy at `/var/cache/serverdesk-agent/serverdesk-agent.previous`. No timestamped or caller-named rollback files are created.

## Typed privileged command boundary

Privileged mutations use typed `RemoteCommandSpec` argv with executable `sudo`, first argument `-n`, explicit `OperationRisk` and `LC_ALL=C`. There is no `/bin/sh -c`, `bash -c`, generic command input, interactive sudo password transport or manifest-provided command.

Before activation, ServerDesk re-reads the staged file using fixed read-only `stat -c %s -- <fixed-path>` and `sha256sum -- <fixed-path>` commands and requires exact authenticated byte length and SHA-256. The installed binary and fixed unit are also re-read after copy.

Service mutations go through the existing `IServerServiceManager`, whose default systemd implementation uses typed `sudo -n systemctl ... -- serverdesk-agent.service` commands and post-action state verification.

## Install

1. Confirm the fixed unit is absent.
2. Confirm remote architecture matches the authenticated artifact architecture.
3. Create the fixed staging directories and upload the already verified binary plus the ServerDesk-owned unit file through SFTP.
4. Re-verify staged binary/unit length and SHA-256 remotely.
5. Install only the fixed binary and fixed unit paths.
6. Reload systemd, enable and start `serverdesk-agent.service`.
7. Open the SSH-controlled local tunnel and require compatible negotiation, healthy agent response and the authenticated target version.
8. Remove the per-operation staging directory only after known successful verification.

A healthy version mismatch is a failure. An unreachable/ambiguous post-mutation state is not treated as success.

## Update and rollback

Update requires the exact healthy version used when the plan was created and a strictly newer authenticated target. Same-version replacement and downgrade are rejected.

Before mutation, ServerDesk records the current binary length/SHA-256. After staging the target, it copies the current fixed binary to the one fixed rollback path and verifies that the rollback copy exactly matches the pre-update bytes. It then activates the verified target and restarts the fixed service.

Rollback is attempted only after a deterministic known post-swap failure. Before restarting the restored service, ServerDesk verifies that the restored binary exactly matches the captured previous integrity. A successful rollback must also restore the previous healthy version through the SSH tunnel.

If restart, transport, command completion, cancellation or health verification becomes uncertain after mutation, ServerDesk returns `Ambiguous`, retains the rollback copy, and does **not** issue a blind retry or automatic rollback. The operator must refresh status before deciding what to do next.

## Status

Status combines fixed systemd state with tunneled agent negotiation and health. It distinguishes:

- `Absent`: fixed unit not installed;
- `Healthy`: active, enabled, compatible and healthy through the tunnel;
- `Degraded`: unit or reachable agent is unhealthy/incomplete;
- `Incompatible`: protocol major version mismatch;
- `Unreachable`: service is known but tunnel/agent cannot be reached reliably;
- `Ambiguous`: ServerDesk cannot prove the fixed service state.

Runtime versions are accepted as canonical `major.minor.patch`, or .NET's four-part form only when the fourth component is exactly zero. Health and negotiated runtime versions must agree.

## Clean uninstall

Uninstall stops/disables only `serverdesk-agent.service`, removes only the fixed unit, binary, bounded rollback copy, state directory and cache directory, reloads systemd, then verifies each fixed path and the unit are absent.

It never removes or rewrites SSH configuration/keys, firewall rules, Docker configuration/data, unrelated systemd units, user files, application logs/data or ServerDesk profiles.

## Ambiguous-state operator rule

An ambiguous result is a safety gate, not a retry hint. Refresh lifecycle status first. Do not retry install/update/uninstall from stale assumptions. If a retained rollback copy exists after an ambiguous update, leave it untouched until the installed binary/service state has been re-observed.

## Operator requirements

- Linux with systemd and the fixed utilities used by the executor (`uname`, `stat`, `sha256sum`, `install`, `rm`, `test`, `systemctl`, `sudo`).
- Existing ServerDesk SSH host-trust/authentication path must already work.
- The SSH account must be permitted by sudoers to execute the narrowly required fixed lifecycle mutations non-interactively; ServerDesk never forwards a sudo password.
- No firewall rule or public agent port is required. The baseline listener remains loopback-only and is accessed through SSH forwarding.
