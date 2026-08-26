using System.Text.Json.Serialization;

namespace SaveEditor.Ui.Settings;

/// <summary>
/// The literal on-disk shape of <c>settings.json</c>, kept separate from
/// <see cref="EditorSettings"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type is the entire set of shapes the deserializer is permitted to construct
/// from an untrusted file. It is <see langword="sealed"/>, non-generic, and every
/// property is a primitive, a nullable primitive, or an array of strings — there is
/// no abstract, interface-typed, or object-typed member anywhere on it, so there is
/// nothing for a <c>$type</c> discriminator to select even if one reached the
/// serializer.
/// </para>
/// <para>
/// Enum-valued settings are carried as <see cref="string"/> rather than as
/// <see cref="ThemeMode"/> or <see cref="CatppuccinAccent"/>. A numeric enum in JSON
/// deserializes to an undefined enum value without error, which would let a hostile
/// file put the theme system into a state no code path expects; parsing the name
/// explicitly and checking it against the defined set is the only form that fails
/// closed.
/// </para>
/// <para>
/// Unmapped members are rejected rather than ignored. A file carrying a property this
/// type does not declare is not a file this version wrote, and the settings schema
/// version — not silent tolerance — is what carries forward compatibility.
/// </para>
/// </remarks>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SettingsDocument
{
    /// <summary>Schema version claimed by the file.</summary>
    [JsonPropertyName("schemaVersion")]
    public int? SchemaVersion { get; set; }

    /// <summary>Theme mode name.</summary>
    [JsonPropertyName("theme")]
    public string? Theme { get; set; }

    /// <summary>Accent name, or absent to follow the editor default.</summary>
    [JsonPropertyName("accent")]
    public string? Accent { get; set; }

    /// <summary>Recently opened files, most recent first.</summary>
    [JsonPropertyName("recentFiles")]
    public string?[]? RecentFiles { get; set; }

    /// <summary>Recently opened folders, most recent first.</summary>
    [JsonPropertyName("recentFolders")]
    public string?[]? RecentFolders { get; set; }

    /// <summary>Key of the section selected when the editor last closed.</summary>
    [JsonPropertyName("lastSectionKey")]
    public string? LastSectionKey { get; set; }

    /// <summary>Persisted window width.</summary>
    [JsonPropertyName("windowWidth")]
    public double? WindowWidth { get; set; }

    /// <summary>Persisted window height.</summary>
    [JsonPropertyName("windowHeight")]
    public double? WindowHeight { get; set; }
}

/// <summary>
/// The closed, source-generated serialization context for <see cref="SettingsDocument"/>.
/// </summary>
/// <remarks>
/// Source generation is not only a trimming or start-up concern here. A generated
/// context resolves exactly the types listed on it and nothing else, so no reflection
/// path exists through which a name in the file could name a type to construct. The
/// store never accepts a caller-supplied <c>JsonTypeInfoResolver</c>, because doing so
/// would hand that decision back to whatever the caller was given.
/// </remarks>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(SettingsDocument))]
internal partial class SettingsJsonContext : JsonSerializerContext
{
}
