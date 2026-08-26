using System.Security.Cryptography;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Display;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Workflow;

/// <summary>
/// The framework's safe file workflow: open, detect, decode, validate, back up, write, and
/// replace, with every destructive step gated (<c>PLAN.md</c> §7).
/// </summary>
/// <typeparam name="TDocument">The editor's in-memory document type.</typeparam>
/// <remarks>
/// <para>
/// <strong>Save As is the default write path.</strong> Overwriting an existing save is a
/// separately named operation that always makes a verified backup first. That asymmetry is
/// the single largest safety property in the product, and it is a property of the API
/// shape rather than of a dialog default.
/// </para>
/// <para>
/// <strong>Original-file preservation means one specific thing.</strong> On any failure,
/// the bytes at the target path are exactly the pre-operation bytes. Not guaranteed and
/// not claimed: file identity — <c>rename(2)</c> unlinks the original inode — hard-link
/// aliasing, views other processes hold through open handles, and creation or change
/// timestamps. Permissions are preserved.
/// </para>
/// <para>
/// <strong>The failure policy is fail-loud, deliberately.</strong> Every operation returns
/// a definitive <see cref="SaveOutcome"/> and every failure is reported to the user. This
/// is the opposite of the settings store, which is fail-soft, and the two must not be
/// unified behind a shared helper that fixes the policy: a save that silently does not
/// happen is the worst outcome this product can produce, and a preference that silently
/// does not persist is a minor one (finding B10).
/// </para>
/// <para>
/// <strong>A codec is contained, not sandboxed.</strong> Codec implementations are
/// in-process, full-privilege .NET running as the user, and a hostile one is explicitly
/// out of scope (<c>PLAN.md</c> §8). What the workflow provides is containment of honest
/// mistakes: bounded inputs, isolated and time-boxed detection, serialization into bounded
/// memory rather than onto the destination, exception containment at this boundary, and
/// round-trip falsification of the preservation claim.
/// <see cref="StackOverflowException"/> and a process-level out-of-memory kill are
/// unrecoverable in .NET and are not contained by anything here; they terminate the
/// process, which is why the residue sweep exists.
/// </para>
/// </remarks>
public sealed class SafeFileWorkflow<TDocument>
{
    private const int CopyChunkBytes = 128 * 1024;

    private readonly SafeFileWorkflowOptions<TDocument> _options;
    private readonly ISaveFormatChooser _chooser;
    private readonly HashSet<string> _writtenDirectories = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Creates a workflow.</summary>
    /// <param name="options">Collaborators and bounds.</param>
    public SafeFileWorkflow(SafeFileWorkflowOptions<TDocument> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Registry);
        ArgumentNullException.ThrowIfNull(options.Interaction);

