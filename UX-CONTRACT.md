# ServerDesk UX Contract

This contract defines observable presentation behavior for ServerDesk. Business, security, architecture, and capability contracts remain authoritative when they constrain a workflow.

## 1. Shell ownership

The main window owns:

- server selection;
- persistent server identity/environment/connection context;
- grouped workspace navigation;
- global preferences;
- non-blocking shell status/error feedback;
- server profile create/edit/connect/disconnect/delete entry points.

Feature modules must not add buttons to the shell by traversing the visual tree or inserting controls at runtime. New modules register in the canonical workspace navigation catalog.

During migration, a navigation item may open an existing feature `Window`. This is a compatibility bridge, not the end-state architecture. A later slice may host appropriate feature content persistently in the workspace without changing route names.

## 2. Navigation contract

Canonical order follows `docs/UI_UX.md` and is grouped by operator intent rather than roadmap milestone.

A server-scoped item is disabled when no server is selected. Disabled navigation remains discoverable and exposes a localized reason. Capability-specific availability is enforced by the feature workflow until capability-aware shell metadata is migrated.

Clicking an enabled item performs exactly one navigation/launch action. It must not trigger a remote mutation.

Global utilities such as global dashboard and connection history may be available without a selected server.

## 3. Server context

Whenever a server is selected, shell chrome shows at minimum:

- friendly server name;
- environment label;
- connection state.

Server-scoped feature windows continue to show their server identity until they are migrated into the persistent host.

Production identity must not disappear behind a feature page or destructive confirmation.

## 4. Action hierarchy

Button presentation has two axes:

- emphasis: primary, secondary, ghost;
- intent: neutral/brand or danger.

Rules:

- one dominant safe primary action per local decision area where practical;
- routine read actions use secondary/ghost emphasis;
- destructive actions use danger intent and visual separation;
- do not place `Force kill`, `Delete`, or equivalent danger actions at the same emphasis as `Refresh`;
- busy buttons preserve dimensions and cannot double-submit.

## 5. Feedback and async states

Async screens deliberately represent:

- initial/loading;
- loaded;
- empty;
- partial/degraded;
- permission required;
- capability unavailable;
- disconnected/reconnecting;
- recoverable error;
- fatal/unsupported error;
- cancellation when applicable.

Shell-level status is non-blocking. Panel/resource errors remain inline near the affected context. Toast infrastructure is the future canonical owner for short transient acknowledgements; until that shared owner exists, do not create one-off toast systems.

## 6. Dialogs and destructive confirmation

Normal information does not use a modal.

Focused credential/trust input, destructive confirmation, and ambiguity resolution may be modal. New or touched destructive flows should use an app-owned accessible confirmation surface instead of introducing additional `MessageBox.Show` usage. Existing MessageBox call sites are migration debt and must be retired by dedicated slices.

Confirmation names the target, explains the consequence, and labels the final action with the real verb. Serious destructive actions initially focus the safer action.

## 7. Forms

- Every field has a visible label.
- Validation is inline and preserves entered values.
- The first invalid field receives focus/visibility after submit where practical.
- Secret fields are masked by default.
- Show/reveal is explicit when introduced and never logs/persists the secret.
- Cancel/back does not silently discard material edits; touched long forms need unsaved-change handling.

## 8. Search

Every touched search surface converges on:

- localized label/accessible name;
- explicit clear action when non-empty;
- immediate local clear;
- 300ms debounce for remote search unless the product contract requires otherwise;
- cancellation/ignoring of stale remote results;
- keyboard-safe input behavior.

Local filtering may update immediately when it is cheap and deterministic.

## 9. Tables

Canonical read-oriented inventories use WPF `DataGrid` with shared styling as migration reaches them.

Required behavior when applicable:

- virtualization;
- keyboard navigation;
- sorting;
- stable selection;
- readable empty/loading/error state;
- context actions and/or a details pane;
- no unbounded visual growth that freezes the UI.

The table panel owns scrolling. Do not force an entire shared page shell into fixed viewport clipping just to fit a grid.

## 10. Localization

Supported preferences: System, English, Vietnamese.

New and changed user-facing copy uses localization resources. Technical identifiers remain untranslated when translation would damage meaning. Runtime language switching must not reconnect SSH, discard the selected server, or mutate remote state.

## 11. Accessibility

Primary workflows remain keyboard-operable with visible focus. Disabled controls do not invoke handlers. Tooltips shown on disabled navigation explain unavailability. Text/status labels accompany semantic colors.

## 12. Migration rule

Large legacy drift is migrated incrementally:

1. harden shared primitives and shell;
2. migrate high-frequency flagship workflows;
3. canonicalize tables/search/command bars;
4. replace legacy modal/feedback patterns;
5. retire obsolete local styles and visual-tree injection.

A touched workflow may use a named temporary compatibility bridge, but must not create a new equivalent implementation that increases drift.
