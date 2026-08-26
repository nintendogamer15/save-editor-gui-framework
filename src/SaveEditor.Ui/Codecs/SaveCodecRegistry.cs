namespace SaveEditor.Ui.Codecs;

/// <summary>One codec and the detector that recognizes its format.</summary>
/// <typeparam name="TDocument">The editor's in-memory document type.</typeparam>
/// <param name="Detector">Recognizes the format from a bounded header slice.</param>
/// <param name="Codec">Reads and writes the format.</param>
public sealed record CodecRegistration<TDocument>(ISaveCodecDetector Detector, ISaveCodec<TDocument> Codec);

/// <summary>What one detector did when it was offered a header slice.</summary>
/// <param name="Format">The format the detector speaks for.</param>
/// <param name="Verdict">The verdict recorded for it.</param>
/// <param name="HeaderBytesOffered">How many bytes it was actually shown.</param>
/// <param name="Faulted">Whether it threw, in which case the verdict is <see cref="DetectionVerdict.Declined"/>.</param>
/// <param name="TimedOut">Whether it exceeded its time box, in which case the verdict is <see cref="DetectionVerdict.Declined"/>.</param>
/// <param name="Detail">Framework-authored explanation when something went wrong.</param>
public sealed record DetectorReport(
    SaveFormatDescriptor Format,
    DetectionVerdict Verdict,
    int HeaderBytesOffered,
    bool Faulted,
    bool TimedOut,
    string? Detail);

/// <summary>The outcome of running every registered detector over one file.</summary>
/// <typeparam name="TDocument">The editor's in-memory document type.</typeparam>
/// <param name="Codec">The single codec that matched, or <see langword="null"/>.</param>
/// <param name="Candidates">
/// Every codec that matched at the winning confidence level. More than one entry means the
/// file is ambiguous and the choice belongs to the user.
/// </param>
/// <param name="Reports">What each detector did, in registration order, for diagnostics.</param>
/// <param name="Detail">Framework-authored summary.</param>
public sealed record DetectionResult<TDocument>(
    ISaveCodec<TDocument>? Codec,
    IReadOnlyList<ISaveCodec<TDocument>> Candidates,
    IReadOnlyList<DetectorReport> Reports,
    string Detail)
{
    /// <summary>
    /// Whether <see cref="Candidates"/> were selected on a
    /// <see cref="DetectionVerdict.RequiresDecode"/> verdict and still have to be settled by
    /// decoding.
    /// </summary>
    /// <remarks>
    /// When this is set, <see cref="Codec"/> is deliberately <see langword="null"/> even for
    /// a single candidate: the header said only that the envelope is consistent, and the
    /// candidate can still decline once it sees the payload (finding F-8).
    /// </remarks>
    public bool RequiresDecode { get; init; }

    /// <summary>Whether more than one codec claimed the file at the same confidence.</summary>
    public bool IsAmbiguous => Candidates.Count > 1;

    /// <summary>Whether exactly one codec claimed the file.</summary>
    public bool IsResolved => Codec is not null;
}

/// <summary>Bounds applied to detection.</summary>
public sealed record SaveCodecRegistryOptions
{
    /// <summary>
    /// The largest header slice any detector may be shown, regardless of what it asks for.
    /// </summary>
    public int MaxHeaderBytes { get; init; } = 64 * 1024;

