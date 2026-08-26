using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SaveEditor.Ui.Gallery.Views;
using SaveEditor.Ui.Hosting;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Shell;
using SaveEditor.Ui.Theming;

namespace SaveEditor.Ui.Gallery;

/// <summary>The gallery window: the framework shell hosting the catalogue pages.</summary>
public partial class MainWindow : Window
{
    private readonly GalleryDocumentSession _session = new();

    /// <summary>Creates the window and composes the shell.</summary>
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // One store, not two. Splitting them would let theme state and shell state
        // diverge, which is harmless in a non-persistent sample and quietly wrong the
        // moment this composition is copied into the P5 template.
        var settings = new InMemorySettingsStore();

        var theme = new ThemeController(
            Application.Current!.Styles.OfType<SaveEditorTheme>().Single(),
            settings);

        var host = new WindowEditorHost(this);

        var viewModel = new EditorShellViewModel(
            _session,
            new GalleryUserInteraction(this),
            settings,
            host,
            theme);

        viewModel.RegisterSections(
        [
            new SectionDescriptor
            {
                Key = "tokens",
                Title = "Semantic tokens",
                Subtitle = "Colours, type, and metrics",
                BodyMode = SectionBodyMode.Custom,
                Body = new TokenGalleryView(),
            },
            new SectionDescriptor
            {
                Key = "controls",
                Title = "Controls",
                Subtitle = "Framework control themes",
                BodyMode = SectionBodyMode.Custom,
                Body = new ControlGalleryView(),
            },
            new SectionDescriptor
            {
                Key = "editing",
                Title = "Editing surface",
                Subtitle = "Field cards, virtualized list, section toolbar",
                BodyMode = SectionBodyMode.Custom,
                Body = new EditingGalleryView(),
            },
        ]);

        var shell = this.FindControl<EditorShell>("Shell")!;
        shell.DataContext = viewModel;
        DragDropAdapter.Attach(shell, viewModel);

        this.FindControl<Button>("SimulateEdit")!.Click += (_, _) =>
        {
            _session.SimulateEdit();
            viewModel.StatusMessage =
                "Simulated an unapplied edit. Try File > Exit, or close the window, to see the guard.";
        };

        Loaded += async (_, _) =>
        {
            await theme.InitializeAsync().ConfigureAwait(true);
            await viewModel.InitializeAsync().ConfigureAwait(true);
            viewModel.StatusMessage = "Ready. The gallery performs no file I/O.";
        };
    }

    /// <summary>
    /// Keeps gallery preferences in memory.
    /// </summary>
    /// <remarks>
    /// Writing to a real settings file would let running the catalogue disturb an
    /// actual editor's stored theme and recents.
    /// </remarks>
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
