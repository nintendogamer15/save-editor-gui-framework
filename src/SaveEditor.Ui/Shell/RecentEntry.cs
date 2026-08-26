using SaveEditor.Ui.Display;

namespace SaveEditor.Ui.Shell;

/// <summary>One entry in the recents list.</summary>
/// <param name="Path">
/// The raw path, used to open the document. Never rendered directly.
/// </param>
/// <param name="Label">
/// The neutralised label, used to show it. Never used to open anything — it is
/// wrapped in directional isolates and so names no file on any filesystem.
/// </param>
public sealed record RecentEntry(string Path, PathLabel Label);
