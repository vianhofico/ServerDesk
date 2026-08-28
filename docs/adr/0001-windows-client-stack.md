# ADR 0001 — Windows client stack: .NET 10 + WPF

**English** | [Tiếng Việt](0001-windows-client-stack.vi.md)

- Status: Accepted
- Date: 2026-08-27

## Context

ServerDesk is intentionally a Windows-first desktop product. It needs native Windows windowing/input/accessibility integration, strong .NET networking support, long-lived desktop process behavior, and the ability to host WebView2 for terminal/editor surfaces.

## Decision

Use:

- .NET 10;
- WPF for the desktop shell;
- MVVM-oriented separation;
- WebView2 only for components that benefit from web rendering such as xterm.js/advanced editors;
- native WPF controls/layout for the main application UI.

## Consequences

Positive:

- strong Windows desktop integration;
- direct access to Windows security/platform APIs;
- mature data binding/control ecosystem;
- straightforward distribution as a Windows desktop application;
- WebView2 can be isolated to terminal/editor use instead of making the whole product a web wrapper.

Trade-offs:

- client is not cross-platform by default;
- WPF-specific UI code must stay in `ServerDesk.App`/UI modules;
- CI for the desktop shell uses Windows runners;
- future macOS/Linux clients would require a deliberate UI-platform decision rather than reusing WPF.

## Revisit when

Revisit only if ServerDesk explicitly changes from Windows-first to a cross-platform desktop product, or if a future Windows UI platform materially improves the product enough to justify a controlled migration.
