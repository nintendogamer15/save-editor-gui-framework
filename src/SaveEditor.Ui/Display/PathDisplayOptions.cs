namespace SaveEditor.Ui.Display;

/// <summary>Which characters count as path separators when splitting for display.</summary>
/// <remarks>
/// This is a display decision, not a filesystem one, and it has to be explicit because
/// the two platforms disagree about a character rather than about a convention. On
/// Windows both <c>\</c> and <c>/</c> separate components. On Linux and macOS only
/// <c>/</c> does, and <c>\</c> is an ordinary, legal character inside a filename.
/// Splitting a Linux name on <c>\</c> would present one component as two and could
/// therefore show the wrong "final two components" -- the exact information A13 exists to
/// protect.
/// </remarks>
public enum PathSeparatorStyle
{
    /// <summary>Follow the platform the process is running on. The default.</summary>
    Native,

    /// <summary>Treat both <c>\</c> and <c>/</c> as separators and render with <c>\</c>.</summary>
    /// <remarks>
    /// Also correct when a Linux session displays a path that roamed in from a Windows
    /// settings file, which the settings store expects to happen.
    /// </remarks>
    Windows,

    /// <summary>Treat only <c>/</c> as a separator and render with <c>/</c>.</summary>
    Posix,
}

/// <summary>Configuration for <see cref="PathDisplayFormatter"/>.</summary>
public sealed record PathDisplayOptions
{
    /// <summary>
    /// Characters the formatter aims to fit into, excluding the two zero-width isolate
    /// characters.
    /// </summary>
    /// <remarks>
    /// A target, not a guarantee -- see <see cref="PathLabel.ExceedsMaxLength"/>. Values
    /// below one are clamped rather than rejected, because a formatter feeding a status
    /// bar during an error report must not be the thing that throws.
    /// </remarks>
    public int MaxLength { get; init; } = PathDisplayFormatter.DefaultMaxLength;

    /// <summary>How to split and render separators.</summary>
    public PathSeparatorStyle SeparatorStyle { get; init; } = PathSeparatorStyle.Native;
}
