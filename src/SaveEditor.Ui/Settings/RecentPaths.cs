using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Settings;

/// <summary>
/// Screening and comparison rules for paths that come out of, or go into, the
/// settings file.
/// </summary>
/// <remarks>
/// <para>
/// Every rule here is purely syntactic. Nothing in this class touches the filesystem
/// or the network, which is the point: a stored path must be judged before anything
/// probes it, because the probe is the exposure.
/// </para>
/// <para>
/// The screening itself is <c>PathSyntaxGuard</c>, the same primitive the safe file
/// workflow screens with. A second implementation living here would be a second thing
/// to keep correct, and the two would drift in the direction of the one nobody is
/// testing.
/// </para>
/// </remarks>
public static class RecentPaths
{
    /// <summary>
    /// Longest stored path accepted. Comfortably past Windows' 260-character limit and
    /// past any Linux <c>PATH_MAX</c>, while still bounding a hostile file.
    /// </summary>
    public const int MaxPathLength = 4096;

    private static readonly PathResolutionOptions ScreeningOptions = new()
    {
        Mode = PathResolutionMode.OpenExisting,
        AllowNonLocalPaths = false,
    };

    /// <summary>
    /// The comparison used to decide that two stored paths name the same file.
    /// </summary>
    /// <remarks>
    /// Ordinal on Linux, ordinal-ignore-case on Windows. Case-insensitive comparison on
    /// Linux would merge <c>Save.dat</c> and <c>Save.DAT</c>, which are two different
    /// files; a recents entry that opens a file other than the one the user believes is
    /// a data-loss hazard in a tool whose entire purpose is not destroying saves.
    /// </remarks>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Reports whether a path may be stored in, or restored from, settings.</summary>
    /// <param name="path">The candidate path.</param>
    /// <returns><see langword="true"/> when the path is rooted, local, and well-formed.</returns>
    public static bool IsStorable(string? path) => Screen(path) is null;

    /// <summary>
    /// Screens a path, returning operator-facing detail when it is refused.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <returns><see langword="null"/> when the path is acceptable.</returns>
    public static string? Screen(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "The path is empty.";
        }

        if (path.Length > MaxPathLength)
        {
            return $"The path is longer than {MaxPathLength} characters.";
        }

        foreach (var c in path)
        {
            // C0 and C1 controls. Never legitimate in a path the user chose, and a
            // reliable marker of a path built to be displayed as something it is not.
            if (char.IsControl(c))
            {
                return "The path contains a control character.";
            }
        }

        var deviceSyntax = ScreenWindowsDeviceSyntax(path);
        if (deviceSyntax is not null)
        {
            return deviceSyntax;
        }

        var refusal = PathSyntaxGuard.Validate(path, ScreeningOptions, out _, out _);
        return refusal?.Detail;
    }

    /// <summary>
    /// Screens, deduplicates, and caps a sequence of stored paths, keeping the first
    /// occurrence of each.
    /// </summary>
    /// <param name="paths">Raw paths, most recent first.</param>
    /// <param name="capacity">Largest number of entries to keep.</param>
    /// <param name="rejected">Number of entries dropped because they failed screening.</param>
    /// <returns>The screened, deduplicated, capped list.</returns>
    public static IReadOnlyList<string> Normalize(
        IEnumerable<string?>? paths,
        int capacity,
        out int rejected) =>
        Normalize(paths, capacity, Comparer, out rejected);

    /// <summary>
    /// Moves a path to the front of an existing list, deduplicating and capping.
    /// </summary>
    /// <param name="existing">The current list, most recent first.</param>
    /// <param name="path">The path to promote.</param>
    /// <param name="capacity">Largest number of entries to keep.</param>
    /// <returns>The new list, or the original when the path fails screening.</returns>
    public static IReadOnlyList<string> Promote(
        IReadOnlyList<string> existing,
        string path,
        int capacity)
    {
        ArgumentNullException.ThrowIfNull(existing);

        if (!IsStorable(path))
        {
            return existing;
        }

        IEnumerable<string?> combined = [path, .. existing];
        return Normalize(combined, capacity, Comparer, out _);
    }

    /// <summary>
    /// Deduplicates against an explicit comparer.
    /// </summary>
    /// <remarks>
    /// Internal so that both platforms' comparison semantics can be exercised from
    /// either platform. <see cref="Comparer"/> is the only comparer production code
    /// uses; a public overload would let a consumer choose the wrong one.
    /// </remarks>
    internal static IReadOnlyList<string> Normalize(
        IEnumerable<string?>? paths,
        int capacity,
        StringComparer comparer,
        out int rejected)
    {
        rejected = 0;

        if (paths is null || capacity <= 0)
        {
            return [];
        }

        var kept = new List<string>(Math.Min(capacity, 16));
        var seen = new HashSet<string>(comparer);

        foreach (var candidate in paths)
        {
            if (kept.Count >= capacity)
            {
                break;
            }

            if (candidate is null || !IsStorable(candidate))
            {
                rejected++;
                continue;
            }

            if (seen.Add(candidate))
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }

    /// <summary>
    /// Refuses Windows UNC and device-namespace syntax on every platform.
    /// </summary>
    /// <remarks>
    /// <c>PathSyntaxGuard</c> already refuses these on Windows, and refuses them on
    /// Linux only incidentally, by way of their not being fully qualified there. The
    /// settings file is explicitly expected to arrive from a roaming profile, so the
    /// same bytes are read by both platforms and the refusal reason should not depend
    /// on which one is reading. This runs first so that a roamed UNC entry is refused
    /// as non-local rather than as merely malformed.
    /// </remarks>
    private static string? ScreenWindowsDeviceSyntax(string path)
    {
        var normalized = path.Replace('/', '\\');

        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return "Device-namespace and extended-length paths are refused.";
        }

        if (normalized.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase))
        {
            return "GLOBALROOT device paths are refused.";
        }

        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return "UNC paths are refused. Probing one opens an outbound SMB connection and attempts NTLM authentication.";
        }

        return null;
    }
}
