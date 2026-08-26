# Save Editor GUI Framework — V1 Implementation Plan

## 1. Objective

Build a reusable Avalonia/.NET 10 framework for save editors on Windows and Linux. A new editor should be able to run:

```text
dotnet new save-editor
```

and reach a themed, testable editor shell with sample field cards in under an hour.

The framework is save-editor-first. It provides reusable UI, services, and safety workflows; it does not replace Avalonia and does not own game formats, crypto, or editor-specific rules.

## 2. Fixed v1 scope

### Included

- `SaveEditor.Ui` NuGet package
- `SaveEditor.Template` NuGet template package
- `SaveEditor.Ui.Gallery` sample/control catalog
- .NET 10 and Avalonia on Windows/Linux
- Catppuccin Mocha dark default and Catppuccin Latte light theme
- Per-editor accent default, user accent override, and persisted accent choice
- Embeddable `EditorShell` with menu bar, header, sidebar, content, and status-bar slots
- Typed field cards, virtualization, section toolbar, dialogs, settings, recents, history, and safe file workflow
- Generated starter app with a working sample document and replaceable codec seam
- Unit, headless UI, template smoke, screenshot, Windows, Ubuntu, and Wayland validation
- 0BSD framework source with third-party notices

### Explicitly out of scope

- macOS development or native macOS menus
- Automatic saving or draft-content persistence
- Telemetry, crash reporting, or network activity
- Tabs/multiple active documents
- Framework-owned raw/advanced-data view
- Domain-specific bulk actions
- Arbitrary custom palettes or a third theme mode
- Dedicated byte-array, bitmask, or date controls in the initial control set

## 3. Repository structure

```text
src/SaveEditor.Ui
src/SaveEditor.Template
samples/SaveEditor.Ui.Gallery
tests/SaveEditor.Ui.Tests
tests/SaveEditor.Ui.HeadlessTests
tests/SaveEditor.Template.Tests
eng/
mockup/index.html
README.md
PLAN.md
LICENSE
THIRD-PARTY-NOTICES
```

The repository should use a central solution and build properties file. The first stable release is `1.0`; public breaking changes require a major version.

## 4. Public architecture

### Shell

Implement `EditorShell` as an embeddable `UserControl`. The consuming app owns its `Window`, lifecycle, size, and platform integration.

The shell exposes named content slots for:

- Branding
- Header actions
- Sidebar
- Content
- Status bar
- Menu extensions

The framework owns the shell grid, spacing, focus behavior, default commands, and accessibility behavior.

Core menus are always available in-window:

- `File`: Open Save, Open Folder, Recent, optional Folder slots, Save As, Overwrite + Backup, Reload, Close, Exit
- `Edit`: Undo, Redo
- `View`: Appearance > Themes, Appearance > Accent, search focus, section shortcuts
- `Help`: About/Credits and safety/manual-testing documentation

Header actions are limited to Open Save, Save As, Undo, and Redo. The sidebar contains detected-save status and section navigation; recent files and folder slots are menu-only, with recent files also shown in the welcome state.

### Sections and content

Register sections through data-driven descriptors containing a stable key, title, subtitle, visibility predicate, optional icon, and body template. Supported body modes are `FieldList`, `ReadOnly`, and `Custom`.

The framework supplies an optional `SectionToolbar` for search, filters, bulk-action slots, and compatibility toggles. Editors provide the filtering and action semantics.

### Field editing

Provide typed field descriptors and view-models for:

- Text
- Numeric values with invariant parsing, validation, and optional spinner affordance
- Boolean values
- In-memory or asynchronous choice/autocomplete providers
- Read-only values
- Warning/help text

`FieldCard` owns labels, paths, warning presentation, accessibility, and Apply actions. Custom editors use templates/content presenters rather than subclassing. `FieldList` virtualizes by default.

Typing creates an in-memory pending draft. Per-field Apply creates one history entry; Apply All creates one transactional section entry.

### MVVM and composition

Use `CommunityToolkit.Mvvm`. Keep plain constructor composition as the default; offer optional `Microsoft.Extensions.DependencyInjection` integration without making DI mandatory.