    /// <summary>How long one detector may run before it is recorded as having declined.</summary>
    public TimeSpan DetectorTimeout { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Runs every registered detector over a bounded header slice and reports which codec —
/// if any — owns the file (<c>PLAN.md</c> §7 step 12, finding A11).
/// </summary>
/// <typeparam name="TDocument">The editor's in-memory document type.</typeparam>
/// <remarks>
/// <para>
/// Every registered detector inspects the same untrusted bytes, so the parsing surface a
/// hostile save file reaches is the union of all installed codecs and not only the one
/// that eventually matches. Three bounds narrow that. Each detector is shown a copy of a
/// bounded prefix — never a seekable stream over the whole file, and never more than it
/// declared it needs — so a detector cannot walk the payload. Each runs in isolation, so
/// one that throws is recorded as having declined rather than aborting detection for every
/// other format. And each is individually time-boxed, so one that loops does not hang the
/// open.
/// </para>
/// <para>
/// <strong>The time box is a boundary, not a kill.</strong> Managed code cannot be aborted;
/// a detector that exceeds its box is abandoned and recorded as declined, and the thread it
/// occupies may still be running. This is the same honesty the workflow applies to
/// cancellation: the framework's decision is authoritative, the third-party code's
/// behaviour is not controlled.
/// </para>
/// <para>
/// <strong>Ambiguity is resolved by the user.</strong> Two detectors that both answer
/// <see cref="DetectionVerdict.Confident"/> produce a
/// <see cref="DetectionResult{TDocument}.IsAmbiguous"/> result. Registration order never
/// breaks the tie, because registration order is an accident of composition-root wiring
/// and the user is the only party who knows which game wrote the file.
/// </para>
/// <para>
/// <strong>A format whose identity is in its payload is resolved by decoding it.</strong>
/// A detector answering <see cref="DetectionVerdict.RequiresDecode"/> sets
/// <see cref="DetectionResult{TDocument}.RequiresDecode"/>, and the workflow decodes each
/// candidate once and asks <see cref="ISaveCodec{TDocument}.ConfirmDecoded"/> to settle it.
/// The cost is accepted deliberately: <em>n</em> codecs all answering
/// <see cref="DetectionVerdict.RequiresDecode"/> means up to <em>n</em> decode attempts over
/// one untrusted file. That is bounded by the registration count, each attempt is contained
/// exactly as any other codec call is, and the alternative was that an entire class of
/// formats could not use the registry at all (finding F-8).
/// </para>
/// </remarks>
public sealed class SaveCodecRegistry<TDocument>
{
    private readonly List<CodecRegistration<TDocument>> _registrations;
    private readonly SaveCodecRegistryOptions _options;

    /// <summary>Creates a registry over a fixed set of codecs.</summary>
    /// <param name="registrations">The registered codecs. Order carries no authority.</param>
    /// <param name="options">Detection bounds, or <see langword="null"/> for defaults.</param>
    public SaveCodecRegistry(
        IEnumerable<CodecRegistration<TDocument>> registrations,
        SaveCodecRegistryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        _registrations = [.. registrations];
        _options = options ?? new SaveCodecRegistryOptions();
    }

    /// <summary>Every registered format, for picker filters.</summary>
    public IReadOnlyList<SaveFormatDescriptor> Formats =>
        [.. _registrations.Select(registration => registration.Codec.Format)];

    /// <summary>Every registered codec.</summary>
    public IReadOnlyList<ISaveCodec<TDocument>> Codecs =>
        [.. _registrations.Select(registration => registration.Codec)];

    /// <summary>
    /// How many leading bytes the workflow needs to read for detection.
    /// </summary>
    /// <remarks>
    /// The largest value any detector declared, clamped to
    /// <see cref="SaveCodecRegistryOptions.MaxHeaderBytes"/>. The file is read once and
    /// sliced per detector rather than read once per detector.
    /// </remarks>
    public int HeaderBytesRequired =>
        _registrations.Count == 0
            ? 0
            : Math.Clamp(_registrations.Max(r => r.Detector.HeaderBytesRequired), 0, _options.MaxHeaderBytes);

    /// <summary>Runs detection over the leading bytes of a file.</summary>
    /// <param name="content">The bytes already read through the retained handle.</param>
    /// <param name="cancellationToken">Cancels detection between detectors.</param>
    /// <returns>Which codec owns the file, or why none does.</returns>
    public async ValueTask<DetectionResult<TDocument>> DetectAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        var reports = new List<DetectorReport>(_registrations.Count);
        var confident = new List<ISaveCodec<TDocument>>();
        var requiresDecode = new List<ISaveCodec<TDocument>>();
        var possible = new List<ISaveCodec<TDocument>>();

        foreach (var registration in _registrations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var wanted = Math.Clamp(registration.Detector.HeaderBytesRequired, 0, _options.MaxHeaderBytes);
            var offered = (int)Math.Min(wanted, content.Length);

            // A private copy of exactly the slice the detector declared it needs. Handing
            // out the shared buffer, or a stream over it, would let one detector read the
            // whole untrusted payload while claiming to want sixteen bytes.
            var slice = content.Span[..offered].ToArray();

            var report = await RunIsolatedAsync(registration.Detector, slice, cancellationToken).ConfigureAwait(false);
            reports.Add(report);

            switch (report.Verdict)
            {
                case DetectionVerdict.Confident:
                    confident.Add(registration.Codec);
                    break;
                case DetectionVerdict.RequiresDecode:
                    requiresDecode.Add(registration.Codec);
                    break;
                case DetectionVerdict.Possible:
                    possible.Add(registration.Codec);
                    break;
                default:
                    break;
            }
        }

        // Confident beats RequiresDecode beats Possible. A detector that identified the
        // format from its header still wins outright, so no existing detection outcome
        // changes; RequiresDecode only ever competes when nothing was confident.
        if (confident.Count == 0 && requiresDecode.Count > 0)
        {
            return new DetectionResult<TDocument>(
                null,
                requiresDecode,
                reports,
                requiresDecode.Count == 1
                    ? $"{requiresDecode[0].Format.DisplayName} recognized the container but cannot identify the format without decoding it."
                    : $"{requiresDecode.Count} codecs recognized the container but cannot identify the format without decoding it.")
            {
                RequiresDecode = true,
            };
        }

        var candidates = confident.Count > 0 ? confident : possible;

        return candidates.Count switch
        {
            1 => new DetectionResult<TDocument>(candidates[0], candidates, reports, $"Recognized as {candidates[0].Format.DisplayName}."),
            0 => new DetectionResult<TDocument>(null, [], reports, "No registered codec recognized this file."),
            _ => new DetectionResult<TDocument>(
                null,
                candidates,
                reports,
                $"{candidates.Count} codecs recognized this file. The choice is the user's, not the registration order's."),
        };
    }

    private async ValueTask<DetectorReport> RunIsolatedAsync(
        ISaveCodecDetector detector,
        byte[] slice,
        CancellationToken cancellationToken)
    {
        SaveFormatDescriptor format;
        try
        {
            format = detector.Format;
        }
        catch (Exception ex)
        {
            return new DetectorReport(
                new SaveFormatDescriptor("unknown", detector.GetType().Name, []),
                DetectionVerdict.Declined,
                slice.Length,
                Faulted: true,
                TimedOut: false,
                $"The detector threw from its Format property ({ex.GetType().Name}).");
        }

        var work = Task.Run(
            () =>
            {
                try
                {
                    return (Verdict: detector.Detect(slice), Fault: (Exception?)null);
                }
                catch (Exception ex)
                {
                    // Containment, not suppression: the verdict becomes Declined and the
                    // fault is reported, so a broken detector is visible without being
                    // able to abort detection for every other format.
                    return (Verdict: DetectionVerdict.Declined, Fault: ex);
                }
            },
            CancellationToken.None);

        try
        {
            var result = await work.WaitAsync(_options.DetectorTimeout, cancellationToken).ConfigureAwait(false);

            return new DetectorReport(
                format,
                result.Verdict,
                slice.Length,
                Faulted: result.Fault is not null,
                TimedOut: false,
                result.Fault is null ? null : $"The detector threw {result.Fault.GetType().Name} and was recorded as having declined.");
        }
        catch (TimeoutException)
        {
            // The abandoned task keeps a thread; it cannot be aborted and the framework
            // does not pretend otherwise. Its result is discarded either way.
            Observe(work);

            return new DetectorReport(
                format,
                DetectionVerdict.Declined,
                slice.Length,
                Faulted: false,
                TimedOut: true,
                $"The detector exceeded its {_options.DetectorTimeout.TotalMilliseconds:F0} ms time box and was recorded as having declined.");
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
}
