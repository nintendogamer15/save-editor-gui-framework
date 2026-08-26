using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SaveEditor.Ui.Gallery.Views;

/// <summary>Catalogue of the framework-owned control themes.</summary>
public partial class ControlGalleryView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ControlGalleryView() => AvaloniaXamlLoader.Load(this);
}