        _options = options;
        _chooser = options.FormatChooser ?? new ConfirmationSaveFormatChooser(options.Interaction);
    }

    /// <summary>Directories this workflow has created a temporary or backup file in.</summary>
    /// <remarks>
    /// The residue sweep is scoped to these — plus anything the caller adds — rather than
    /// to the filesystem at large. A sweep that ranged wider would be a deletion pass over
    /// directories the framework never wrote to.
    /// </remarks>
    public IReadOnlyCollection<string> DirectoriesWrittenTo
    {
        get
        {
            lock (_gate)
            {
                return [.. _writtenDirectories];
            }
        }
    }

    /// <summary>Removes framework temporary files left behind by a kill or a power loss.</summary>
    /// <param name="additionalDirectories">
    /// Directories to sweep beyond <see cref="DirectoriesWrittenTo"/> — at startup, the
    /// directories of the recent files, which are where the framework wrote last run.
    /// </param>
    /// <returns>What the sweep did.</returns>
    public TempSweepReport SweepTemporaryResidue(IEnumerable<string>? additionalDirectories = null)
    {
        var directories = new List<string>(DirectoriesWrittenTo);
        if (additionalDirectories is not null)
        {
            directories.AddRange(additionalDirectories);
        }

        return TempResidueSweeper.Sweep(
            directories,
            _options.TemporaryResidueMinimumAge,
            _options.TimeProvider);
    }

    /// <summary>Opens, detects, decodes, and tests the preservation claim of one save file.</summary>
    /// <param name="path">Path supplied by a picker, a recent entry, or a drop.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the open at the workflow boundary.</param>
    /// <returns>The open file, or why it was not opened.</returns>
    public async ValueTask<OpenOutcome<TDocument>> OpenAsync(
        string path,
        IProgress<SaveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        ResolvedFile? file = null;
        try
        {
            var resolution = await _options.PathResolver
                .ResolveAsync(path, _options.ReadResolution, cancellationToken)
                .ConfigureAwait(false);

            switch (resolution)
            {
                case PathResolution.Resolved resolved:
                    file = resolved.File;
                    break;

                case PathResolution.NeedsConfirmation needs:
                    file = needs.File;
                    if (!await ConfirmResolutionAsync(path, needs.Kind, cancellationToken).ConfigureAwait(false))
                    {
                        return new OpenOutcome<TDocument>.Declined("The open was declined.");
                    }

                    break;

                case PathResolution.Refused refused:
                    await ReportAsync("Cannot open this file", refused.Detail, cancellationToken).ConfigureAwait(false);
                    return new OpenOutcome<TDocument>.Failed(
                        refused.Reason == PathRefusalReason.TooLarge ? SaveFailureReason.TooLarge : SaveFailureReason.PathRefused,
                        refused.Detail);

                default:
                    return new OpenOutcome<TDocument>.Failed(SaveFailureReason.Unexpected, "The path resolver returned an unrecognized result.");
            }

            progress?.Report(new SaveProgress(SavePhase.Reading));
            var (baseline, bytes) = await _options.ChangeGuard.CaptureAsync(file, cancellationToken).ConfigureAwait(false);

            progress?.Report(new SaveProgress(SavePhase.Detecting, bytes.LongLength, bytes.LongLength));
            var detection = await _options.Registry.DetectAsync(bytes, cancellationToken).ConfigureAwait(false);

            var codec = detection.Codec;
            if (codec is null && detection.IsAmbiguous)
            {
                var chosen = await _chooser
                    .ChooseAsync([.. detection.Candidates.Select(c => c.Format)], Path.GetFileName(file.CanonicalPath), cancellationToken)
                    .ConfigureAwait(false);

                if (chosen is null)
                {
                    return new OpenOutcome<TDocument>.Declined("No format was chosen, so the file was not opened.");
                }

                codec = detection.Candidates.FirstOrDefault(c => string.Equals(c.Format.Id, chosen.Id, StringComparison.Ordinal));
            }

            if (codec is null)
            {
                await ReportAsync("Unsupported file", detection.Detail, cancellationToken).ConfigureAwait(false);
                return new OpenOutcome<TDocument>.Failed(SaveFailureReason.DetectionFailed, detection.Detail);
            }

            progress?.Report(new SaveProgress(SavePhase.Decoding, bytes.LongLength, bytes.LongLength));

            TDocument document;
            try
            {
                document = await RunCodecAsync(
                    () => codec.DecodeAsync(new MemoryStream(bytes, writable: false), cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var detail = $"The {codec.Format.DisplayName} codec failed while decoding this file ({ex.GetType().Name}: {ex.Message}).";
                await ReportAsync("Cannot read this save", detail, cancellationToken).ConfigureAwait(false);
                return new OpenOutcome<TDocument>.Failed(SaveFailureReason.CodecFailed, detail);
            }

            var (verification, verificationDetail) = await VerifyPreservationClaimAsync(
                codec, document, bytes, progress, cancellationToken).ConfigureAwait(false);

            var open = new OpenSaveFile<TDocument>(file, codec, document, baseline, verification, verificationDetail);
            file = null;

            progress?.Report(new SaveProgress(SavePhase.Completed));
            return new OpenOutcome<TDocument>.Opened(open);
        }
        catch (OperationCanceledException)
        {
            return new OpenOutcome<TDocument>.Cancelled();
        }
        catch (Exception ex)
        {
            return new OpenOutcome<TDocument>.Failed(
                SaveFailureReason.Unexpected,
                $"The open failed unexpectedly ({ex.GetType().Name}: {ex.Message}).");
        }
        finally
        {
            file?.Dispose();
        }
    }

    /// <summary>
    /// Writes the document to a path the user chooses. This is the default write path.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <param name="codec">The codec that will serialize it.</param>
    /// <param name="current">The currently open file, when there is one.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the write at the workflow boundary.</param>
    /// <returns>The definitive outcome.</returns>
    /// <remarks>
    /// A picker may declare that it already obtained overwrite confirmation. That
    /// declaration suppresses only the duplicate prompt: whenever the framework
    /// independently observes that the chosen target exists and is not the currently open
    /// document, it confirms anyway. A picker that claims to confirm and does not would
    /// otherwise produce exactly the silent overwrite this workflow exists to prevent, and
    /// one redundant prompt costs far less than one destroyed save (finding A7).
    /// </remarks>
    public async ValueTask<SaveOutcome> SaveAsAsync(
        TDocument document,
        ISaveCodec<TDocument> codec,
        OpenSaveFile<TDocument>? current = null,
        IProgress<SaveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(codec);

        ResolvedFile? destination = null;
        var ownsDestination = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var blocked = await BlockOnValidationErrorsAsync(codec, document, progress, cancellationToken).ConfigureAwait(false);
            if (blocked is not null)
            {
                return blocked;
            }

            var pick = await _options.Interaction.PickSaveFileAsync(
                new FilePickerRequest(
                    "Save a copy",
                    _options.Registry.Formats,
                    current is null ? null : Path.GetFileName(current.Path),
                    current is null ? null : Path.GetDirectoryName(current.Path)),
                cancellationToken).ConfigureAwait(false);

            if (pick is null)
            {
                return SaveOutcome.Declined("No destination was chosen.");
            }

            var destinationExists = false;
            var isCurrentDocument = false;
            ContentBaseline? destinationBaseline = null;

            if (current is not null && !current.IsStale && SamePath(pick.Path, current.Path))
            {
                // Saving over the document that is already open re-uses its retained
                // handle rather than resolving its path a second time. Re-resolving would
                // both re-open the check-then-use window the first resolution closed and,
                // on Windows, collide with the framework's own deny-write share mode.
                destination = current.File;
                destinationExists = true;
                destinationBaseline = current.Baseline;
                isCurrentDocument = true;
            }
            else
            {
                var resolution = await _options.PathResolver
                    .ResolveAsync(pick.Path, _options.WriteResolution, cancellationToken)
                    .ConfigureAwait(false);

                switch (resolution)
                {
                    case PathResolution.Resolved resolved:
                        destination = resolved.File;
                        ownsDestination = true;
                        destinationExists = true;
                        break;

                    case PathResolution.NeedsConfirmation needs:
                        destination = needs.File;
                        ownsDestination = true;
                        destinationExists = true;
                        if (!await ConfirmResolutionAsync(pick.Path, needs.Kind, cancellationToken).ConfigureAwait(false))
                        {
                            return SaveOutcome.Declined("The write was declined.");
                        }

                        break;

                    case PathResolution.Refused { Reason: PathRefusalReason.NotFound }:
                        break;

                    case PathResolution.Refused { Reason: PathRefusalReason.WriteProtected } protectedRefusal:
                        return await FailAsync(SaveFailureReason.WriteProtected, protectedRefusal.Detail, pick.Path, cancellationToken).ConfigureAwait(false);

                    case PathResolution.Refused refused:
                        return await FailAsync(SaveFailureReason.PathRefused, refused.Detail, pick.Path, cancellationToken).ConfigureAwait(false);

                    default:
                        return await FailAsync(SaveFailureReason.Unexpected, "The path resolver returned an unrecognized result.", pick.Path, cancellationToken).ConfigureAwait(false);
                }
            }

            if (destinationExists && destination is not null)
            {
                var protection = WriteProtection.Describe(destination.Stream);
                if (protection is not null)
                {
                    return await FailAsync(SaveFailureReason.WriteProtected, protection, destination.CanonicalPath, cancellationToken).ConfigureAwait(false);
                }

                // The picker's declaration suppresses only the duplicate prompt. A target
                // the framework independently observes to exist and to be something other
                // than the open document is confirmed regardless of what the picker claims.
                if (!pick.PickerConfirmedOverwrite || !isCurrentDocument)
                {
                    var accepted = await ConfirmOverwriteAsync(
                        destination.CanonicalPath,
                        isCurrentDocument ? current : null,
                        details: [],
                        cancellationToken).ConfigureAwait(false);

                    if (!accepted)
                    {
                        return SaveOutcome.Declined("The overwrite was declined.");
                    }
                }
            }

            var destinationPath = destinationExists && destination is not null ? destination.CanonicalPath : pick.Path;

            var directory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(directory))
            {
                return await FailAsync(SaveFailureReason.PathRefused, "The destination has no containing directory.", pick.Path, cancellationToken).ConfigureAwait(false);
            }

            var write = await WriteAsync(
                document,
                codec,
                directory,
                destinationPath,
                destination,
                destinationBaseline,
                destinationExists,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (write.Outcome.Status == SaveStatus.Succeeded && isCurrentDocument && current is not null && write.Baseline is not null)
            {
                await RebindAsync(current, write.Baseline, cancellationToken).ConfigureAwait(false);
            }

            return write.Outcome;
        }
        catch (OperationCanceledException)
        {
            return SaveOutcome.Cancelled();
        }
        catch (Exception ex)
        {
            return await FailAsync(
                SaveFailureReason.Unexpected,
                $"The save failed unexpectedly ({ex.GetType().Name}: {ex.Message}).",
                path: null,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (ownsDestination)
            {
                destination?.Dispose();
            }
        }
    }

    /// <summary>Whether two path strings name the same entry, by this platform's rules.</summary>
    /// <remarks>
    /// Used only to decide whether the workflow may re-use a handle it already holds. It is
    /// never used to decide that a write is safe: that decision comes from file identity
    /// recorded at resolution time, which a path comparison cannot substitute for.
    /// </remarks>
    private static bool SamePath(string candidate, string canonical)
    {
        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(full, canonical, comparison) || string.Equals(candidate, canonical, comparison);
    }

    /// <summary>
    /// Backs the original up, verifies the backup, and only then replaces it.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <param name="open">The open file, whose retained handle is the original.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the write at the workflow boundary.</param>
    /// <returns>The definitive outcome.</returns>
    /// <remarks>
    /// <para>
    /// The backup is <strong>all-or-nothing</strong>. It is written from the same retained
    /// handle that produced the change-detection baseline, flushed, and then hashed and
    /// compared against that baseline — so "a backup exists" and "the backup is the file
    /// that was there" are the same statement. A failure at any step aborts the overwrite
    /// with the original untouched and the partial backup removed. If the sibling directory
    /// cannot be written, the user is offered an explicit alternate location; the workflow
    /// never proceeds without a backup (finding B1).
    /// </para>
    /// </remarks>
    public async ValueTask<SaveOutcome> OverwriteWithBackupAsync(
        TDocument document,
        OpenSaveFile<TDocument> open,
        IProgress<SaveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(open);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (open.IsStale)
            {
                return await FailAsync(
                    SaveFailureReason.IdentityChanged,
                    "This document is no longer bound to a handle that can prove what it points at. Reopen it before overwriting.",
                    open.Path,
                    cancellationToken).ConfigureAwait(false);
            }

            var directory = Path.GetDirectoryName(open.Path);
            if (string.IsNullOrEmpty(directory))
            {
                return await FailAsync(SaveFailureReason.PathRefused, "The target has no containing directory.", open.Path, cancellationToken).ConfigureAwait(false);
            }

            if (!open.File.ReassertIdentity())
            {
                return await FailAsync(
                    SaveFailureReason.IdentityChanged,
                    "The retained handle no longer refers to the file that was opened. The overwrite was abandoned.",
                    open.Path,
                    cancellationToken).ConfigureAwait(false);
            }

            var protection = WriteProtection.Describe(open.File.Stream);
            if (protection is not null)
            {
                return await FailAsync(SaveFailureReason.WriteProtected, protection, open.Path, cancellationToken).ConfigureAwait(false);
            }

            var blocked = await BlockOnValidationErrorsAsync(open.Codec, document, progress, cancellationToken).ConfigureAwait(false);
            if (blocked is not null)
            {
                return blocked;
            }

            // The warnings shown inside the destructive confirmation are the most severe
            // eight of the same report, sorted by severity rather than taken in codec
            // order: a codec emitting thousands of trivial warnings must not be able to
            // bury the one that mattered under the accept button.
            var warnings = await CollectWarningsAsync(open.Codec, document, cancellationToken).ConfigureAwait(false);

            var accepted = await ConfirmOverwriteAsync(open.Path, open, warnings, cancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                return SaveOutcome.Declined("The overwrite was declined.");
            }

            var before = await _options.ChangeGuard.VerifyAsync(open.File, open.Baseline, cancellationToken).ConfigureAwait(false);
            if (before.Verdict != ExternalChangeVerdict.Unchanged)
            {
                return await FailAsync(SaveFailureReason.ExternalChange, before.Detail, open.Path, cancellationToken).ConfigureAwait(false);
            }

            var backup = await CreateVerifiedBackupAsync(open, directory, progress, cancellationToken).ConfigureAwait(false);
            if (backup.Path is null)
            {
                return await FailAsync(SaveFailureReason.BackupFailed, backup.Detail, open.Path, cancellationToken).ConfigureAwait(false);
            }

            var write = await WriteAsync(
                document,
                open.Codec,
                directory,
                open.Path,
                open.File,
                open.Baseline,
                destinationExists: true,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (write.Outcome.Status != SaveStatus.Succeeded)
            {
                return write.Outcome with { BackupPath = backup.Path };
            }

            await RebindAsync(open, write.Baseline!, cancellationToken).ConfigureAwait(false);

            return SaveOutcome.Success(open.Path, backup.Path) with
            {
                Message = SaveOutcome.Success(open.Path, backup.Path).Message + " " + write.Detail,
            };
        }
        catch (OperationCanceledException)
        {
            return SaveOutcome.Cancelled();
        }
        catch (Exception ex)
        {
            return await FailAsync(
                SaveFailureReason.Unexpected,
                $"The overwrite failed unexpectedly ({ex.GetType().Name}: {ex.Message}).",
                open.Path,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async ValueTask RebindAsync(OpenSaveFile<TDocument> open, ContentBaseline baseline, CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await _options.PathResolver
                .ResolveAsync(open.Path, _options.ReadResolution, cancellationToken)
                .ConfigureAwait(false);

            var refreshed = resolution switch
            {
                PathResolution.Resolved resolved => resolved.File,
                PathResolution.NeedsConfirmation needs => needs.File,
                _ => null,
            };

            if (refreshed is null)
            {
                open.IsStale = true;
                return;
            }

            open.Rebind(refreshed, baseline);
        }
        catch (Exception)
        {
            open.IsStale = true;
        }
    }

    private async ValueTask<(SaveOutcome Outcome, ContentBaseline? Baseline, string Detail)> WriteAsync(
        TDocument document,
        ISaveCodec<TDocument> codec,
        string directory,
        string destinationPath,
        ResolvedFile? destination,
        ContentBaseline? baseline,
        bool destinationExists,
        IProgress<SaveProgress>? progress,
        CancellationToken cancellationToken)
    {
        // The last full validation, immediately before the bytes are produced. The earlier
        // pass gated the destructive confirmation; this one gates the write itself, because
        // the document is editable in between.
        var blocked = await BlockOnValidationErrorsAsync(codec, document, progress, cancellationToken).ConfigureAwait(false);
        if (blocked is not null)
        {
            return (blocked, null, string.Empty);
        }

        progress?.Report(new SaveProgress(SavePhase.Serializing));

        byte[] payload;
        try
        {
            using var buffer = new BoundedWriteStream(_options.MaxSerializedBytes);
            await RunCodecAsync(() => codec.SerializeAsync(document, buffer, cancellationToken), cancellationToken).ConfigureAwait(false);
            payload = buffer.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = $"The {codec.Format.DisplayName} codec failed while serializing ({ex.GetType().Name}: {ex.Message}). Nothing was written and the target is unchanged.";
            return (await FailAsync(SaveFailureReason.CodecFailed, detail, destinationPath, cancellationToken).ConfigureAwait(false), null, string.Empty);
        }

        var roundTrip = await VerifyRoundTripAsync(codec, document, payload, progress, cancellationToken).ConfigureAwait(false);
        if (roundTrip is not null)
        {
            return (await FailAsync(SaveFailureReason.RoundTripMismatch, roundTrip, destinationPath, cancellationToken).ConfigureAwait(false), null, string.Empty);
        }

        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report(new SaveProgress(SavePhase.WritingTemp, 0, payload.LongLength));

        var temporaryPath = Path.Combine(directory, _options.FileNames.NextTemporaryFileName());
        var creation = await _options.PathResolver
            .CreateNewAsync(temporaryPath, _options.CreateResolution, cancellationToken)
            .ConfigureAwait(false);

        if (creation is not PathResolution.Resolved created)
        {
            var detail = creation is PathResolution.Refused refused
                ? $"The temporary file could not be created exclusively: {refused.Detail} The save was abandoned rather than retried through a link-following open."
                : "The temporary file could not be created exclusively.";

            return (await FailAsync(SaveFailureReason.TempCreationFailed, detail, destinationPath, cancellationToken).ConfigureAwait(false), null, string.Empty);
        }

        Track(directory);

        var temporary = created.File;
        var replaced = false;

        try
        {
            await temporary.Stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            progress?.Report(new SaveProgress(SavePhase.WritingTemp, payload.LongLength, payload.LongLength));

            if (destination is not null)
            {
                progress?.Report(new SaveProgress(SavePhase.PreservingPermissions));

                var original = _options.Permissions.Capture(destination.Stream);
                var copy = _options.Permissions.CopyOnto(destination.Stream, temporary.Stream, temporary.CanonicalPath, temporary.Identity);

                if (copy.Status == PermissionCopyStatus.Failed)
                {
                    return (await FailAsync(SaveFailureReason.PermissionCopyFailed, copy.Detail, destinationPath, cancellationToken).ConfigureAwait(false), null, string.Empty);
                }

                var candidate = _options.Permissions.Capture(temporary.Stream);
                if (_options.Permissions.IsBroaderThan(candidate, original, out var widening))
                {
                    return (await FailAsync(SaveFailureReason.PermissionWidening, widening, destinationPath, cancellationToken).ConfigureAwait(false), null, string.Empty);
                }
            }

            if (destination is not null && baseline is not null)
            {
                progress?.Report(new SaveProgress(SavePhase.CheckingForExternalChange));

                var check = await _options.ChangeGuard.VerifyAsync(destination, baseline, cancellationToken).ConfigureAwait(false);
                if (check.Verdict != ExternalChangeVerdict.Unchanged)
                {
                    return (await FailAsync(SaveFailureReason.ExternalChange, check.Detail, destinationPath, cancellationToken).ConfigureAwait(false), null, string.Empty);
                }
            }

            // Authoritative cancellation: the last gate before anything irreversible.
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new SaveProgress(SavePhase.Replacing));

            await _options.Durability.FlushFileAsync(temporary.Stream, cancellationToken).ConfigureAwait(false);

            var temporaryIdentity = temporary.Identity;
            temporary.Dispose();

            var replace = await _options.Durability
                .ReplaceAsync(temporaryPath, temporaryIdentity, destinationPath, destinationExists, cancellationToken)
                .ConfigureAwait(false);

            if (replace.Status != ReplaceStatus.Replaced)
            {
                var reason = replace.Status == ReplaceStatus.NotAtomic
                    ? SaveFailureReason.AtomicReplaceUnsupported
                    : SaveFailureReason.Unexpected;

                return (await FailAsync(reason, replace.Detail, destinationPath, cancellationToken).ConfigureAwait(false), null, string.Empty);
            }

            replaced = true;

            var flush = await _options.Durability.FlushDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);

            progress?.Report(new SaveProgress(SavePhase.Completed, payload.LongLength, payload.LongLength));

            var newBaseline = new ContentBaseline(SHA256.HashData(payload), payload.LongLength, null);

            return (
                SaveOutcome.Success(destinationPath),
                newBaseline,
                flush.Status == DirectoryFlushStatus.Failed ? flush.Detail : string.Empty);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (
                await FailAsync(
                    SaveFailureReason.Unexpected,
                    $"The write failed ({ex.GetType().Name}: {ex.Message}). The target is unchanged.",
                    destinationPath,
                    cancellationToken).ConfigureAwait(false),
                null,
                string.Empty);
        }
        finally
        {
            if (!replaced)
            {
                temporary.Dispose();
                TryDelete(temporaryPath);
            }
        }
    }

    private async ValueTask<(string? Path, string Detail)> CreateVerifiedBackupAsync(
        OpenSaveFile<TDocument> open,
        string directory,
        IProgress<SaveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var originalName = Path.GetFileName(open.Path);

        var acquired = await AcquireBackupAsync(directory, originalName, cancellationToken).ConfigureAwait(false);
        if (acquired.File is null)
        {
            return (null, acquired.Detail);
        }

        var backupPath = acquired.Path!;
        var backupDirectory = Path.GetDirectoryName(backupPath)!;
        Track(backupDirectory);

        var verified = false;
        var detail = string.Empty;

        try
        {
            using var backup = acquired.File;

            var source = open.File.Stream;
            source.Seek(0, SeekOrigin.Begin);

            var total = source.Length;
            long copied = 0;
            var chunk = new byte[CopyChunkBytes];

            while (true)
            {
                var read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await backup.Stream.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                progress?.Report(new SaveProgress(SavePhase.WritingBackup, copied, total));
            }

            await _options.Durability.FlushFileAsync(backup.Stream, cancellationToken).ConfigureAwait(false);

            progress?.Report(new SaveProgress(SavePhase.VerifyingBackup, copied, total));

            backup.Stream.Seek(0, SeekOrigin.Begin);
            var hash = await ComputeHashAsync(backup.Stream, cancellationToken).ConfigureAwait(false);

            if (!CryptographicOperations.FixedTimeEquals(hash, open.Baseline.Hash.Span))
            {
                detail =
                    "The backup was written but its hash does not match the bytes that were read from the original. " +
                    "The overwrite was abandoned with the original untouched and the backup removed.";
            }
            else
            {
                // Backups inherit the original's mode rather than the directory default.
                _ = _options.Permissions.CopyOnto(open.File.Stream, backup.Stream, backupPath, backup.Identity);
                verified = true;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            detail =
                $"The backup could not be completed ({ex.GetType().Name}: {ex.Message}). " +
                "The overwrite was abandoned with the original untouched and the partial backup removed.";
        }
        finally
        {
            if (!verified)
            {
                TryDelete(backupPath);
            }
        }

        if (!verified)
        {
            return (null, detail);
        }

        _ = BackupRetention.Apply(backupDirectory, originalName, _options.BackupRetention);

        return (backupPath, "The backup was written and verified against the bytes that were read.");
    }

    private async ValueTask<(ResolvedFile? File, string? Path, string Detail)> AcquireBackupAsync(
        string directory,
        string originalName,
        CancellationToken cancellationToken)
    {
        var candidate = Path.Combine(directory, _options.FileNames.NextBackupFileName(originalName));

        var resolution = await _options.PathResolver
            .CreateNewAsync(candidate, _options.CreateResolution, cancellationToken)
            .ConfigureAwait(false);

        if (resolution is PathResolution.Resolved resolved)
        {
            return (resolved.File, candidate, string.Empty);
        }

        var refused = resolution as PathResolution.Refused;

        if (refused is { Reason: PathRefusalReason.AlreadyExists or PathRefusalReason.LinkTarget or PathRefusalReason.LinkInAncestor })
        {
            // Something is already sitting at a name carrying 32 bits of fresh entropy.
            // That is not a collision, it is a plant, and it aborts rather than retrying.
            return (null, null, $"The backup could not be created exclusively: {refused.Detail} The overwrite was abandoned.");
        }

        // The sibling directory will not take a backup. Offer an explicit alternate
        // location rather than silently proceeding without one.
        var alternate = await _options.Interaction.PickFolderAsync(
            "Choose where to keep the backup",
            directory,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(alternate))
        {
            return (null, null,
                $"A backup could not be written next to the save ({refused?.Detail ?? "the directory refused it"}) and no alternate location was chosen. " +
                "The overwrite was abandoned; the workflow never proceeds without a backup.");
        }

        var alternateCandidate = Path.Combine(alternate, _options.FileNames.NextBackupFileName(originalName));

        var alternateResolution = await _options.PathResolver
            .CreateNewAsync(alternateCandidate, _options.CreateResolution, cancellationToken)
            .ConfigureAwait(false);

        if (alternateResolution is PathResolution.Resolved alternateResolved)
        {
            return (alternateResolved.File, alternateCandidate, string.Empty);
        }

        var alternateRefused = alternateResolution as PathResolution.Refused;
        return (null, null,
            $"A backup could not be written to the chosen location ({alternateRefused?.Detail ?? "it was refused"}). The overwrite was abandoned.");
    }

    private async ValueTask<(UnknownDataVerification Verification, string Detail)> VerifyPreservationClaimAsync(
        ISaveCodec<TDocument> codec,
        TDocument document,
        byte[] source,
        IProgress<SaveProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!codec.PreservesUnknownData)
        {
            return (UnknownDataVerification.NotClaimed,
                $"{codec.Format.DisplayName} does not claim to preserve data it does not understand, so writing may drop unrecognized fields.");
        }

        if (!_options.VerifyPreservationClaim || source.LongLength > _options.RoundTripVerificationMaxBytes)
        {
            return (UnknownDataVerification.Skipped,
                "The unknown-data preservation claim was not tested, so it rests on the codec's own assertion.");
        }

        progress?.Report(new SaveProgress(SavePhase.VerifyingPreservationClaim, source.LongLength, source.LongLength));

        byte[] reserialized;
        try
        {
            using var buffer = new BoundedWriteStream(_options.MaxSerializedBytes, source.Length);
            await RunCodecAsync(() => codec.SerializeAsync(document, buffer, cancellationToken), cancellationToken).ConfigureAwait(false);
            reserialized = buffer.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (UnknownDataVerification.Unavailable,
                $"The preservation claim could not be tested: the codec threw while re-serializing ({ex.GetType().Name}). Treat it as untested.");
        }

        if (reserialized.AsSpan().SequenceEqual(source))
        {
            return (UnknownDataVerification.Verified,
                "Re-serializing the untouched document reproduced the file byte for byte, so the preservation claim holds for this file.");
        }

        return (UnknownDataVerification.Falsified,
            $"{codec.Format.DisplayName} declares that it preserves data it does not understand, but re-serializing this file without changing anything produced " +
            $"{reserialized.LongLength} bytes where the file has {source.LongLength}. The declaration is false for this file, so saving will lose data that is in it today.");
    }

    private async ValueTask<string?> VerifyRoundTripAsync(
        ISaveCodec<TDocument> codec,
        TDocument document,
        byte[] payload,
        IProgress<SaveProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_options.VerifyRoundTripBeforeReplace || payload.LongLength > _options.RoundTripVerificationMaxBytes)
        {
            return null;
        }

        progress?.Report(new SaveProgress(SavePhase.VerifyingRoundTrip, payload.LongLength, payload.LongLength));

        TDocument decoded;
        try
        {
            decoded = await RunCodecAsync(
                () => codec.DecodeAsync(new MemoryStream(payload, writable: false), cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"The bytes this codec produced could not be read back by the same codec ({ex.GetType().Name}: {ex.Message}). The save was abandoned rather than written.";
        }

        if (_options.DocumentComparer.Equals(decoded, document))
        {
            return null;
        }

        var lost = "The bytes this codec produced do not decode back to the document that is open. " +
                   "Something was lost in serialization, so the save was abandoned rather than written.";

        // The overwhelmingly likely cause, for a document type that has not defined
        // equality, is that the comparison is by reference and can never succeed — so
        // every save fails identically. Saying "something was lost in serialization"
        // there sends the author hunting for a codec bug that does not exist.
        return ComparesByReference()
            ? lost + " This may not be a codec fault: "
                   + $"'{typeof(TDocument).Name}' does not define value equality, so the round-trip check "
                   + "is comparing object references and cannot ever match. Make the document a record, "
                   + "override Equals, or supply SafeFileWorkflowOptions.DocumentComparer."
            : lost;
    }

    /// <summary>
    /// Whether the configured comparer will fall back to reference equality for this
    /// document type.
    /// </summary>
    private bool ComparesByReference() =>
        ReferenceEquals(_options.DocumentComparer, EqualityComparer<TDocument>.Default)
        && !typeof(TDocument).IsValueType
        && typeof(TDocument).GetMethod(nameof(Equals), [typeof(object)])?.DeclaringType == typeof(object);

    private async ValueTask<SaveOutcome?> BlockOnValidationErrorsAsync(
        ISaveCodec<TDocument> codec,
        TDocument document,
        IProgress<SaveProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new SaveProgress(SavePhase.Validating));

        ValidationReport report;
        try
        {
            report = await RunCodecAsync(() => codec.ValidateAsync(document, cancellationToken), cancellationToken).ConfigureAwait(false)
                ?? ValidationReport.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var detail = $"The {codec.Format.DisplayName} codec failed while validating ({ex.GetType().Name}: {ex.Message}). Nothing was written and the target is unchanged.";
            return await FailAsync(SaveFailureReason.CodecFailed, detail, path: null, cancellationToken).ConfigureAwait(false);
        }

        if (!report.HasErrors)
        {
            return null;
        }

        // Errors block on Save As to a new path and on Overwrite alike. That symmetry is a
        // decision, not an accident: an invalid document is invalid wherever it is written,
        // and a "new file" that no codec can read back is not a safer outcome (finding B11).
        var details = MostSevere(report);

        await _options.Interaction.ShowMessageAsync(
            new MessageRequest(
                "This save cannot be written",
                "Validation found errors that block writing. They block a copy to a new file exactly as they block an overwrite.",
                details),
            cancellationToken).ConfigureAwait(false);

        return SaveOutcome.Failure(SaveFailureReason.ValidationErrors, "Validation errors block the write.");
    }

    private async ValueTask<IReadOnlyList<UntrustedText>> CollectWarningsAsync(
        ISaveCodec<TDocument> codec,
        TDocument document,
        CancellationToken cancellationToken)
    {
        try
        {
            var report = await RunCodecAsync(() => codec.ValidateAsync(document, cancellationToken), cancellationToken).ConfigureAwait(false);
            return report is null ? [] : MostSevere(report);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static IReadOnlyList<UntrustedText> MostSevere(ValidationReport report) =>
    [
        .. report.Messages
            .OrderByDescending(message => message.Severity)
            .Take(8)
            .Select(message => message.Text),
    ];

    private async ValueTask<bool> ConfirmOverwriteAsync(
        string path,
        OpenSaveFile<TDocument>? open,
        IReadOnlyList<UntrustedText> details,
        CancellationToken cancellationToken)
    {
        var label = PathDisplayFormatter.Default.Format(path).Label;

        var message = $"Replace the file at {label} with the document that is open?";

        if (open is { UnknownData: UnknownDataVerification.Falsified })
        {
            message +=
                " This format claims to preserve data it does not understand, but the framework tested that claim against this file and it is false: " +
                "saving will lose bytes that are in the file today.";
        }
        else if (open is { UnknownData: UnknownDataVerification.NotClaimed or UnknownDataVerification.Skipped or UnknownDataVerification.Unavailable })
        {
            message += " " + open.UnknownDataDetail;
        }

        return await _options.Interaction.ConfirmAsync(
            new ConfirmationRequest
            {
                Title = "Overwrite this save file?",
                Message = message,
                AcceptLabel = "Overwrite save file",
                CancelLabel = "Keep the existing file",
                IsDestructive = true,
                Details = details,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> ConfirmResolutionAsync(
        string path,
        PathConfirmationKind kind,
        CancellationToken cancellationToken)
    {
        var label = PathDisplayFormatter.Default.Format(path).Label;

        var (message, accept) = kind switch
        {
            PathConfirmationKind.MultipleHardLinks => (
                $"{label} has more than one name on this volume. Changing its contents changes every one of them.",
                "Use this file anyway"),
            _ => (
                $"{label} is unusually large. Reading it may take a while and will use memory in proportion to its size.",
                "Read this file anyway"),
        };

        return await _options.Interaction.ConfirmAsync(
            new ConfirmationRequest
            {
                Title = "This file needs confirmation",
                Message = message,
                AcceptLabel = accept,
                IsDestructive = false,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SaveOutcome> FailAsync(
        SaveFailureReason reason,
        string detail,
        string? path,
        CancellationToken cancellationToken)
    {
        await ReportAsync("The save did not happen", detail, cancellationToken).ConfigureAwait(false);
        return SaveOutcome.Failure(reason, detail, path);
    }

    private async ValueTask ReportAsync(string title, string detail, CancellationToken cancellationToken)
    {
        try
        {
            await _options.Interaction
                .ShowMessageAsync(new MessageRequest(title, detail), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fail-loud means the caller always receives a definitive status. It does not
            // mean a failing dialog may turn a reported failure into an exception.
        }
    }

    /// <summary>
    /// Runs a codec call so that cancellation is authoritative at this boundary.
    /// </summary>
    /// <remarks>
    /// A <see cref="CancellationToken"/> handed to third-party code is cooperative only: a
    /// codec may ignore it, may not check it, or may be blocked somewhere that cannot check
    /// it. The workflow therefore stops waiting when the token fires, abandons the
    /// operation, and discards whatever the call eventually returns. The call keeps its
    /// thread until it finishes — that cannot be helped in .NET and is not pretended
    /// otherwise — but no write can originate from it, because every write is downstream of
    /// this await (finding B6).
    /// </remarks>
    private static async Task<T> RunCodecAsync<T>(Func<ValueTask<T>> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var work = Task.Run(async () => await call().ConfigureAwait(false), CancellationToken.None);

        try
        {
            return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Observe(work);
            throw;
        }
    }

    private static async Task RunCodecAsync(Func<ValueTask> call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var work = Task.Run(async () => await call().ConfigureAwait(false), CancellationToken.None);

        try
        {
            await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Observe(work);
            throw;
        }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static async ValueTask<byte[]> ComputeHashAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = SHA256.Create();

        var chunk = new byte[CopyChunkBytes];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            hash.TransformBlock(chunk, 0, read, null, 0);
        }

        hash.TransformFinalBlock([], 0, 0);
        return hash.Hash ?? [];
    }

    private void Track(string directory)
    {
        lock (_gate)
        {
            _writtenDirectories.Add(directory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
