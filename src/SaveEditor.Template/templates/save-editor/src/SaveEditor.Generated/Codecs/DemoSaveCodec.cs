using System.Text.Json;
using System.Text.Json.Serialization;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Interaction;
using SaveEditor.Generated.Document;

namespace SaveEditor.Generated.Codecs;

// ============================================================================
// REPLACE ME FIRST. See Document/DemoSaveDocument.cs.
//
// This codec reads and writes an obviously fake ".demosave" format: an 8-byte
// magic prefix followed by plain, indented-off JSON. Real save formats are
// binary, versioned, checksummed, and full of bytes nobody has documented —
// this one deliberately is not, so it is easy to read while you are learning
// the framework's shape, and easy to tell apart from anything you write next.
// ============================================================================

/// <summary>Reads and writes the demo save format. Delete and replace this.</summary>
/// <remarks>
/// <para>
/// <strong>A codec is a correctness boundary, not a security boundary.</strong>
/// It runs in-process, at full privilege, as part of your editor — the
/// framework cannot sandbox it, and neither can you sandbox one you install
/// from someone else. See README.md, "Trust boundaries", and <c>PLAN.md</c>
/// §8 in the framework repository for the full threat model this implies.
/// </para>
/// <para>
/// <see cref="PreservesUnknownData"/> is <see langword="false"/> here because
/// this demo format has no unknown fields to lose — every byte it reads, it
/// also writes. A real format that carries data your document type does not
/// model (an unused block, a trailing checksum, fields from a newer game
/// version) must either preserve those bytes through the round trip and
/// declare <see langword="true"/>, or declare <see langword="false"/> and
/// accept that saving through this codec can silently drop them. The
/// framework verifies a <see langword="true"/> claim by re-serializing the
/// freshly decoded document and byte-comparing against the source — a false
/// claim is caught, not trusted.
/// </para>
/// </remarks>
public sealed class DemoSaveCodec : ISaveCodec<DemoSaveDocument>
{
    /// <summary>The fixed byte prefix every file in this format starts with.</summary>
    internal static readonly byte[] Magic = "DEMOSAVE"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <inheritdoc />
    public SaveFormatDescriptor Format { get; } = new(
        Id: "demo-save",
        DisplayName: "Demo save (obviously fake — replace me)",
        Extensions: ["demosave"]);

    /// <inheritdoc />
    public bool PreservesUnknownData => false;

    /// <inheritdoc />
    public async ValueTask<DemoSaveDocument> DecodeAsync(
        Stream source, CancellationToken cancellationToken = default)
    {
        var header = new byte[Magic.Length];
        var read = await source.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);

        if (read != Magic.Length || !header.AsSpan().SequenceEqual(Magic))
        {
            throw new InvalidDataException("This does not look like a demo save file (missing magic header).");
        }

        var payload = await JsonSerializer
            .DeserializeAsync<DemoSaveWireFormat>(source, JsonOptions, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("The demo save's JSON payload is empty or malformed.");

        return new DemoSaveDocument
        {
            HeroName = payload.HeroName,
            Level = payload.Level,
            HardcoreMode = payload.HardcoreMode,
            Difficulty = payload.Difficulty,
            Gold = payload.Gold,
            SaveId = payload.SaveId,
        };
    }

    /// <inheritdoc />
    public async ValueTask SerializeAsync(
        DemoSaveDocument document, Stream destination, CancellationToken cancellationToken = default)
    {
        await destination.WriteAsync(Magic, cancellationToken).ConfigureAwait(false);

        var wire = new DemoSaveWireFormat(
            document.HeroName,
            document.Level,
            document.HardcoreMode,
            document.Difficulty,
            document.Gold,
            document.SaveId);

        await JsonSerializer.SerializeAsync(destination, wire, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<ValidationReport> ValidateAsync(
        DemoSaveDocument document, CancellationToken cancellationToken = default)
    {
        var messages = new List<ValidationMessage>();

        if (string.IsNullOrWhiteSpace(document.HeroName))
        {
            messages.Add(new ValidationMessage(
                ValidationSeverity.Error,
                new UntrustedText("The hero needs a name before this can be saved."),
                FieldPath: "hero.name"));
        }

        if (document.Level > 99)
        {
            messages.Add(new ValidationMessage(
                ValidationSeverity.Warning,
                new UntrustedText("A level above 99 is outside what the demo format's own game ever produces."),
                FieldPath: "hero.level"));
        }

        return ValueTask.FromResult(new ValidationReport { Messages = messages });
    }

    private sealed record DemoSaveWireFormat(
        string HeroName,
        long Level,
        bool HardcoreMode,
        string Difficulty,
        long Gold,
        string SaveId);
}
