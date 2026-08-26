using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace SaveEditor.Ui.Shell;

/// <summary>
/// Forwards dropped files into the same open workflow the menus use.
/// </summary>
/// <remarks>
/// <para>
/// The adapter is deliberately thin: it extracts paths and hands them to
/// <see cref="EditorShellViewModel.OpenPathAsync"/>. It performs no validation of
/// its own, because a second validation path is a second place for the rules to
/// drift — a dropped file must be subject to exactly the checks a menu-opened file
/// is, including the discard guard and the path resolver.
/// </para>
/// <para>
/// Headless tests inject paths straight into the view-model rather than simulating
/// a drop, so workflow correctness never depends on a compositor being available.
/// </para>
/// </remarks>
public static class DragDropAdapter
{
    /// <summary>Attaches drop handling to a control.</summary>
    /// <param name="target">The control that accepts drops, usually the shell.</param>
    /// <param name="viewModel">The view-model that owns the open workflow.</param>
    /// <returns>A disposable that detaches the handlers.</returns>
    public static IDisposable Attach(Control target, EditorShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(viewModel);

        void OnDragOver(object? sender, DragEventArgs e) =>
            e.DragEffects = HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;

        async void OnDrop(object? sender, DragEventArgs e)
        {
            if (FirstPath(e) is { } path)
            {
                await viewModel.OpenPathAsync(path).ConfigureAwait(true);
            }
        }

        target.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        target.AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(target, true);

        return new Detacher(() =>
        {
            target.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
            target.RemoveHandler(DragDrop.DropEvent, OnDrop);
        });
    }

    private static bool HasFiles(DragEventArgs e) => FirstPath(e) is not null;

    private static string? FirstPath(DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
        {
            return null;
        }

        // One document at a time, so a multi-file drop opens the first rather than
        // silently discarding the drop or opening an arbitrary one.
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } path)
            {
                return path;
            }
        }

        return null;
    }

    private sealed class Detacher(Action detach) : IDisposable
    {
        private Action? _detach = detach;

        public void Dispose()
        {
            _detach?.Invoke();
            _detach = null;
        }
    }
}
