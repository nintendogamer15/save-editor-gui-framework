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
/// <strong>Save As is the default write path, and every destructive write is backed
/// up.</strong> Overwriting an existing save is a separately named operation, which is
/// what makes the risky choice a deliberate one rather than a dialog default. It is not
/// the thing that makes it safe: a <c>Save As</c> whose target already exists is just as
/// destructive, so it takes the same all-or-nothing verified backup. Only a
/// <c>Save As</c> to a path that does not exist yet writes without one, because there is
/// nothing there to lose (finding F-2).
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
            TDocument? settledDocument = default;
            var alreadyDecoded = false;

            if (codec is null && detection.RequiresDecode)
            {
                // The header only established that the container is consistent. Decode each
                // candidate once and let it settle the question from the payload, which is the
                // only place the discriminator exists for an encrypted or compressed envelope.
                progress?.Report(new SaveProgress(SavePhase.Decoding, bytes.LongLength, bytes.LongLength));

                var settled = await SettleByDecodingAsync(detection.Candidates, bytes, cancellationToken).ConfigureAwait(false);

                if (settled.Count == 1)
                {
                    codec = settled[0].Codec;
                    settledDocument = settled[0].Document;
                    alreadyDecoded = true;
                }
                else if (settled.Count > 1)
                {
                    var chosenFormat = await _chooser
                        .ChooseAsync([.. settled.Select(s => s.Codec.Format)], Path.GetFileName(file.CanonicalPath), cancellationToken)
                        .ConfigureAwait(false);

                    if (chosenFormat is null)
                    {
                        return new OpenOutcome<TDocument>.Declined("No format was chosen, so the file was not opened.");
                    }

                    var match = settled.FirstOrDefault(s => string.Equals(s.Codec.Format.Id, chosenFormat.Id, StringComparison.Ordinal));
                    if (match.Codec is not null)
                    {
                        codec = match.Codec;
                        settledDocument = match.Document;
                        alreadyDecoded = true;
                    }
                }
            }
            else if (codec is null && detection.IsAmbiguous)
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
                var unsupported = detection.RequiresDecode
                    ? "No registered codec could identify this file after decoding it, so it was not opened."
                    : detection.Detail;

                await ReportAsync("Unsupported file", unsupported, cancellationToken).ConfigureAwait(false);
                return new OpenOutcome<TDocument>.Failed(SaveFailureReason.DetectionFailed, unsupported);
            }

            progress?.Report(new SaveProgress(SavePhase.Decoding, bytes.LongLength, bytes.LongLength));

            TDocument document;
            if (alreadyDecoded)
            {
                // Decoding a second time would run untrusted code over the same bytes for no
                // new information, and a codec whose decode is not deterministic would then
                // hold a document that differs from the one it was chosen on.
                document = settledDocument!;
            }
            else
            {
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
    /// <para>
    /// <strong>A target that already exists is backed up first, and the confirmation is
    /// unsuppressable.</strong> <see cref="Interaction.SaveFilePickResult.PickerConfirmedOverwrite"/>
    /// no longer suppresses anything. The operating system's dialog asks "replace this
    /// file?"; it cannot ask "replace this file, having taken a verified backup, with a
    /// codec whose preservation claim reads like this", which is the question the framework
    /// is in a position to ask and a picker is not. Revision 3 let the declaration suppress
    /// the prompt for the currently-open document, on the reasoning that the prompt was
    /// then genuinely duplicated; that reasoning held only while this path took no backup
    /// and made no preservation claim worth restating (finding F-2 supersedes finding A7).
    /// </para>
    /// <para>
    /// A <c>Save As</c> to a path that does not exist takes no backup and asks
    /// nothing: there is nothing at the destination to lose, and an entry that appears in
    /// the meantime is refused by the replace rather than clobbered.
    /// </para>
    /// </remarks>
    public ValueTask<SaveOutcome> SaveAsAsync(
        TDocument document,
        ISaveCodec<TDocument> codec,
        OpenSaveFile<TDocument>? current = null,
        IProgress<SaveProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        SaveAsCoreAsync(document, codec, destinationPath: null, current, progress, cancellationToken);

    /// <summary>
    /// Writes the document to a destination the caller already chose, without invoking a
    /// picker.
    /// </summary>
    /// <param name="document">The document to write.</param>
    /// <param name="codec">The codec that will serialize it.</param>
    /// <param name="destinationPath">Where to write. Resolved and guarded exactly as a picked path is.</param>
    /// <param name="current">The currently open file, when there is one.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the write at the workflow boundary.</param>
    /// <returns>The definitive outcome.</returns>
    /// <remarks>
    /// <para>
    /// Every guard the picker-driven overload applies applies here: path resolution, the
    /// write-protection check, identity re-assertion, the unsuppressable confirmation, the
    /// verified backup for an existing target, and the external-change check. The only
    /// difference is where the path came from.
    /// </para>
    /// <para>
    /// This exists so that an application whose save policy differs from the framework's does
    /// not have to reimplement <see cref="Shell.IDocumentSession"/> to express it, nor
    /// intercept picks through a substituted
    /// <see cref="Interaction.IUserInteraction"/> — which is policy enforcement smuggled
    /// through a dialog service (finding F-15). The caller is treated as not having confirmed
    /// an overwrite, because a caller-supplied path carries no evidence that anyone was asked.
    /// </para>
    /// </remarks>
    public ValueTask<SaveOutcome> SaveAsAsync(
        TDocument document,
        ISaveCodec<TDocument> codec,
        string destinationPath,
        OpenSaveFile<TDocument>? current = null,
        IProgress<SaveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);
        return SaveAsCoreAsync(document, codec, destinationPath, current, progress, cancellationToken);
    }

    private async ValueTask<SaveOutcome> SaveAsCoreAsync(
        TDocument document,
        ISaveCodec<TDocument> codec,
        string? destinationPath,
        OpenSaveFile<TDocument>? current,
        IProgress<SaveProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codec);

        // Held for the whole operation, picker included: two concurrent saves of one document
        // is precisely what this prevents, and a modal picker means the user is already
        // mid-operation.
        if (current is not null && !current.TryEnter())
        {
            return await FailAsync(SaveFailureReason.Busy, "Another operation is already writing this document. The two would race the same file handle, so this one was refused rather than interleaved with it.", current.Path, cancellationToken).ConfigureAwait(false);
        }

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

            SaveFilePickResult pick;
            if (destinationPath is null)
            {
                var picked = await _options.Interaction.PickSaveFileAsync(
                    new FilePickerRequest(
                        "Save a copy",
                        _options.Registry.Formats,
                        current is null ? null : Path.GetFileName(current.Path),
                        current is null ? null : Path.GetDirectoryName(current.Path)),
                    cancellationToken).ConfigureAwait(false);

                if (picked is null)
                {
                    return SaveOutcome.Declined("No destination was chosen.");
                }

                pick = picked;
            }
            else
            {
                // A caller-supplied path carries no evidence that anyone was asked about an
                // overwrite, so it is treated as unconfirmed. Since the declaration no longer
                // suppresses anything, this is belt and braces rather than load-bearing.
                pick = new SaveFilePickResult(destinationPath, PickerConfirmedOverwrite: false);
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

            var resolvedPath = destinationExists && destination is not null ? destination.CanonicalPath : pick.Path;

            var directory = Path.GetDirectoryName(resolvedPath);
            if (string.IsNullOrEmpty(directory))
            {
                return await FailAsync(SaveFailureReason.PathRefused, "The destination has no containing directory.", pick.Path, cancellationToken).ConfigureAwait(false);
            }

            var policy = await EvaluatePolicyAsync(
                new PlannedWrite
                {
                    Kind = PlannedWriteKind.SaveAs,
                    DestinationPath = resolvedPath,
                    DestinationExists = destinationExists,
                    IsCurrentDocument = isCurrentDocument,
                    BackupWillBeWritten = destinationExists,
                    UnknownData = current?.UnknownData ?? UnknownDataVerification.NotClaimed,
                },
                cancellationToken).ConfigureAwait(false);

            if (policy is not null)
            {
                return policy;
            }

            string? backupPath = null;

            if (destinationExists && destination is not null)
            {
                var protection = WriteProtection.Describe(destination.Stream);
                if (protection is not null)
                {
                    return await FailAsync(SaveFailureReason.WriteProtected, protection, destination.CanonicalPath, cancellationToken).ConfigureAwait(false);
                }

                if (!destination.ReassertIdentity())
                {
                    return await FailAsync(
                        SaveFailureReason.IdentityChanged,
                        "The handle held for the destination no longer refers to the file that was resolved. The write was abandoned.",
                        resolvedPath,
                        cancellationToken).ConfigureAwait(false);
                }

                // Unsuppressable. What the picker confirmed is not what this asks; see the
                // remarks on this method.
                var accepted = await ConfirmOverwriteAsync(
                    destination.CanonicalPath,
                    isCurrentDocument ? current : null,
                    details: [],
                    backupWillBeWritten: true,
                    cancellationToken).ConfigureAwait(false);

                if (!accepted)
                {
                    return SaveOutcome.Declined("The overwrite was declined.");
                }

                // A destination the workflow did not open has no baseline yet. Capturing one
                // here has a second effect worth naming: it is what lets the pre-replace
                // external-change check below run at all on this path, which it previously
                // skipped for every target other than the open document.
                destinationBaseline ??= await CaptureDestinationBaselineAsync(destination, cancellationToken).ConfigureAwait(false);

                var backup = await CreateVerifiedBackupAsync(destination, destinationBaseline, directory, progress, cancellationToken).ConfigureAwait(false);
                if (backup.Path is null)
                {
                    return await FailAsync(SaveFailureReason.BackupFailed, backup.Detail, resolvedPath, cancellationToken).ConfigureAwait(false);
                }

                backupPath = backup.Path;
            }

            var write = await WriteAsync(
                document,
                codec,
                directory,
                resolvedPath,
                destination,
                destinationBaseline,
                destinationExists,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (write.Replaced && isCurrentDocument && current is not null && write.Baseline is not null)
            {
                await RebindAsync(current, write.Baseline, CancellationToken.None).ConfigureAwait(false);
            }

            if (backupPath is null)
            {
                return write.Outcome;
            }

            if (write.Outcome.Status != SaveStatus.Succeeded)
            {
                // The backup is a good copy of a file that still exists, so it is reported
                // rather than discarded.
                return write.Outcome with { BackupPath = backupPath };
            }

            ApplyBackupRetention(backupPath, Path.GetFileName(resolvedPath));

            var success = SaveOutcome.Success(resolvedPath, backupPath) with
            {
                RoundTrip = write.Outcome.RoundTrip,
                RoundTripDetail = write.Outcome.RoundTripDetail,
            };

            return string.IsNullOrEmpty(write.Detail) ? success : success with { Message = success.Message + " " + write.Detail };
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

            current?.Exit();
        }
    }

    /// <summary>
    /// Settles a payload-discriminated detection by decoding each candidate and asking it.
    /// </summary>
    /// <param name="candidates">Codecs whose detectors answered <c>RequiresDecode</c>.</param>
    /// <param name="bytes">The bytes read through the retained handle.</param>
    /// <param name="cancellationToken">Cancels between candidates and inside each call.</param>
    /// <returns>
    /// The candidates that confirmed, each with the document it decoded, so the caller does
    /// not decode a second time. Confident confirmations if there are any, otherwise
    /// possible ones.
    /// </returns>
    /// <remarks>
    /// A candidate whose decode throws has declined the file — a codec that cannot read
    /// another schema's payload needs no <c>ConfirmDecoded</c> override to say so. Every call
    /// goes through the same containment boundary as any other codec call, so a throwing or
    /// cancelled candidate removes itself rather than aborting detection for the rest
    /// (finding F-8).
    /// </remarks>
    private async ValueTask<List<(ISaveCodec<TDocument> Codec, TDocument Document)>> SettleByDecodingAsync(
        IReadOnlyList<ISaveCodec<TDocument>> candidates,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var confident = new List<(ISaveCodec<TDocument> Codec, TDocument Document)>();
        var possible = new List<(ISaveCodec<TDocument> Codec, TDocument Document)>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TDocument document;
            try
            {
                document = await RunCodecAsync(
                    () => candidate.DecodeAsync(new MemoryStream(bytes, writable: false), cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            DetectionVerdict verdict;
            try
            {
                verdict = await RunCodecAsync(
                    () => new ValueTask<DetectionVerdict>(candidate.ConfirmDecoded(document)),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            switch (verdict)
            {
                case DetectionVerdict.Confident:
                    confident.Add((candidate, document));
                    break;
                case DetectionVerdict.Possible:
                    possible.Add((candidate, document));
                    break;
                default:
                    break;
            }
        }

        return confident.Count > 0 ? confident : possible;
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

        if (!open.TryEnter())
        {
            return await FailAsync(SaveFailureReason.Busy, "Another operation is already writing this document. The two would race the same file handle, so this one was refused rather than interleaved with it.", open.Path, cancellationToken).ConfigureAwait(false);
        }

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
            var policy = await EvaluatePolicyAsync(
                new PlannedWrite
                {
                    Kind = PlannedWriteKind.Overwrite,
                    DestinationPath = open.Path,
                    DestinationExists = true,
                    IsCurrentDocument = true,
                    BackupWillBeWritten = true,
                    UnknownData = open.UnknownData,
                },
                cancellationToken).ConfigureAwait(false);

            if (policy is not null)
            {
                return policy;
            }

            var warnings = await CollectWarningsAsync(open.Codec, document, cancellationToken).ConfigureAwait(false);

            var accepted = await ConfirmOverwriteAsync(open.Path, open, warnings, backupWillBeWritten: true, cancellationToken).ConfigureAwait(false);
            if (!accepted)
            {
                return SaveOutcome.Declined("The overwrite was declined.");
            }

            var before = await _options.ChangeGuard.VerifyAsync(open.File, open.Baseline, cancellationToken).ConfigureAwait(false);
            if (before.Verdict != ExternalChangeVerdict.Unchanged)
            {
                return await FailAsync(SaveFailureReason.ExternalChange, before.Detail, open.Path, cancellationToken).ConfigureAwait(false);
            }

            var backup = await CreateVerifiedBackupAsync(open.File, open.Baseline, directory, progress, cancellationToken).ConfigureAwait(false);
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

            // Driven by whether the replacement happened, never by what the operation
            // reported, and with CancellationToken.None because this is the bookkeeping that
            // keeps the handle honest: a successful replace unlinks the inode this handle was
            // opened on, and leaving it bound there would let a later overwrite sail through
            // ReassertIdentity and the change guard against a ghost (finding F-3).
            if (write.Replaced)
            {
                await RebindAsync(open, write.Baseline!, CancellationToken.None).ConfigureAwait(false);
            }

            if (write.Outcome.Status != SaveStatus.Succeeded)
            {
                return write.Outcome with { BackupPath = backup.Path };
            }

            ApplyBackupRetention(backup.Path, Path.GetFileName(open.Path));

            var success = SaveOutcome.Success(open.Path, backup.Path) with
            {
                RoundTrip = write.Outcome.RoundTrip,
                RoundTripDetail = write.Outcome.RoundTripDetail,
            };

            return string.IsNullOrEmpty(write.Detail) ? success : success with { Message = success.Message + " " + write.Detail };
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
        finally
        {
            open.Exit();
        }
    }

    /// <summary>
    /// Puts a backup's bytes back at the open document's path, backing up the current state
    /// first.
    /// </summary>
    /// <param name="backupPath">The backup to restore. Any readable file, not only one the framework named.</param>
    /// <param name="open">The open document, whose path is the destination.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the restore at the workflow boundary.</param>
    /// <returns>The definitive outcome, and the restored document when it succeeded.</returns>
    /// <remarks>
    /// <para>
    /// The framework created backups, verified them, and reported their paths, then left
    /// recovery entirely to the adopter — so every adopter was going to write this routine,
    /// and the framework already held everything needed to write it correctly: the resolver,
    /// the change guard, the permission policy, and the atomic replace (finding F-10).
    /// </para>
    /// <para>
    /// <strong>A restore is a destructive overwrite and is treated as one.</strong> The
    /// backup is resolved through the same hardened path primitive as any other read, its
    /// bytes are decoded with the open document's codec before anything is written — a
    /// restore that lands bytes the format cannot read is worse than no restore — the current
    /// state is itself backed up so the restore can be undone, the external-change guard runs,
    /// and the replacement is atomic with the landed bytes verified.
    /// </para>
    /// <para>
    /// On success the document in memory is the one decoded from the backup and is returned;
    /// the caller adopts it. Nothing else can, because the framework does not own the
    /// application's document reference.
    /// </para>
    /// </remarks>
    public async ValueTask<RestoreResult<TDocument>> RestoreFromBackupAsync(
        string backupPath,
        OpenSaveFile<TDocument> open,
        IProgress<SaveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(backupPath);
        ArgumentNullException.ThrowIfNull(open);

        if (!open.TryEnter())
        {
            return new RestoreResult<TDocument>(
                await FailAsync(SaveFailureReason.Busy, "Another operation is already writing this document. The two would race the same file handle, so this one was refused rather than interleaved with it.", open.Path, cancellationToken).ConfigureAwait(false),
                default);
        }

        ResolvedFile? source = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (open.IsStale)
            {
                return Refuse(await FailAsync(
                    SaveFailureReason.IdentityChanged,
                    "This document is no longer bound to a handle that can prove what it points at. Reopen it before restoring over it.",
                    open.Path,
                    cancellationToken).ConfigureAwait(false));
            }

            var directory = Path.GetDirectoryName(open.Path);
            if (string.IsNullOrEmpty(directory))
            {
                return Refuse(await FailAsync(SaveFailureReason.PathRefused, "The target has no containing directory.", open.Path, cancellationToken).ConfigureAwait(false));
            }

            if (!open.File.ReassertIdentity())
            {
                return Refuse(await FailAsync(
                    SaveFailureReason.IdentityChanged,
                    "The retained handle no longer refers to the file that was opened. The restore was abandoned.",
                    open.Path,
                    cancellationToken).ConfigureAwait(false));
            }

            var protection = WriteProtection.Describe(open.File.Stream);
            if (protection is not null)
            {
                return Refuse(await FailAsync(SaveFailureReason.WriteProtected, protection, open.Path, cancellationToken).ConfigureAwait(false));
            }

            // The backup goes through the same resolver as any other read: link following
            // disabled, every ancestor checked, identity recorded.
            var resolution = await _options.PathResolver
                .ResolveAsync(backupPath, _options.ReadResolution, cancellationToken)
                .ConfigureAwait(false);

            source = resolution switch
            {
                PathResolution.Resolved resolved => resolved.File,
                PathResolution.NeedsConfirmation needs => needs.File,
                _ => null,
            };

            if (source is null)
            {
                var detail = resolution is PathResolution.Refused refused
                    ? $"The backup could not be read: {refused.Detail}"
                    : "The backup could not be read.";

                return Refuse(await FailAsync(SaveFailureReason.PathRefused, detail, backupPath, cancellationToken).ConfigureAwait(false));
            }

            if (source.Identity == open.File.Identity)
            {
                return Refuse(await FailAsync(
                    SaveFailureReason.PathRefused,
                    "The chosen backup is the file being restored over. There is nothing to restore.",
                    backupPath,
                    cancellationToken).ConfigureAwait(false));
            }

            progress?.Report(new SaveProgress(SavePhase.Reading));
            var (_, bytes) = await _options.ChangeGuard.CaptureAsync(source, cancellationToken).ConfigureAwait(false);

            // Decoded before anything is written. A restore that lands bytes this codec
            // cannot read leaves the user with neither their edit nor a readable save.
            progress?.Report(new SaveProgress(SavePhase.Decoding, bytes.LongLength, bytes.LongLength));

            TDocument restored;
            try
            {
                restored = await RunCodecAsync(
                    () => open.Codec.DecodeAsync(new MemoryStream(bytes, writable: false), cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var detail = $"The {open.Codec.Format.DisplayName} codec could not read the chosen backup ({ex.GetType().Name}: {ex.Message}). " +
                             "The restore was abandoned rather than writing bytes this editor cannot open.";
                return Refuse(await FailAsync(SaveFailureReason.CodecFailed, detail, backupPath, cancellationToken).ConfigureAwait(false));
            }

            var policy = await EvaluatePolicyAsync(
                new PlannedWrite
                {
                    Kind = PlannedWriteKind.Restore,
                    DestinationPath = open.Path,
                    DestinationExists = true,
                    IsCurrentDocument = true,
                    BackupWillBeWritten = true,
                    UnknownData = open.UnknownData,
                },
                cancellationToken).ConfigureAwait(false);

            if (policy is not null)
            {
                return Refuse(policy);
            }

            if (!await ConfirmRestoreAsync(open.Path, backupPath, cancellationToken).ConfigureAwait(false))
            {
                return Refuse(SaveOutcome.Declined("The restore was declined."));
            }

            var before = await _options.ChangeGuard.VerifyAsync(open.File, open.Baseline, cancellationToken).ConfigureAwait(false);
            if (before.Verdict != ExternalChangeVerdict.Unchanged)
            {
                return Refuse(await FailAsync(SaveFailureReason.ExternalChange, before.Detail, open.Path, cancellationToken).ConfigureAwait(false));
            }

            // The state being replaced is itself backed up, so a restore is recoverable.
            var backup = await CreateVerifiedBackupAsync(open.File, open.Baseline, directory, progress, cancellationToken).ConfigureAwait(false);
            if (backup.Path is null)
            {
                return Refuse(await FailAsync(SaveFailureReason.BackupFailed, backup.Detail, open.Path, cancellationToken).ConfigureAwait(false));
            }

            var write = await CommitPayloadAsync(
                bytes,
                (RoundTripVerification.Verified, $"The backup was decoded by the {open.Codec.Format.DisplayName} codec before it was written.", null),
                directory,
                open.Path,
                open.File,
                open.Baseline,
                destinationExists: true,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (write.Replaced)
            {
                await RebindAsync(open, write.Baseline!, CancellationToken.None).ConfigureAwait(false);
            }

            if (write.Outcome.Status != SaveStatus.Succeeded)
            {
                return new RestoreResult<TDocument>(write.Outcome with { BackupPath = backup.Path }, default);
            }

            ApplyBackupRetention(backup.Path, Path.GetFileName(open.Path));

            var success = SaveOutcome.Success(open.Path, backup.Path) with
            {
                Message = "Restored. The state that was replaced was backed up and verified first.",
                RoundTrip = write.Outcome.RoundTrip,
                RoundTripDetail = write.Outcome.RoundTripDetail,
            };

            return new RestoreResult<TDocument>(
                string.IsNullOrEmpty(write.Detail) ? success : success with { Message = success.Message + " " + write.Detail },
                restored);
        }
        catch (OperationCanceledException)
        {
            return Refuse(SaveOutcome.Cancelled());
        }
        catch (Exception ex)
        {
            return Refuse(await FailAsync(
                SaveFailureReason.Unexpected,
                $"The restore failed unexpectedly ({ex.GetType().Name}: {ex.Message}).",
                open.Path,
                CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            source?.Dispose();
            open.Exit();
        }

        static RestoreResult<TDocument> Refuse(SaveOutcome outcome) => new(outcome, default);
    }

    /// <summary>Consults the application's write policy, if it supplied one.</summary>
    /// <returns>
    /// <see langword="null"/> to continue, or the outcome the operation should return.
    /// </returns>
    /// <remarks>
    /// A policy that throws refuses the write. Treating a broken policy as permission would
    /// make the strictest thing in the composition root the easiest thing to defeat.
    /// </remarks>
    private async ValueTask<SaveOutcome?> EvaluatePolicyAsync(PlannedWrite plan, CancellationToken cancellationToken)
    {
        if (_options.WritePolicy is not { } policy)
        {
            return null;
        }

        WriteDecision decision;
        try
        {
            decision = await policy.EvaluateAsync(plan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return await FailAsync(
                SaveFailureReason.Unexpected,
                $"The application's write policy failed while deciding whether this write was permitted ({ex.GetType().Name}: {ex.Message}). The write was abandoned.",
                plan.DestinationPath,
                cancellationToken).ConfigureAwait(false);
        }

        if (decision is null)
        {
            return await FailAsync(
                SaveFailureReason.Unexpected,
                "The application's write policy returned no decision. The write was abandoned.",
                plan.DestinationPath,
                cancellationToken).ConfigureAwait(false);
        }

        return decision.IsAllowed
            ? null
            : SaveOutcome.Declined(decision.Message ?? "This editor's own policy does not permit that write.");
    }

    private async ValueTask<bool> ConfirmRestoreAsync(string targetPath, string backupPath, CancellationToken cancellationToken)
    {
        var target = PathDisplayFormatter.Default.Format(targetPath).Label;
        var backup = PathDisplayFormatter.Default.Format(backupPath).Label;

        return await _options.Interaction.ConfirmAsync(
            new ConfirmationRequest
            {
                Title = "Restore this backup?",
                Message =
                    $"Replace the file at {target} with the backup at {backup}? " +
                    "Everything saved since that backup was taken will be gone from the file. " +
                    "The state being replaced is backed up and verified first, so this can itself be undone.",
                AcceptLabel = "Restore the backup",
                CancelLabel = "Keep the current file",
                IsDestructive = true,
            },
            cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The result of one write attempt, including whether the replacement actually happened.
    /// </summary>
    /// <param name="Outcome">The definitive outcome.</param>
    /// <param name="Baseline">What the bytes are now, when the replacement happened.</param>
    /// <param name="Detail">A note to append to the caller's message, or empty.</param>
    /// <param name="Replaced">
    /// Whether the destination was superseded. This is the flag callers must drive rebinding
    /// from, never <see cref="Outcome"/>: a replacement that happened has unlinked the inode
    /// the caller's handle was opened on, whatever the operation went on to report.
    /// </param>
    private readonly record struct WriteAttempt(
        SaveOutcome Outcome,
        ContentBaseline? Baseline,
        string Detail,
        bool Replaced)
    {
        internal static WriteAttempt Abandoned(SaveOutcome outcome) =>
            new(outcome, null, string.Empty, Replaced: false);
    }

    private async ValueTask<WriteAttempt> WriteAsync(
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
            return WriteAttempt.Abandoned(blocked);
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
            return WriteAttempt.Abandoned(await FailAsync(SaveFailureReason.CodecFailed, detail, destinationPath, cancellationToken).ConfigureAwait(false));
        }

        var roundTrip = await VerifyRoundTripAsync(codec, document, payload, progress, cancellationToken).ConfigureAwait(false);
        if (roundTrip.Mismatch is not null)
        {
            var failure = await FailAsync(SaveFailureReason.RoundTripMismatch, roundTrip.Mismatch, destinationPath, cancellationToken).ConfigureAwait(false);
            return WriteAttempt.Abandoned(failure with
            {
                RoundTrip = RoundTripVerification.Mismatched,
                RoundTripDetail = roundTrip.Detail,
            });
        }

        return await CommitPayloadAsync(
            payload,
            roundTrip,
            directory,
            destinationPath,
            destination,
            baseline,
            destinationExists,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts a finished byte payload at the destination: exclusive temp, permissions,
    /// external-change check, flush, read-back verification, atomic replace.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="WriteAsync"/> so that restoring a backup goes through exactly
    /// the same machinery as writing a serialized document (finding F-10). Everything above
    /// this point is about producing bytes and is codec business; everything below is about
    /// landing them and is identical whatever produced them.
    /// </remarks>
    private async ValueTask<WriteAttempt> CommitPayloadAsync(
        byte[] payload,
        (RoundTripVerification Verdict, string Detail, string? Mismatch) roundTrip,
        string directory,
        string destinationPath,
        ResolvedFile? destination,
        ContentBaseline? baseline,
        bool destinationExists,
        IProgress<SaveProgress>? progress,
        CancellationToken cancellationToken)
    {
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

            return WriteAttempt.Abandoned(await FailAsync(SaveFailureReason.TempCreationFailed, detail, destinationPath, cancellationToken).ConfigureAwait(false));
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
                    return WriteAttempt.Abandoned(await FailAsync(SaveFailureReason.PermissionCopyFailed, copy.Detail, destinationPath, cancellationToken).ConfigureAwait(false));
                }

                var candidate = _options.Permissions.Capture(temporary.Stream);
                if (_options.Permissions.IsBroaderThan(candidate, original, out var widening))
                {
                    return WriteAttempt.Abandoned(await FailAsync(SaveFailureReason.PermissionWidening, widening, destinationPath, cancellationToken).ConfigureAwait(false));
                }
            }

            if (destination is not null && baseline is not null)
            {
                progress?.Report(new SaveProgress(SavePhase.CheckingForExternalChange));

                var check = await _options.ChangeGuard.VerifyAsync(destination, baseline, cancellationToken).ConfigureAwait(false);
                if (check.Verdict != ExternalChangeVerdict.Unchanged)
                {
                    return WriteAttempt.Abandoned(await FailAsync(SaveFailureReason.ExternalChange, check.Detail, destinationPath, cancellationToken).ConfigureAwait(false));
                }
            }

            // Authoritative cancellation: the last gate before anything irreversible.
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new SaveProgress(SavePhase.VerifyingTemp, payload.LongLength, payload.LongLength));

            await _options.Durability.FlushFileAsync(temporary.Stream, cancellationToken).ConfigureAwait(false);

            var landed = await VerifyTemporaryBytesAsync(temporary.Stream, payload, cancellationToken).ConfigureAwait(false);
            if (landed is not null)
            {
                return WriteAttempt.Abandoned(await FailAsync(SaveFailureReason.TempVerificationFailed, landed, destinationPath, cancellationToken).ConfigureAwait(false));
            }

            progress?.Report(new SaveProgress(SavePhase.Replacing));

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

                return WriteAttempt.Abandoned(await FailAsync(reason, replace.Detail, destinationPath, cancellationToken).ConfigureAwait(false));
            }

            replaced = true;

            // Past the point of no return. Everything below is bookkeeping, and none of it
            // is allowed to turn a completed replacement into a report that nothing was
            // written. Previously three awaits sat here inside the enclosing try -- a
            // directory flush over the caller's token, a progress report, and the baseline --
            // so a cancellation in this window, or a throwing IProgress sink, surfaced as
            // "Cancelled. Nothing was written" or "The target is unchanged" about a file that
            // had already been replaced (finding F-3).
            var newBaseline = new ContentBaseline(SHA256.HashData(payload), payload.LongLength, null);
            var detail = string.Empty;

            try
            {
                // CancellationToken.None deliberately. FlushDirectoryAsync is a Task.Run over
                // the token it is given, and there is nothing left to cancel usefully: the
                // rename has landed and the only question is whether its directory entry is
                // durable, which cancelling does not improve.
                var flush = await _options.Durability.FlushDirectoryAsync(directory, CancellationToken.None).ConfigureAwait(false);
                if (flush.Status == DirectoryFlushStatus.Failed)
                {
                    detail = flush.Detail;
                }
            }
            catch (Exception ex)
            {
                detail = $"The containing directory could not be flushed after the replacement ({ex.GetType().Name}: {ex.Message}). The new bytes are on disk.";
            }

            try
            {
                progress?.Report(new SaveProgress(SavePhase.Completed, payload.LongLength, payload.LongLength));
            }
            catch (Exception)
            {
                // Progress is observational; nothing about correctness depends on a consumer
                // subscribing, and a sink that throws does not get to change the outcome of a
                // write that already landed.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                detail = Join(detail, "The operation was cancelled after the replacement had already completed, so the new bytes are on disk.");
            }

            // A skipped round trip is said out loud. Left off the message, "not checked" and
            // "checked and fine" read identically (finding F-6).
            if (roundTrip.Verdict == RoundTripVerification.Skipped)
            {
                detail = Join(detail, roundTrip.Detail);
            }

            return new WriteAttempt(
                SaveOutcome.Success(destinationPath) with
                {
                    RoundTrip = roundTrip.Verdict,
                    RoundTripDetail = roundTrip.Detail,
                },
                newBaseline,
                detail,
                Replaced: true);
        }
        catch (OperationCanceledException)
        {
            // Defensive. Nothing between the replace and the return above awaits the caller's
            // token any more, so this should be unreachable once replaced is true. If it ever
            // becomes reachable again, the contract still holds rather than silently
            // inverting.
            if (replaced)
            {
                return Landed(destinationPath, payload, "The operation was cancelled after the replacement had already completed, so the new bytes are on disk.", roundTrip);
            }

            throw;
        }
        catch (Exception ex)
        {
            if (replaced)
            {
                return Landed(
                    destinationPath,
                    payload,
                    $"The replacement completed, but bookkeeping after it failed ({ex.GetType().Name}: {ex.Message}).",
                    roundTrip);
            }

            return WriteAttempt.Abandoned(await FailAsync(
                SaveFailureReason.Unexpected,
                $"The write failed ({ex.GetType().Name}: {ex.Message}). The target is unchanged.",
                destinationPath,
                cancellationToken).ConfigureAwait(false));
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

    /// <summary>Copies a file the workflow holds open into a verified backup beside it.</summary>
    /// <param name="original">
    /// The retained handle for the file about to be replaced. Taken as a
    /// <see cref="ResolvedFile"/> rather than an <see cref="OpenSaveFile{TDocument}"/> so
    /// that both destructive paths can use it: <c>Save As</c> onto an existing file is
    /// replacing something the workflow never decoded, and it needs the same backup.
    /// </param>
    /// <param name="baseline">
    /// What the bytes were when they were read. The written backup is hashed against this,
    /// so "a backup exists" and "the backup is the file that was there" are one statement.
    /// </param>
    /// <param name="directory">Where the backup is written, before any fallback prompt.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the backup.</param>
    /// <returns>The backup path, or <see langword="null"/> and why not.</returns>
    private async ValueTask<(string? Path, string Detail)> CreateVerifiedBackupAsync(
        ResolvedFile original,
        ContentBaseline baseline,
        string directory,
        IProgress<SaveProgress>? progress,
        CancellationToken cancellationToken)
    {
        var originalName = Path.GetFileName(original.CanonicalPath);

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

            var source = original.Stream;
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

            if (!CryptographicOperations.FixedTimeEquals(hash, baseline.Hash.Span))
            {
                detail =
                    "The backup was written but its hash does not match the bytes that were read from the original. " +
                    "The overwrite was abandoned with the original untouched and the backup removed.";
            }
            else
            {
                // Backups inherit the original's mode rather than the directory default.
                _ = _options.Permissions.CopyOnto(original.Stream, backup.Stream, backupPath, backup.Identity);
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

        // Retention deliberately does not run here. Applied between the backup's
        // verification and its use, a misconfigured cap could delete the very file the
        // overwrite is about to depend on; it now runs only once the write has succeeded
        // (finding F-4).
        return (backupPath, "The backup was written and verified against the bytes that were read.");
    }

    /// <summary>Trims older backups of one original, after a write has already succeeded.</summary>
    /// <remarks>
    /// Housekeeping, and never allowed to affect the outcome of a save that has completed.
    /// Running only after success is also what stops a cap of zero — now rejected at
    /// construction — from ever having been able to remove the backup being relied on.
    /// </remarks>
    private void ApplyBackupRetention(string backupPath, string originalFileName)
    {
        var directory = Path.GetDirectoryName(backupPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        // The backup just written is named explicitly: within one second, name ordering
        // cannot tell it apart from an older sibling, and this is the one file the caller is
        // about to report to the user.
        _ = BackupRetention.Apply(directory, originalFileName, _options.BackupRetention, protect: backupPath);
    }

    /// <summary>Captures a change-detection baseline for a file the workflow did not decode.</summary>
    /// <remarks>
    /// Streams the retained handle instead of going through
    /// <see cref="IExternalChangeGuard.CaptureAsync"/>, whose byte array exists so that
    /// detection and decode can run over the same read. A <c>Save As</c> onto an existing
    /// file needs the hash and the length and nothing else, and materialising a
    /// half-gigabyte destination to obtain them would be a pointless allocation.
    /// </remarks>
    private static async ValueTask<ContentBaseline> CaptureDestinationBaselineAsync(
        ResolvedFile destination,
        CancellationToken cancellationToken)
    {
        var stream = destination.Stream;
        stream.Seek(0, SeekOrigin.Begin);

        var length = stream.Length;
        var hash = await ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false);

        DateTime? lastWrite;
        try
        {
            lastWrite = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        }
        catch (Exception)
        {
            // Metadata is an optimization for the change guard, never an authority. Its
            // absence costs a fast-path negative, not a guarantee.
            lastWrite = null;
        }

        return new ContentBaseline(hash, length, lastWrite);
    }

    /// <summary>Reads the temporary file back and compares it against the bytes written to it.</summary>
    /// <param name="temporary">The flushed temporary file, still open.</param>
    /// <param name="payload">The bytes the codec produced.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns><see langword="null"/> when what landed is the payload, or why not.</returns>
    /// <remarks>
    /// The temporary file used to be written, fsynced and renamed without ever being read
    /// back, and the new change-detection baseline was hashed from the payload — the
    /// <em>intended</em> bytes rather than the landed ones. The backup path a few lines away
    /// already re-read from disk and compared, so this was the one place the framework was
    /// weaker than the audited pipeline it replaces (finding F-5).
    /// <para>
    /// Ordinary failures — ENOSPC, EIO — do throw from <c>WriteAsync</c> or the flush, so this
    /// is not a common-case gap. What it closes is silent media corruption and storage that
    /// reports a successful write it did not perform, and, just as importantly, it stops a
    /// wrong baseline being recorded and then used by every later external-change check.
    /// </para>
    /// <para>
    /// One seek and one pass over a file that was written moments ago and is still in page
    /// cache.
    /// </para>
    /// </remarks>
    private static async ValueTask<string?> VerifyTemporaryBytesAsync(
        FileStream temporary,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var length = temporary.Length;
        if (length != payload.LongLength)
        {
            return $"The temporary file holds {length} bytes where {payload.LongLength} were written to it, so the storage did not record what the codec produced. " +
                   "The save was abandoned with the target unchanged.";
        }

        temporary.Seek(0, SeekOrigin.Begin);
        var landed = await ComputeHashAsync(temporary, cancellationToken).ConfigureAwait(false);

        return CryptographicOperations.FixedTimeEquals(landed, SHA256.HashData(payload))
            ? null
            : "The bytes read back from the temporary file are not the bytes written to it, so the storage did not durably record what the codec produced. " +
              "The save was abandoned with the target unchanged.";
    }

    /// <summary>Reports a replacement that happened, whatever went wrong after it.</summary>
    private static WriteAttempt Landed(
        string destinationPath,
        byte[] payload,
        string detail,
        (RoundTripVerification Verdict, string Detail, string? Mismatch) roundTrip) =>
        new(
            SaveOutcome.Success(destinationPath) with
            {
                RoundTrip = roundTrip.Verdict,
                RoundTripDetail = roundTrip.Detail,
            },
            new ContentBaseline(SHA256.HashData(payload), payload.LongLength, null),
            detail,
            Replaced: true);

    private static string Join(string first, string second) =>
        first.Length == 0 ? second : first + " " + second;

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

        // Byte equality is checked by the framework, and only divergence reaches the codec.
        // That order is the whole safeguard: a codec whose RoundTripEquivalent returns true
        // unconditionally can reach the weaker VerifiedEquivalent verdict but can never
        // manufacture the byte-identical one, and an honest codec's comparison is not
        // invoked at all in the common case.
        bool equivalent;
        try
        {
            equivalent = await RunCodecAsync(
                () => new ValueTask<bool>(codec.RoundTripEquivalent(source, reserialized)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (UnknownDataVerification.Unavailable,
                $"The preservation claim could not be tested: the codec threw while comparing the round trip ({ex.GetType().Name}). Treat it as untested.");
        }

        var divergence = DescribeDivergence(source, reserialized);

        if (equivalent)
        {
            return (UnknownDataVerification.VerifiedEquivalent,
                $"Re-serializing the untouched document did not reproduce the file byte for byte ({divergence}), but {codec.Format.DisplayName} " +
                "reports the two as the same document. The claim holds for this file under the codec's own comparison rather than under byte " +
                "equality, so it rests on the codec being right about its own format.");
        }

        return (UnknownDataVerification.Falsified,
            $"{codec.Format.DisplayName} declares that it preserves data it does not understand, but re-serializing this file without changing " +
            $"anything did not reproduce it ({divergence}), and the codec does not report the two as equivalent. The declaration is false for " +
            "this file, so saving will lose data that is in it today.");
    }

    /// <summary>Says what differs between two serializations, not merely how big they are.</summary>
    /// <remarks>
    /// A length-only comparison prints two identical numbers whenever the divergence happens
    /// to preserve size, which reads as a non-difference — inside the destructive confirmation
    /// the user is about to accept, which is the worst possible place for a message that looks
    /// like it is saying nothing is wrong.
    /// </remarks>
    private static string DescribeDivergence(ReadOnlySpan<byte> source, ReadOnlySpan<byte> reserialized)
    {
        var shared = Math.Min(source.Length, reserialized.Length);

        var offset = -1;
        for (var i = 0; i < shared; i++)
        {
            if (source[i] != reserialized[i])
            {
                offset = i;
                break;
            }
        }

        if (source.Length != reserialized.Length)
        {
            var lengths = $"it produced {reserialized.Length} bytes where the file has {source.Length}";
            return offset >= 0
                ? $"{lengths}, and they first differ at byte {offset}"
                : $"{lengths}, and the shorter is a prefix of the longer";
        }

        // Equal lengths: the byte count carries no information at all, so the offset and the
        // hashes are the only things that do.
        return offset >= 0
            ? $"both are {source.Length} bytes and they first differ at byte {offset}, SHA-256 {ShortHash(source)} against {ShortHash(reserialized)}"
            : $"both are {source.Length} bytes";
    }

    private static string ShortHash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes))[..12];

    /// <summary>Decodes the bytes about to be written and compares them to the document.</summary>
    /// <returns>
    /// The verdict, a framework-authored explanation of it, and — only when the comparison
    /// ran and failed — the message the write should fail with.
    /// </returns>
    /// <remarks>
    /// This used to return <see langword="null"/> for both "passed" and "not run", so above
    /// <see cref="SafeFileWorkflowOptions{TDocument}.RoundTripVerificationMaxBytes"/> the
    /// primary integrity guard stopped running and the result was a plain "Saved."
    /// (finding F-6).
    /// </remarks>
    private async ValueTask<(RoundTripVerification Verdict, string Detail, string? Mismatch)> VerifyRoundTripAsync(
        ISaveCodec<TDocument> codec,
        TDocument document,
        byte[] payload,
        IProgress<SaveProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_options.VerifyRoundTripBeforeReplace)
        {
            return (
                RoundTripVerification.Skipped,
                "The written bytes were not decoded back and compared to the document, because that check is switched off.",
                null);
        }

        if (payload.LongLength > _options.RoundTripVerificationMaxBytes)
        {
            return (
                RoundTripVerification.Skipped,
                $"The written bytes were not decoded back and compared to the document: at {payload.LongLength} bytes they are above the {_options.RoundTripVerificationMaxBytes}-byte limit for that check.",
                null);
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
            var unreadable = $"The bytes this codec produced could not be read back by the same codec ({ex.GetType().Name}: {ex.Message}). The save was abandoned rather than written.";
            return (RoundTripVerification.Mismatched, unreadable, unreadable);
        }

        if (_options.DocumentComparer.Equals(decoded, document))
        {
            return (
                RoundTripVerification.Verified,
                "The written bytes were decoded back and matched the document in memory.",
                null);
        }

        var lost = "The bytes this codec produced do not decode back to the document that is open. " +
                   "Something was lost in serialization, so the save was abandoned rather than written.";

        // The overwhelmingly likely cause, for a document type that has not defined
        // equality, is that the comparison is by reference and can never succeed — so
        // every save fails identically. Saying "something was lost in serialization"
        // there sends the author hunting for a codec bug that does not exist.
        var mismatch = ComparesByReference()
            ? lost + " This may not be a codec fault: "
                   + $"'{typeof(TDocument).Name}' does not define value equality, so the round-trip check "
                   + "is comparing object references and cannot ever match. Make the document a record, "
                   + "override Equals, or supply SafeFileWorkflowOptions.DocumentComparer."
            : lost;

        return (RoundTripVerification.Mismatched, mismatch, mismatch);
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
        bool backupWillBeWritten,
        CancellationToken cancellationToken)
    {
        var label = PathDisplayFormatter.Default.Format(path).Label;

        var message = $"Replace the file at {label} with the document that is open?";

        message += backupWillBeWritten
            ? " A verified backup of the existing file is written first, and the replacement is abandoned if that backup cannot be written or cannot be verified."
            : " No backup of the existing file will be taken.";

        // VerifiedEquivalent deliberately adds nothing here. It is a pass, and appending a
        // caveat to every overwrite of a legitimately lossless salted format would rebuild a
        // milder version of the crying-wolf problem this branch exists to avoid. Callers that
        // want the nuance read OpenSaveFile.UnknownDataDetail.
        if (open is { UnknownData: UnknownDataVerification.Falsified })
        {
            message +=
                " This format claims to preserve data it does not understand, but the framework tested that claim against this file and it is false: " +
                "saving will lose bytes that are in the file today. " + open.UnknownDataDetail;
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
