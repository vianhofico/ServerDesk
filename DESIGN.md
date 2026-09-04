# ServerDesk Design System

## Product register

ServerDesk is a native Windows administration workspace for developers, DevOps engineers, sysadmin-lite users, and learners who manage Linux servers through SSH-first workflows. The interface should feel like a focused Windows operations console, not a web analytics dashboard transplanted into WPF.

## Visual thesis

**Quiet operational chrome, strong server context, dense technical work surfaces.**

The UI borrows the familiarity of Windows 11 / Fluent interaction patterns while keeping Linux concepts explicit. Decoration is restrained so paths, ports, process IDs, service states, logs, capabilities, and risk signals remain the visual priority.

The signature element is the **server context strip**: whenever a server-scoped workflow is active, server name, environment, endpoint/identity, and connection state remain visible and stable. Users should never have to infer which server a command applies to.

## Typography

- UI / body: `Segoe UI Variable Text, Segoe UI`
- Display / section headings: `Segoe UI Variable Display, Segoe UI`
- Technical values (paths, hostnames, ports, PIDs, commands, hashes, logs): `Cascadia Mono, Consolas`
- Use sentence case for actions and headings.
- Prefer concise labels over explanatory button copy; put detail in descriptions/tooltips/callouts.

## Spacing and geometry

Use an 8px-oriented rhythm with 4px half-steps where density requires it.

- Page gutter: 24–32px
- Panel/card padding: 16–24px
- Control height: 34–36px default
- Compact controls: 30–32px
- Radius: 6px controls, 8–10px panels, 12px only for prominent empty states
- Border: 1px semantic separator; avoid stacking borders around every nested region
- Shadows: rare and shallow; use borders/surface contrast for structure

## Semantic palette

Runtime color ownership lives in `src/ServerDesk.App/Themes/Light.xaml` and `Dark.xaml`. This document defines intent, not a second independent palette.

### Light

- Canvas: `#F5F6F8`
- Surface: `#FFFFFF`
- Secondary surface: `#F0F2F5`
- Primary text: `#1B1B1F`
- Accent: `#2563EB`
- Danger: `#C42B1C`

### Dark

- Canvas: `#15171A`
- Surface: `#1F2226`
- Secondary surface: `#292D32`
- Primary text: `#F5F6F7`
- Accent: `#6EA8FE`
- Danger: `#FF8A7A`

Semantic state colors must never be the only carrier of meaning. Pair color with text, iconography, or both.

## Layout system

Primary desktop shell:

```text
+------------------------------------------------------------------+
| Brand | current server / environment / connection | preferences  |
+----------------------+-------------------------------------------+
| Servers              | feature/page command area                 |
| server list          +-------------------------------------------+
|                      |                                           |
| Workspace navigation | current workflow                          |
| grouped by job       |                                           |
|                      |                                           |
+----------------------+-------------------------------------------+
```

Navigation groups encode real operator tasks rather than roadmap milestones:

- Overview
- Work
- Operate
- Deploy
- Admin
- Server

A selected server is context, not a giant collection of feature buttons. Server lifecycle/profile actions stay near the server identity. Feature entry points live in workspace navigation.

## Components and ownership

Canonical shared WPF presentation resources are merged from `Styles/Controls.xaml`.

Shared owners include:

- primary, secondary, ghost, and danger buttons;
- field text inputs;
- surface cards;
- shell navigation buttons;
- inline information/error callouts.

Feature windows may retain local styles while they are being migrated, but touched flows should move to shared owners instead of introducing another equivalent local style.

Theme dictionaries own values. Shared styles own presentation behavior. Feature XAML owns composition only.

## Interaction states

Every enabled pointer target must have intentional hover, pressed, keyboard focus, and disabled behavior. Keyboard focus must remain visible in both themes.

Busy states must preserve control geometry. Long work needs cancellation when technically possible. Destructive actions are visually separated from safe primary actions and are never the default focus in a serious confirmation.

## Tables and technical work surfaces

Processes, services, containers, ports, logs, files, databases, and similar inventories should converge on one dense table language:

- stable headers and row height;
- sorting where supported;
- search/filter with explicit clearing;
- keyboard row navigation;
- selection plus details pane;
- virtualization for large data sets;
- empty/loading/degraded states that do not collapse layout.

Do not replace operational tables with large card grids merely for visual novelty.

## Localization and accessibility

- English and Vietnamese are first-class runtime locales.
- Layouts must tolerate longer Vietnamese strings and Windows text scaling.
- New user-facing strings belong in localization resources.
- Primary workflows target WCAG 2.2 AA principles: semantic native WPF controls, accessible names, visible focus, sufficient contrast, and keyboard operation.
- Never encode Running/Stopped/Error only with green/red.

## Motion

Motion is functional and sparse: progress, connection transitions, and deliberate state change. Avoid ambient animation, decorative gradients, glow effects, or large entrance sequences; they reduce trust and density in an operations tool.
