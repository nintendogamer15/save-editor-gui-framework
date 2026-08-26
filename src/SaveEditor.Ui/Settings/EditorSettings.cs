namespace SaveEditor.Ui.Settings;

/// <summary>The two supported theme modes.</summary>
public enum ThemeMode
{
    /// <summary>Catppuccin Mocha. The default.</summary>
    Dark,

    /// <summary>Catppuccin Latte.</summary>
    Light,
}

/// <summary>The fourteen Catppuccin accents.</summary>
public enum CatppuccinAccent
{
    /// <summary>Rosewater.</summary>
    Rosewater,
    /// <summary>Flamingo.</summary>
    Flamingo,
    /// <summary>Pink.</summary>
    Pink,
    /// <summary>Mauve.</summary>
    Mauve,
    /// <summary>Red.</summary>
    Red,
    /// <summary>Maroon.</summary>
    Maroon,
    /// <summary>Peach.</summary>
    Peach,
    /// <summary>Yellow.</summary>
    Yellow,
    /// <summary>Green.</summary>
    Green,
    /// <summary>Teal.</summary>
    Teal,
    /// <summary>Sky.</summary>
    Sky,
    /// <summary>Sapphire.</summary>
    Sapphire,
    /// <summary>Blue.</summary>
    Blue,
    /// <summary>Lavender.</summary>
    Lavender,
}

/// <summary>Persisted user preferences.</summary>
/// <remarks>
/// Deliberately excluded: window position, and any document content. Drafts are
/// never persisted, so an interrupted session cannot silently resurrect edits the
/// user did not commit.
/// </remarks>
public sealed record EditorSettings
{
    /// <summary>Largest number of retained file recents.</summary>
    public const int MaxRecentFiles = 10;

    /// <summary>Largest number of retained folder recents.</summary>
    public const int MaxRecentFolders = 10;

    /// <summary>Current settings schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of the loaded document.</summary>
    /// <remarks>
    /// An unknown or absurd value routes to the malformed-settings path rather than
    /// to the newest migrator, because this field comes from an untrusted file and
    /// selects which code runs.
    /// </remarks>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Selected theme mode.</summary>
    public ThemeMode Theme { get; init; } = ThemeMode.Dark;

    /// <summary>
    /// User-selected accent, or <see langword="null"/> to follow the editor default.
    /// </summary>
    public CatppuccinAccent? Accent { get; init; }

    /// <summary>Recently opened files, most recent first.</summary>
    public IReadOnlyList<string> RecentFiles { get; init; } = [];

    /// <summary>Recently opened folders, most recent first.</summary>
    public IReadOnlyList<string> RecentFolders { get; init; } = [];

    /// <summary>Key of the section selected when the editor last closed.</summary>
    public string? LastSectionKey { get; init; }

    /// <summary>Persisted window width, or <see langword="null"/> if never recorded.</summary>
    public double? WindowWidth { get; init; }

    /// <summary>Persisted window height, or <see langword="null"/> if never recorded.</summary>
    public double? WindowHeight { get; init; }

    /// <summary>Compares by value, including the contents of the recents lists.</summary>
    /// <param name="other">The instance to compare with.</param>
    /// <returns><see langword="true"/> when every field and every recent entry matches.</returns>
    /// <remarks>
    /// The compiler-generated equality would compare the recents lists by reference,
    /// because <see cref="IReadOnlyList{T}"/> has no value equality. Two settings
    /// objects holding equal-but-distinct lists would then compare unequal, and any
    /// consumer using this record for dirty tracking would see a change on every
    /// load. Recents are compared ordinally here regardless of platform: this is
    /// asking whether two in-memory objects are the same, not whether two paths name
    /// the same file, which is the platform-sensitive question handled where recents
    /// are deduplicated.
    /// </remarks>
    public bool Equals(EditorSettings? other) =>
        other is not null
        && SchemaVersion == other.SchemaVersion
        && Theme == other.Theme
        && Accent == other.Accent
        && string.Equals(LastSectionKey, other.LastSectionKey, StringComparison.Ordinal)
        && Nullable.Equals(WindowWidth, other.WindowWidth)
        && Nullable.Equals(WindowHeight, other.WindowHeight)
        && RecentFiles.SequenceEqual(other.RecentFiles, StringComparer.Ordinal)
        && RecentFolders.SequenceEqual(other.RecentFolders, StringComparer.Ordinal);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = default(HashCode);

        hash.Add(SchemaVersion);
        hash.Add(Theme);
        hash.Add(Accent);
        hash.Add(LastSectionKey, StringComparer.Ordinal);
        hash.Add(WindowWidth);
        hash.Add(WindowHeight);

        foreach (var path in RecentFiles)
        {
            hash.Add(path, StringComparer.Ordinal);
        }

        foreach (var path in RecentFolders)
        {
            hash.Add(path, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }
}
