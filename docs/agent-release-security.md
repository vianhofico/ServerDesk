# Agent release signing and lifecycle security

**English** | [Tiếng Việt](agent-release-security.vi.md)

This document completes the release-metadata design required by ADR 0004 before ServerDesk may install or update `serverdesk-agent`.

## Distribution trust model

ServerDesk accepts an agent release only when all of these checks succeed, in order:

1. The manifest uses a known pinned signing-key id from the application release package.
2. The canonical manifest signature verifies with ECDSA P-256 and SHA-256.
3. Authenticated manifest metadata passes schema, product, protocol, Linux platform, architecture, file-name, size, digest and timestamp policy.
4. The artifact byte length exactly matches the authenticated manifest.
5. The artifact SHA-256 digest exactly matches the authenticated manifest.
6. Only then may ServerDesk create an install or update lifecycle plan.

Any failure is fail-closed. No install/update mutation is allowed from an unverified manifest or artifact.

## Signing key custody

Only public ECDSA P-256 SubjectPublicKeyInfo values belong in the ServerDesk application package. The immutable trust store maps a bounded key id to a pinned public key and rejects unknown ids.

The corresponding private signing keys are release-engineering secrets. They must be generated and held outside this repository and outside the `serverdesk-agent` runtime, preferably in a hardware-backed or managed signing service. Private keys must never be committed, embedded in ServerDesk, copied to managed servers, or placed in agent manifests.

Key rotation is performed by shipping a ServerDesk build that contains the next trusted public key id before releases are signed exclusively by that key. Removing an old key is a client release decision, not an agent-provided instruction.

## Canonical signed manifest

The signature covers deterministic UTF-8 lines in this exact order:

```text
schema=<integer>
product=serverdesk-agent
version=<major.minor.patch>
protocol-major=<integer>
protocol-minor=<integer>
platform=linux
architecture=<x64|arm64>
artifact-file=serverdesk-agent-linux-<architecture>
artifact-length=<bytes>
artifact-sha256=<64 lowercase hex characters>
released-unix-seconds=<UTC unix seconds>
```

String values containing CR, LF or NUL are rejected before signature verification so a field cannot inject canonical lines. Semantic interpretation of authenticated version/platform/digest metadata happens only after a valid signature.

The M8 baseline accepts canonical numeric `major.minor.patch` releases only. Pre-release/build metadata is intentionally outside this contract.

## Artifact integrity

The authenticated manifest binds both byte length and SHA-256. A signature by itself does not make an artifact trusted: ServerDesk must hash the exact artifact bytes and compare both values before planning install/update.

The baseline maximum artifact size is 256 MiB. This is a safety bound, not a target package size.

## Fixed ownership boundary

Lifecycle planning never accepts a path, service unit or command from the signed manifest. The only resources owned by the ServerDesk agent installation flow are:

- `/opt/serverdesk-agent/serverdesk-agent`
- `/var/lib/serverdesk-agent`
- `/var/cache/serverdesk-agent`
- `/etc/systemd/system/serverdesk-agent.service`
- systemd unit `serverdesk-agent.service`

A clean uninstall may remove only those agent-owned resources. It must not remove or rewrite SSH configuration, firewall rules, Docker configuration/data, unrelated systemd units, application logs/data, user files or ServerDesk server profiles.

## Lifecycle planning

M8.8 produces reviewable plans but intentionally performs no remote mutation.

Install plans stage the already verified artifact, verify the remote staged digest/length, install the fixed binary/unit, reload systemd, enable/start the fixed service, then require tunneled health/version verification.

Update plans require the authenticated target version to be strictly newer than the installed version. Same-version replacement and downgrade are rejected. The execution slice may keep one bounded previous-binary rollback copy, but rollback must never activate an unauthenticated artifact.

Uninstall plans stop/disable the fixed unit, remove only the fixed owned resources, reload systemd and verify the unit/resources are absent.

Every step carries an explicit `OperationRisk` and a post-action verification requirement. M8.9 must preserve those gates and treat connection loss/timeout during mutation as ambiguous state requiring re-observation before retry.

## Release-generation requirements

The release pipeline that eventually publishes `serverdesk-agent` must:

1. build the Linux artifact for the declared architecture;
2. calculate exact byte length and SHA-256;
3. create the canonical manifest using those values;
4. sign the canonical bytes with the external ECDSA P-256 release key;
5. publish artifact, manifest, key id and DER signature together;
6. never publish the signing private key.

The later install/update executor may download or receive these files, but it must call the verifier before any server mutation.