Use async, cancellation-aware interfaces for file operations and all `IUserInteraction` methods. A thin Avalonia drag/drop adapter forwards paths to the same view-model open workflow used by menus and pickers.

## 5. Theme system

Generate and pin the official Catppuccin Latte/Mocha palette source revision. Include its source URL and attribution in `THIRD-PARTY-NOTICES`.

Expose only semantic resources to application views:

```text
WindowBackground, PanelBackground, CardBackground, InputBackground
Foreground, MutedForeground, SubtleForeground
Border, FocusRing, Primary, Danger, Warning, Success
```

Raw palette names remain internal. Every promised control gets a framework-owned default style; Avalonia theme infrastructure may provide behavior, but Fluent-default visuals are not the visual contract.

Support exactly two modes:

- Mocha/Dark, default
- Latte/Light

`View > Appearance > Themes` changes the mode immediately and persists it. `View > Appearance > Accent` exposes all 14 Catppuccin accents. The framework supplies a default accent, an editor may override that default, and a user selection wins on later launches. A reset action returns to the editor default. Palette roles use contrast-safe semantic mappings rather than forcing weak Latte accent text.

Inter is the default font and must be easy to replace. English is the only shipped culture, but framework strings use resource keys. Layout should respect RTL flow direction without making RTL a v1 release gate.

## 6. State, settings, and history

Track pending drafts separately from committed document changes:

- Pending edits stay in memory while typing and survive section navigation.
- Committed edits make the document dirty and enter `EditHistory`.
- The title gains ` *`; the sidebar and status bar report dirty state.
- Reload, open, close, and exit guard against pending or unsaved changes.

Use revision-based dirty tracking with a saved-baseline revision. Cap history at 1,000 committed operations by default; do not persist history.

Store versioned settings at:

```text
LocalApplicationData/<ApplicationId>/settings.json
```

Require an explicit stable `ApplicationId`. Persist theme, accent, up to 10 file recents, up to 10 folder recents, last-selected section, and window size. Do not persist window position or document drafts. Writes are atomic and fail-soft; migrations handle known schema versions, while malformed settings are backed up/replaced with defaults without blocking startup.

Recent files are case-insensitively deduplicated. Confirmed-missing paths are pruned automatically; temporarily inaccessible paths are retained. Folder slots are optional and provider-driven.

## 7. Safe file workflow

Keep format logic in an application-provided codec boundary:

```text
ISaveCodec<TDocument>
  Decode / Open
  Serialize
  Validate
  Format metadata and picker filters
  Unknown-data preservation capability
```

Provide a registry/detector for one or more codecs. Ambiguous detection produces a confirmation flow; unsupported files fail safely.

`SafeFileWorkflow` owns:

1. Async open, detect, decode, and validation.
2. Save As as the default write path; `Ctrl+S` always means Save As.
3. OS picker overwrite confirmation without duplicating it. Custom picker implementations declare whether they provide confirmation; the framework supplies a fallback when they do not.
4. Full validation immediately before writing. Errors block; warnings show the first eight plus a count and require explicit continuation.
5. Symlink/reparse-point refusal, no privilege elevation, and no automatic permission changes.
6. UTC timestamped sibling backup before explicit Overwrite + Backup.
7. Same-directory temp write, flush, and atomic replacement.
8. Original-file preservation and temp cleanup on failure.
9. External-change detection using watcher hints plus an authoritative pre-action metadata/hash check.
10. Progress, cancellation, definitive status, and close-operation coordination.

Unknown data must be preserved whenever a codec declares that capability. A codec that cannot preserve it must surface a warning requiring explicit confirmation.

## 8. Dialogs and feedback

Ship a themed default implementation behind `IUserInteraction` for storage pickers, confirmations, messages, and read-only documents. Destructive actions require verb-specific accept labels; generic `OK` is not used for destructive choices.

Keep the status bar as the canonical outcome channel, with full-sentence status, current path, last backup, progress, and cancellation. Add a persistent accessible inline announcement region for important errors and outcomes instead of transient toasts.

