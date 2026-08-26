using System.Text;
using System.Text.Json;

namespace SaveEditor.Ui.Tests.Settings;

/// <summary>
/// One tampered <c>settings.json</c> variant.
/// </summary>
/// <param name="Name">What the variant is called in failure output.</param>
/// <param name="Json">The literal file contents.</param>
/// <param name="Why">What the variant is trying to achieve.</param>
internal sealed record TamperedSettings(string Name, string Json, string Why)
{
    public override string ToString() => Name;
}

/// <summary>
/// The tampered settings corpus.
/// </summary>
/// <remarks>
/// Generated rather than committed as files. The build constraint is that fixtures never
/// land in the repository, and a corpus of hostile JSON is exactly the kind of fixture
/// that gets committed once and then quietly scanned, quarantined, or "fixed" by tooling
/// that does not know what it is looking at.
/// </remarks>
internal static class TamperedSettingsCorpus
{
    /// <summary>A path that passes screening on the running platform.</summary>
    internal static string GoodPath { get; } = SettingsWorkspace.LocalPath("slot1.dat");

    internal static TamperedSettings UncRecentFile { get; } = new(
        nameof(UncRecentFile),
        Document(recentFiles: [@"\\attacker-host\share\slot1.dat", GoodPath]),
        "A recents entry whose mere existence check opens an SMB connection and offers an NTLM handshake.");

    internal static TamperedSettings DeviceNamespaceRecentFile { get; } = new(
        nameof(DeviceNamespaceRecentFile),
        Document(recentFiles: [@"\\.\PhysicalDrive0", @"\\?\C:\slot1.dat", GoodPath]),
        "Device-namespace paths, which bypass Win32 normalization and so bypass every name-based check.");

    internal static TamperedSettings GlobalRootRecentFile { get; } = new(
        nameof(GlobalRootRecentFile),
        Document(recentFiles: [@"\\?\GLOBALROOT\Device\HarddiskVolume2\slot1.dat", GoodPath]),
        "A GLOBALROOT path reaching the raw device namespace.");

    internal static TamperedSettings RelativeRecentFile { get; } = new(
        nameof(RelativeRecentFile),
        Document(recentFiles: ["slot1.dat", GoodPath]),
        "A relative path, which resolves against whatever the process working directory happens to be.");

    internal static TamperedSettings TraversalRecentFile { get; } = new(
        nameof(TraversalRecentFile),
        Document(recentFiles: [OperatingSystem.IsWindows() ? @"C:\saves\..\..\Windows\System32\config\SAM" : "/var/saves/../../etc/shadow", GoodPath]),
        "Traversal components, refused rather than normalized away.");

    internal static TamperedSettings ControlCharacterRecentFile { get; } = new(
        nameof(ControlCharacterRecentFile),
        Document(recentFiles: [SettingsWorkspace.LocalPath("slot1\u0007\u001b[2J.dat"), GoodPath]),
        "Control characters, which corrupt any terminal or label the path is written into.");

    internal static TamperedSettings BidiOverrideRecentFile { get; } = new(
        nameof(BidiOverrideRecentFile),
        Document(recentFiles: [SettingsWorkspace.LocalPath("harmless\u202Etad.exe"), GoodPath]),
        "A right-to-left override, so the rendered name reads as something other than what opens.");

    internal static TamperedSettings OversizedFile { get; } = new(
        nameof(OversizedFile),
        // Whitespace padding: legal JSON, so the only thing that can stop it is a size
        // check that happens before the parser is handed the bytes.
        "{\"schemaVersion\": 1" + new string(' ', 400 * 1024) + "}",
        "A file far past the parse cap, to prove the cap is applied before parsing rather than after.");

    internal static TamperedSettings DeeplyNestedDocument { get; } = new(
        nameof(DeeplyNestedDocument),
        "{\"schemaVersion\":1,\"recentFiles\":" + new string('[', 200) + new string(']', 200) + "}",
        "Two hundred levels of nesting, aimed at the parser's own stack.");

    internal static TamperedSettings HundredThousandRecents { get; } = new(
        nameof(HundredThousandRecents),
        BuildManyRecents(100_000),
        "A hundred thousand recents entries, which must not become a hundred thousand objects.");

    internal static TamperedSettings OverlongString { get; } = new(
        nameof(OverlongString),
        Document(lastSectionKey: new string('k', 4000)),
        "A single string past the string cap.");

    internal static TamperedSettings NegativeWindowSize { get; } = new(
        nameof(NegativeWindowSize),
        Document(windowWidth: "-1920", windowHeight: "-1080"),
        "A negative window size.");

    internal static TamperedSettings ZeroWindowSize { get; } = new(
        nameof(ZeroWindowSize),
        Document(windowWidth: "0", windowHeight: "0"),
        "A zero window size, which a host would render as an invisible window.");

    internal static TamperedSettings MaxIntWindowSize { get; } = new(
        nameof(MaxIntWindowSize),
        Document(windowWidth: "2147483647", windowHeight: "2147483647"),
        "int.MaxValue in both extents.");

    internal static TamperedSettings NonFiniteWindowSize { get; } = new(
        nameof(NonFiniteWindowSize),
        Document(windowWidth: "1e400", windowHeight: "1e400"),
        "A number that overflows to infinity on conversion.");

    internal static TamperedSettings UnknownSchemaVersion { get; } = new(
        nameof(UnknownSchemaVersion),
        Document(schemaVersion: "999"),
        "A version from the future, aimed at whichever migrator handles the newest schema.");

    internal static TamperedSettings AbsurdSchemaVersion { get; } = new(
        nameof(AbsurdSchemaVersion),
        Document(schemaVersion: "-2147483648"),
        "A version that is not a version.");

