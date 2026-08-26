using System.Text;

namespace SaveEditor.Ui.Io;

/// <summary>
/// Purely syntactic screening applied before any syscall touches the path.
/// </summary>
/// <remarks>
/// This runs first so that device namespaces, reserved device names, and traversal
/// components are refused without the filesystem — or, for UNC, the network stack —
/// ever being asked about them. Probing a UNC path is itself the leak described by
/// finding A3, so the refusal has to precede the probe.
/// </remarks>
internal static class PathSyntaxGuard
{
    private static readonly string[] ReservedDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private static readonly char[] WindowsSeparators = ['\\', '/'];
    private static readonly char[] UnixSeparators = ['/'];

    /// <summary>
    /// Screens the path and splits it into a volume root plus the components that
    /// must each be checked.
    /// </summary>
    /// <returns><see langword="null"/> when the path is syntactically acceptable.</returns>
    internal static PathResolution.Refused? Validate(
        string path,
        PathResolutionOptions options,
        out string root,
        out IReadOnlyList<string> components)
    {
        root = string.Empty;
        components = [];

        if (path.Contains('\0', StringComparison.Ordinal))
        {
            return Refuse(PathRefusalReason.InvalidPath, "The path contains an embedded NUL character.");
        }

        var windows = OperatingSystem.IsWindows();

        if (windows)
        {
            var prefixRefusal = ValidateWindowsPrefix(path, options);
            if (prefixRefusal is not null)
            {
                return prefixRefusal;
            }
        }

        if (!Path.IsPathFullyQualified(path))
        {
            return Refuse(
                PathRefusalReason.InvalidPath,
                "The path is not fully qualified. Relative and drive-relative paths resolve against ambient state and are refused.");
        }

        var pathRoot = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(pathRoot))
        {
            return Refuse(PathRefusalReason.InvalidPath, "The path has no volume root.");
        }

        // A drive letter mapped to an SMB share carries the same outbound connection and
        // NTLM exposure as the UNC path it stands for, so it is gated by the same option.
        // GetDriveType reads the local drive table; it reports an existing mapping rather
        // than establishing one, so this stays ahead of any open.
        if (windows && !options.AllowNonLocalPaths &&
            WindowsPathFacts.IsRemoteDriveType(WindowsPathFacts.GetDriveType(pathRoot)))
        {
            return Refuse(
                PathRefusalReason.NonLocalPath,
                "The path is on a mapped network drive. Mapped drives are refused unless PathResolutionOptions.AllowNonLocalPaths is set, for the same reason UNC paths are.");
        }

        var separators = windows ? WindowsSeparators : UnixSeparators;
        var parts = path[pathRoot.Length..].Split(separators);
        var collected = new List<string>(parts.Length);

        foreach (var part in parts)
        {
            if (part.Length == 0 || string.Equals(part, ".", StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(part, "..", StringComparison.Ordinal))
            {
                return Refuse(
                    PathRefusalReason.InvalidPath,
                    "The path contains a '..' component. Traversal components are refused rather than normalized away.");
            }

            if (windows)
            {
                var componentRefusal = ValidateWindowsComponent(part);
                if (componentRefusal is not null)
                {
                    return componentRefusal;
                }
            }

            collected.Add(part);
        }

        if (collected.Count == 0)
        {
            return Refuse(PathRefusalReason.InvalidPath, "The path names a volume root rather than a file.");
        }

        root = pathRoot;
        components = collected;
        return null;
    }

    /// <summary>Rebuilds the checked path for display and logging.</summary>
    internal static string BuildCanonicalPath(string root, IReadOnlyList<string> components, int count)
    {
        var separator = OperatingSystem.IsWindows() ? '\\' : '/';
        var builder = new StringBuilder(root);

        if (builder.Length > 0 && builder[^1] != separator)
        {
            builder.Append(separator);
        }

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(separator);
            }

            builder.Append(components[i]);
        }

        return builder.ToString();
    }

    private static PathResolution.Refused? ValidateWindowsPrefix(string path, PathResolutionOptions options)
    {
        var normalized = path.Replace('/', '\\');

        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return Refuse(
                PathRefusalReason.InvalidPath,
                "Extended-length paths bypass Win32 path normalization, which would defeat the reserved-name and trailing-character checks, and are refused.");
        }

        if (normalized.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return Refuse(PathRefusalReason.InvalidPath, "Device-namespace paths are refused.");
        }

        if (normalized.Contains("GLOBALROOT", StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(PathRefusalReason.InvalidPath, "GLOBALROOT device paths are refused.");
        }

        if (normalized.StartsWith(@"\\", StringComparison.Ordinal) && !options.AllowNonLocalPaths)
        {
            return Refuse(
                PathRefusalReason.NonLocalPath,
                "UNC paths are refused unless PathResolutionOptions.AllowNonLocalPaths is set.");
        }

        return null;
    }

    private static PathResolution.Refused? ValidateWindowsComponent(string component)
    {
        foreach (var c in component)
        {
            if (c < ' ')
            {
                return Refuse(PathRefusalReason.InvalidPath, "The path contains a control character.");
            }

            if (c is '<' or '>' or '"' or '|' or '?' or '*' or ':')
            {
                return Refuse(
                    PathRefusalReason.InvalidPath,
                    "A path component contains a character Windows reserves; a colon additionally names an alternate data stream.");
            }
        }

        if (component[^1] is '.' or ' ')
        {
            return Refuse(
                PathRefusalReason.InvalidPath,
                "A path component ends with a dot or a space. Win32 silently strips those, so the name checked would not be the name opened.");
        }

        var baseName = component;
        var dot = baseName.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            baseName = baseName[..dot];
        }

        baseName = baseName.TrimEnd(' ', '.');

        foreach (var reserved in ReservedDeviceNames)
        {
            if (string.Equals(baseName, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return Refuse(
                    PathRefusalReason.InvalidPath,
                    "A path component names a reserved Windows device.");
            }
        }

        return null;
    }

    private static PathResolution.Refused Refuse(PathRefusalReason reason, string detail) => new(reason, detail);
}
