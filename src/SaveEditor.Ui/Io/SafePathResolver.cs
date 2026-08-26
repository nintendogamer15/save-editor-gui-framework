using Microsoft.Win32.SafeHandles;

namespace SaveEditor.Ui.Io;

/// <summary>
/// The framework's implementation of <see cref="ISafePathResolver"/> for Windows and Linux.
/// </summary>
/// <remarks>
/// <para>
/// Resolution runs in a fixed order, and every step happens before the next one is
/// allowed to touch the filesystem:
/// </para>
/// <list type="number">
/// <item><description>
/// Syntactic screening. Device namespaces, GLOBALROOT, extended-length prefixes,
/// Windows reserved device names, trailing dots and spaces, traversal components, and
/// — unless <see cref="PathResolutionOptions.AllowNonLocalPaths"/> is set — UNC paths
/// are refused before any syscall. A UNC path that reaches the filesystem has already
/// triggered an outbound SMB connection and an NTLM authentication attempt, so the
/// refusal has to come first.
/// </description></item>
/// <item><description>
/// An ancestor walk from the volume root. Every intermediate component is checked for
/// being a link, not just the leaf, because a junction or symlink in the middle of the
/// path redirects the write just as effectively while the leaf still looks like a plain
/// file. That is why <see cref="PathRefusalReason.LinkInAncestor"/> exists alongside
/// <see cref="PathRefusalReason.LinkTarget"/>.
/// </description></item>
/// <item><description>
/// A leaf open with link following disabled — <c>O_NOFOLLOW | O_CLOEXEC</c> on Linux,
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> with reparse-tag inspection on Windows. A Windows
/// reparse point is refused when its tag carries the name-surrogate bit, which is what
/// marks a tag as standing in for an object elsewhere: symbolic links and junctions do,
/// and are refused in either leaf or ancestor position. Tags that do not — cloud
/// placeholders, deduplicated content, WOF-compressed files — name the same object and
/// redirect nothing, so they pass through to the regular-file and identity checks
/// unchanged. Note that reading such a file may cause the operating system to hydrate it
/// from cloud storage; that is the OS acting on a file the user selected, not the
/// framework opening a connection of its own.
/// </description></item>
/// <item><description>
/// A regular-file check read from the open descriptor. FIFOs, devices, sockets, named
/// pipes, and directories are refused.
/// </description></item>
/// <item><description>
/// Identity, hard-link count, and size recorded from the open handle, followed by the
/// size and hard-link policies.
/// </description></item>
/// </list>
/// <para>
/// Refusal is a result, not an exception: a hostile or malformed path produces
/// <see cref="PathResolution.Refused"/>. Exceptions escaping the platform layer are
/// converted to refusals. Only cancellation propagates.
/// </para>
/// <para>
/// <strong>Non-local paths.</strong> Both UNC syntax and a drive letter mapped to a
/// network share are gated by <see cref="PathResolutionOptions.AllowNonLocalPaths"/>.
/// The exposure the option exists for is the outbound SMB connection and the NTLM
/// authentication attempt, which a mapped drive letter produces exactly as a UNC path
/// does, so the two are refused identically.
/// </para>
/// <para>
/// <strong>Out of scope.</strong> Bind mounts on Linux, and volume mount points reached
/// through a drive letter on Windows, carry no link attribute on the components the
/// caller names. They are undetectable by this primitive and are stated as out of scope
/// rather than implied to be covered.
/// </para>
/// <para>
/// <strong>The ancestor walk is not equally strong on both platforms.</strong> On Linux
/// it is a chain of <c>openat</c> calls, each relative to the descriptor of the component
/// already validated, so the leaf is opened relative to the directory that was actually
/// checked and there is no window between check and use. Windows has no handle-relative
/// open short of <c>NtCreateFile</c>, so each ancestor is re-opened by absolute path and a
/// directory component can in principle be swapped for a junction between its check and
/// the leaf open. The window is narrow and the leaf-level race is closed separately by
/// comparing file identity across the two leaf opens, but it is not zero. This is the same
/// platform asymmetry that finding A5 is dispositioned <c>FIX (narrow)</c> for, and it is
/// recorded here because <c>SafeFileWorkflow</c> is built on this primitive and should not
/// assume a guarantee Windows does not provide.
/// </para>
/// </remarks>
public sealed class SafePathResolver : ISafePathResolver
{
    /// <summary>Creates a resolver.</summary>
    /// <remarks>
    /// The resolver holds no state. It is safe to share one instance across the
    /// application, and safe to call concurrently.
    /// </remarks>
    public SafePathResolver()
    {
    }

