using Avalonia;
using Avalonia.Styling;
using SaveEditor.Ui.Settings;

namespace SaveEditor.Ui.Theming;

/// <summary>
/// Applies and persists the theme mode and accent.
/// </summary>
/// <remarks>
/// <para>
/// Accent precedence has three levels: the framework default, an editor override,
/// and a user selection. A user selection wins on later launches, and resetting
/// returns to the editor default rather than to the framework one — otherwise an
/// editor that had deliberately chosen its own accent would silently lose it the
/// first time a user pressed reset.
/// </para>
/// <para>
/// Persistence is fail-soft, matching the settings store. A theme change that
/// cannot be written still applies for the session; it is not worth refusing to
/// change the colours because a preference file is unwritable.
/// </para>
/// </remarks>
public sealed class ThemeController
{
    /// <summary>The accent used when neither the editor nor the user chose one.</summary>
    public const CatppuccinAccent FrameworkDefaultAccent = CatppuccinAccent.Blue;

    private readonly SaveEditorTheme _theme;
    private readonly IEditorSettingsStore _store;
    private EditorSettings _settings = new();

    /// <summary>Creates a controller over a theme instance and a settings store.</summary>
    /// <param name="theme">The theme added to <c>Application.Styles</c>.</param>
    /// <param name="store">Where the selection is persisted.</param>
    /// <param name="editorDefaultAccent">
    /// The consuming editor's preferred accent, or <see langword="null"/> to use
    /// <see cref="FrameworkDefaultAccent"/>.
    /// </param>
    public ThemeController(
        SaveEditorTheme theme,
        IEditorSettingsStore store,
        CatppuccinAccent? editorDefaultAccent = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(store);

        _theme = theme;
        _store = store;
        EditorDefaultAccent = editorDefaultAccent ?? FrameworkDefaultAccent;
    }

    /// <summary>The accent this editor falls back to when the user has not chosen.</summary>
    public CatppuccinAccent EditorDefaultAccent { get; }

    /// <summary>The mode currently applied.</summary>
    public ThemeMode Mode { get; private set; } = ThemeMode.Dark;

    /// <summary>The accent currently applied.</summary>
    public CatppuccinAccent Accent { get; private set; } = FrameworkDefaultAccent;

    /// <summary>Whether the accent comes from the editor default rather than a user choice.</summary>
    public bool IsUsingEditorDefault { get; private set; } = true;

    /// <summary>Loads the persisted selection and applies it.</summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(true);

        Mode = _settings.Theme;
        IsUsingEditorDefault = _settings.Accent is null;
        Accent = _settings.Accent ?? EditorDefaultAccent;

        Apply();
    }

    /// <summary>Changes the mode, applying immediately and persisting.</summary>
    /// <param name="mode">The mode to apply.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public ValueTask SetModeAsync(ThemeMode mode, CancellationToken cancellationToken = default)
    {
        Mode = mode;
        Apply();
        return PersistAsync(_settings with { Theme = mode }, cancellationToken);
    }

    /// <summary>Chooses an accent explicitly, overriding the editor default.</summary>
    /// <param name="accent">The accent to apply.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    public ValueTask SetAccentAsync(CatppuccinAccent accent, CancellationToken cancellationToken = default)
    {
        Accent = accent;
        IsUsingEditorDefault = false;
        Apply();
        return PersistAsync(_settings with { Accent = accent }, cancellationToken);
    }

    /// <summary>Clears the user's accent choice and returns to the editor default.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    public ValueTask ResetAccentAsync(CancellationToken cancellationToken = default)
    {
        Accent = EditorDefaultAccent;
        IsUsingEditorDefault = true;
        Apply();
        return PersistAsync(_settings with { Accent = null }, cancellationToken);
    }

    private void Apply()
    {
        _theme.ApplyAccent(Accent);

        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant =
                Mode == ThemeMode.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }

    private async ValueTask PersistAsync(EditorSettings updated, CancellationToken cancellationToken)
    {
        _settings = updated;

        try
        {
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fail-soft by design: the appearance change already applied, and refusing
            // to recolour the window because a preference file is unwritable helps
            // nobody. The store surfaces unwritable storage once through its own
            // IsPersistent flag rather than throwing on every change.
        }
    }
}
