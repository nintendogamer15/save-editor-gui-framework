# Save Editor GUI Framework

A reusable Avalonia framework for building **game save editors** on Windows and Linux.

It gives you the parts every save editor needs and nobody enjoys rebuilding: a themed
shell with menus and section navigation, typed field cards with pending-edit and undo
semantics, and a file workflow that goes to considerable lengths not to destroy the
save it is editing. You bring the format.

```sh
dotnet new install SaveEditor.Template
dotnet new save-editor -n MyGameEditor
cd MyGameEditor
dotnet run --project src/MyGameEditor
```

That produces a running editor — themed, with a working demo format, sample fields,
undo/redo, drag-and-drop, recents, and the full safe-save workflow already wired.
Nothing is left as a TODO. Your first real task is deleting the demo codec.

### Before it's on NuGet

Nothing is published yet, so the line above has nothing to install. Build the
packages from a clone instead — same two packages, produced the same way a release
would, and the path CI exercises on every commit:

```sh
git clone https://github.com/nintendogamer15/save-editor-gui-framework
cd save-editor-gui-framework
dotnet pack src/SaveEditor.Ui       -c Release -o ./local-feed
dotnet pack src/SaveEditor.Template -c Release -o ./local-feed
dotnet new install ./local-feed/SaveEditor.Template.1.0.0-alpha.1.nupkg
dotnet new save-editor -n MyGameEditor -o ../MyGameEditor
```

The generated editor consumes `SaveEditor.Ui` as a package rather than as a project
reference, so tell it where that package is. A `NuGet.config` at the generated
project's root, pointing at the feed you just built:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="save-editor-local" value="../save-editor-gui-framework/local-feed" />
  </packageSources>
</configuration>
```

Then `dotnet run --project src/MyGameEditor` as above. Skip that file and restore
fails with `NU1101: Unable to find package SaveEditor.Ui`, because it is not on
nuget.org.

Two things that look like shortcuts and are not. **Installing the template from the
source tree** — `dotnet new install src/SaveEditor.Template/templates/save-editor` —
appears to work and generates a project that cannot restore: the framework version
is a `__SaveEditorUiVersion__` token substituted during `pack`, so installing from
source leaves it literal and restore fails with an `MSB4181` that names nothing
useful. Install the packed `.nupkg`. And **the version in the install command
tracks the package version**, currently `1.0.0-alpha.1` — if you have bumped it,
the filename changes with it.

If you are modifying the framework itself rather than building an editor on it,
skip all of this and work in the repository: `dotnet run --project
samples/SaveEditor.Ui.Gallery` runs the gallery against your working copy directly.

---

## What you get

**A shell.** `EditorShell` is an embeddable `UserControl`, so your application keeps
ownership of its own `Window`. In-window menus (File, Edit, View, Help) on every
platform, a sidebar with data-driven section navigation, a welcome state with recents,
and a status bar that reports what actually happened. Named slots for branding, header
actions, sidebar content, status-bar content, and extra menus.

**Typed fields.** Text, numeric, boolean, choice with autocomplete, read-only, plus
help and warning text. Cards handle labels, validation display, pending-edit
indication, and per-field Apply. The list virtualizes — 2,000 fields realize about
five cards. Custom editors are supplied as templates, never by subclassing.

**Two themes and fourteen accents.** Catppuccin Mocha and Latte. Every text role is
tested at 4.5:1 against every surface it can sit on, for all 14 accents in both modes.
Accent and theme persist across restarts.

**A safe file workflow.** Covered in its own section below, because it is the part
most worth understanding before you trust it.

**Tests you inherit.** The generated project ships with headless tests covering a
field edit, a theme switch, and an open→edit→save round trip. They run without a
display.

---

## Replacing the demo codec

This is the actual first job, and the generated project marks it. Every file you need
to touch carries a `REPLACE ME FIRST` comment.

| File | Replace with |
| --- | --- |
| `Document/DemoSaveDocument.cs` | Your document type |
| `Codecs/DemoSaveCodec.cs` | Your decode / serialize / validate |
| `Codecs/DemoSaveDetector.cs` | Your format detection, from a bounded header slice |
| `Sections/DemoSectionFactory.cs` | Your sections and fields |

A codec is four things:

```csharp
public sealed class MyCodec : ISaveCodec<MySave>
{
    public SaveFormatDescriptor Format { get; } = new("mygame.sav", "My Game Save", ["sav"]);

