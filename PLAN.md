# Save Editor GUI Framework — V1 Implementation Plan

> **Revision 3 — 2026-08-26.** Supersedes revisions 1 and 2.
>
> Revision 2 closed seven readiness blockers from the first review and folded in a
> pre-approval security review of the state and file-workflow surfaces (findings and
> dispositions in §9). Material changes there: pinned dependencies (§3), host
> lifecycle seam (§4), a measurable contrast contract (§5), hardened settings and
> file workflow (§6–§7), an explicit threat model (§8), and phases restructured into
> independently approvable slices (§13).
>
> Revision 3 closes five internal-consistency defects found in revision 2 itself:
> §5's assertion table named surfaces its derivation rule did not cover; §9 claimed
> universal test backing it did not have; P4 omitted P1 from its prerequisites while
> depending on it; P2's deferred acceptance checks were referenced but never
> enumerated; and P0's acceptance named CI that P6 owned. Recomputing §5 against the
> full surface set also corrected two figures revision 2 got wrong — `MutedForeground`
> needs derivation in Latte, and `OnPrimaryForeground` requires pure rather than
> palette endpoints.

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
- .NET 10 and Avalonia 12 on Windows/Linux
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
- Sandboxing or otherwise defending against a hostile codec (see §8)

## 3. Repository structure and pinned dependencies

```text
src/SaveEditor.Ui
src/SaveEditor.Template
samples/SaveEditor.Ui.Gallery
tests/SaveEditor.Ui.Tests
tests/SaveEditor.Ui.HeadlessTests
tests/SaveEditor.Template.Tests
eng/
docs/
.github/workflows/
mockup/index.html
README.md
PLAN.md
LICENSE
THIRD-PARTY-NOTICES
```

The repository uses a central solution, `Directory.Build.props`, and `Directory.Packages.props` with central package management. The first stable release is `1.0`; public breaking changes require a major version.

**The contract freezes before the version does.** `eng/PublicApi.SaveEditor.Ui.txt` pins the public surface and any change to it fails the build, so the major-version promise is enforced from now on rather than from the moment a tag is cut. The packages remain `1.0.0-alpha.1` until the two gates that require a person have actually been run — the Windows screen-reader pass in `docs/ACCESSIBILITY.md` and the Wayland session checklist in `docs/WAYLAND-CHECKLIST.md`. Shipping `1.0.0` while a named release gate has never been executed would make the version claim something the evidence does not support, and those two gates are precisely the ones no amount of CI can close.

### Pinned versions

All versions below are verified to restore and build together against `net10.0`. Central package management pins exact versions; the shipped package declares minimum-version dependencies.

| Component | Pinned | Notes |
| --- | --- | --- |
| .NET SDK | 10.0.400 | `global.json`, `rollForward: latestFeature` |
| Target framework | `net10.0` | Single TFM for all projects |
| Avalonia | 12.1.1 | Native `net10.0` target. **Major version 12, not 11** — control-theme and headless APIs differ materially |
| Avalonia.Themes.Fluent | 12.1.1 | Behavior/infrastructure only; not the visual contract |
| Avalonia.Fonts.Inter | 12.1.1 | Embedded font package — see §5 |
| Avalonia.Headless.XUnit | 12.1.1 | Headless UI tests |
| Avalonia.Skia | 12.1.1 | Screenshot capture backend |
| CommunityToolkit.Mvvm | 8.4.2 | Source-generated observables/commands |
| Microsoft.Extensions.DependencyInjection | 10.0.11 | Optional integration only; never mandatory |
| xunit.v3 | 3.2.2 | Test framework |
| Catppuccin palette | commit `07d02aa110ef9eb7e7427afca5c73ba9cf7f8ebd` | `catppuccin/palette`, `palette.json`; attributed in `THIRD-PARTY-NOTICES` |

Screenshot comparison uses a framework-owned pixel comparator in `eng/` rather than an external image-diff dependency, so the visual gate carries no unpinned tooling.

## 4. Public architecture

### Shell

Implement `EditorShell` as an embeddable `UserControl`. The consuming app owns its `Window`, lifecycle, size, and platform integration.

The shell exposes named content slots for branding, header actions, sidebar, content, status bar, and menu extensions. The framework owns the shell grid, spacing, focus behavior, default commands, and accessibility behavior.

### Host lifecycle seam

`EditorShell` is a `UserControl` and cannot own window sizing or application shutdown. Everything requiring window or application authority is delegated to a host-supplied `IEditorHost`, with a shipped `WindowEditorHost` covering the common single-window case:

```text
IEditorHost
  ApplySize(Size)            framework -> host, restoring persisted size
  SizeChanged                host -> framework, on host resize, for persistence
  SetShutdownGuard(guard)    framework -> host, installed once at composition
  RequestShutdownAsync()     framework -> host, raised by File > Exit
```

Shutdown is veto-capable in both directions, through **one** installed guard rather than an event per route. The framework installs the guard once; the host consults that same guard whether shutdown began at the window's close button or at `File > Exit`. A guard rather than a cancellable event is what makes this work: the check is asynchronous and can show a dialog, and a synchronous close cannot be held open while it does. The host performs the actual shutdown only on allow, and the framework never calls application-lifetime APIs itself.

When no `IEditorHost` is supplied, window size is not persisted and `File > Exit` is hidden rather than present-but-inert.

### Menus

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

Provide typed field descriptors and view-models for text, numeric values with invariant parsing and validation, boolean values, in-memory or asynchronous choice/autocomplete providers, read-only values, and warning/help text.

`FieldCard` owns labels, paths, warning presentation, accessibility, and Apply actions. Custom editors use templates and content presenters rather than subclassing. `FieldList` virtualizes by default.

Typing creates an in-memory pending draft. Per-field Apply creates one history entry; Apply All creates one transactional section entry.

### MVVM and composition

Use `CommunityToolkit.Mvvm`. Keep plain constructor composition as the default; offer optional `Microsoft.Extensions.DependencyInjection` integration without making DI mandatory.

Use async, cancellation-aware interfaces for file operations and all `IUserInteraction` methods. A thin Avalonia drag/drop adapter forwards paths to the same view-model open workflow used by menus and pickers.

## 5. Theme system

Generate Latte/Mocha resources at build time from the pinned `palette.json` revision (§3) using a generator in `eng/`. Generated resources are committed; a test regenerates and asserts no drift.

### Semantic resources

Only semantic resources are exposed to application views. Raw palette names (`mauve`, `surface0`, …) stay internal, enforced by a resource-resolution test that fails if any view or control theme references a raw palette key.

```text
Surfaces      WindowBackground, PanelBackground, CardBackground,
              InputBackground, OverlayBackground
Text          Foreground, MutedForeground, SubtleForeground
Lines         Border, BorderStrong, FocusRing
Accent        Primary, PrimaryHover, PrimaryPressed,
              PrimaryText, OnPrimaryForeground
Status        Danger, Warning, Success,
              DangerText, WarningText, SuccessText,
              DangerBackground, WarningBackground, SuccessBackground
Elevation     ShadowColor
Typography    FontFamilyDefault, FontFamilyMono
Metrics       SpaceXs, SpaceSm, SpaceMd, SpaceLg, SpaceXl,
              RadiusSm, RadiusMd, RadiusLg
```

