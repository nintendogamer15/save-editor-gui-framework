using System.Security.Cryptography;
using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Workflow;

/// <summary>
/// What the framework knows about the bytes it decoded, so it can tell later whether
/// anything else has written over them.
/// </summary>
/// <param name="Hash">SHA-256 of the whole file as it was read.</param>
/// <param name="Length">Length in bytes at capture time.</param>
/// <param name="LastWriteUtc">Last-write timestamp read from the handle, or <see langword="null"/>.</param>
/// <remarks>
/// The hash is the authority. The metadata is carried only so that an unambiguous
/// difference — a changed length — can short-circuit to "changed" without a full read.
/// </remarks>
public sealed record ContentBaseline(ReadOnlyMemory<byte> Hash, long Length, DateTime? LastWriteUtc);

/// <summary>Whether the bytes behind a retained handle still match a baseline.</summary>
public enum ExternalChangeVerdict
{
    /// <summary>The hash matched. Nothing observable changed between capture and check.</summary>
    Unchanged,

    /// <summary>The bytes, or the object identity, changed.</summary>
    Changed,

    /// <summary>The check could not be completed, so nothing may be concluded.</summary>
    /// <remarks>Treated as a failure by the workflow. An unreadable guard never means "proceed".</remarks>
    Indeterminate,
}

/// <summary>The result of one external-change check.</summary>
/// <param name="Verdict">What was concluded.</param>
/// <param name="HashCompared">
/// Whether a full hash was computed. <see langword="false"/> means the verdict came from
/// the metadata short-circuit, which is only ever used to reach
/// <see cref="ExternalChangeVerdict.Changed"/>.
/// </param>
/// <param name="MetadataDiffered">Whether length or last-write time differed from the baseline.</param>
/// <param name="Detail">Framework-authored explanation.</param>
public readonly record struct ExternalChangeCheck(
    ExternalChangeVerdict Verdict,
    bool HashCompared,
    bool MetadataDiffered,
    string Detail);

/// <summary>
/// Captures and re-verifies the change-detection baseline.
/// </summary>
/// <remarks>
/// Exposed as an interface so a test can drive the workflow's abort wiring on a platform
/// where an external write cannot be staged. The framework itself always installs
/// <see cref="ExternalChangeGuard"/>.
/// </remarks>
public interface IExternalChangeGuard
{
    /// <summary>Reads the whole file through the retained handle and records its hash.</summary>
    /// <param name="file">The retained, identity-recorded handle.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The baseline, and the bytes that produced it.</returns>
    ValueTask<(ContentBaseline Baseline, byte[] Bytes)> CaptureAsync(
        ResolvedFile file,
        CancellationToken cancellationToken = default);

