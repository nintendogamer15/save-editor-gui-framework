Catppuccin palette, vendored for deterministic offline builds.

Source:   https://github.com/catppuccin/palette
File:     palette.json
Revision: 07d02aa110ef9eb7e7427afca5c73ba9cf7f8ebd
License:  MIT (see THIRD-PARTY-NOTICES)

Do not edit. To move the pin, replace this file, update the revision above and
in THIRD-PARTY-NOTICES and PLAN.md section 3, regenerate theme resources, and
review the resulting contrast-test and screenshot diffs.

Regenerating theme resources
----------------------------
    dotnet run --project eng/SaveEditor.PaletteGen -- src/SaveEditor.Ui/Themes

ThemeResourceDriftTests regenerates in memory and compares against the committed
files, so forgetting to run this fails the build rather than shipping resources
that disagree with the palette.
