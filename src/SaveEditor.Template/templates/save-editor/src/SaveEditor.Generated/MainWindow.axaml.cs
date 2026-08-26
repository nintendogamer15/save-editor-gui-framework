using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using SaveEditor.Generated.Codecs;
using SaveEditor.Generated.Document;
using SaveEditor.Generated.Sections;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Controls;
using SaveEditor.Ui.Dialogs;
using SaveEditor.Ui.Editing;
using SaveEditor.Ui.Hosting;
using SaveEditor.Ui.Settings;
using SaveEditor.Ui.Shell;
using SaveEditor.Ui.Theming;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Generated;

/// <summary>
/// The generated app's composition root: the window that hosts the framework
/// shell and wires it to the demo document, codec, settings, and theme.
/// </summary>
/// <remarks>
/// This is the "no manual framework wiring" the template exists to deliver —
/// every piece below is plain constructor composition, matching README.md's
/// "Composing an editor" section in the framework repository. Nothing here is
/// generated or hidden by tooling; read it, and change it.
/// </remarks>
public partial class MainWindow : Window
{
    // One settings store, shared between the shell and the theme controller.
    // Splitting it would let shell state and theme state diverge.
    private readonly EditHistory _history = new();
    private readonly FieldList _fieldsControl = new();
    private readonly SectionToolbar _toolbarControl = new();
    private readonly DocumentSession<DemoSaveDocument> _session;
    private SectionEditor? _heroSection;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);

        // Settings live at LocalApplicationData/<ApplicationId>/settings.json.
        // This literal is renamed along with the rest of the project when the
        // template is instantiated with -n, so it tracks your app's name
        // automatically. Change it explicitly if you ever want the two to
        // diverge (e.g. after a product rename that keeps the old settings).
        var applicationId = EditorApplicationId.Parse("SaveEditor.Generated");
        var settings = new EditorSettingsStore(applicationId);

        var theme = new ThemeController(
            Application.Current!.Styles.OfType<SaveEditorTheme>().Single(),
            settings);

        var host = new WindowEditorHost(this);
        var interaction = new ThemedUserInteraction(this);

        // ------------------------------------------------------------------
        // REPLACE ME FIRST: swap DemoSaveDetector/DemoSaveCodec for your own,
        // and register every format your editor understands. See
        // Codecs/DemoSaveCodec.cs and README.md, "Replacing the demo format".
        // ------------------------------------------------------------------
        var registry = new SaveCodecRegistry<DemoSaveDocument>(
        [
            new CodecRegistration<DemoSaveDocument>(new DemoSaveDetector(), new DemoSaveCodec()),
        ]);

        var workflow = new SafeFileWorkflow<DemoSaveDocument>(new SafeFileWorkflowOptions<DemoSaveDocument>
        {
            Registry = registry,
            Interaction = interaction,
        });

        _session = new DocumentSession<DemoSaveDocument>(workflow, _history, new DemoSaveCodec())
        {
            // Drafts live on the field view-models, which DocumentSession
            // deliberately knows nothing about. Leaving this unset makes the
            // exit guard blind to typed-but-unapplied edits, so every editor
            // with fields must set it — see DocumentSession<T>.PendingEditProbe.
            PendingEditProbe = () => _heroSection?.HasPendingEdits ?? false,
        };
        _session.DocumentChanged += (_, _) => RebuildHeroSection();

        var viewModel = new EditorShellViewModel(_session, interaction, settings, host, theme);

        var sectionBody = new DockPanel();
        DockPanel.SetDock(_toolbarControl, Dock.Top);
        sectionBody.Children.Add(_toolbarControl);
        sectionBody.Children.Add(_fieldsControl);

        viewModel.RegisterSections(
        [
            new SectionDescriptor
            {
                Key = DemoSectionFactory.SectionKey,
                Title = "Hero",
                Subtitle = "Text, numeric, boolean, choice, warning, and read-only fields",
                BodyMode = SectionBodyMode.Custom,
                Body = sectionBody,
            },
        ]);

        var shell = this.FindControl<EditorShell>("Shell")!;
        shell.DataContext = viewModel;
        DragDropAdapter.Attach(shell, viewModel);

        this.FindControl<Button>("OpenSampleButton")!.Click += async (_, _) =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "SaveEditor.Generated-Samples");
            var path = await DemoSampleFile.WriteAsync(directory);
            await viewModel.OpenPathAsync(path);
        };

        Closed += (_, _) => _session.Dispose();

        Loaded += async (_, _) =>
        {
            await theme.InitializeAsync().ConfigureAwait(true);
            await viewModel.InitializeAsync().ConfigureAwait(true);
        };
    }

    /// <summary>
    /// Rebuilds the hero section over whichever document is now open.
    /// </summary>
    /// <remarks>
    /// Field accessors close over one specific document instance, so a new
    /// document — a fresh open, not an in-place edit — means new field
    /// view-models, not new values pushed into the old ones.
    /// </remarks>
    private void RebuildHeroSection()
    {
        if (_session.Document is { } document)
        {
            _heroSection = DemoSectionFactory.Create(document, _history);
            _toolbarControl.Editor = _heroSection;
            _fieldsControl.Fields = _heroSection.VisibleFields;
        }
        else
        {
            _heroSection = null;
            _toolbarControl.Editor = null;
            _fieldsControl.Fields = null;
        }
    }
}
