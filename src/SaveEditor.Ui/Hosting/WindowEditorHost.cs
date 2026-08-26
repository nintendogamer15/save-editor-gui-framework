using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace SaveEditor.Ui.Hosting;

/// <summary>
/// The default <see cref="IEditorHost"/> for an editor that owns one window.
/// </summary>
/// <remarks>
/// <para>
/// Both routes out of the application — the window's close button and
/// <c>File &gt; Exit</c> — run the same installed guard. The close button is the one
/// that is easy to get wrong: it must cancel the close, await a guard that can show
/// a dialog, and only then close for real. Without the re-entrancy flag the second
/// close would raise the guard again.
/// </para>
/// <para>
/// The framework never calls application-lifetime APIs itself; that is this type's
/// job, and it is deliberately small enough to replace.
/// </para>
/// </remarks>
public sealed class WindowEditorHost : IEditorHost, IDisposable
{
    private readonly Window _window;
    private Func<CancellationToken, ValueTask<bool>>? _guard;
    private bool _closeApproved;
    private bool _disposed;

    /// <summary>Wraps a window as an editor host.</summary>
    /// <param name="window">The window the editor shell is hosted in.</param>
    public WindowEditorHost(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
        _window.Closing += OnClosing;
        _window.SizeChanged += OnSizeChanged;
    }

    /// <inheritdoc />
    public event EventHandler<HostSizeChangedEventArgs>? SizeChanged;

    /// <inheritdoc />
    public void ApplySize(Size size)
    {
        if (size.Width > 0)
        {
            _window.Width = size.Width;
        }

        if (size.Height > 0)
        {
            _window.Height = size.Height;
        }
    }

    /// <inheritdoc />
    public void SetShutdownGuard(Func<CancellationToken, ValueTask<bool>> guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        _guard = guard;
    }

    /// <inheritdoc />
    public async ValueTask RequestShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_guard is not null && !await _guard(cancellationToken).ConfigureAwait(true))
        {
            return;
        }

        _closeApproved = true;
        _window.Close();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.Closing -= OnClosing;
        _window.SizeChanged -= OnSizeChanged;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) =>
        SizeChanged?.Invoke(this, new HostSizeChangedEventArgs(e.NewSize));

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeApproved || _guard is null)
        {
            return;
        }

        // Cancel first and re-close on approval: the guard is asynchronous and may
        // show a dialog, and there is no way to hold a synchronous close open.
        e.Cancel = true;

        try
        {
            if (await _guard(CancellationToken.None).ConfigureAwait(true))
            {
                _closeApproved = true;
                _window.Close();
            }
        }
        catch (Exception)
        {
            // A guard that throws must not strand the user in an unclosable window,
            // but it must also not discard their work by closing anyway. Leaving the
            // window open is the safe half of that trade; the exception surfaces
            // through the shell's own error reporting.
        }
    }
}
