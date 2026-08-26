using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using SaveEditor.Ui.Settings;

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
    private const int AccentDictionaryIndex = 1;

    /// <summary>Loads the theme.</summary>
    public SaveEditorTheme() => AvaloniaXamlLoader.Load(this);

    /// <summary>Swaps the merged accent dictionary.</summary>
    /// <param name="accent">The accent to apply.</param>
    /// <remarks>
    /// Exactly one accent dictionary is merged at a time, at a known index, so
    /// switching accent replaces it rather than stacking dictionaries whose
    /// last-writer-wins order would drift as accents were changed repeatedly.
    /// </remarks>
    public void ApplyAccent(CatppuccinAccent accent)
    {
        if (Resources is not ResourceDictionary dictionary
            || dictionary.MergedDictionaries.Count <= AccentDictionaryIndex)
        {
            throw new InvalidOperationException(
                "The theme's merged dictionaries are not in the expected shape; " +
                "SaveEditorTheme.axaml must merge the semantic dictionary followed by one accent.");
        }

        dictionary.MergedDictionaries[AccentDictionaryIndex] = new ResourceInclude((Uri?)null)
        {
            Source = new Uri($"avares://SaveEditor.Ui/Themes/Accents/{accent}.axaml"),
        };
    }
}
