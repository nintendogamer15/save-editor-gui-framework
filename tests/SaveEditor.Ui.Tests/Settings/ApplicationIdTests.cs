using SaveEditor.Ui.Settings;
using ApplicationId = SaveEditor.Ui.Settings.ApplicationId;

namespace SaveEditor.Ui.Tests.Settings;

/// <summary>
/// Finding A9. <c>ApplicationId</c> becomes a path component under
/// <c>LocalApplicationData</c>, and consuming editors are free to compute it rather
/// than write it as a literal.
/// </summary>
public sealed class ApplicationIdTests
{
    [Fact]
    public void ApplicationId_RejectsTraversalReservedNamesAndSeparators()
    {
        string[] traversal =
        [
            ".",
            "..",
            "../Startup",
            @"..\..\Startup",
            "....",
        ];

        string[] separators =
        [
            "a/b",
            @"a\b",
            "/absolute",
            @"C:\absolute",
            "a:b",
            "a:stream",
            "a\0b",
            "a|b",
            "a*b",
            "a?b",
            "a<b",
            "a>b",
            "a\"b",
        ];

        string[] reserved =
        [
            "CON", "con", "Con",
            "PRN", "AUX", "NUL", "nul",
            "COM1", "com9", "LPT1", "lpt9",
            "CON.txt", "nul.settings", "LPT1.dat.bak",
        ];

        string[] trailing =
        [
            "editor.",
            "editor ",
            "editor  ",
        ];

        string[] shapes =
        [
            string.Empty,
            " ",
            new string('a', 65),
            "editor name",
            "édite",
            "emoji\U0001F600",
        ];

        foreach (var candidate in traversal.Concat(separators).Concat(reserved).Concat(trailing).Concat(shapes))
        {
            Assert.False(
                ApplicationId.TryParse(candidate, out _),
                $"'{candidate}' must not be accepted as an application id.");

            Assert.Throws<ArgumentException>(() => ApplicationId.Parse(candidate));
        }

        string[] acceptable =
        [
            "MyEditor",
            "my-editor",
            "my_editor",
            "my.editor",
            "Editor2",
            "console",
            "COM10",
            "NULL",
            "a",
            new string('a', 64),
        ];

        foreach (var candidate in acceptable)
        {
            Assert.True(
                ApplicationId.TryParse(candidate, out var id),
                $"'{candidate}' is a legitimate application id.");

            Assert.Equal(candidate, id.Value);
        }
    }

    [Fact]
    public void ApplicationId_ThatWasNeverValidatedCannotReachTheStore()
    {
        // A readonly record struct is constructible with `default`, which bypasses
        // Parse entirely. That instance must not be usable as a path component.
        var uninitialized = default(ApplicationId);

        Assert.Throws<ArgumentException>(() => new EditorSettingsStore(uninitialized));
    }

    [Fact]
    public void ApplicationId_BecomesExactlyOnePathComponent()
    {
        using var workspace = new SettingsWorkspace("appid-path");

        var subject = new EditorSettingsStore(ApplicationId.Parse("My.Editor-1"), workspace.Options());

        Assert.Equal(Path.Combine(workspace.Root, "My.Editor-1"), subject.SettingsDirectory);
        Assert.Equal(
            Path.Combine(workspace.Root, "My.Editor-1", "settings.json"),
            subject.SettingsFilePath);

        // The directory the store actually created is a direct child of the base, not
        // somewhere reached by traversal out of it.
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.Root)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(Directory.GetParent(subject.SettingsDirectory)!.FullName)));
    }
}
