using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace SaveEditor.Ui.HeadlessTests;

public class HeadlessHarnessTests
{
    [AvaloniaFact]
    public void Headless_Harness_Hosts_A_Window()
    {
        var window = new Window { Width = 200, Height = 100, Content = new TextBlock { Text = "ok" } };
        window.Show();
        Assert.IsType<TextBlock>(window.Content);
    }
}