The text, status-background, elevation, typography, and metric roles exist because the mockup already depends on them (`--accent-contrast`, `--danger-bg`, `--success-bg`, `--warning-bg`, `--shadow`, `--font-mono`, and the spacing/radius scales) and revision 1's list omitted every one of them.

### Contrast contract

**Text-bearing surfaces.** Every text-role assertion below is made against exactly this enumerated set, and against no others:

| Token | Latte | Mocha |
| --- | --- | --- |
| `WindowBackground` | `base` | `base` |
| `PanelBackground` | `mantle` | `mantle` |
| `InputBackground` | `crust` | `crust` |
| `CardBackground` | `surface0` | `surface0` |

`CardBackground` is the binding constraint in both flavors — it is the darkest surface in Latte and the lightest in Mocha — so a role that passes on `CardBackground` passes on all four.

`OverlayBackground` is a scrim, never a text surface, and is excluded. `Border` is a decorative separator, not a UI-component boundary, and is likewise excluded from the gate; `BorderStrong` carries any boundary that conveys state.

Measured against the pinned palette, raw Catppuccin accents are **not** usable as text in Latte: only `mauve` (4.79:1) and `red` (4.80:1) reach 4.5:1 even on `base`, and none reaches it on `CardBackground`. They also fail as *indicators*: eleven of fourteen fall below the 3:1 non-text floor (`yellow` is 1.70:1 on `CardBackground`), so a raw-accent focus ring is unusable in the light theme. Mocha accents pass everywhere (5.43:1 worst). The theme therefore separates accent-as-fill from accent-as-line-and-text.

Required ratios, asserted by test across all 14 accents × 2 modes:

| Role | Measured against | Minimum |
| --- | --- | --- |
| `Foreground` | all four text-bearing surfaces | 4.5:1 |
| `MutedForeground` | all four text-bearing surfaces | 4.5:1 |
| `PrimaryText`, `DangerText`, `WarningText`, `SuccessText` | all four text-bearing surfaces | 4.5:1 |
| `DangerText`, `WarningText`, `SuccessText` | their own `…Background` token | 4.5:1 |
| `SubtleForeground` (non-essential text only) | all four text-bearing surfaces | 3.0:1 |
| `OnPrimaryForeground` | `Primary` | 4.5:1 |
| `FocusRing`, `BorderStrong` | all four text-bearing surfaces | 4.5:1 (see below) |

Resolution rules:

- **`Primary`** is the raw accent. It is used only for fill interiors and decorative wash — never for text, lines, focus rings, or any boundary that conveys state.
- **`PrimaryText`** is derived: darken the raw accent in sRGB toward black by the smallest factor achieving ≥4.5:1 against *all four* text-bearing surfaces. Verified for all 14 Latte accents (factors 0.561–0.853, all landing ≥4.50:1) and a no-op in Mocha (factor 1.0, worst 5.43:1). Hue is preserved.
- **`FocusRing` and `BorderStrong` resolve to `PrimaryText`, not `Primary`.** Their WCAG requirement is 3.0:1, but reusing the 4.5:1 ramp satisfies it with margin and avoids a second derivation. In Mocha this is the raw accent (factor 1.0), matching the mockup; in Latte it is the darkened variant, which is also the better design — a pale yellow focus ring on a light surface is not perceivable.
- **`MutedForeground`** is derived by the same rule. Raw Latte `subtext1` reaches only 4.05:1 on `CardBackground`, so it is darkened by factor 0.929; Mocha is a no-op (7.10:1).
- **`SubtleForeground`** uses raw `subtext0` in both modes (Latte 3.20:1, Mocha 5.65:1) and is restricted to non-essential text.
- **`OnPrimaryForeground`** is chosen per accent per mode as whichever of **pure white or pure black** yields the higher ratio against `Primary`. The endpoints are deliberately pure rather than palette neutrals: against `base`/`crust` twelve of fourteen Latte accents fail (worst 2.31:1), whereas pure endpoints always clear 4.5:1 (worst Latte `blue`, 4.91:1; worst Mocha `red`, 9.07:1). The mockup's single `--accent-contrast` value per theme is a simplification that holds only for the two accents it demonstrates; the framework computes it per accent.
- `DangerText`, `WarningText`, and `SuccessText` derive by the same rule and are additionally asserted against their own status background, since Latte `yellow` (2.31:1) and `green` (2.96:1) fail as text for the same reason accents do.

Derived values are produced by the `eng/` generator and asserted by test; they are not hand-authored constants. No accent is excluded from the picker — the derivation makes all 14 usable in both modes.

### Modes, fonts, and culture

Support exactly two modes: Mocha/Dark (default) and Latte/Light.

`View > Appearance > Themes` changes the mode immediately and persists it. `View > Appearance > Accent` exposes all 14 Catppuccin accents. The framework supplies a default accent, an editor may override that default, and a user selection wins on later launches. A reset action returns to the editor default.

Inter is the default font and ships **embedded** via `Avalonia.Fonts.Inter`, not resolved by family name. Embedding is required: Inter is absent from a default Ubuntu image, so a name-resolved default would silently fall back and make the Ubuntu screenshot baseline both non-deterministic and unrepresentative of Windows. The OFL-1.1 notice obligation is recorded in `THIRD-PARTY-NOTICES`. `FontFamilyDefault` stays overridable by the consuming app in one place.

English is the only shipped culture, but framework strings use resource keys. Layout should respect RTL flow direction without making RTL a v1 release gate.

## 6. State, settings, and history

Track pending drafts separately from committed document changes:

- Pending edits stay in memory while typing and survive section navigation.
- Committed edits make the document dirty and enter `EditHistory`.
- The title gains ` *`; the sidebar and status bar report dirty state.
- Reload, open, close, and exit guard against pending or unsaved changes.

Use revision-based dirty tracking with a saved-baseline revision. Cap history at 1,000 committed operations by default; do not persist history.

### Settings storage

Store versioned settings at:

```text
LocalApplicationData/<ApplicationId>/settings.json
```

`ApplicationId` is validated at construction against `[A-Za-z0-9._-]{1,64}`, rejecting `.`, `..`, Windows reserved device names (case-insensitively, including with extensions), trailing dots or spaces, and any path or stream separator. An invalid id throws rather than falling back to a default directory.

Persist theme, accent, up to 10 file recents, up to 10 folder recents, last-selected section, and window size. Do not persist window position or document drafts.

### Settings as a trust boundary

`settings.json` is user-writable, may arrive from a roaming profile or a restored backup, and feeds paths into the recents menu and the open workflow. It is treated as untrusted input:

