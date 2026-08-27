using System.Windows.Input;

namespace SaveEditor.Ui.Shell;

/// <summary>
/// One row in a data-driven shell menu: Recent, Themes, or Accent.
/// </summary>
/// <remarks>
/// The command lives on the row so a submenu popup does not have to walk
/// ancestors to find the shell. Popup items sit in their own visual tree, and
/// <c>$parent[EditorShell]</c> cannot reach it from there.
/// </remarks>
internal sealed class ShellMenuOption
{
    public required object Header { get; init; }

    public required ICommand Command { get; init; }

    public required object Parameter { get; init; }

    public string? Tip { get; init; }
}
