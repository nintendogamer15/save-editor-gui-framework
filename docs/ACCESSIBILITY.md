# Accessibility release gate

`PLAN.md` §12 makes accessibility a release gate: keyboard operation, visible
focus, logical tab order, accessible names and descriptions, the contrast ratios
of §5, and Windows screen-reader smoke coverage.

Most of that is already enforced by tests. This checklist exists so a human
spends their attention on the part that cannot be automated, rather than
re-checking what CI already fails on.

## Already gated — do not re-test by hand

Each of these fails the build if it regresses. If you are checking one manually,
either the test is missing or you have found a gap worth reporting.

| Requirement | Enforced by |
| --- | --- |
| Every semantic text role meets 4.5:1 on all four text-bearing surfaces | `ContrastContractTests`, `ThemeResourceTests` — 14 accents × 2 modes, asserted against the **committed XAML**, not the generator |
| Focus rings and stateful borders are perceivable | They resolve to the derived `PrimaryText` ramp, never raw `Primary`; eleven of fourteen raw Latte accents fall below even 3:1 |
| Text on an accent fill is legible | `OnPrimaryForeground` picks a pure endpoint per accent; worst case 4.91:1 |
| Header actions are reachable by Tab, in visual order | `Header_Actions_Are_Reachable_By_Tab_In_Visual_Order` — presses Tab twenty times, so an action pushed out of reach fails rather than being missed |
| Focusable controls carry accessible names | `Every_Focusable_Header_Control_Carries_An_Accessible_Name` |
| Section navigation is exposed to assistive technology | `Section_Navigation_Is_Exposed_To_Assistive_Technology` |
| Status and progress are announced | `AutomationProperties.LiveSetting` on the status text and progress; errors map to assertive, everything else polite |
| Paths reach screen readers unabbreviated | Every path surface carries `AutomationProperties.HelpText` = the full label; the truncated label is display-only |
| Validation messages are associated with their input | `FieldCard` tests |
| No view references a raw palette name | `Views_Reference_Only_Semantic_Resources`, `Code_Built_Templates_Reference_Only_Semantic_Resources` |

## Manual checks — required before release

Run against the gallery (`dotnet run --project samples/SaveEditor.Ui.Gallery`) in
**both themes** and with at least two accents, including a light-theme accent.

### Screen reader — Windows

Use Narrator or NVDA. This is the only part with no automated coverage at all.

- [ ] Tabbing through the shell announces each control's purpose, not just its type
- [ ] The section list announces the current section and the total count
- [ ] Changing section announces the new section
- [ ] A field announces its label, its value, and its validation message when invalid
- [ ] Applying a field announces the outcome
- [ ] Attempting to close with unapplied edits announces the confirmation, and the
      accept button announces the **verb** ("Discard and exit"), never "OK"
- [ ] The status bar announces outcomes without stealing focus
- [ ] A path containing right-to-left text is announced as the actual path, not a
      reordered one

### Keyboard only — unplug the mouse

- [ ] Every menu is reachable and operable, including the Appearance submenus
- [ ] Every field is reachable, editable, and applicable
- [ ] Apply All and Revert All are reachable from the section toolbar
- [ ] Focus is visible at every stop, in both themes, on every surface a control
      sits on — including on a card, which is the lowest-contrast surface
- [ ] Focus never lands on something invisible or off-screen
- [ ] Focus is not trapped anywhere; Escape closes dialogs
- [ ] After a dialog closes, focus returns somewhere sensible

### Visual

- [ ] Nothing conveys state by colour alone — check pending fields and validation
      errors specifically, which carry border weight as well as colour
- [ ] At 200% OS scaling nothing overlaps or clips
- [ ] The window is usable at 1024×720

## Recording the result

Note the date, the commit, the screen reader and version, and any deviation. A
checklist that was run but not recorded is indistinguishable from one that was
not run.