    /// <inheritdoc />
    /// <remarks>
    /// When <see cref="PathResolutionOptions.Mode"/> is
    /// <see cref="PathResolutionMode.CreateNew"/> this behaves exactly as
    /// <see cref="CreateNewAsync"/>: the only way to hand back a
    /// <see cref="ResolvedFile"/> for a path that must not already exist is to create
    /// it exclusively, and creating it any other way would reintroduce the window the
    /// mode exists to close.
    /// </remarks>
    public ValueTask<PathResolution> ResolveAsync(
        string path,
        PathResolutionOptions options,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(path, options, forceCreateNew: false, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The exclusive-create mode is taken from the method, not from
    /// <see cref="PathResolutionOptions.Mode"/>; passing
    /// <see cref="PathResolutionMode.OpenExisting"/> here cannot downgrade the call to a
    /// link-following open.
    /// </remarks>
    public ValueTask<PathResolution> CreateNewAsync(
        string path,
        PathResolutionOptions options,
        CancellationToken cancellationToken = default)
    {
        return RunAsync(path, options, forceCreateNew: true, cancellationToken);
    }

    private static ValueTask<PathResolution> RunAsync(
        string path,
        PathResolutionOptions options,
        bool forceCreateNew,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<PathResolution>(cancellationToken);
        }

        // Every step below is a blocking syscall. A spun-down disk, a cloud-placeholder
        // hydration, or an opted-in network share can stall any of them, so the work is
        // kept off the caller's thread.
        return new ValueTask<PathResolution>(
            Task.Run(() => Resolve(path, options, forceCreateNew), cancellationToken));
    }

    private static PathResolution Resolve(string path, PathResolutionOptions options, bool forceCreateNew)
    {
        try
        {
            if (options is null)
            {
                return Refuse(PathRefusalReason.InvalidPath, "No resolution options were supplied.");
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                return Refuse(PathRefusalReason.InvalidPath, "The path is empty.");
            }

            var mode = forceCreateNew ? PathResolutionMode.CreateNew : options.Mode;

            var syntaxRefusal = PathSyntaxGuard.Validate(path, options, out var root, out var components);
            if (syntaxRefusal is not null)
            {
                return syntaxRefusal;
            }

            var outcome = OperatingSystem.IsWindows()
                ? WindowsSafeOpen.Open(root, components, mode, options)
                : UnixSafeOpen.Open(root, components, mode, options);

            if (outcome is NativeOpenOutcome.Refused refused)
            {
                return new PathResolution.Refused(refused.Reason, refused.Detail);
            }

            var opened = (NativeOpenOutcome.Opened)outcome;
            return Complete(opened, root, components, mode, options);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Requirement: a hostile or malformed path is refused, never thrown from.
            return Refuse(
                PathRefusalReason.Unreadable,
                $"Resolution failed with an unexpected {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static PathResolution Complete(
        NativeOpenOutcome.Opened opened,
        string root,
        IReadOnlyList<string> components,
        PathResolutionMode mode,
        PathResolutionOptions options)
    {
        var handle = opened.Handle;
        var facts = opened.Facts;

        if (facts.Size > options.MaxBytes)
        {
            handle.Dispose();
            return Refuse(
                PathRefusalReason.TooLarge,
                $"The target is {facts.Size} bytes, above the configured maximum of {options.MaxBytes} bytes.");
        }

        var access = mode == PathResolutionMode.CreateNew || options.ForWriting
            ? FileAccess.ReadWrite
            : FileAccess.Read;

        FileStream stream;
        try
        {
            stream = new FileStream(handle, access);
        }
        catch (Exception ex)
        {
            handle.Dispose();
            return Refuse(
                PathRefusalReason.Unreadable,
                $"The opened handle could not be wrapped for use ({ex.GetType().Name}: {ex.Message}).");
        }

        var canonicalPath = PathSyntaxGuard.BuildCanonicalPath(root, components, components.Count);

        ResolvedFile file;
        try
        {
            file = new ResolvedFile(stream, canonicalPath, facts.Identity, facts.HardLinkCount, CreateIdentityProbe(facts.Identity));
        }
        catch (Exception ex)
        {
            stream.Dispose();
            return Refuse(
                PathRefusalReason.Unreadable,
                $"The resolved file could not be constructed ({ex.GetType().Name}: {ex.Message}).");
        }

        // Hard-link aliasing is reported ahead of size: replacing the content of an
        // aliased file changes every alias, which is a correctness consequence rather
        // than a cost-of-reading one.
        if (facts.HardLinkCount > 1)
        {
            return new PathResolution.NeedsConfirmation(file, PathConfirmationKind.MultipleHardLinks);
        }

        if (facts.Size > options.ConfirmAboveBytes)
        {
            return new PathResolution.NeedsConfirmation(file, PathConfirmationKind.UnusuallyLarge);
        }

        return new PathResolution.Resolved(file);
    }

    /// <summary>
    /// Builds the delegate <see cref="ResolvedFile.ReassertIdentity"/> calls.
    /// </summary>
    /// <remarks>
    /// Failure to read identity must not be reported as a match. Rather than returning a
    /// sentinel that could collide with a real identity, the probe returns a value derived
    /// from — and guaranteed unequal to — the recorded one, so a failed probe always fails
    /// closed.
    /// </remarks>
    private static Func<FileStream, FileIdentity> CreateIdentityProbe(FileIdentity recorded)
    {
        return stream =>
        {
            try
            {
                SafeFileHandle handle = stream.SafeFileHandle;

                var identity = OperatingSystem.IsWindows()
                    ? WindowsSafeOpen.ReadIdentity(handle)
                    : UnixSafeOpen.ReadIdentity(handle);

                return identity ?? GuaranteedMismatch(recorded);
            }
            catch (Exception)
            {
                return GuaranteedMismatch(recorded);
            }
        };
    }

    private static FileIdentity GuaranteedMismatch(FileIdentity recorded) =>
        new(recorded.VolumeId ^ 1UL, ~recorded.FileId);

    private static PathResolution.Refused Refuse(PathRefusalReason reason, string detail) => new(reason, detail);
}
