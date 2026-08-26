using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SaveEditor.Ui.Gallery.Views;

/// <summary>
/// Visual catalogue of every semantic token, and the screenshot surface for P1.
/// </summary>
public partial class TokenGalleryView : UserControl
{
    /// <summary>Creates the view.</summary>
    public TokenGalleryView() => AvaloniaXamlLoader.Load(this);
}
