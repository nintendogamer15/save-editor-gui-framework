using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Theming;

namespace SaveEditor.Ui.Gallery;

/// <summary>The gallery shell: appearance controls above the token catalogue.</summary>
public partial class MainWindow : Window
{
    private readonly ThemeController _theme;

    /// <summary>Creates the window and wires the appearance controls.</summary>
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // The gallery keeps preferences in memory. It is a catalogue, not an editor,
        // and writing to a real settings file would let running it disturb an actual
        // editor's stored preferences.
        _theme = new ThemeController(
            Application.Current!.Styles.OfType<SaveEditorTheme>().Single(),
            new InMemorySettingsStore());

        var themeSelector = this.FindControl<ComboBox>("ThemeSelector")!;
        var accentSelector = this.FindControl<ComboBox>("AccentSelector")!;
        var resetAccent = this.FindControl<Button>("ResetAccent")!;

        themeSelector.ItemsSource = Enum.GetValues<ThemeMode>();
        accentSelector.ItemsSource = Enum.GetValues<CatppuccinAccent>();

        Loaded += async (_, _) =>
        {
            await _theme.InitializeAsync().ConfigureAwait(true);
            themeSelector.SelectedItem = _theme.Mode;
            accentSelector.SelectedItem = _theme.Accent;
        };

        themeSelector.SelectionChanged += async (_, _) =>
        {
            if (themeSelector.SelectedItem is ThemeMode mode && mode != _theme.Mode)
            {
                await _theme.SetModeAsync(mode).ConfigureAwait(true);
            }
        };

        accentSelector.SelectionChanged += async (_, _) =>
        {
            if (accentSelector.SelectedItem is CatppuccinAccent accent && accent != _theme.Accent)
            {
                await _theme.SetAccentAsync(accent).ConfigureAwait(true);
            }
        };

        resetAccent.Click += async (_, _) =>
        {
            await _theme.ResetAccentAsync().ConfigureAwait(true);
            accentSelector.SelectedItem = _theme.Accent;
        };
    }

    private sealed class InMemorySettingsStore : IEditorSettingsStore
    {
        private EditorSettings _settings = new();

        public bool IsPersistent => false;

        public ValueTask<EditorSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_settings);

        public ValueTask SaveAsync(EditorSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return ValueTask.CompletedTask;
        }
    }
}