    /// <summary>Re-reads the file through the retained handle and compares it to a baseline.</summary>
    /// <param name="file">The retained, identity-recorded handle.</param>
    /// <param name="baseline">What was recorded at decode time.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The verdict.</returns>
    ValueTask<ExternalChangeCheck> VerifyAsync(
        ResolvedFile file,
        ContentBaseline baseline,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The framework's external-change guard (<c>PLAN.md</c> §7 step 9, finding A5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>A hash is required for a positive result.</strong> Concluding "unchanged" is
/// only ever done from a full SHA-256 comparison read through the retained handle.
/// Metadata is used for exactly one thing: a length that differs from the baseline is an
/// unambiguous content change, so it short-circuits to
/// <see cref="ExternalChangeVerdict.Changed"/> without a full read. A last-write
/// timestamp that differs does <em>not</em> short-circuit, because mtime granularity is
/// coarse and mtime is trivially restorable in both directions — a sync client touching a
/// file it did not modify must not abort a save, and a writer who restored the timestamp
/// must not be believed. Equal metadata proves nothing at all and never satisfies the
/// guard on its own.
/// </para>
/// <para>
/// <strong>The residual is platform-dependent and is not overstated.</strong> On Windows
/// the retained handle is held with write sharing denied from resolution through the
/// replace, which excludes cooperative external writers for the whole operation. On Linux
/// locks are advisory and <c>rename(2)</c> offers no compare-and-swap, so re-verifying
/// immediately before the replace <em>narrows</em> the window to the last instruction
/// rather than closing it. Neither the documentation nor the status text claims more than
/// "no change was detected between the check and the write".
/// </para>
/// </remarks>
public sealed class ExternalChangeGuard : IExternalChangeGuard
{
    private const int ChunkSize = 128 * 1024;

    /// <summary>Creates a guard. The type holds no state and is safe to share.</summary>
    public ExternalChangeGuard()
    {
    }

    /// <inheritdoc />
    public async ValueTask<(ContentBaseline Baseline, byte[] Bytes)> CaptureAsync(
        ResolvedFile file,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var bytes = await ReadAllAsync(file, cancellationToken).ConfigureAwait(false);
        var baseline = new ContentBaseline(
            SHA256.HashData(bytes),
            bytes.LongLength,
            TryReadLastWriteUtc(file));

        return (baseline, bytes);
    }

    /// <inheritdoc />
    public async ValueTask<ExternalChangeCheck> VerifyAsync(
        ResolvedFile file,
        ContentBaseline baseline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(baseline);

        if (!file.ReassertIdentity())
        {
            return new ExternalChangeCheck(
                ExternalChangeVerdict.Changed,
                HashCompared: false,
                MetadataDiffered: true,
                "The retained handle no longer refers to the object that was resolved.");
        }

        long length;
        DateTime? lastWrite;
        try
        {
            length = file.Stream.Length;
            lastWrite = TryReadLastWriteUtc(file);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ExternalChangeCheck(
                ExternalChangeVerdict.Indeterminate,
                HashCompared: false,
                MetadataDiffered: false,
                $"The metadata of the target could not be read ({ex.GetType().Name}: {ex.Message}).");
        }

        var metadataDiffered = length != baseline.Length || lastWrite != baseline.LastWriteUtc;

        if (length != baseline.Length)
        {
            // The one sound metadata inference: a different length is a different file.
            return new ExternalChangeCheck(
                ExternalChangeVerdict.Changed,
                HashCompared: false,
                MetadataDiffered: true,
                $"The target is now {length} bytes; it was {baseline.Length} bytes when it was read.");
        }

        byte[] current;
        try
        {
            current = await ReadAllAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExternalChangeCheck(
                ExternalChangeVerdict.Indeterminate,
                HashCompared: false,
                metadataDiffered,
                $"The target could not be re-read for hashing ({ex.GetType().Name}: {ex.Message}).");
        }

        var hash = SHA256.HashData(current);
        var matched = CryptographicOperations.FixedTimeEquals(hash, baseline.Hash.Span);

        return new ExternalChangeCheck(
            matched ? ExternalChangeVerdict.Unchanged : ExternalChangeVerdict.Changed,
            HashCompared: true,
            metadataDiffered,
            matched
                ? "No change was detected between the check and the write."
                : "The bytes at the target changed after they were read, even though its size is unchanged.");
    }

    private static async ValueTask<byte[]> ReadAllAsync(ResolvedFile file, CancellationToken cancellationToken)
    {
        var stream = file.Stream;
        stream.Seek(0, SeekOrigin.Begin);

        var length = stream.Length;
        using var buffer = new MemoryStream(length > 0 && length < int.MaxValue ? (int)length : 0);

        var chunk = new byte[ChunkSize];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static DateTime? TryReadLastWriteUtc(ResolvedFile file)
    {
        try
        {
            return File.GetLastWriteTimeUtc(file.Stream.SafeFileHandle);
        }
        catch (Exception)
        {
            // Metadata is an optimization here, never an authority. Its absence costs a
            // short-circuit, not a guarantee.
            return null;
        }
    }
}
