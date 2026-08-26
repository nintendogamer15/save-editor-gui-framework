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
eng/palette                        pinned Catppuccin palette.json (vendored)
eng/SaveEditor.PaletteGen          palette loading, contrast math, token names
eng/SaveEditor.ScreenshotDiff      zero-tolerance pixel comparator
.github/workflows                  two-platform CI
mockup/index.html                  self-contained design prototype
```

The first complete scope is released as `1.0`. Framework source is 0BSD, with a `THIRD-PARTY-NOTICES` file for Catppuccin, Inter, Avalonia, and other redistributed assets.

### Building

Requires the .NET 10 SDK (pinned in `global.json`). Dependency versions are centrally managed in `Directory.Packages.props`; Avalonia is major version **12**.

```sh
dotnet build -c Release
dotnet test  -c Release
```

Tests run on Microsoft.Testing.Platform rather than VSTest — `dotnet test` drives the test executables directly, and neither `Microsoft.NET.Test.Sdk` nor a VSTest adapter is referenced.

## Architecture decisions

- `EditorShell` is an embeddable `UserControl`; applications retain ownership of their `Window`.
- Because a `UserControl` cannot size a window or shut down an application, that authority is delegated to a host-supplied `IEditorHost`, with a shipped `WindowEditorHost` for the ordinary case. One shutdown guard serves both the window close button and `File > Exit`, so pending changes cannot be lost through whichever route was not anticipated.
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
- Views use semantic resources only; raw palette names are internal and a resource-resolution test enforces it. The roles are window/panel/card/input/overlay backgrounds, three foreground tiers, border and border-strong, focus ring, the accent set (`Primary`, `PrimaryText`, `OnPrimaryForeground`), status fills with matching text and background washes, shadow, font families, and the spacing and radius scales.
- A raw accent is only safe as a fill. In Latte just two of the fourteen reach 4.5:1 as text on the window background and none does on a card, so accent text, focus rings, and stateful borders take a derived, hue-preserving `PrimaryText` ramp instead. Text on an accent fill uses pure white or black per accent, since the palette's own neutrals fail for twelve of fourteen. Every ratio is asserted by test across 14 accents in both modes.
- The framework default accent is Blue. An editor may provide a default; the user may choose any of the 14 Catppuccin accents under `View > Appearance > Accent`. The persisted user selection wins, and a reset returns to the editor default.
- `View > Appearance > Themes` contains Dark and Light. Changes apply immediately and persist.
- Settings live in versioned JSON at `LocalApplicationData/<ApplicationId>/settings.json`. It stores theme, accent, up to 10 file recents, up to 10 folder recents, and window size. Position is not persisted. Writes are atomic, fail-soft, and migratable.
- Framework-owned strings are resource-backed and English is the only shipped culture. Layout is RTL-compatible but RTL is not a v1 release gate.

## Safety and file workflow

`SafeFileWorkflow` is first-class, while formats remain application-owned:

- `ISaveCodec<TDocument>` handles decode, typed serialization, validation, format metadata, and unknown-data preservation.
- A codec registry/detector supports one-codec and multi-format apps, with codec-declared picker filters.
- `Save As` is the default and `Ctrl+S` always means Save As. A picker may declare that it already confirmed an overwrite, but the default is that it did not, and the declaration suppresses only the duplicate prompt: the framework still confirms whenever it independently observes the target exists. One redundant prompt costs less than one silent overwrite.
- Every path goes through one resolver that opens with link following disabled, checks **every ancestor component** rather than just the leaf, records volume and file identity, and refuses anything that is not a regular file. Later steps re-assert that identity instead of re-resolving a path string.
- `Overwrite + Backup` is all-or-nothing. The backup is written from the same retained handle that produced the change-detection baseline, flushed, and hash-verified; any failure aborts the overwrite with the original untouched. Backup and temp files are created exclusively, with entropy in the name, so a pre-planted link at a predictable path cannot redirect the write.
- Replacement fsyncs the file, replaces atomically, and fsyncs the containing directory on Linux. There is no non-atomic fallback: a filesystem that cannot support atomic replacement aborts with a message naming the limitation rather than degrading to delete-then-move. Permissions are carried across so a `0600` save does not silently become `0644`.
- On failure the bytes at the target path are exactly the pre-operation bytes. Not guaranteed, and stated plainly: file identity, hardlink aliasing, open-handle views held by other processes, and timestamps.
- A codec's unknown-data preservation claim is **verified, not trusted** — the framework re-serializes the freshly decoded document and byte-compares against the source, and downgrades a falsified claim to a confirmation prompt instead of reporting success.
- External-change detection requires a hash, re-checked immediately before the replace. Windows denies write sharing for the duration, which closes the window against cooperative writers; Linux can only narrow it, and the docs and status text say so rather than claiming more.
- Validation findings are structured (`Path`, `Message`, severity, optional code/remediation). Inline validation is lightweight; Apply/save runs full validation. Errors block; warnings show the **most severe** eight plus a count and require explicit continuation. Codec-supplied text is sanitized and capped before it reaches a destructive dialog.
- No autosave, telemetry, or network activity exists.

### What this does not defend against

The safety work above is aimed at three things: malformed or hostile save-file bytes, another local process writing into the same directory, and accidents such as crashes, full disks, and concurrent writers.

**A hostile codec is not one of them.** `ISaveCodec` implementations run in-process at full privilege, so the codec boundary is a correctness boundary the framework can bound and instrument — not a sandbox. Likewise, a game rewriting its own save while the editor is open, or a cloud-sync client rewriting the file after a successful write, is outside the guard's reach; the status wording claims only that no change was detected between the check and the write. See `PLAN.md` §8.
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