- Deserialization uses `System.Text.Json` with a **source-generated, closed POCO context**. Polymorphic type resolution is prohibited — no `TypeNameHandling`, no `$type` discriminators, no caller-supplied type resolvers.
- Bounds are enforced **on read**, not only on write: maximum file size before parsing, `MaxDepth`, maximum string length, and the 10-entry recents caps.
- Window size is clamped to a sane range intersected with available screen bounds; negative, zero, and `int.MaxValue` values are rejected rather than applied.
- Path values must be rooted and local. UNC paths, `\\?\` and `\\.\` device namespaces, and `GLOBALROOT` paths are rejected unless the consuming editor explicitly opts in. This is a security control, not only a robustness one: a stored UNC path probed at startup triggers an outbound SMB connection and an automatic NTLM authentication attempt, which would both leak credential material and violate the no-network non-goal.
- An unknown or absurd schema version routes to the malformed path, never to the newest migrator.
- **Validation failure is two-tiered.** *Structural* problems — malformed JSON, any bound exceeded, a type discriminator, an unknown schema version — back the file up and reset to defaults. *Value-level* problems — one UNC recents entry, an implausible window size, an unknown accent name — drop or clamp that value and keep the rest, reporting the load as sanitized. Revision 2 said a file failing validation follows the same route as a malformed one; read literally that lets a single bad recents entry wipe the user's theme, which this same section contradicts by clamping window size rather than treating it as fatal. Structural validation is the one that resets.
- **An unreadable file is left alone rather than reset.** If `settings.json` resolves to a symlink, a FIFO, a directory, or exceeds the size gate, the editor runs on in-memory defaults and does not touch the file. The store will not destroy something it could not identify; the next successful save replaces the directory entry.
- Bidi and control characters are handled asymmetrically on purpose: C0/C1 controls are rejected, but bidi marks in a stored path are kept **verbatim**. Rewriting a stored path would make a recents entry resolve to a file other than the one it names — the same hazard as case-insensitive deduplication on Linux. Bidi is a display concern and is neutralized by the shared path formatter in §10, not by editing the stored value.

Writes are atomic and fail-soft. Malformed settings are backed up and replaced with defaults without blocking startup; the backup uses exclusive-create with a disambiguating component and bounded retention, so a second malformed startup never destroys the first backup. If the settings directory is unwritable, the framework runs on in-memory defaults and says so once through the announcement region rather than silently discarding every later change.

### Recents

Recent files are deduplicated on the canonical path from the resolution primitive in §7, using **platform-appropriate comparison** — ordinal on Linux, ordinal-ignore-case on Windows. Case-insensitive comparison on Linux would merge `Save.dat` and `save.dat`, which are different files, and a recents entry that resolves to a file other than the one the user believes is a data-loss hazard in a tool whose purpose is not destroying saves.

Existence checking is **lazy and time-boxed**, performed when a recent is rendered or activated — never as an unconditional startup scan, and never automatically against a non-local path. Confirmed-missing paths are pruned at that point; temporarily inaccessible paths are retained. Folder slots are optional and provider-driven.

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

Provide a registry/detector for one or more codecs. Ambiguous detection produces a confirmation flow resolved by user choice, never by registration order; unsupported files fail safely.

### Path resolution primitive

`SafePath` is a first-class public contract, not an internal detail of the workflow, and is defined in Phase 0 because the workflow, settings, recents, and backup paths all depend on it. It implements **resolve once, then operate on the handle**:

1. Open the target with link-following disabled — `O_NOFOLLOW | O_CLOEXEC` on Linux, `FILE_FLAG_OPEN_REPARSE_POINT` with reparse-tag inspection on Windows.
2. Walk and check **every ancestor component** from the volume root, not just the leaf. A junction or symlink in an intermediate directory redirects the write just as effectively as one on the file itself.
3. Record the resolved identity: `dev` + `ino` on Linux, volume serial + file ID on Windows.
4. Require that every later step re-asserts that identity or operates on the retained handle. No later step re-resolves a path string.
5. Verify the target is a **regular file**. FIFOs, devices, and named pipes are refused: opening a FIFO blocks decode forever behind a cancel button that cannot help, and `/dev/zero` yields an unbounded read.
6. Enforce a configurable maximum input size, with explicit confirmation above it, and time-box all reads.

A hardlink count greater than 1 is a distinct condition requiring its own confirmation, since replacing content changes every alias. Bind mounts carry no link attribute, are undetectable, and are stated as out of scope rather than implied to be covered.

**Reparse refusal is scoped to name-surrogate tags** (`tag & 0x20000000`), which is the flag marking namespace redirection — symbolic links and mount points or junctions. Non-surrogate reparse points such as cloud placeholders, deduplication, and WOF compression resolve to the same file and redirect nothing, so they pass through to the ordinary regular-file and identity checks. Refusing every tag would reject saves in a OneDrive-synced Documents folder, which is the default on many Windows 11 installs; that is a false positive rather than a safety gain, and the pressure it creates is for consumers to route around the resolver entirely. A non-surrogate placeholder may hydrate on first read, which is the OS acting on a file the user chose and not framework network activity.

**Mapped network drive letters count as non-local.** Gating UNC syntax alone leaves `Z:\saves` pointing at an SMB share fully exposed, and the concern behind the gate was never the backslashes — it was the automatic SMB connection and NTLM authentication. The Windows resolver checks the drive type and treats a remote drive exactly as it treats UNC.

### Workflow steps

`SafeFileWorkflow` owns:

1. Async open, detect, decode, and validation.
2. Save As as the default write path; `Ctrl+S` always means Save As.
3. Overwrite confirmation. Custom picker implementations may declare that they confirm, but the default is **does not confirm**, and the declaration only suppresses the *duplicate* prompt: the framework still confirms whenever it independently observes that the chosen target exists and is not the currently-open document. A duplicated prompt costs less than one silent overwrite.
4. Full validation immediately before writing. Errors block; warnings show the most severe eight plus a count and require explicit continuation. Errors block on both Save As and Overwrite in v1 — a deliberate decision, recorded so it is not an implementation accident.
5. Refusal — never modification — for symlink, reparse-point, ReadOnly, and immutable targets. No privilege elevation, no attribute clearing, no delete-and-recreate workaround. If an attribute is cleared transiently by platform replace semantics, it is restored on every exit path including failure.
6. Backup before explicit Overwrite + Backup, as an **all-or-nothing** step: written from the same retained handle that produced the external-change baseline hash, flushed, and its hash compared against that baseline. Any failure at any step aborts the overwrite with the original untouched and the partial backup removed. If the sibling directory is unwritable, the user is offered an explicit alternate location; the workflow never silently proceeds without a backup. The backup filename grammar uses a Windows-safe time separator, carries a random disambiguating component, and is subject to a stated retention cap applied only to files matching the framework's own grammar.
7. Same-directory temp write, then atomic replacement. Both the temp and backup files are created with **exclusive-create semantics only** (`FileMode.CreateNew` / `O_CREAT|O_EXCL|O_NOFOLLOW`) and the temp name carries cryptographic entropy rather than a derived suffix, so a pre-planted symlink or hardlink at a predictable path cannot turn the safety feature into an arbitrary-write or disclosure primitive. An `AlreadyExists` result aborts rather than retrying through a link-following open.
8. Durability and preservation. The file is flushed **and** fsync'd, replaced atomically, and on Linux the containing directory is fsync'd after the rename — without which the rename itself can be lost on power failure. There is **no non-atomic fallback**: a destination on a filesystem that cannot support atomic replacement, or held open without share-delete, aborts with a message naming the limitation rather than degrading to delete-then-move.
    **On Windows the replace is `FileRenameInfoEx` with POSIX semantics, not `File.Replace` or `MoveFileEx`.** Revision 3 named those APIs; implementation showed both require releasing the original's handle first — `MoveFileEx` returns `ERROR_ACCESS_DENIED` against a destination held open even with share-delete, and `ReplaceFile` needs write access the deny-write handle refuses. Releasing the handle reopens exactly the check-to-replace window step 9 holds it to close, so honouring the named API would have traded away the guarantee it exists to serve. `FileRenameInfoEx` requires Windows 10 1709 or later; older builds abort naming the limitation, consistent with having no fallback. Linux uses `rename(2)` as stated.

9. External-change detection. The baseline hash is captured at decode from the retained handle and re-verified immediately before the replace, not only at the start of the action. A hash is required for a positive result; metadata may serve only as a fast-path negative, since mtime granularity is coarse and trivially restorable. On Windows the original is held with write sharing denied from check through replace, which closes the window against cooperative writers. On Linux locks are advisory and `rename` offers no compare-and-swap, so the window is *narrowed* to the last instruction rather than closed — the documentation and the status text say so rather than claiming more. Mismatch aborts and prompts; it never auto-overwrites. The baseline updates after a successful write.
10. Permission preservation. `rename(2)` gives the destination the temp file's mode and ownership, which would silently widen a `0600` save to `0644` — the exact opposite of step 5's promise. Before replacing, the framework copies mode, ACL, and extended attributes from the retained original handle onto the temp, aborts if the resulting permission set would be broader than the original, and treats ACL and xattr copying as best-effort with the widening check as the hard gate. Backups inherit the original's mode, not the directory default.
11. Round-trip verification of unknown data. A codec's preservation capability is **tested, not trusted**: immediately after decode the framework serializes the unmodified document and compares the result against the source. A mismatch means the declaration is empirically false, and the workflow automatically downgrades to the warning-requiring-confirmation path instead of reporting success.
    **The comparison relation belongs to the codec, and the verdict records which relation was used (finding F-1).** Byte equality is checked first and is proven by the framework, reported as `Verified`. Only on divergence is `ISaveCodec.RoundTripEquivalent` consulted, so a codec that answers `true` unconditionally can suppress a `Falsified` verdict it deserves but can never manufacture the byte-identical one; that outcome is reported as the weaker `VerifiedEquivalent`. Revision 3 demanded byte-identical re-serialization outright, which was wrong: a format deriving its key and IV from an embedded random salt could satisfy it only by pinning the salt, i.e. reusing one key and IV across differing plaintexts. The strict rule therefore incentivised a real cryptographic regression, and — for a codec that refused — injected a false data-loss sentence into every destructive confirmation, which trains users to click through the one dialog that protects them. A weaker guarantee reported honestly beats a stronger one nobody can satisfy safely. Before replacing, the serialized temp is decoded and compared against the in-memory document to catch serializers that lose fields. Both checks are skippable through a documented opt-out for very large saves. This converts the framework's central promise from codec self-assertion into a property the framework itself checks.
12. Codec containment. The codec never receives a handle or path resolving to the destination; serialization completes into the temp or memory in full and is size- and hash-checked before any replace is attempted. Any exception from `Decode`, `Validate`, or `Serialize` is caught at the workflow boundary, converted to a definitive failure status, leaves the destination byte-identical, and removes the temp. Detectors receive a bounded read-only header slice rather than a seekable stream over the whole file, run isolated so a throwing detector is recorded as "declined" instead of aborting detection, and are individually time-boxed.
13. Progress, cancellation, definitive status, and close coordination. `CancellationToken` against third-party codec code is cooperative only, so cancellation is made authoritative **at the workflow boundary**: after cancellation the workflow abandons the operation, unconditionally discards any late-returning result, and guarantees no write can originate from a cancelled operation. Status text reports the user-visible operation as cancelled without implying the background work stopped.
14. Temp residue cleanup. Cleanup on handled failure does not cover process kill, OOM, or power loss, each of which can leave a complete copy of the save payload in the user's directory. Temp names carry a fixed recognizable prefix, and a bounded startup sweep removes only prefix-matching entries older than a stated age, in directories the framework itself has written to.

### Guarantee wording

"Original-file preservation" means precisely: **on any failure, the bytes at the target path are exactly the pre-operation bytes.** Explicitly *not* guaranteed, and stated as such in the README: file identity (`rename` unlinks the original inode on Linux), hardlink aliasing, views held by other processes through open handles, and creation/change timestamps. Permissions *are* preserved per step 10.

The settings writer and the save writer share the hardened primitive but **not** the failure policy: settings are fail-soft, saves are fail-loud with definitive status. Failure policy is a parameter of the caller, never of the primitive.

## 8. Threat model and trust boundaries

The safety properties in §6–§7 are stated against three adversaries:

- **U — untrusted bytes.** A malicious or malformed save file processed by an honest-but-buggy codec. Mitigated by size and time bounds, detector isolation, exception containment, and sanitized display of codec-supplied text.
- **L — local unprivileged process.** Another process able to write into the save file's directory or the settings directory — removable media, shared machines, loosely-ACL'd game directories such as Steam `userdata`, network shares. Mitigated by the resolution primitive, exclusive-create with entropy, and identity re-assertion.
- **A — accident.** Crash, power loss, disk-full, concurrent writer, misclick. Mitigated by durability ordering, all-or-nothing backup, and fail-loud save status.

**A hostile codec is explicitly not defended against.** `ISaveCodec` implementations are in-process, full-privilege .NET running as the user. The codec boundary is a *correctness* boundary the framework can instrument and bound — not a sandbox. The README states this plainly rather than letting "safe file workflow" imply otherwise.

Concurrent writers outside this process — the game itself rewriting saves on exit or autosave, a second editor instance, or a cloud-sync client such as Steam Cloud or OneDrive rewriting the file after a successful save — are outside the guard's reach. The documentation says so, and the status wording claims only "no change detected between check and write."

## 9. Security findings and dispositions

Recorded 2026-08-26 from a read-only pre-approval review of §6–§7. Every finding carries an explicit disposition.

Dispositions use four labels:

- **`FIX`** — closed by the cited text *and* backed by a named test in §12.
- **`FIX (narrow)`** — the risk cannot be eliminated on every platform, so the design reduces it as far as the platform allows and the plan states the residual honestly rather than claiming closure. Test-backed like `FIX`. Applies only to A5.
- **`FIX (wording)`** — the finding was a claim the plan overstated; corrected text closes it and there is no behavior to test. These rows are deliberately excluded from the "every `FIX` row maps to a passing test" gate in §13 and §14.
- **`DEFER`** — accepted but not addressed in v1, with rationale.

Counts: 24 `FIX`, 1 `FIX (narrow)`, 2 `FIX (wording)`, 1 `DEFER` — 28 findings total.

| ID | Finding | Pri | Disposition | Closed by |
| --- | --- | --- | --- | --- |
| GAP | No stated adversary for the safety claims | — | FIX (wording) | §8 |
| A1 | Path resolution order undefined; leaf-only checks; incomplete link taxonomy | P0 | FIX | §7 SafePath 1–4 |
| A2 | Predictable backup/temp names allow symlink or hardlink planting | P1 | FIX | §7 step 7 |
| A3 | Settings path values untrusted; startup probe of a UNC path leaks NTLM | P1 | FIX | §6 trust boundary; lazy recents |
| A4 | Non-regular files (FIFO, device, pipe) and unbounded sizes openable | P1 | FIX | §7 SafePath 5–6 |
| A5 | TOCTOU between the change check and the replace | P1 | FIX (narrow) | §7 step 9; residual documented |
| A6 | `rename` silently widens `0600` to `0644`, contradicting step 5 | P2 | FIX | §7 step 10 |
| A7 | Picker self-asserts that it confirmed; framework suppresses its own prompt | P2 | FIX | §7 step 3 (fail closed) |
| A8 | Codec-supplied text rendered inside a destructive confirmation | P2 | FIX | §7 step 4; §10 sanitization |
| A9 | `ApplicationId` unvalidated; traversal and reserved names | P2 | FIX | §6 validation |
| A10 | Settings deserialization unbounded and possibly polymorphic | P2 | FIX | §6 trust boundary |
| A11 | Detector fan-out widens parse surface to all installed codecs | P2 | FIX | §7 step 12 |
| A12 | ReadOnly/immutable target invites attribute clearing | P2 | FIX | §7 step 5 |
| A13 | Bidi/control characters in displayed paths spoof the overwrite target | P3 | FIX | §10 path formatter |
| A14 | Temp residue survives process kill | P3 | FIX | §7 step 14 |
| B1 | Backup only attempted, never verified, before a destructive overwrite | P1 | FIX | §7 step 6 |
| B2 | Atomic replace under-specified; missing directory fsync; unsafe fallback | P1 | FIX | §7 step 8 |
| B3 | "Original-file preservation" undefined and false under some readings | P1 | FIX | §7 guarantee wording |
| B4 | Unknown-data preservation entirely codec-self-asserted | P1 | FIX | §7 step 11 |
| B5 | Codec exception mid-serialize leaves state unspecified | P2 | FIX | §7 step 12 |
| B6 | Cooperative cancellation presented as authoritative | P2 | FIX | §7 step 13 |
| B7 | Case-insensitive recents dedup is wrong on Linux | P2 | FIX | §6 recents |
| B8 | Backup timestamp collisions; unbounded growth | P2 | FIX | §7 step 6 |
| B9 | Settings backup overwrites the last good copy | P2 | FIX | §6 settings storage |
| B10 | Two write paths with opposite failure policies share a helper | P2 | FIX | §7 guarantee wording |
| B11 | Validation blocking asymmetry between Save As and Overwrite | P3 | FIX | §7 step 4 (decision recorded) |
| B12 | Concurrent writers outside the process; status overclaims coverage | P3 | FIX (wording) | §8 |
| B13 | Drag-dropped temp path later used as an overwrite target | P3 | **DEFER** | Post-1.0 — see below |

**B13 deferral rationale.** Detecting "a known temp location" requires a platform-specific directory list that is incomplete by construction and would produce false positives on legitimate save locations. The existing posture already covers the substance of the risk: Save As is the default write path, Overwrite is a separately named command, and §10's path formatter shows the full final two path components in every destructive confirmation. Revisit if real usage shows users overwriting into browser-download or archive-extraction directories.

## 10. Dialogs and feedback

Ship a themed default implementation behind `IUserInteraction` for storage pickers, confirmations, messages, and read-only documents. Destructive actions require verb-specific accept labels naming the actual outcome — "Overwrite save file", not "Continue" or "OK".

Codec-supplied strings — validation messages and unknown-data warnings — are **untrusted display data** derived from attacker-controlled bytes. They render as plain text in a visually distinct, non-chrome region; control and bidi characters are stripped; per-warning length, line count, and total warning count are capped; and the framework's own title, framing sentence, and accept label stay entirely outside codec influence. Shown warnings are the most severe eight, not the first eight.

One shared path-display formatter serves the recents menu, status bar, announcement region, and every confirmation dialog. It strips or replaces control and bidi characters, isolates with directional-isolate marks, truncates in the middle while always showing the full final two components, and exposes the full raw path through the tooltip and accessible description.

**The final-two-components invariant outranks the length budget**, so a formatted label may overrun the width it was given; the formatter reports this. A surface receiving an overrunning label must wrap, scroll, or clip — **never end-trim it**. Trimming the tail removes the filename, which is exactly the end-truncation the formatter refuses to perform, and doing it at the display layer would reintroduce the substitution hazard the formatter exists to prevent. Framework-authored status sentences may still be trimmed; paths may not.

Formatter output is not a path and cannot be used as one: the isolate wrapping is unconditional, so a label never equals the path it describes and names no file on any filesystem.

Keep the status bar as the canonical outcome channel, with full-sentence status, current path, last backup, progress, and cancellation. Add a persistent accessible inline announcement region for important errors and outcomes instead of transient toasts.

Include a reusable themed About/Credits dialog with consumer slots for app identity, credits, and licenses. Raw/advanced data presentation remains editor-owned.

## 11. Template and gallery

`dotnet new save-editor` generates:

- .NET 10/Avalonia app and solution
- Framework-owned themed shell, in-window menus, and a `WindowEditorHost`
- Welcome state and one populated example section
- Text, numeric, boolean, choice, warning, Apply, Apply All, undo/redo, and dirty-state examples
- In-memory document, codec/workflow seams, settings/recents, accent/theme configuration, and drag/drop adapter
- Sample `IUserInteraction` composition
- README explaining replacement points, the safe workflow guarantees of §7, and the trust boundaries of §8

The gallery starts with the full shell and includes token swatches, both themes, all 14 accents, controls, dialogs, workflow states, keyboard/accessibility notes, and custom-section examples. It is the visual regression surface and must not require network access.

## 12. Verification plan

### Unit tests

Cover settings migration and failure, recent pruning and deduplication, revision dirty state, history limits, validation aggregation, codec detection, unknown-data capability, path safety, symlink/reparse rejection, backup naming, atomic workflow ordering, cancellation, and external-change guards.

### Security test corpus

Every §9 row dispositioned `FIX` maps to a named test below. Rows dispositioned `FIX (wording)` (GAP, B12) have no behavior to test and are excluded by construction; B13 is deferred. This table *is* the gate referenced by P4's acceptance and by §14 — a `FIX` row absent from it is a plan defect, not an implementation detail.

| Finding | Named test | Owning phase |
| --- | --- | --- |
| A1 | `SafePath_RejectsLinkInIntermediateComponent` | P0 |
| A1 | `SafePath_RejectsJunctionAndNonSymlinkReparseTags` | P0 |
| A1 | `SafePath_ConfirmsWhenHardlinkCountExceedsOne` | P0 |
| A4 | `SafePath_RefusesFifoDeviceAndNamedPipe` | P0 |
| A4 | `SafePath_RefusesInputAboveConfiguredSizeCap` | P0 |
| A3 | `Settings_RejectsUncAndDeviceNamespacePaths` | P1 |
| A3 | `Recents_DoesNotProbeFilesystemDuringStartup` | P1 |
| A9 | `ApplicationId_RejectsTraversalReservedNamesAndSeparators` | P1 |
| A10 | `Settings_RejectsPolymorphicTypeDiscriminators` | P1 |
| A10 | `Settings_EnforcesSizeDepthAndCountCapsOnRead` | P1 |
| A10 | `Settings_ClampsWindowSizeToScreenBounds` | P1 |
| A10 | `Settings_UnknownSchemaVersionRoutesToMalformedPath` | P1 |
| B7 | `Recents_DeduplicatesOrdinallyOnLinuxAndIgnoreCaseOnWindows` | P1 |
| B9 | `Settings_SecondMalformedStartupPreservesFirstBackup` | P1 |
| A13 | `PathFormatter_StripsBidiAndShowsFinalTwoComponents` | P2 |
| A2 | `Workflow_AbortsWhenTempOrBackupPathPrePlantedAsLink` | P4 |
| A2 | `Workflow_TempNameCarriesEntropyAndUsesExclusiveCreate` | P4 |
| A5 | `Workflow_AbortsWhenContentChangesBetweenCheckAndReplace` | P4 |
| A5 | `Workflow_MetadataOnlyMatchDoesNotSatisfyPositiveGuard` | P4 |
| A6 | `Workflow_PreservesModeSoZeroSixHundredStaysZeroSixHundred` | P4 |
| A6 | `Workflow_AbortsWhenReplaceWouldWidenPermissions` | P4 |
| A7 | `Workflow_ConfirmsOverwriteEvenWhenPickerDeclaresConfirmation` | P4 |
| A8 | `Dialogs_SanitizeAndCapCodecSuppliedWarningText` | P4 |
| A8 | `Dialogs_ShowMostSevereEightWarningsNotFirstEight` | P4 |
| A11 | `Detection_ThrowingDetectorIsDeclinedNotFatal` | P4 |
| A11 | `Detection_DetectorReceivesBoundedHeaderSliceOnly` | P4 |
| A12 | `Workflow_RefusesReadOnlyTargetWithoutClearingAttribute` | P4 |
| A14 | `Workflow_StartupSweepRemovesOnlyPrefixedAgedTempFiles` | P4 |
| B1 | `Workflow_AbortsOverwriteWhenBackupWriteFailsMidway` | P4 |
| B1 | `Workflow_AbortsOverwriteWhenBackupHashMismatchesBaseline` | P4 |
| B2 | `Workflow_FsyncsFileAndContainingDirectoryInOrder` | P4 |
| B2 | `Workflow_AbortsRatherThanFallingBackToDeleteThenMove` | P4 |
| B3 | `Workflow_TargetBytesUnchangedAfterFailureAtEveryStage` | P4 |
| B4 | `Workflow_DowngradesFalsifiedUnknownDataCapabilityClaim` | P4 |
| B4 | `Workflow_DetectsLossySerializerViaPreReplaceRoundTrip` | P4 |
| B5 | `Workflow_CodecThrowFromValidateAfterBackupLeavesTargetIntact` | P4 |
| B5 | `Workflow_CodecThrowMidSerializeNeverReachesReplace` | P4 |
| B6 | `Workflow_DiscardsLateResultFromCancelledOperation` | P4 |
| B8 | `Backup_TwoOverwritesWithinOneSecondDoNotCollide` | P4 |
| B8 | `Backup_RetentionCapAppliesOnlyToFrameworkGrammar` | P4 |
| B10 | `FailurePolicy_SettingsFailSoftWhileSaveFailsLoud` | P4 |
| B11 | `Validation_ErrorsBlockSaveAsToNewPathAndOverwriteAlike` | P4 |

Platform-specific rows run on the platform they describe; cross-volume, removable-media, and destination-held-open replace cases (B2) run on both.

**Fixtures that need a privilege.** Creating a symbolic link on Windows requires elevation or Developer Mode, which GitHub's `windows-latest` runners do not grant a default non-elevated agent. Those tests skip with a stated reason rather than passing vacuously, and junction and hard-link fixtures — neither of which needs a privilege — carry the Windows link coverage instead. A test that silently passes because its fixture could not be built is worse than one that is honestly skipped, so no skip is permitted without a reason string naming the missing capability. If P6 wants the symlink cases to run in CI, the workflow needs an explicit Developer Mode step.

### Headless UI tests

Use xUnit and Avalonia headless testing for shell commands, keyboard shortcuts, menu routing, focus and tab order, section selection, pending/committed transitions, dialogs, and accessible names. Drop paths are injected directly so workflow correctness does not depend on compositor availability.

### Visual regression

Ubuntu is the golden baseline. The harness captures through `Avalonia.Skia` headless rendering at a fixed 1600×1000 logical size, scale 1.0, with the embedded Inter font and animations disabled. Baselines live in `tests/SaveEditor.Ui.HeadlessTests/baselines/`. Comparison uses the `eng/` pixel comparator with a **zero-tolerance** default: any differing pixel fails. A failing comparison **blocks** CI and emits a side-by-side diff artifact; updating a baseline is an explicit committed change reviewed like code.

Covered: Mocha and Latte welcome states, populated shell, dirty/pending state, validation banner, read-only and custom section bodies, appearance menus, and dialogs.

Determinism is itself gated: two runs of the same commit on Ubuntu must produce byte-identical captures.

Behavioral and headless tests run on both Ubuntu and Windows. Screenshot baselines are Ubuntu-only.

**What a baseline cannot do.** A golden image pins whatever was on screen when it
was taken, defects included. The numeric spinner shipped its glyphs clipped to 2px
by a padding-versus-width conflict, and the baselines were seeded while that was
already true — so the gate compared broken against broken and stayed green across
every run. Screenshot comparison catches *change*; it does not establish
*correctness*, and it silently blesses any defect present at seeding time.

Two rules follow. A seeded baseline is reviewed by looking at the image before it
is committed, not merely diffed against its predecessor. And any property worth
guaranteeing — a control being visible, a glyph being legible — is asserted
directly by a test that fails when the property is violated, rather than left to a
reference image to encode.

### Template and platform smoke

Build and run the generated template in a smoke test, exercise its sample fields and theme settings, and validate both NuGet packages install and restore from a local feed.

**The suite has to run on both platforms, and packaging is why.** It was added to
CI late, and its first Linux run failed: the template package shipped with no
content at all. MSBuild translates a forward slash to the platform separator but
does not reliably translate a backslash on Unix, where it is an ordinary filename
character, so the packaging target's staging copy and `Content` glob matched
nothing there while packing correctly on Windows.

Nothing about the symptom pointed at packaging. `dotnet new install` accepted the
empty package, `dotnet new save-editor` resolved the short name and exited 0, and
the first visible evidence was an empty output directory two steps later. The test
now inspects the archive immediately after packing, so an empty package fails at
the step that produced it. A packaging bug that reproduces on one platform is only
found by packing on both.

Provide a manually invoked Wayland-session job that runs the gallery and checks file drop, folder drop, menus, dialogs, resizing, theme switching, and keyboard behavior.

**This is XWayland, not the Wayland protocol, and the distinction is load-bearing.** Avalonia 12.1.1 ships no Wayland backend — no Wayland assembly, no `UseWayland`, and `UsePlatformDetect` resolves to X11 on Linux. The first run of the job proved it by aborting with `XOpenDisplay failed` against a pure Wayland socket. That is not a gap to close but the real deployment shape: on a GNOME or KDE Wayland desktop this application is an X11 client under XWayland, so testing it any other way would test something users never run. It matters for diagnosis — fractional scaling, clipboard, and drag-and-drop all behave differently under XWayland than under a native client, and a tester who assumes otherwise will misattribute what they see.

Accessibility is a release gate: keyboard operation, visible focus, logical tab order, accessible names and descriptions, the contrast ratios of §5, and Windows screen-reader smoke coverage.

## 13. Delivery phases

Each phase has a stable ID, one outcome, explicit prerequisites, and acceptance checks that prove that phase alone. A phase is approvable without approving its successors.

### P0 — Repository, contracts, and safety primitives

**Prerequisites:** none.
**Outcome:** the solution builds on both platforms, every public contract exists, and the path-safety primitive is implemented and tested.

- Solution, `LICENSE`, `THIRD-PARTY-NOTICES`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, package metadata, README skeleton.
- Public interfaces: `ISaveCodec`, codec registry, `IUserInteraction`, `IEditorHost`, settings and recents stores, and `SafePath`.
- **`SafePath` implementation** — moved here from revision 1's Phase 4 because the workflow, settings, recents, and backup paths all depend on it, and because A1 changes the shape of the primitive rather than adding tests to it.
- Palette pin plus the `eng/` generator and semantic resource names.
- Test fixtures, the headless harness, and the screenshot harness skeleton — moved here from revision 1's Phase 6 so later phases can assert visually as they land.
- **Minimal two-platform CI bootstrap** in `.github/workflows/` — restore, build, and test on Windows and Ubuntu, nothing else. Moved here from revision 1's Phase 6 because P0's acceptance asserts two-platform behavior and cannot demonstrate it otherwise. P6 extends this same workflow with the screenshot review gate, the manual Wayland job, and packaging; it does not create CI from scratch.

**Acceptance:** solution restores and builds on Windows and Ubuntu with every dependency at its pinned version; `SafePath` tests pass on both platforms, including `SafePath_RejectsLinkInIntermediateComponent`, `SafePath_RejectsJunctionAndNonSymlinkReparseTags`, `SafePath_ConfirmsWhenHardlinkCountExceedsOne`, `SafePath_RefusesFifoDeviceAndNamedPipe`, and `SafePath_RefusesInputAboveConfiguredSizeCap`; one placeholder headless test and one placeholder screenshot comparison run green in the P0 CI workflow. Every artifact named here is delivered by P0 itself.

**Status: complete.** CI run `32945378542` passed on both `ubuntu-latest` (35s) and `windows-latest` (1m19s), closing the two clauses that could not be evaluated locally: MSBuild does build on Linux, and `actions/setup-dotnet` does acquire the pinned SDK. Verification had already confirmed every other criterion and independently reproduced Linux execution.

The screenshot clause is satisfied by a determinism assertion rather than a stored baseline: baselines are Ubuntu-golden per §12, and one generated on a Windows development machine would be a false artifact that failed CI on first run. P0 also has no real screens to baseline; §12's covered list is all P1–P3 content. Correctness against a stored reference is P1's to add.

### P1 — Theme and primitives

**Prerequisites:** P0.
**Outcome:** both themes and all 14 accents render correctly, meet the §5 contrast contract, and persist across restart.

- Generated Latte/Mocha resources, derived accent ramps, semantic styles, embedded Inter, custom default control themes.
- Settings store *implementation* (interface came from P0) — needed here, not in P4, because §5 requires theme and accent to persist.
- **The settings trust boundary of §6 in full**, including `ApplicationId` validation, bounded deserialization, path validation, lazy recents, and backup preservation. P1 owns this because P1 ships the implementation; leaving the hardening to P4 would ship a security-relevant trust boundary with acceptance covering only theme persistence.
- Gallery token and control pages.

**Acceptance:** a test enumerates 14 accents × 2 modes and asserts every ratio in §5's table against the enumerated text-bearing surfaces; the resource-resolution test proves no view references a raw palette key; the generator reproduces committed resources with no drift; theme and accent survive a simulated restart; the gallery token page produces stable baselines in both modes; and the P1-owned rows of §12's security table pass — A3, A9, A10, B7, and B9.

**Status: complete.** The baseline clause closed once Actions recovered: six references were seeded from an `ubuntu-latest` run and committed, and CI run `32988407970` compares them there with **zero skips in the headless suite** — the comparisons run rather than decline. Windows continues to skip them with its stated reason, since the platforms rasterise text differently and one golden set cannot serve both.

References are stored as PNG. The raw frames were 6.4 MB each, which would have put 38 MB into the repository and another 38 MB on every reseed; the committed set is 472 KB, and PNG is lossless so the comparison stays byte-exact.

Historic note on the original wording: Verification confirmed every other criterion, including that the contrast assertions read the committed XAML rather than recomputing (proved by mutating a shipped accent file), that the drift test catches a hand-edit, and that the accent swap changes what `Primary` actually resolves to at runtime rather than only a field on the controller.

"Produces stable baselines" is currently met by reproducibility and cross-theme divergence, not by comparison against a committed reference — the same deferral as P0, and for the load-bearing half of that reasoning rather than the incidental half: §12 makes Ubuntu the golden baseline, so a Windows-rasterised reference committed from a development machine would fail CI on its first run and destroy trust in the gate at the moment it was introduced. What the present assertions prove is *reproducibility*; what a baseline proves is *correctness against a reviewed reference*. A page that renders wrongly but stably passes today. The colour half of that residual is independently covered by the contrast test against committed XAML; the uncovered part is layout, spacing, and typography — precisely what this slice's control themes and embedded font introduce.

**Bound to the first Ubuntu CI run, together:** seeding the baselines, and wiring animation suppression in the screenshot harness. Suppression is currently unimplemented because nothing rendered so far animates, but this slice ships real control themes; if baselines land before suppression the gate will flap from its first commit.

### P2 — Shell

**Prerequisites:** P0, P1.
**Outcome:** the shell, its menus, and its navigation work end to end against a stubbed document session.

- `EditorShell`, menus, slots, section descriptors, welcome state, sidebar, status bar, responsive behavior, keyboard commands, drag/drop adapter, and the `IEditorHost` seam.
- Open and save commands route through a stubbed `IDocumentSession`; the real codec registry and `SafeFileWorkflow` arrive in P4. Recents render from the P1 settings store.
- The shared path-display formatter of §10, used by the sidebar, status bar, and recents menu.

**Acceptance:** headless tests prove Exit with pending edits raises the guard and does *not* shut down; every menu command routes to its handler; tab order and accessible names are correct; the welcome state lists recents; an injected drop path reaches the same open entry point as the menu; `PathFormatter_StripsBidiAndShowsFinalTwoComponents` passes (A13).

**Status: complete.** Verification initially refuted the "every menu command routes to its handler" clause: `Open Folder…` was bound to the file-open command, and command coverage stood at six of twelve — the untested half being exactly where the mis-route survived. Both are closed, and the guards were mutation-checked rather than assumed.

One lesson is recorded here because it will recur: **a command-level test passes straight through a wrong menu binding.** Two tests now inspect bindings directly — one walking the statically declared menu, one realizing the `ItemsSource`-driven groups (Recent, Themes, Accent), which the static walk cannot see. The second exists because a mis-bound Accent item was demonstrated to ship green against the first.

**Checks deferred to P4.** The stubbed `IDocumentSession` cannot prove the following. This list is finite and is the exact set P4's acceptance refers to:

| ID | Deferred check |
| --- | --- |
| D1 | Open Save decodes a real file through the codec registry and reports detected format |
| D2 | Save As writes through `SafeFileWorkflow` and reports a definitive success or failure status |
| D3 | Overwrite + Backup produces a verified backup and refuses when the backup cannot be verified |
| D4 | Reload re-reads from disk and guards pending edits |
| D5 | A dropped path opens through the real workflow, not merely reaching the entry point |
| D6 | Activating a recent entry opens a real document and prunes a confirmed-missing path at that moment |
| D7 | The status bar reports real path, last backup, progress, and cancellation sourced from the workflow |

### P3 — Editing surface

**Prerequisites:** P2.
**Outcome:** typed fields edit, validate, commit, and undo correctly.

- Field descriptors, `FieldCard`, virtualized `FieldList`, `SectionToolbar`, custom editor templates, pending drafts, Apply and Apply All, validation, history.

**Acceptance:** pending edits survive section navigation; per-field Apply produces exactly one history entry and Apply All exactly one transactional entry; the 1,000-entry cap holds; `FieldList` virtualizes under a large section; dirty/pending and validation-banner baselines are captured using the P0 harness.

**Status: complete.** The baseline clause closed with P1's; the editing-surface references are seeded and compared on Ubuntu. `FieldList` realizes 5 of 2,000 fields, counted as actual containers rather than inferred.

Two decisions recorded because they will look arbitrary later. **A numeric field holds the typed text, not a parsed number** — binding a numeric control directly makes `abc` indistinguishable from zero, and a save where a stat silently became zero is worse than one that refused to apply. Consequently **pending-ness compares the text**: comparing the parsed value reported no pending edit for a field containing unparseable text, and since the exit guard is driven by pending state, closing the editor would have discarded what the user typed without asking.

**Out-of-range values are reported when typed and clamped when stepped.** Typing a number past the bound is a statement the user made, and silently rewriting it would put a value in the save file they never chose; pressing increment means "one more", so stopping at the bound is what was asked for.

### P4 — Services and safety

**Prerequisites:** P0, P1, P2, P3. P1 is required because the workflow consumes the settings and recents store it delivers, and because §12's settings rows are owned there.
**Outcome:** the safe file workflow is implemented and every test-backed `FIX` disposition in §9 is closed by a passing test.

- `SafeFileWorkflow` on the P0 `SafePath` primitive; codec registry and detection; backup, temp write, and atomic replace; permission preservation; round-trip verification; external-change guards; `IUserInteraction` default dialogs; progress, cancellation, and status announcements.
- Replaces P2's stubbed document session with the real workflow.

**Status: complete.** Verification refuted D7 on first pass: the status bar reported the workflow's sentence and path but not progress or the backup location — `SaveProgress` had no production consumer at all, and `SaveOutcome.BackupPath` was populated and never bound. Both are wired now, with a test asserting progress phases are observed during a real overwrite and cleared afterwards.

D1 through D7 pass against the real workflow rather than the stub, which is what makes P2's deferral honest rather than a way of never proving the hard half. Writing them surfaced three gaps the plan had not anticipated: an immutable document type cannot be edited through a session that only exposes a getter, the status bar was composing its own sentences rather than reporting what the workflow did, and activating a recent that had been deleted left the entry in place.

Two behaviours are asymmetric across platforms by design and are asserted as such rather than smoothed over. On Windows an external process **cannot** rewrite the open document at all, because the workflow holds it with write sharing denied; on Linux locks are advisory, so the write lands and the change guard catches it at save time. And a recent whose file is merely unreachable is kept while one confirmed missing is pruned — an unplugged drive is not a deleted save.

**Acceptance:** every P4-owned row of §12's security table passes on both platforms; every §9 row dispositioned `FIX` (excluding the two `FIX (wording)` rows, which have no test by construction) maps to a passing test in that table; and P2's deferred checks D1 through D7 all pass against the real workflow.

### P5 — Template and adoption

**Prerequisites:** P1, P2, P3, P4.
**Outcome:** `dotnet new save-editor` produces a running editor with no manual framework wiring.

**Acceptance:** the template smoke test generates, builds, and runs the app headlessly, exercises a sample field edit and a theme switch, and both packages install and restore from a local feed.

### P6 — Release hardening

**Prerequisites:** P5.
**Outcome:** the `1.0` public contract is frozen and every release gate is green.

- Extend P0's CI workflow with the screenshot review gate, the manual Wayland job, packaging, and the accessibility checklist; finalize third-party notices and package metadata.
- Design review of the gallery against `mockup/index.html`.

**Acceptance:** all gates in §14 pass on a clean checkout.

## 14. Definition of done

V1 is complete when both packages pack and install, the generated app runs without manual framework wiring, the gallery demonstrates every promised control in Latte and Mocha across all 14 accents, the §5 contrast contract passes for every accent and mode against the enumerated text-bearing surfaces, every row of §12's security table passes on its owning platform, every §9 row dispositioned `FIX` appears in that table and passes (the two `FIX (wording)` rows are closed by §8's text and carry no test), P2's deferred checks D1–D7 pass, the Ubuntu screenshot baselines compare clean and reproducibly, the Ubuntu/Windows behavioral checks pass, the Wayland smoke checklist is executable, accessibility gates pass, and the README is sufficient for a new editor author to replace the demo codec — and to understand, from §8, exactly what the framework does and does not defend against.
