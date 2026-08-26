using SaveEditor.PaletteGen;

// Writes the generated theme resources. Invoked as:
//
//     dotnet run --project eng/SaveEditor.PaletteGen -- src/SaveEditor.Ui/Themes
//
// A drift test regenerates in memory and compares, so forgetting to run this
// fails the build rather than shipping resources that disagree with the palette.

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: SaveEditor.PaletteGen <output-directory>");
    return 2;
}

var outputDirectory = args[0];

foreach (var (relativePath, content) in ThemeGenerator.GenerateAll())
{
    var destination = Path.Combine(outputDirectory, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

    // Written with LF explicitly: the drift test compares generator output against
    // these bytes, and a platform-dependent newline would make it fail on one OS.
    await File.WriteAllTextAsync(destination, content).ConfigureAwait(false);
    Console.WriteLine($"wrote {relativePath}");
}

return 0;
