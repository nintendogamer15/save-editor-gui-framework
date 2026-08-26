using Avalonia;

namespace SaveEditor.Ui.Hosting;

/// <summary>Reports a size change originating from the host.</summary>
/// <param name="Size">The new client size.</param>
public sealed record HostSizeChangedEventArgs(Size Size);

/// <summary>
/// The window and application authority that an embeddable shell cannot hold itself.
/// </summary>
/// <remarks>
/// <para>
/// <c>EditorShell</c> is a <see cref="Avalonia.Controls.UserControl"/>. It cannot
/// size a window and it cannot shut down an application, so everything requiring
/// that authority is delegated here and the consuming app supplies the
/// implementation. A default <c>WindowEditorHost</c> covers the ordinary
/// single-window case.
/// </para>
/// <para>
/// Shutdown is veto-capable from both directions. The framework installs a guard
/// through <see cref="SetShutdownGuard"/>; the host consults that same guard
/// whether shutdown was initiated by the window's close button or by
/// <c>File &gt; Exit</c> calling <see cref="RequestShutdownAsync"/>. One guard
/// serves both paths, so pending and unsaved changes cannot be lost through
/// whichever route the plan did not anticipate.
/// </para>
/// <para>
/// When no host is supplied, window size is not persisted and <c>File &gt; Exit</c>
/// is hidden rather than present and inert.
/// </para>
/// </remarks>
public interface IEditorHost
{
    /// <summary>Applies a previously persisted size to the host window.</summary>
    /// <param name="size">The size to restore, already clamped to screen bounds.</param>
    void ApplySize(Size size);

    /// <summary>Raised by the host when its size changes, so it can be persisted.</summary>
    event EventHandler<HostSizeChangedEventArgs>? SizeChanged;

    /// <summary>
    /// Installs the guard the host must consult before shutting down.
    /// </summary>
    /// <param name="guard">
    /// Returns <see langword="true"/> to allow shutdown. Runs the pending and
    /// unsaved-change confirmation flow, so it may show UI and may take time.
    /// </param>
    void SetShutdownGuard(Func<CancellationToken, ValueTask<bool>> guard);

    /// <summary>
    /// Asks the host to shut down, as raised by <c>File &gt; Exit</c>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <remarks>
    /// The host runs the installed guard and shuts down only if it allows. The
    /// framework never calls application-lifetime APIs itself.
    /// </remarks>
    ValueTask RequestShutdownAsync(CancellationToken cancellationToken = default);
}
