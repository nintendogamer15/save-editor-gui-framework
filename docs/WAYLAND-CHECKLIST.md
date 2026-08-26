# Wayland smoke checklist

`PLAN.md` §12 requires a manually invoked real-Wayland job covering file drop,
folder drop, menus, dialogs, resizing, theme switching, and keyboard behaviour.

The reason it is manual is worth stating, because it is easy to assume a green CI
job covers it. **It does not, and cannot.** The headless tests deliberately inject
drop paths straight into the view-model rather than simulating a drop, so that
workflow correctness never depends on a compositor being available. That is the
right trade — but it means the compositor half of drag-and-drop is exercised by
nothing except a human doing it.

## Automated part

`.github/workflows/wayland.yml` (workflow_dispatch) launches the gallery under a
headless Weston session on `ubuntu-latest`. It proves the app **starts, renders,
and survives** under a real Wayland compositor. That is a genuine smoke test — an
Avalonia backend failure or a missing native dependency shows up here — but it
proves nothing about interaction.

## Manual part — required before release

On a real Wayland session (GNOME or KDE, not XWayland — check with
`echo $XDG_SESSION_TYPE`, which must print `wayland`):

```sh
dotnet run --project samples/SaveEditor.Ui.Gallery
```

### Drag and drop

This is the section that exists because nothing else covers it.

- [ ] Dragging a file from the file manager onto the shell opens it
- [ ] Dragging a **folder** is handled — either opened or cleanly declined, never
      silently ignored
- [ ] Dragging a file from a browser download shelf works, or fails visibly
- [ ] Dropping while a document has unapplied edits raises the discard guard —
      a drop must not bypass the prompt a menu open would have raised
- [ ] Dropping onto the sidebar and the status bar behaves the same as the content
      area, or is rejected consistently

### Windowing

- [ ] Resizing is smooth and the layout reflows; the status bar does not clip
- [ ] Maximise, restore, and tile to half-screen
- [ ] The window is usable at 1024×720
- [ ] On a mixed-DPI multi-monitor setup, dragging between monitors rescales
      without blurring or clipping
- [ ] Closing with unapplied edits raises the guard and **cancelling leaves the
      window open**

### Menus and dialogs

- [ ] Every menu opens, positions on-screen, and closes on Escape
- [ ] Submenus (Appearance → Themes, Appearance → Accent) position correctly near
      a screen edge
- [ ] A confirmation dialog centres on its owner and is modal
- [ ] The file picker is the portal picker, and returns a usable path
- [ ] The folder picker likewise

### Theme and accent

- [ ] Switching Dark ↔ Light repaints everything, with no stale panels
- [ ] Switching accent repaints focus rings and accent text, not just fills
- [ ] Both survive a restart

### Keyboard

- [ ] Ctrl+O, Ctrl+S, Ctrl+Z, Ctrl+Y, Ctrl+R, Ctrl+W all fire
- [ ] Menu access keys work
- [ ] Focus is visible throughout — see `ACCESSIBILITY.md`

## Recording the result

Note the date, commit, distribution, compositor and version, and anything that
deviated. An unrecorded run cannot be distinguished from one that never happened.
