using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SaveEditor.Ui.Shell;

/// <summary>
/// The embeddable editor shell: menu bar, header, sidebar, content, and status bar.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="UserControl"/>, not a window. The consuming application owns its
/// <see cref="Window"/>, lifecycle, and platform integration; anything needing that
/// authority is delegated through <see cref="Hosting.IEditorHost"/>.
/// </para>
/// <para>
/// The framework owns the grid, spacing, focus behaviour, default commands, and
/// accessibility. Editors contribute through the named slots below rather than by
/// subclassing, so shell behaviour cannot be partially overridden into an
/// inconsistent state.
/// </para>
/// </remarks>
public partial class EditorShell : UserControl
{
    /// <summary>Identifies the <see cref="Branding"/> property.</summary>
    public static readonly StyledProperty<object?> BrandingProperty =
        AvaloniaProperty.Register<EditorShell, object?>(nameof(Branding));

    /// <summary>Identifies the <see cref="HeaderActions"/> property.</summary>
    public static readonly StyledProperty<object?> HeaderActionsProperty =
        AvaloniaProperty.Register<EditorShell, object?>(nameof(HeaderActions));

    /// <summary>Identifies the <see cref="SidebarExtension"/> property.</summary>
    public static readonly StyledProperty<object?> SidebarExtensionProperty =
        AvaloniaProperty.Register<EditorShell, object?>(nameof(SidebarExtension));

    /// <summary>Identifies the <see cref="StatusBarExtension"/> property.</summary>
    public static readonly StyledProperty<object?> StatusBarExtensionProperty =
        AvaloniaProperty.Register<EditorShell, object?>(nameof(StatusBarExtension));

    /// <summary>Identifies the <see cref="MenuExtensions"/> property.</summary>
    public static readonly StyledProperty<object?> MenuExtensionsProperty =
        AvaloniaProperty.Register<EditorShell, object?>(nameof(MenuExtensions));

    /// <summary>The menus contributed by <see cref="MenuExtensions"/>, in bar order.</summary>
    /// <remarks>
    /// Tracked explicitly so a later assignment removes exactly what the previous one
    /// added. Clearing by index or by type would also take the framework's own four.
    /// </remarks>
    private readonly List<Control> contributedMenus = [];

    /// <summary>The menu bar, resolved by name rather than through a generated field.</summary>
    /// <remarks>
    /// The generated <c>x:Name</c> fields are assigned by <c>InitializeComponent</c>,
    /// which this shell does not call; loading the markup directly leaves them null.
    /// </remarks>
    private readonly Menu? menuBar;

    static EditorShell() =>
        MenuExtensionsProperty.Changed.AddClassHandler<EditorShell, object?>(
            (shell, e) => shell.SyncMenuExtensions(e.NewValue.GetValueOrDefault()));

    /// <summary>Creates the shell.</summary>
    public EditorShell()
    {
        AvaloniaXamlLoader.Load(this);

        menuBar = this.FindControl<Menu>("MenuBar")
                  ?? throw new InvalidOperationException("EditorShell markup has no MenuBar.");

        // A value assigned before the markup was loaded has no menu bar to land in.
        SyncMenuExtensions(MenuExtensions);
    }

    /// <summary>Editor identity shown at the top of the sidebar.</summary>
    public object? Branding
    {
        get => GetValue(BrandingProperty);
        set => SetValue(BrandingProperty, value);
    }

    /// <summary>
    /// Extra header actions, placed after the framework's own.
    /// </summary>
    /// <remarks>
    /// The framework's four — Open Save, Save As, Undo, Redo — are fixed. Header
    /// space is finite and a header that grows without limit stops being scannable,
    /// so bulk and domain actions belong in the section toolbar or the menus.
    /// </remarks>
    public object? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }

    /// <summary>Editor content appended below the framework's section navigation.</summary>
    public object? SidebarExtension
    {
        get => GetValue(SidebarExtensionProperty);
        set => SetValue(SidebarExtensionProperty, value);
    }

    /// <summary>Editor content appended to the status bar.</summary>
    public object? StatusBarExtension
    {
        get => GetValue(StatusBarExtensionProperty);
        set => SetValue(StatusBarExtensionProperty, value);
    }

    /// <summary>Additional top-level menus, placed after Help.</summary>
    /// <value>
    /// A <see cref="MenuItem"/> or <see cref="Separator"/>, or an
    /// <see cref="IEnumerable"/> of them. <see langword="null"/> contributes nothing.
    /// </value>
    /// <remarks>
    /// Only menu-bar entries are accepted. Anything else would be wrapped by
    /// <see cref="Menu"/> in a generated <see cref="MenuItem"/> and drawn as a menu
    /// titled with a control, so it is rejected rather than rendered that way.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The value, or an element of it, is not a <see cref="MenuItem"/> or
    /// <see cref="Separator"/>.
    /// </exception>
    public object? MenuExtensions
    {
        get => GetValue(MenuExtensionsProperty);
        set => SetValue(MenuExtensionsProperty, value);
    }

    /// <summary>Rewrites the contributed tail of the menu bar.</summary>
    /// <param name="value">The new <see cref="MenuExtensions"/> value.</param>
    /// <remarks>
    /// The menus go into <see cref="ItemsControl.Items"/> themselves. A host control cannot
    /// carry them: <see cref="Menu"/> only treats an item as its own container when it
    /// already is a <see cref="MenuItem"/>, so a placeholder is a blank menu entry when
    /// empty, and setting <c>IsVisible</c> on it leaves the generated container drawn.
    /// </remarks>
    private void SyncMenuExtensions(object? value)
    {
        // Materialized before the bar is touched, so an invalid value is rejected
        // whether or not there is anywhere to put it yet, and a rejected assignment
        // cannot leave the previous menus half-removed.
        List<Control> contributions = [.. Contributions(value)];

        // Set from a style or a binding before the markup ran; the constructor
        // re-syncs once the bar exists.
        if (menuBar is null)
        {
            return;
        }

        foreach (Control stale in contributedMenus)
        {
            menuBar.Items.Remove(stale);
        }

        contributedMenus.Clear();

        foreach (Control menu in contributions)
        {
            menuBar.Items.Add(menu);
            contributedMenus.Add(menu);
        }
    }

    /// <summary>Validates and flattens a <see cref="MenuExtensions"/> value.</summary>
    /// <param name="value">The value to interpret.</param>
    /// <returns>The menu-bar entries it contributes, in order.</returns>
    private static IEnumerable<Control> Contributions(object? value)
    {
        switch (value)
        {
            case null:
                yield break;

            case MenuItem or Separator:
                yield return (Control)value;
                yield break;

            // Checked before IEnumerable: a MenuItem is itself enumerable over its
            // own children, and would otherwise contribute its submenu instead.
            case Control control:
                throw Rejected(control);

            case IEnumerable items:
                foreach (object? item in items)
                {
                    yield return item switch
                    {
                        MenuItem or Separator => (Control)item,
                        null => throw new ArgumentException(
                            $"{nameof(MenuExtensions)} contains a null entry.", nameof(value)),
                        _ => throw Rejected(item),
                    };
                }

                yield break;

            default:
                throw Rejected(value);
        }
    }

    private static ArgumentException Rejected(object value) =>
        new(
            $"{nameof(MenuExtensions)} accepts a MenuItem or Separator, or a sequence of " +
            $"them, because the menu bar can only hold menu entries. Got {value.GetType()}.",
            nameof(value));
}