    internal static TamperedSettings MissingSchemaVersion { get; } = new(
        nameof(MissingSchemaVersion),
        "{\"theme\":\"Light\"}",
        "No version at all, testing whether absence is treated as 'current'.");

    internal static TamperedSettings TypeDiscriminator { get; } = new(
        nameof(TypeDiscriminator),
        "{\"$type\":\"System.Windows.Data.ObjectDataProvider, PresentationFramework\",\"schemaVersion\":1,\"theme\":\"Light\"}",
        "A polymorphic type discriminator naming a well-known gadget type.");

    internal static TamperedSettings EscapedTypeDiscriminator { get; } = new(
        nameof(EscapedTypeDiscriminator),
        "{\"\\u0024type\":\"System.Diagnostics.Process, System.Diagnostics.Process\",\"schemaVersion\":1}",
        "The same discriminator with its dollar sign escaped, to slip a raw-byte comparison.");

    internal static TamperedSettings ReferenceMetadata { get; } = new(
        nameof(ReferenceMetadata),
        "{\"$id\":\"1\",\"schemaVersion\":1,\"recentFiles\":{\"$ref\":\"1\"}}",
        "Reference-handling metadata, which turns a document into a graph.");

    internal static TamperedSettings UnknownProperty { get; } = new(
        nameof(UnknownProperty),
        "{\"schemaVersion\":1,\"theme\":\"Light\",\"pluginAssembly\":\"C:\\\\evil.dll\"}",
        "A property this version does not declare.");

    internal static TamperedSettings NumericThemeEnum { get; } = new(
        nameof(NumericThemeEnum),
        "{\"schemaVersion\":1,\"theme\":7}",
        "A numeric enum value, which would deserialize to an undefined ThemeMode without complaint.");

    internal static TamperedSettings UnknownThemeName { get; } = new(
        nameof(UnknownThemeName),
        Document(theme: "\"Neon\""),
        "A theme name outside the two supported modes.");

    internal static TamperedSettings UnknownAccentName { get; } = new(
        nameof(UnknownAccentName),
        Document(accent: "\"Chartreuse\""),
        "An accent name outside the fourteen.");

    internal static TamperedSettings NotAnObject { get; } = new(
        nameof(NotAnObject),
        "[1,2,3]",
        "A document whose root is not an object.");

    internal static TamperedSettings EmptyFile { get; } = new(
        nameof(EmptyFile),
        string.Empty,
        "Zero bytes, which is what a crash during a non-atomic write would leave.");

    internal static TamperedSettings TrailingGarbage { get; } = new(
        nameof(TrailingGarbage),
        "{\"schemaVersion\":1}{\"schemaVersion\":1}",
        "A second document appended after the first.");

    internal static TamperedSettings NullRecentEntries { get; } = new(
        nameof(NullRecentEntries),
        "{\"schemaVersion\":1,\"recentFiles\":[null,null," + Quote(GoodPath) + "]}",
        "Null array elements, which a naive mapper dereferences.");

    /// <summary>Every variant, for the sweep test.</summary>
    internal static IReadOnlyList<TamperedSettings> All { get; } =
    [
        UncRecentFile,
        DeviceNamespaceRecentFile,
        GlobalRootRecentFile,
        RelativeRecentFile,
        TraversalRecentFile,
        ControlCharacterRecentFile,
        BidiOverrideRecentFile,
        OversizedFile,
        DeeplyNestedDocument,
        HundredThousandRecents,
        OverlongString,
        NegativeWindowSize,
        ZeroWindowSize,
        MaxIntWindowSize,
        NonFiniteWindowSize,
        UnknownSchemaVersion,
        AbsurdSchemaVersion,
        MissingSchemaVersion,
        TypeDiscriminator,
        EscapedTypeDiscriminator,
        ReferenceMetadata,
        UnknownProperty,
        NumericThemeEnum,
        UnknownThemeName,
        UnknownAccentName,
        NotAnObject,
        EmptyFile,
        TrailingGarbage,
        NullRecentEntries,
    ];

    /// <summary>Builds a well-formed document with selected members overridden.</summary>
    internal static string Document(
        string schemaVersion = "1",
        string? theme = null,
        string? accent = null,
        IReadOnlyList<string>? recentFiles = null,
        IReadOnlyList<string>? recentFolders = null,
        string? lastSectionKey = null,
        string? windowWidth = null,
        string? windowHeight = null)
    {
        var builder = new StringBuilder("{\"schemaVersion\":").Append(schemaVersion);

        if (theme is not null)
        {
            builder.Append(",\"theme\":").Append(theme);
        }

        if (accent is not null)
        {
            builder.Append(",\"accent\":").Append(accent);
        }

        if (recentFiles is not null)
        {
            builder.Append(",\"recentFiles\":").Append(Array(recentFiles));
        }

        if (recentFolders is not null)
        {
            builder.Append(",\"recentFolders\":").Append(Array(recentFolders));
        }

        if (lastSectionKey is not null)
        {
            builder.Append(",\"lastSectionKey\":").Append(Quote(lastSectionKey));
        }

        if (windowWidth is not null)
        {
            builder.Append(",\"windowWidth\":").Append(windowWidth);
        }

        if (windowHeight is not null)
        {
            builder.Append(",\"windowHeight\":").Append(windowHeight);
        }

        return builder.Append('}').ToString();
    }

    internal static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string Array(IReadOnlyList<string> values) =>
        "[" + string.Join(",", values.Select(Quote)) + "]";

    private static string BuildManyRecents(int count)
    {
        var builder = new StringBuilder("{\"schemaVersion\":1,\"recentFiles\":[");

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(Quote(SettingsWorkspace.LocalPath($"slot{i}.dat")));
        }

        return builder.Append("]}").ToString();
    }
}
