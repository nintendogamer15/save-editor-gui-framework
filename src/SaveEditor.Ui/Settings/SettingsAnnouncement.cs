namespace SaveEditor.Ui.Settings;

/// <summary>What happened the last time settings were loaded.</summary>
public enum SettingsLoadOutcome
{
    /// <summary>The stored file was read and accepted as written.</summary>
    Loaded,

    /// <summary>
    /// The stored file was read and accepted, but individual values were dropped or
    /// clamped.
    /// </summary>
    /// <remarks>
    /// Reported rather than silent. A recents entry that vanishes because it named a
    /// UNC share, or a window size that shrinks because it claimed forty thousand
    /// pixels, is evidence the file was edited by something other than this framework
    /// and the user is entitled to know.
    /// </remarks>
    Sanitized,

    /// <summary>No settings file exists yet. Defaults are in use.</summary>
    NoStoredSettings,

    /// <summary>
    /// The file was unusable, was backed up under a non-colliding name, and was
    /// replaced with defaults.
    /// </summary>
    Replaced,

    /// <summary>
    /// The file could not be read at all and was deliberately left untouched.
    /// </summary>
    /// <remarks>
    /// This covers a settings path that resolved to a symbolic link, a non-regular
    /// file, or content far past the size the framework will even copy. The framework
    /// does not delete or rewrite something it could not identify; it runs on defaults
    /// and says so.
    /// </remarks>
    Unreadable,

    /// <summary>
    /// The settings directory is not writable. The editor runs on in-memory defaults.
    /// </summary>
    NotPersistent,
}

/// <summary>Why a settings file was rejected.</summary>
public enum SettingsRejection
{
    /// <summary>Nothing was rejected.</summary>
    None,

    /// <summary>The file is larger than the parser is allowed to see.</summary>
    FileTooLarge,

    /// <summary>The bytes are not well-formed JSON, or do not match the schema.</summary>
    MalformedJson,

    /// <summary>The document nests deeper than the configured maximum.</summary>
    DepthExceeded,

    /// <summary>A string or property name is longer than the configured maximum.</summary>
    StringTooLong,

    /// <summary>An array claims more elements than the configured structural maximum.</summary>
    ArrayTooLong,

    /// <summary>
    /// The document carries a serializer metadata property such as <c>$type</c>,
    /// <c>$id</c>, <c>$ref</c>, or <c>$values</c>.
    /// </summary>
    /// <remarks>
    /// The closed context cannot act on one, so this is refused for what it indicates
    /// rather than for what it could do: a file carrying a type discriminator was
    /// written by something trying to choose which types this process constructs, and
    /// that file is not merely malformed, it is hostile.
    /// </remarks>
    TypeDiscriminator,

    /// <summary>
    /// The schema version is absent, unknown, or absurd.
    /// </summary>
    /// <remarks>
    /// This value selects which migration code runs and comes from the untrusted file,
    /// so an unrecognized value routes to the malformed path rather than to the newest
    /// migrator.
    /// </remarks>
    UnknownSchemaVersion,

    /// <summary>The file exists but could not be opened or read.</summary>
    Unreadable,
}

/// <summary>
/// A one-time, user-facing statement about the settings file.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Rejection">Why, when something was rejected.</param>
/// <param name="Message">Operator-facing detail, suitable for the announcement region.</param>
/// <param name="BackupPath">Where the previous file was preserved, when it was.</param>
public sealed record SettingsAnnouncement(
    SettingsLoadOutcome Outcome,
    SettingsRejection Rejection,
    string Message,
    string? BackupPath);
