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

    /// <summary>Creates the shell.</summary>
    public EditorShell() => AvaloniaXamlLoader.Load(this);

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
    public object? MenuExtensions
    {
        get => GetValue(MenuExtensionsProperty);
        set => SetValue(MenuExtensionsProperty, value);
    }
}
