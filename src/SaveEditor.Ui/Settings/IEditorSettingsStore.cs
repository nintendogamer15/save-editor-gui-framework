namespace SaveEditor.Ui.Settings;

/// <summary>Loads and persists <see cref="EditorSettings"/>.</summary>
/// <remarks>
/// <para>
/// The backing file is user-writable, may arrive from a roaming profile or a
/// restored backup, and feeds paths into the recents menu and the open workflow.
/// It is treated as untrusted input: bounds are enforced on read as well as write,
/// polymorphic type resolution is prohibited, and path values are validated before
/// they reach the filesystem.
/// </para>
/// <para>
/// Writes are atomic and fail-soft. This differs deliberately from the save
/// workflow, which is atomic and fail-loud — a save that silently does not happen
/// is the worst outcome in the product. Both share the hardened primitive; neither
/// shares the other's failure policy.
/// </para>
/// </remarks>
public interface IEditorSettingsStore
{
    /// <summary>
    /// Whether settings can actually be persisted.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> when the settings directory is unwritable. The editor
    /// runs on in-memory defaults and announces this once, rather than silently
    /// discarding every subsequent change.
    /// </remarks>
    bool IsPersistent { get; }

    /// <summary>Loads settings, falling back to defaults if the file is unusable.</summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>
    /// The stored settings, or defaults. A malformed or invalid file is backed up
    /// under a non-colliding name and replaced; startup is never blocked.
    /// </returns>
    ValueTask<EditorSettings> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Persists settings.</summary>
    /// <param name="settings">The settings to write.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask SaveAsync(EditorSettings settings, CancellationToken cancellationToken = default);
}