    // A claim the framework VERIFIES rather than trusts — see below.
    public bool PreservesUnknownData => true;

    public ValueTask<MySave> DecodeAsync(Stream source, CancellationToken ct = default);
    public ValueTask SerializeAsync(MySave doc, Stream destination, CancellationToken ct = default);
    public ValueTask<ValidationReport> ValidateAsync(MySave doc, CancellationToken ct = default);
}
```

Fields are descriptors over accessors into your own type — the framework never reflects
over your document:

```csharp
var health = new NumericFieldViewModel(
    new NumericFieldDescriptor
    {
        Key = "health", Label = "Health", Path = "player.hp",
        Minimum = 0, Maximum = 9999, ShowSpinner = true,
        Read  = () => document.Health,
        Write = value => document.Health = value,
    },
    history);
```

`MainWindow.axaml.cs` composes everything generically over whatever document type and
codecs you register. In most editors you never touch it again.

---

## The safe file workflow

The framework's central promise is that it will not destroy a save file. Concretely:

**Save As is the default write path**, and `Ctrl+S` always means Save As. Overwriting
is a separate, explicitly named command — so the risky choice is a deliberate one and
not a dialog default.

**Every destructive replacement is backed up, all-or-nothing.** That includes a Save As
whose chosen target already exists: it is just as destructive as the command named after
it, and it takes the same backup. Only a Save As to a path that does not exist yet
writes without one, because there is nothing there to lose. The backup is written from
the same retained file handle that produced the change-detection baseline, flushed, and
hash-verified against it. If it cannot be written or cannot be verified, the write is
abandoned and the original is untouched. You never end up with neither a good save nor a
good backup.

**No picker can suppress the overwrite confirmation.** `SaveFilePickResult` still carries
`PickerConfirmedOverwrite`, but the framework confirms every destination it observes to
exist regardless. The OS dialog asks "replace this file?" — it cannot ask "replace this
file, having taken a verified backup, with a codec whose preservation claim reads like
this", which is the question that actually matters here.

**Nothing is written through a path string.** Every filesystem access goes through one
resolver that opens with link-following disabled, checks *every* ancestor directory
rather than just the leaf, records the file's identity, and re-asserts that identity
before each destructive step. Temporary and backup files are created exclusively with
random names, so a pre-planted symlink at a predictable path cannot redirect a write.

**Replacement is atomic, with no fallback.** Flush, fsync, replace, then fsync the
containing directory on Linux. A filesystem that cannot support atomic replacement
aborts with a message naming the limitation rather than degrading to delete-then-move.

**Permissions are preserved.** A `0600` save does not silently become `0644` because
`rename(2)` handed the destination the temp file's mode.

**Your codec's `PreservesUnknownData` claim is tested, not believed.** Immediately
after decoding, the framework re-serializes the untouched document and compares the
result against the source. A codec that declares it preserves unknown data and quietly
drops a checksum region is caught and downgraded to a confirmation prompt instead of
silently destroying part of the file.

The comparison is byte equality by default, and that much the framework proves for
itself. Some formats cannot be byte-identical even when they are perfectly lossless —
anything embedding a fresh random salt or IV, a timestamp, or normalised whitespace. For
those, override `ISaveCodec.RoundTripEquivalent` to say what "same document" means for
your format; an encrypting codec decrypts both sides and compares documents. **Doing so
changes what is proven, and the framework says which it got:** `Verified` means the
framework reproduced the bytes, `VerifiedEquivalent` means your codec was taken at its
word. Both are passes. Only the first is independent of your codec being right.

The reason this seam exists rather than a stricter check: demanding byte-identical
re-serialization from a codec that derives its key and IV from an embedded random salt
would force it to pin that salt across saves — reusing one key and IV across differing
plaintexts. The strict check rewarded a cryptographic regression, and for an AEAD format
nonce reuse is catastrophic rather than merely a leak.

**On failure, the bytes at the target path are exactly the pre-operation bytes.**
Explicitly *not* guaranteed: file identity (`rename` unlinks the original inode on
Linux), hardlink aliasing, views held through other processes' open handles, and
timestamps.

### What this does not defend against

**A hostile codec.** `ISaveCodec` implementations run in-process at full privilege.
The codec boundary is a correctness boundary the framework can bound and instrument —
it is **not a sandbox**. Only install codecs you trust, and review any you did not
write.

**Another program writing the file.** On Windows the workflow holds the open document
with write sharing denied, so an external write is refused outright. On Linux locks are
advisory, so the write lands and the change guard catches it at save time. Neither
covers a game rewriting its own save on exit, or a cloud-sync client rewriting it after
a successful save. The status wording claims only "no change detected between the check
and the write".

**Everything else it deliberately doesn't do:** no autosave, no telemetry, no network
activity, no privilege elevation.

---

## Things that will cost you an afternoon

Found the hard way. Each has a test pinning it.

**A mutable document needs a comparer.** The pre-replace round-trip compares the
decoded document to the in-memory one with `EqualityComparer<T>.Default`. If your
document is a plain class, that is *reference* equality and can never match, so every
save fails. Make it a `record`, override `Equals`, or supply
`SafeFileWorkflowOptions.DocumentComparer`. The framework detects this case and says so
in the failure message.

**Set `PendingEditProbe`.** `DocumentSession` cannot see your drafts — they live on the
field view-models. Leave it unset and the exit guard is blind to typed-but-unapplied
edits, and closing the editor discards them without asking.

**Section bodies own their scrolling.** The shell does not wrap them, because wrapping
starves a virtualizing `FieldList` of the viewport it needs. Wrap non-scrolling content
yourself.

**`Primary` is fill-only.** Use `PrimaryText` for accent-coloured text and `FocusRing`
for focus. Eleven of the fourteen raw Latte accents fall below even the 3:1 non-text
contrast floor, so a raw-accent focus ring is invisible in the light theme.

**Out-of-range values are reported, not clamped.** Silently rewriting a number the user
typed puts a value in their save file they never chose.

---

## Platform reality

**Windows** and **Linux**. macOS is out of scope.

Avalonia has no Wayland backend, so on a Wayland desktop the app is an X11 client under
XWayland. That is the shape everyone actually runs, and it matters for diagnosis:
fractional scaling, clipboard, and drag-and-drop behave differently under XWayland than
under a native client, so a surprise there is usually XWayland rather than the
framework.

Some behaviour is genuinely asymmetric and the framework says so rather than pretending
otherwise — the external-write protection above is the main one.

---

## Repository layout

```text
src/SaveEditor.Ui                  the framework package
src/SaveEditor.Template            the dotnet new template package
samples/SaveEditor.Ui.Gallery      the shell, hosting live token/control/editing catalogues
tests/SaveEditor.Ui.Tests          unit and security tests
tests/SaveEditor.Ui.HeadlessTests  headless UI, screenshots, shell behaviour
tests/SaveEditor.Template.Tests    pack → install → generate → build → run
eng/                               palette generator, pixel comparator, pinned public API
docs/                              accessibility and Wayland release checklists
mockup/index.html                  the original design prototype
```

## Building and testing

Requires the .NET 10 SDK (pinned in `global.json`). Avalonia is major version **12**.

```sh
dotnet build -c Release
dotnet test  -c Release
```

Tests run on Microsoft.Testing.Platform rather than VSTest. CI invokes the test
executables directly rather than through `dotnet test`, because that bridge reports
success when tests *error* during host setup — which once left 87 tests unrun behind a
green tick.

Run the gallery to see everything:

```sh
dotnet run --project samples/SaveEditor.Ui.Gallery
```

Two release gates need a person and are documented rather than automated:
[`docs/ACCESSIBILITY.md`](docs/ACCESSIBILITY.md) (screen-reader pass) and
[`docs/WAYLAND-CHECKLIST.md`](docs/WAYLAND-CHECKLIST.md) (drag-and-drop against a real
compositor). Both list what is already enforced by tests so you only spend attention on
what is not.

## Status and versioning

The public surface is pinned in `eng/PublicApi.SaveEditor.Ui.txt`; any change to it
fails the build, so the "breaking changes require a major version" promise is enforced
rather than merely stated.

Packages are `1.0.0-alpha.1`. They stay prerelease until the two human gates above have
actually been run — shipping `1.0.0` while a named release gate has never been executed
would claim more than the evidence supports.

For the full design rationale, the security findings and their dispositions, and the
phase-by-phase record, see [PLAN.md](PLAN.md).

## Licence

Framework source is **0BSD**. Bundled components and their licences are listed in
[THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES).
