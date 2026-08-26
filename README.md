# Save Editor GUI Framework

This repository is the v1 plan and visual starting point for an Avalonia/.NET 10 framework for save editors on Windows and Linux. macOS is out of scope.

Open the [interactive shell mockup](mockup/index.html) directly in a browser. It demonstrates both Catppuccin themes, persisted accent selection, menu-driven situational actions, field cards, pending/unsaved states, validation messaging, safe-save dialogs, and the welcome state. It performs no real file I/O.

For the phased implementation plan, public architecture, safety contract, testing matrix, and definition of done, see [PLAN.md](PLAN.md).

## Product contract

The framework is save-editor-first, with generic reusable primitives rather than a replacement for Avalonia. The consumer owns its `Window`, codec, document model, validation rules, and editor-specific sections. The framework owns the reusable shell, styling, interaction contracts, and safe workflow.

### Packages and repository layout

```text
src/SaveEditor.Ui                 NuGet package: shell, themes, controls, services
src/SaveEditor.Template            NuGet template package: dotnet new save-editor
samples/SaveEditor.Ui.Gallery      interactive control and shell gallery
tests/SaveEditor.Ui.Tests          unit tests for services and public contracts
tests/SaveEditor.Ui.HeadlessTests  Avalonia headless shell/keyboard tests
tests/SaveEditor.Template.Tests    generated-project smoke tests
docs/                              optional future expansion; README is the v1 guide
eng/                               pack, snapshot, and validation scripts
mockup/index.html                  self-contained design prototype
```

The first complete scope is released as `1.0`. Framework source is 0BSD, with a `THIRD-PARTY-NOTICES` file for Catppuccin, Inter, Avalonia, and other redistributed assets.

## Architecture decisions

- `EditorShell` is an embeddable `UserControl`; applications retain ownership of their `Window`.
- The shell exposes named slots for branding, header actions, sidebar, content, status bar, and menu extensions.
- Core menus are always present and in-window: `File`, `Edit`, `View`, and `Help`. Editors may add domain items.
- Header actions are `Open Save`, `Save As`, `Undo`, and `Redo`. Overwrite, recent paths, folder slots, reload, and exit are menu actions.
- One active document is supported in v1; opening another document is guarded by dirty/pending-state confirmation.
- Sections are data-driven descriptors with stable key, title, subtitle, visibility, optional icon, and content mode: `FieldList`, `ReadOnly`, or `Custom`.
- `FieldCard` supports text, typed numeric, boolean, choice/autocomplete, read-only, warnings, and per-field Apply. Custom editors use templates/content presenters, not subclassing.
- `FieldList` virtualizes by default. Contextual search/filter/bulk-action rows are provided by an optional `SectionToolbar`.
- `CommunityToolkit.Mvvm` is the MVVM standard. Plain constructor composition is the default; DI integration is optional.
- Inter is the default font but is replaceable through a documented font setting.

## Theme and settings contract

- Supported themes are exactly Catppuccin Mocha (default dark) and Latte (light).
- Views use semantic resources only: window/panel/card/input backgrounds, foreground tiers, border, focus ring, primary, danger, warning, and success. Raw palette names are internal.
- The framework default accent is Blue. An editor may provide a default; the user may choose any of the 14 Catppuccin accents under `View > Appearance > Accent`. The persisted user selection wins, and a reset returns to the editor default.
- `View > Appearance > Themes` contains Dark and Light. Changes apply immediately and persist.
- Settings live in versioned JSON at `LocalApplicationData/<ApplicationId>/settings.json`. It stores theme, accent, up to 10 file recents, up to 10 folder recents, and window size. Position is not persisted. Writes are atomic, fail-soft, and migratable.
- Framework-owned strings are resource-backed and English is the only shipped culture. Layout is RTL-compatible but RTL is not a v1 release gate.

## Safety and file workflow

`SafeFileWorkflow` is first-class, while formats remain application-owned:

- `ISaveCodec<TDocument>` handles decode, typed serialization, validation, format metadata, and unknown-data preservation.
- A codec registry/detector supports one-codec and multi-format apps, with codec-declared picker filters.
- `Save As` is the default and `Ctrl+S` always means Save As. Native picker overwrite confirmation is trusted without a duplicate prompt; custom picker implementations must declare whether they provide that confirmation, with a framework fallback otherwise.
- `Overwrite + Backup` validates, rejects symlink/reparse-point targets, creates a UTC timestamped sibling backup, writes a same-directory temp file, flushes, and atomically replaces. It never elevates permissions or changes read-only state.
- Replacement failures leave the original untouched, clean the temp file by default, and report a corrective status/error. No autosave, telemetry, or network activity exists.
- External changes are hinted by a watcher and confirmed by a pre-action metadata/hash check. Overwrite blocks if the source changed externally; Save As remains available.
- Validation findings are structured (`Path`, `Message`, severity, optional code/remediation). Inline validation is lightweight; Apply/save runs full validation. Errors block, warnings show the first eight and require explicit continuation.
- Operations are asynchronous, cancellable, and report progress. Close waits for a definitive operation result. User interaction is uniformly async and implemented through `IUserInteraction`.

## Editing model

Typed drafts remain in memory while typing. Applying one field creates one history entry; Apply All creates one transactional section entry. Revision-based dirty tracking compares the current revision with the saved baseline, and history is capped at 1,000 committed operations by default. Undo/redo, dirty title `*`, status sentences, and discard guards are first-class.

## Testing and acceptance

- xUnit covers settings/migration, recent/folder persistence, validation, history, codecs, workflow safety, cancellation, external-change guards, and path safety.
- Avalonia headless tests cover shell commands, keyboard navigation, dialogs, focus, and pending/dirty transitions.
- Ubuntu owns golden screenshots for dark/light welcome, populated shell, dirty state, validation banner, and read-only/custom section states. Windows runs the same behavioral suite.
- A generated-template smoke test installs/builds the template and exercises its sample fields, theme toggle, recents, drag/drop seam, and safe workflow seam.
- A manually invoked real-Wayland smoke job verifies file/folder drop, menus, dialogs, resizing, theme switching, and keyboard behavior. Headless tests inject drop paths independently of the compositor.
- Accessibility is a release gate: keyboard operation, visible focus, logical tab order, accessible names/descriptions, WCAG AA body text, and Windows screen-reader smoke coverage.

## Generated template

`dotnet new save-editor` creates a runnable .NET 10/Avalonia app with the themed shell, menu, welcome state, one sample section containing text/numeric/boolean/choice fields, in-memory document, codec/workflow seams, `IUserInteraction`, settings/recents, theme and accent configuration, undo/redo, dirty tracking, drag/drop adapter, README, and tests. Demo format logic is intentionally obvious and replaceable.
