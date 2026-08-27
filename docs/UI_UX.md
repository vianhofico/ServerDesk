# UI / UX Design Contract

## 1. UX goal

ServerDesk must feel like a native, modern Windows administration workspace rather than a web dashboard embedded in a desktop shell. The user should be able to discover common Linux operations by recognition, with advanced/raw views available when needed.

## 2. Visual language

- Windows 11 / Fluent-inspired hierarchy.
- Support `System`, `Light`, and `Dark` themes.
- Neutral surfaces; accent color reserved for selection, focus, primary action, progress, and status emphasis.
- Avoid decorative gradients/glows that reduce information density.
- Use consistent 8px-oriented spacing rhythm.
- Prefer Segoe UI/system typography.
- Icons must be consistent and recognizable; avoid mixing multiple icon styles.
- Tables, trees, split panes, breadcrumbs, tabs, command bars, and context menus are primary patterns.

## 3. Application shell

Desktop layout:

```text
+--------------------------------------------------------------+
| ServerDesk | server/environment | connection | search/actions|
+----------------+---------------------------------------------+
| Servers        | Breadcrumb / page command bar              |
| Production     +---------------------------------------------+
| Staging        |                                             |
|                | Current feature                             |
| Navigation     |                                             |
| Dashboard      |                                             |
| Explorer       |                                             |
| Terminal       |                                             |
| Processes      |                                             |
| Services       |                                             |
| Docker         |                                             |
| ...            |                                             |
+----------------+---------------------------------------------+
| optional status/activity strip                               |
+--------------------------------------------------------------+
```

The current server identity and connection state must always be visible when operating on a server.

Environment identity can use restrained labels such as `PROD`, `STAGING`, `DEV`; destructive actions on production may use stronger confirmation policies.

## 4. Navigation

Primary navigation order:

1. Dashboard
2. Explorer
3. Terminal
4. Processes
5. Services
6. Docker
7. Storage
8. Network
9. Logs
10. Scheduled Tasks
11. Git
12. Nginx
13. Security
14. Databases
15. Backups
16. Server Settings

Navigation is capability-aware. For an unavailable feature:

- hide it when it is irrelevant and undiscoverable value is low; or
- disable it and show a reason when discovery/installation guidance is useful.

Never navigate to a blank page that simply fails a command.

## 5. State model for every screen

Every asynchronous feature must deliberately design:

- initial/loading;
- loaded;
- empty;
- partial/degraded;
- permission required;
- capability unavailable;
- disconnected;
- reconnecting;
- recoverable error;
- fatal/unsupported error.

Use skeleton/progress states for normal loading. Never freeze the window or show an indefinite spinner with no cancellation for long work.

## 6. Feedback

### Toasts

Use for short non-blocking results:

- copied path;
- upload finished;
- service restarted;
- tunnel started.

### Inline status

Use for errors or warnings tied to a panel/resource.

### Modal dialogs

Use only when:

- credentials/trust require focused input;
- a destructive action requires explicit confirmation;
- the user must resolve an ambiguity before proceeding.

Normal information should not require dismissing a modal.

## 7. Risk-aware action design

### Read-only

Immediate action, no confirmation.

### Mutating but routine

Examples: restart service, start container.

- action is visible;
- confirmation only when impact is meaningful;
- show progress and final state;
- provide technical details on failure.

### Destructive

Examples: delete Docker volume, recursively delete protected directory, destructive restore.

Requirements can include:

- warning callout;
- target identity;
- consequence summary;
- typed target-name confirmation for irreversible/high-impact cases;
- destructive button visually distinct;
- no default keyboard focus on destructive button.

## 8. Explorer UX

Use a Windows Explorer mental model:

- tree/quick locations optional left pane;
- breadcrumb path;
- address entry;
- file table/grid;
- multi-select;
- right-click context menu;
- drag/drop upload;
- keyboard shortcuts;
- visible transfer/activity panel for longer operations.

Important shortcuts:

- `Ctrl+C`, `Ctrl+X`, `Ctrl+V` where semantics are safe;
- `F2` rename;
- `Delete` with policy-aware confirmation;
- `Ctrl+L` focus remote path;
- `Alt+Left/Right` history;
- `Alt+Up` parent.

Do not fake unsupported local-Windows behaviors on remote Linux filesystems; surface symlinks, ownership, and permissions explicitly.

## 9. Terminal UX

Terminal can be keyboard-heavy and should not receive app-wide shortcuts while terminal focus is active unless specifically designed.

Required visual features:

- tabs;
- new/close/reconnect session controls;
- server/path title;
- search scrollback;
- clear disconnected indicator;
- copy/paste behavior compatible with terminal expectations;
- configurable font size/family from approved monospace choices.

## 10. Tables

Processes, Services, Containers, Ports, and Logs use consistent table patterns:

- column sorting;
- filtering/search;
- column resizing;
- row selection;
- keyboard navigation;
- context menu;
- details pane instead of opening unnecessary windows;
- virtualization for large datasets.

Never rely on color alone for state; use icon/text plus color.

## 11. Dashboard

Dashboard should answer quickly:

- Is the server healthy?
- Is storage close to full?
- Is CPU/memory under pressure?
- Are important services/containers failing?
- Is the connection degraded?

Avoid a dashboard made from dozens of equally weighted cards. Prefer a clear performance summary, warning area, service/container summary, and recent activity.

## 12. Forms

- Label all inputs.
- Explain advanced fields such as jump host/proxy with short help text.
- Validate inline before submission.
- Never erase a user-entered form because connection validation failed.
- Password fields never reveal values by default; show/reveal control is explicit.
- Secret values are never copied to clipboard automatically.

## 13. Errors

User-facing pattern:

```text
Unable to reload nginx
Configuration validation failed, so the live configuration was not replaced.

[View validation output] [Open config]
```

Not:

```text
System.Exception: command returned 1
```

Technical details remain accessible in an expandable area and may include safe command/exit information, but redact secret-bearing arguments/environment.

## 14. Accessibility

- Primary workflows keyboard accessible.
- Visible focus indication.
- Sufficient contrast in all themes.
- Screen-reader-friendly labels for actionable controls.
- Do not encode Running/Stopped/Error only by green/red.
- Touch is not the primary target, but controls should not be unnecessarily tiny.
- Respect Windows text scaling where practical.

## 15. Window behavior

- Remember safe layout preferences such as window size, splitter positions, and selected theme.
- Do not remember secrets in UI state.
- Restore tabs cautiously; never automatically reconnect to production and execute commands on launch.
- Multiple server workspaces may use tabs/documents later, but resource ownership must remain obvious.

## 16. Search and command palette

Long-term command palette (`Ctrl+P` or a consciously chosen shortcut) can expose:

- switch server;
- navigate feature;
- open remote path;
- run saved safe command;
- open terminal;
- search services/containers.

Destructive operations must not be one accidental Enter away from command palette search results.

## 17. Empty/capability states

Good:

```text
Docker isn't available on this server.
ServerDesk could not find a supported Docker CLI/daemon for the current user.
[View detection details]
```

If installation guidance is added, it must be distro/version-aware and must not auto-install without explicit consent.

## 18. Design review gate

A user-facing feature is not complete until reviewed for:

- visual hierarchy;
- loading/error/empty states;
- keyboard behavior;
- dark/light themes;
- risk treatment;
- responsiveness to window resizing;
- long names/paths/values;
- accessibility labels;
- production-server identity visibility.