Include a reusable themed About/Credits dialog with consumer slots for app identity, credits, and licenses. Raw/advanced data presentation remains editor-owned.

## 9. Template and gallery

`dotnet new save-editor` generates:

- .NET 10/Avalonia app and solution
- Framework-owned themed shell and in-window menus
- Welcome state and one populated example section
- Text, numeric, boolean, choice, warning, Apply, Apply All, undo/redo, and dirty-state examples
- In-memory document, codec/workflow seams, settings/recents, accent/theme configuration, and drag/drop adapter
- Sample `IUserInteraction` composition
- README explaining replacement points and safe workflow guarantees

The gallery starts with the full shell and includes token swatches, both themes, controls, dialogs, workflow states, keyboard/accessibility notes, and custom-section examples. It is the visual regression surface and must not require network access.

## 10. Verification plan

### Unit tests

Cover settings migration/failure, recent pruning/deduplication, revision dirty state, history limits, validation aggregation, codec detection, unknown-data capability, path safety, symlink/reparse rejection, backup naming, atomic workflow ordering, cancellation, and external-change guards.

### Headless UI tests

Use xUnit and Avalonia headless testing for shell commands, keyboard shortcuts, menu routing, focus/tab order, section selection, pending/committed transitions, dialogs, and accessible names.

### Visual regression

Use Ubuntu as the golden screenshot baseline. Cover:

- Mocha and Latte welcome states
- Populated shell
- Dirty/pending state
- Validation banner
- Read-only and custom section bodies
- Appearance menus and dialogs

Run behavioral/headless tests on Ubuntu and Windows. Review screenshot changes deliberately.

### Template and platform smoke

Build and run the generated template in a smoke test, exercise its sample fields and theme settings, and validate both NuGet packages.

Provide a manually invoked real-Wayland job that runs the gallery and checks file drop, folder drop, menus, dialogs, resizing, theme switching, and keyboard behavior. Headless tests inject drop paths separately so workflow correctness is not dependent on compositor availability.

Accessibility is a release gate: keyboard operation, visible focus, logical tab order, accessible names/descriptions, WCAG AA body text, and Windows screen-reader smoke coverage.

## 11. Delivery phases

### Phase 0 — Repository and contracts

- Initialize solution, license, notices, build properties, package metadata, and README.
- Define public interfaces and test fixtures before control implementation.
- Pin palette source and establish semantic resource names.

### Phase 1 — Theme and primitives

- Implement Latte/Mocha resources, accent precedence, font override, semantic styles, and custom default control themes.
- Build the gallery token/control pages.
- Add contrast and resource-resolution tests.

### Phase 2 — Shell

- Implement `EditorShell`, menus, slots, section descriptors, welcome state, sidebar, status bar, responsive behavior, keyboard commands, and drag/drop adapter.
- Add headless navigation/focus tests.

### Phase 3 — Editing surface

- Implement typed field descriptors, `FieldCard`, virtualized `FieldList`, `SectionToolbar`, custom editor templates, pending drafts, Apply/Apply All, validation, and history.
- Add representative gallery sections and screenshots.

### Phase 4 — Services and safety

- Implement settings/recents, codec registry/detection, `IUserInteraction`, dialogs, `SafeFileWorkflow`, external-change guards, progress/cancellation, and status announcements.
- Add failure-path and filesystem safety tests.

### Phase 5 — Template and adoption

- Build the generated starter app and template package.
- Add template smoke tests, replacement-point documentation, and package-install validation.

### Phase 6 — Release hardening

- Add Ubuntu/Windows CI, screenshot review workflow, manual Wayland job, accessibility checklist, third-party notices, and package metadata.
- Run the mockup/design review against the gallery and freeze the `1.0` public contract.

## 12. Definition of done

V1 is complete when both packages pack and install, the generated app runs without manual framework wiring, the gallery demonstrates every promised control in Latte and Mocha, the safe workflow and failure paths are tested, the Ubuntu/Windows checks pass, the Wayland smoke checklist is executable, accessibility gates pass, and the README is sufficient for a new editor author to replace the demo codec and start editing real saves.
