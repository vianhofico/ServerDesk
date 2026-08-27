## Summary

<!-- What user-visible problem does this PR solve? -->

## Scope

- Issue:
- Milestone:
- Affected modules:

## Architecture

<!-- Explain boundary/abstraction changes. Link ADR if needed. -->

## Remote operation risk

- [ ] No remote operation added
- [ ] ReadOnly
- [ ] ElevatedRead
- [ ] Mutating
- [ ] Destructive

Safety workflow for mutations/destructive changes:

<!-- preview / confirmation / backup / validation / execute / verify / rollback -->

## Security checklist

- [ ] No secret committed, logged, or stored in SQLite/plaintext
- [ ] SSH host verification is not weakened
- [ ] Remote user-controlled values do not create a shell-injection path
- [ ] Privilege/sudo is scoped to the required operation
- [ ] Destructive operations are not automatically retried after ambiguous failure
- [ ] No new public management/database/Docker socket exposure

## UX checklist

- [ ] Loading state
- [ ] Empty state (if applicable)
- [ ] Error state
- [ ] Permission/capability unavailable state
- [ ] Disconnect/reconnect state (if remote)
- [ ] Cancellation for long operation
- [ ] Light/dark/system theme reviewed
- [ ] Keyboard primary workflow reviewed
- [ ] Production/server identity remains clear

## Compatibility

Certified targets exercised/affected:

<!-- e.g. Ubuntu 24.04, Ubuntu 26.04, Debian 13 -->

## Testing

Commands/tests run:

```text

```

Results:

<!-- Include integration/fixture/UI evidence as applicable. -->

## Screenshots / recordings

<!-- Required for meaningful UI changes. -->

## Known limitations / follow-ups

<!-- Be explicit; create/link issues for deferred scope. -->

## Agent self-review

- [ ] Read `AGENTS.md` and relevant docs
- [ ] Inspected complete diff
- [ ] No unrelated cleanup
- [ ] No UI -> concrete SSH coupling
- [ ] No distro-specific command logic in ViewModels
- [ ] Errors mapped to typed/user-safe errors
- [ ] Docs/support matrix updated if contracts changed
- [ ] CI is green
