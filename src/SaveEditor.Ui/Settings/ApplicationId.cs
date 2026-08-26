using System.Buffers;
using System.Diagnostics.CodeAnalysis;

namespace SaveEditor.Ui.Settings;

/// <summary>
/// A validated identifier naming the consuming editor's settings directory.
/// </summary>
/// <remarks>
/// <para>
/// Settings live at <c>LocalApplicationData/&lt;ApplicationId&gt;/settings.json</c>,
/// so this value becomes a path component. It is a public API surface consumed by
/// third-party editors, and nothing guarantees it is a literal — an editor may
/// compute it from a config file, an assembly attribute, or a command-line switch.
/// Values such as <c>..\..\Startup</c>, <c>CON</c>, or a name containing <c>:</c>
/// would otherwise redirect the settings write out of the intended directory or
/// into a reserved name or NTFS alternate data stream.
/// </para>
/// <para>
/// An invalid identifier throws at construction rather than silently falling back
/// to a default directory, so the "stable" requirement is enforceable rather than
/// aspirational.
/// </para>
/// </remarks>
public readonly record struct ApplicationId
{
    private const int MaxLength = 64;

    private static readonly SearchValues<char> AllowedCharacters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-");

    // Reserved on Windows regardless of extension: "CON.txt" is still CON.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private ApplicationId(string value) => Value = value;

    /// <summary>The validated identifier.</summary>
    public string Value { get; }

    /// <summary>Validates and creates an identifier.</summary>
    /// <param name="value">The candidate identifier.</param>
    /// <returns>The validated identifier.</returns>
    /// <exception cref="ArgumentException">The value is not a valid identifier.</exception>
    public static ApplicationId Parse(string value) =>
        TryParse(value, out var id)
            ? id
            : throw new ArgumentException(
                $"'{value}' is not a valid application id. Use 1-{MaxLength} characters from " +
                "A-Z, a-z, 0-9, dot, underscore, or hyphen; it must not be '.' or '..', must not " +
                "end with a dot or space, and must not be a reserved device name.",
                nameof(value));

    /// <summary>Validates an identifier without throwing.</summary>
    /// <param name="value">The candidate identifier.</param>
    /// <param name="id">The validated identifier when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the value is a valid identifier.</returns>
    public static bool TryParse([NotNullWhen(true)] string? value, out ApplicationId id)
    {
        id = default;

        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        if (value.AsSpan().ContainsAnyExcept(AllowedCharacters))
        {
            return false;
        }

        if (value is "." or "..")
        {
            return false;
        }

        // Trailing dots and spaces are silently stripped by Windows, so a name
        // ending in one does not round-trip to the directory it appears to name.
        if (value[^1] is '.' or ' ')
        {
            return false;
        }

        var stem = value.AsSpan();
        var dot = stem.IndexOf('.');
        if (dot >= 0)
        {
            stem = stem[..dot];
        }

        if (ReservedNames.Contains(stem.ToString()))
        {
            return false;
        }

        id = new ApplicationId(value);
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
