using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace SaveEditor.Ui.Theming;

/// <summary>
/// The framework theme: semantic resources for both modes plus one accent.
/// </summary>
/// <remarks>
/// Add this to <c>Application.Styles</c> after a base Avalonia theme. The base
/// theme supplies control behaviour; the visual contract is this one's.
/// </remarks>
public class SaveEditorTheme : Styles
{
    /// <summary>Loads the theme.</summary>
    public SaveEditorTheme() => AvaloniaXamlLoader.Load(this);
}
