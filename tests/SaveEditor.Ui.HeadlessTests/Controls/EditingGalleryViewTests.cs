using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SaveEditor.Ui.Controls;
using SaveEditor.Ui.Gallery.Views;

namespace SaveEditor.Ui.HeadlessTests.Controls;

/// <summary>
/// Smoke test for the P3 gallery section: it renders, and it actually demonstrates
/// the five field types plus warning, validation, and pending state, as required
/// by the P3 slice brief.
/// </summary>
public class EditingGalleryViewTests
{
    [AvaloniaFact]
    public async Task Renders_All_Five_Field_Types_With_A_Warning_Validation_Error_And_Pending_Edit()
    {
        var view = new EditingGalleryView();
        var window = new Window { Width = 900, Height = 800, Content = view };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        await Task.Yield();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(view.GetVisualDescendants().OfType<SectionToolbar>().SingleOrDefault());
        var list = view.GetVisualDescendants().OfType<FieldList>().Single();
        Assert.NotNull(list.Fields);

        var fields = list.Fields!.ToList();
        Assert.Contains(fields, f => f.GetType().Name == "TextFieldViewModel");
        Assert.Contains(fields, f => f.GetType().Name == "NumericFieldViewModel");
        Assert.Contains(fields, f => f.GetType().Name == "BooleanFieldViewModel");
        Assert.Contains(fields, f => f.GetType().Name == "ChoiceFieldViewModel");
        Assert.Contains(fields, f => f.GetType().Name == "ReadOnlyFieldViewModel");

        Assert.Contains(fields, f => !string.IsNullOrEmpty(f.WarningText));
        Assert.Contains(fields, f => f.ValidationError is not null);
        Assert.Contains(fields, f => f.HasPendingEdit);
    }
}
