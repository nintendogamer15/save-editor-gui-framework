using System.Text;
using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// Detection for formats whose identity is not in a plaintext prefix.
/// </summary>
/// <remarks>
/// The adopter's case exactly: two game schemas share one encrypted envelope, and telling
/// them apart needs the decrypted object. A detector limited to a bounded header slice cannot
/// answer, so before this the whole class of formats had to register one codec and
/// discriminate inside <c>DecodeAsync</c> — which works, but puts the registry's ambiguity
/// resolution and picker filtering out of reach (finding F-8).
/// </remarks>
public sealed class PayloadDetectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A detector that recognises the shared envelope and admits it cannot go further.
    /// </summary>
    private sealed class EnvelopeDetector : ISaveCodecDetector
    {
        public required SaveFormatDescriptor Format { get; init; }

        public int HeaderBytesRequired => Envelope.Length;

        public int DetectCalls { get; private set; }

        internal const string Envelope = "ENVL";

        public DetectionVerdict Detect(ReadOnlySpan<byte> header)
        {
            DetectCalls++;

            return header.Length >= Envelope.Length &&
                   Encoding.UTF8.GetString(header[..Envelope.Length]) == Envelope
                ? DetectionVerdict.RequiresDecode
                : DetectionVerdict.Declined;
        }
    }

    /// <summary>
    /// A codec for one schema inside the shared envelope. The schema marker follows the
    /// envelope tag, standing in for a field that only exists once decrypted.
    /// </summary>
    private sealed class SchemaCodec : ISaveCodec<TestDocument>
    {
        public required SaveFormatDescriptor Format { get; init; }

        public required string Schema { get; init; }

        /// <summary>When set, decoding another schema's payload throws instead of declining.</summary>
        public bool ThrowOnForeignSchema { get; init; }

        public bool PreservesUnknownData => false;

        public int DecodeCalls { get; private set; }

        public int ConfirmCalls { get; private set; }

        internal static byte[] Encode(string schema, TestDocument document) =>
            Encoding.UTF8.GetBytes($"{EnvelopeDetector.Envelope}{schema}|{document.Name}|{document.Level}|{document.Trailer}");

        public ValueTask<TestDocument> DecodeAsync(Stream source, CancellationToken cancellationToken = default)
        {
            DecodeCalls++;

            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var text = Encoding.UTF8.GetString(buffer.ToArray());

            var body = text[EnvelopeDetector.Envelope.Length..];
            var schema = body[..Schema.Length];

            if (ThrowOnForeignSchema && schema != Schema)
            {
                throw new InvalidDataException($"not a {Schema} payload");
            }

            var parts = body[Schema.Length..].TrimStart('|').Split('|', 3);
            return ValueTask.FromResult(new TestDocument(
                parts[0],
                int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                parts[2]));
        }

        public DetectionVerdict ConfirmDecoded(TestDocument document)
        {
            ConfirmCalls++;

            // Stands in for reading a schema marker out of the decrypted object.
            return document.Trailer.StartsWith(Schema, StringComparison.Ordinal)
                ? DetectionVerdict.Confident
                : DetectionVerdict.Declined;
        }

        public ValueTask SerializeAsync(TestDocument document, Stream destination, CancellationToken cancellationToken = default) =>
            destination.WriteAsync(Encode(Schema, document), cancellationToken);

        public ValueTask<ValidationReport> ValidateAsync(TestDocument document, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ValidationReport.Empty);
    }

    private static (SaveCodecRegistry<TestDocument> Registry, SchemaCodec Alpha, SchemaCodec Beta) BuildRegistry(
        bool throwOnForeignSchema = false)
    {
        var alpha = new SchemaCodec
        {
            Format = new SaveFormatDescriptor("game.alpha", "Alpha Save", ["sav"]),
            Schema = "A",
            ThrowOnForeignSchema = throwOnForeignSchema,
        };

        var beta = new SchemaCodec
        {
            Format = new SaveFormatDescriptor("game.beta", "Beta Save", ["sav"]),
            Schema = "B",
            ThrowOnForeignSchema = throwOnForeignSchema,
        };

        var registry = new SaveCodecRegistry<TestDocument>(
        [
            new CodecRegistration<TestDocument>(new EnvelopeDetector { Format = alpha.Format }, alpha),
            new CodecRegistration<TestDocument>(new EnvelopeDetector { Format = beta.Format }, beta),
        ]);

        return (registry, alpha, beta);
    }

    [Fact]
    public async Task Detection_ReportsThatTheContainerNeedsDecodingBeforeItCanBeIdentified()
    {
        var (registry, _, _) = BuildRegistry();

        var result = await registry.DetectAsync(
            SchemaCodec.Encode("A", new TestDocument("hero", 3, "A-trailer")),
            Token);

        Assert.True(result.RequiresDecode);

        // Deliberately unresolved even though both candidates are at the same tier: the
        // header established only that the envelope is consistent.
        Assert.Null(result.Codec);
        Assert.Equal(2, result.Candidates.Count);
    }

    /// <summary>
    /// A confident header detector still wins outright, so nothing about existing detection
    /// changes.
    /// </summary>
    [Fact]
    public async Task Detection_RanksAConfidentHeaderAboveOneNeedingThePayload()
    {
        var (_, alpha, _) = BuildRegistry();

        var certain = new TestDetector
        {
            Format = new SaveFormatDescriptor("game.certain", "Certain Save", ["sav"]),
            HeaderBytesRequired = EnvelopeDetector.Envelope.Length,
            DetectOverride = _ => DetectionVerdict.Confident,
        };

        var certainCodec = new SchemaCodec
        {
            Format = certain.Format,
            Schema = "C",
        };

        var registry = new SaveCodecRegistry<TestDocument>(
        [
            new CodecRegistration<TestDocument>(new EnvelopeDetector { Format = alpha.Format }, alpha),
            new CodecRegistration<TestDocument>(certain, certainCodec),
        ]);

        var result = await registry.DetectAsync(
            SchemaCodec.Encode("A", new TestDocument("hero", 3, "A-trailer")),
            Token);

        Assert.False(result.RequiresDecode);
        Assert.Same(certainCodec, result.Codec);
    }

    /// <summary>
    /// The whole point: the right codec is chosen from the payload, through the registry,
    /// with no prompt — and each candidate decodes exactly once.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Open_ResolvesTheSchemaFromThePayloadWithoutAskingTheUser(bool throwOnForeignSchema)
    {
        using var harness = new WorkflowHarness($"payload-detect-{throwOnForeignSchema}");
        var (registry, alpha, beta) = BuildRegistry(throwOnForeignSchema);

        var document = new TestDocument("hero", 7, "B-trailer");
        var target = harness.Workspace.Path("save.sav");
        File.WriteAllBytes(target, SchemaCodec.Encode("B", document));

        var options = harness.Options with { Registry = registry };
        var outcome = await new SafeFileWorkflow<TestDocument>(options).OpenAsync(target, cancellationToken: Token);

        var opened = Assert.IsType<OpenOutcome<TestDocument>.Opened>(outcome);
        using var open = opened.File;

        Assert.Same(beta, open.Codec);
        Assert.Equal(document, open.Document);

        // Resolved through the registry, not around it, and without a prompt the user could
        // not have answered.
        Assert.Empty(harness.Interaction.Prompts);

        // Each candidate decoded once. The winner is not decoded a second time after being
        // chosen -- that would run untrusted code over the same bytes for no new information.
        Assert.Equal(1, beta.DecodeCalls);
        Assert.True(alpha.DecodeCalls <= 1, $"Alpha decoded {alpha.DecodeCalls} times.");
    }

    [Fact]
    public async Task Open_AsksTheUserWhenTwoCodecsBothConfirmThePayload()
    {
        using var harness = new WorkflowHarness("payload-ambiguous");

        // Both codecs claim every payload, so the decode stage cannot separate them either.
        var alpha = new SchemaCodec { Format = new SaveFormatDescriptor("game.alpha", "Alpha Save", ["sav"]), Schema = string.Empty };
        var beta = new SchemaCodec { Format = new SaveFormatDescriptor("game.beta", "Beta Save", ["sav"]), Schema = string.Empty };

        var registry = new SaveCodecRegistry<TestDocument>(
        [
            new CodecRegistration<TestDocument>(new EnvelopeDetector { Format = alpha.Format }, alpha),
            new CodecRegistration<TestDocument>(new EnvelopeDetector { Format = beta.Format }, beta),
        ]);

        var document = new TestDocument("hero", 7, "shared");
        var target = harness.Workspace.Path("save.sav");
        File.WriteAllBytes(target, SchemaCodec.Encode(string.Empty, document));

        // The default chooser offers candidates one at a time through ConfirmAsync, ordered
        // by display name rather than registration order, so declining Alpha selects Beta.
        harness.Interaction.Confirm = request => request.Message.Contains("Beta Save", StringComparison.Ordinal);

        var options = harness.Options with { Registry = registry };
        var outcome = await new SafeFileWorkflow<TestDocument>(options).OpenAsync(target, cancellationToken: Token);

        var opened = Assert.IsType<OpenOutcome<TestDocument>.Opened>(outcome);
        using var open = opened.File;

        // Both were offered, and both had already decoded -- the choice is between documents
        // the framework is holding, so accepting one costs no further codec work.
        Assert.Equal(2, harness.Interaction.Confirmations.Count);
        Assert.All(harness.Interaction.Confirmations, c => Assert.Contains("recognize this file", c.Message, StringComparison.Ordinal));
        Assert.Same(beta, open.Codec);
        Assert.Equal(1, alpha.DecodeCalls);
        Assert.Equal(1, beta.DecodeCalls);
    }

    [Fact]
    public async Task Open_DeclinesWhenTheAmbiguityPromptIsDismissed()
    {
        using var harness = new WorkflowHarness("payload-dismissed");

        var alpha = new SchemaCodec { Format = new SaveFormatDescriptor("game.alpha", "Alpha Save", ["sav"]), Schema = string.Empty };
        var beta = new SchemaCodec { Format = new SaveFormatDescriptor("game.beta", "Beta Save", ["sav"]), Schema = string.Empty };

        var registry = new SaveCodecRegistry<TestDocument>(
        [
            new CodecRegistration<TestDocument>(new EnvelopeDetector { Format = alpha.Format }, alpha),
            new CodecRegistration<TestDocument>(new EnvelopeDetector { Format = beta.Format }, beta),
        ]);

        var target = harness.Workspace.Path("save.sav");
        File.WriteAllBytes(target, SchemaCodec.Encode(string.Empty, new TestDocument("hero", 7, "shared")));

        // Declining every candidate is not a selection, and must not fall back to a guess.
        harness.Interaction.Confirm = _ => false;

        var options = harness.Options with { Registry = registry };
        var outcome = await new SafeFileWorkflow<TestDocument>(options).OpenAsync(target, cancellationToken: Token);

        Assert.IsType<OpenOutcome<TestDocument>.Declined>(outcome);
    }

    [Fact]
    public async Task Open_FailsWhenNoCandidateConfirmsThePayload()
    {
        using var harness = new WorkflowHarness("payload-none");
        var (registry, _, _) = BuildRegistry();

        // A payload inside the shared envelope that neither schema claims.
        var target = harness.Workspace.Path("save.sav");
        File.WriteAllBytes(target, SchemaCodec.Encode("Z", new TestDocument("hero", 7, "Z-trailer")));

        var options = harness.Options with { Registry = registry };
        var outcome = await new SafeFileWorkflow<TestDocument>(options).OpenAsync(target, cancellationToken: Token);

        var failed = Assert.IsType<OpenOutcome<TestDocument>.Failed>(outcome);
        Assert.Equal(SaveFailureReason.DetectionFailed, failed.Reason);
        Assert.Contains("after decoding", failed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A codec that declines rather than a detector that does: the default
    /// <c>ConfirmDecoded</c> confirms, so a codec whose decode succeeds is accepted.
    /// </summary>
    [Fact]
    public async Task ConfirmDecoded_DefaultsToConfidentForACodecThatDecoded()
    {
        using var harness = new WorkflowHarness("payload-default-confirm");

        var codec = new TestCodec { PreservesUnknownData = false };
        var detector = new TestDetector
        {
            HeaderBytesRequired = TestCodec.Magic.Length,
            DetectOverride = header =>
                header.Length >= TestCodec.Magic.Length && Encoding.UTF8.GetString(header) == TestCodec.Magic
                    ? DetectionVerdict.RequiresDecode
                    : DetectionVerdict.Declined,
        };

        var registry = new SaveCodecRegistry<TestDocument>([new CodecRegistration<TestDocument>(detector, codec)]);

        var document = new TestDocument("hero", 3, "trailer");
        var target = harness.WriteSave("save.sav", document);

        var options = harness.Options with { Registry = registry };
        var outcome = await new SafeFileWorkflow<TestDocument>(options).OpenAsync(target, cancellationToken: Token);

        var opened = Assert.IsType<OpenOutcome<TestDocument>.Opened>(outcome);
        using var open = opened.File;

        Assert.Same(codec, open.Codec);
        Assert.Equal(document, open.Document);
        Assert.Empty(harness.Interaction.Prompts);
    }
}
