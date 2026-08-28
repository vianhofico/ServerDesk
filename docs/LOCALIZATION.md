# Localization Architecture and Migration

**English** | [Tiếng Việt](LOCALIZATION.vi.md)

English is the technical source of truth if the two documents accidentally diverge.

## 1. Scope

ServerDesk V1 localization is local and deterministic. Supported language preferences are:

- `System`
- `English` (`en`)
- `Vietnamese` (`vi`)

`System` resolves Vietnamese Windows UI cultures (`vi`, `vi-*`) to Vietnamese. English and every unsupported system culture resolve to English.

No cloud translation service, translation database, CMS, or runtime auto-translation is part of V1.

## 2. Dependency boundaries

Localization follows the existing dependency direction:

```text
Domain
  no presentation localization

Application.Settings
  language preference enum
  language resolution rules
  settings persistence contract

Platform.Windows
  system UI culture detector
  JSON persistence adapter

ServerDesk.App
  WPF localization service
  English/Vietnamese ResourceDictionaries
  runtime resource switching
  localized presentation strings
```

Domain and infrastructure errors remain typed. Presentation maps user-facing states/messages to localized resources rather than pushing English or Vietnamese strings into Domain.

## 3. Resource policy

English is the fallback resource set. At runtime ServerDesk always loads English resources and, when Vietnamese is active, loads Vietnamese resources as an override.

Resource keys must exist in both languages. Missing lookups fail safely by returning the resource key rather than crashing the UI.

Parameterized messages use complete format strings such as:

```text
Unable to connect to {0}.
Không thể kết nối tới {0}.
```

Do not concatenate translated sentence fragments when a complete format resource is practical.

Technical identifiers such as `SSH`, `SFTP`, `Docker`, `systemctl`, paths, executable names, protocol names, API/type identifiers, raw terminal output, and raw server logs are not translated when translation would alter technical meaning.

## 4. Preference persistence

Language preference is stored as a stable configuration value:

- `system`
- `en`
- `vi`

Localized display strings such as `Tiếng Việt` are never persisted as configuration values.

Existing settings files that predate localization and contain only theme preference remain valid; missing language resolves to `System`.

## 5. Runtime switching

The WPF localization service changes the effective culture and swaps localization resource dictionaries at runtime. Language changes do not recreate remote sessions, reconnect SSH, or discard the current server workspace.

UI using `DynamicResource` updates immediately. Presentation models that expose localized display choices refresh those choices on `LanguageChanged`.

## 6. Migration policy

Localization is incremental, not a giant translation rewrite.

1. Phase 1: foundation, resources, preference, fallback, startup resolution, selector, tests.
2. Phase 2: shell/navigation/common dialogs and shared states.
3. Phase 3: whenever a feature is touched, migrate its user-facing text as part of that feature change.
4. Phase 4: scan and remove remaining hard-coded user-facing strings after major roadmap milestones stabilize.

After Phase 1 is merged, new user-facing UI text must be added through localization resources. Existing hard-coded text may remain until its migration slice, but new code must not add to that debt without a documented technical reason.

## 7. UI requirements

Vietnamese text is often longer than English. New localized UI should prefer flexible layouts, wrapping where appropriate, resizable dialogs, and controls that do not clip translated labels at normal Windows text scaling.

Do not rely on a fixed width that is only sufficient for English when a flexible layout is practical.

## 8. Testing gate

Localization changes require tests appropriate to the touched slice, including:

- explicit English and Vietnamese resolution;
- `System` culture resolution;
- unsupported culture fallback to English;
- language preference persistence/backward compatibility;
- English/Vietnamese resource-key parity;
- parameterized resource formatting;
- missing-resource safe fallback;
- runtime switching behavior where presentation state is involved;
- layout review for materially longer Vietnamese labels.

The normal ServerDesk build, format, unit-test, and integration gates still apply. Localization must not bypass or weaken milestone prerequisites.
