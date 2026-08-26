using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Settings;

/// <summary>
/// Limits and injected collaborators for <see cref="EditorSettingsStore"/>.
/// </summary>
/// <remarks>
/// Every bound here is enforced on read as well as on write. A store that only bounded
/// what it wrote would be bounding the one party that was never the threat: the file is
/// user-writable, and may arrive from a roaming profile or a restored backup written by
/// a different version, a different machine, or a different program entirely.
/// </remarks>
public sealed record EditorSettingsStoreOptions
{
    /// <summary>Largest settings file the parser is allowed to see, in bytes.</summary>
    /// <remarks>
    /// A legitimate file with ten file recents and ten folder recents is well under two
    /// kilobytes. This is generous by two orders of magnitude and still bounds the read.
    /// </remarks>
    public long MaxFileBytes { get; init; } = 256 * 1024;

    /// <summary>
    /// Largest file the store will still copy aside when it turns out to be unusable.
    /// </summary>
    /// <remarks>
    /// Between <see cref="MaxFileBytes"/> and this, the file is too big to parse but
    /// small enough to preserve, so it is backed up and replaced. Past this it is left
    /// exactly where it is: the framework will not read an unbounded amount of data in
    /// order to be polite about something it has already refused.
    /// </remarks>
    public long MaxBackupCopyBytes { get; init; } = 16L * 1024 * 1024;

    /// <summary>Deepest permitted JSON nesting.</summary>
    public int MaxDepth { get; init; } = 16;

    /// <summary>Longest permitted JSON string or property name, in raw bytes.</summary>
    public int MaxStringLength { get; init; } = 1024;

    /// <summary>
    /// Largest permitted JSON array, as a structural limit distinct from the recents cap.
    /// </summary>
    /// <remarks>
    /// The recents caps in <see cref="EditorSettings"/> are the product rule: a list of
    /// eleven is truncated to ten and the file is otherwise fine. This is the structural
    /// rule: a list of a hundred thousand is not an overlong recents list, it is an
    /// attempt to make the process allocate, and it is refused during the scan without
    /// a single entry being constructed.
    /// </remarks>
    public int MaxArrayElements { get; init; } = 64;

    /// <summary>How many malformed-settings backups are retained.</summary>
    /// <remarks>
    /// Retention applies only to files matching the framework's own backup grammar.
    /// Anything else in the settings directory belongs to somebody else.
    /// </remarks>
    public int BackupRetention { get; init; } = 5;

    /// <summary>Time box applied to one lazy recents existence check.</summary>
    public TimeSpan RecentProbeTimeout { get; init; } = RecentsList.DefaultProbeTimeout;

    /// <summary>
    /// Directory the per-application settings directory is created under.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="Environment.SpecialFolder.LocalApplicationData"/>. Tests
    /// point it at a temporary directory; a portable install may point it beside the
    /// executable. It is never derived from the settings file itself.
    /// </remarks>
    public string BaseDirectory { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The path resolver used for every filesystem access the store makes.</summary>
    public ISafePathResolver PathResolver { get; init; } = new SafePathResolver();

    /// <summary>The screen areas a restored window size is clamped against.</summary>
    public IScreenBoundsSource ScreenBounds { get; init; } = UnknownScreenBoundsSource.Instance;

    /// <summary>The lazy existence check used by recents lists this store creates.</summary>
    public IRecentEntryProbe RecentProbe { get; init; } = FileSystemRecentEntryProbe.Instance;

    /// <summary>Smallest window width restored from settings.</summary>
    public double MinWindowWidth { get; init; } = 320;

    /// <summary>Smallest window height restored from settings.</summary>
    public double MinWindowHeight { get; init; } = 240;

    /// <summary>
    /// Largest window extent that is treated as a plausible value at all.
    /// </summary>
    /// <remarks>
    /// Values past this are rejected outright rather than clamped. A stored width of
    /// 3840 on a 1920-wide screen is a user who unplugged a monitor, and clamping is
    /// the right answer. A stored width of <see cref="int.MaxValue"/> is not a window
    /// anybody ever had, and treating it as one — even a clamped one — would be
    /// dressing up a tampered value as a preference.
    /// </remarks>
    public double MaxPlausibleWindowExtent { get; init; } = 32768;
}
