using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// <see cref="SafeFileWorkflowOptions{TDocument}.DocumentComparer"/> is a trusted
/// collaborator, on the same footing as the codec.
/// </summary>
/// <remarks>
/// <para>
/// The adopter's brief calls a hand-written comparer that omits a field "the single most
/// dangerous adopter misconfiguration, and nothing detects it". That is correct, and this
/// file pins it rather than pretending otherwise.
/// </para>
/// <para>
/// <strong>Why it is not fixed.</strong> The framework holds exactly one document pair at the
/// moment it asks — the document in memory and the one decoded back from the bytes it just
/// produced — and no oracle for what equality ought to mean for a type it knows nothing about.
/// A comparer that returns <see langword="true"/> for a pair that differs in a field is
/// indistinguishable, from inside, from a comparer correctly reporting that a lossless
/// round trip round-tripped. <c>ComparesByReference</c> catches the one case that is decidable
/// without an oracle — the <em>default</em> comparer falling back to reference equality for a
/// type with no equality contract, which can never match and so fails every save loudly with a
/// message naming the real cause. That case is covered by <see cref="RoundTripDiagnosticTests"/>.
/// </para>
/// <para>
/// So the honest posture is the one the codec already has: a bounded, instrumented
/// correctness boundary that the framework verifies where it can and documents where it
/// cannot. These tests exist so the boundary is stated in executable form, and so that anyone
/// who later believes the round-trip check is unconditional finds out here.
/// </para>
/// </remarks>
public sealed class ComparerTrustTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TestDocument SampleDocument => new("hero", 3, "trailer-bytes");

    /// <summary>A comparer that ignores the field the codec drops.</summary>
    private sealed class IgnoresTheTrailer : IEqualityComparer<TestDocument>
    {
        public bool Equals(TestDocument? x, TestDocument? y) =>
            x is not null && y is not null && x.Name == y.Name && x.Level == y.Level;

        public int GetHashCode(TestDocument obj) => HashCode.Combine(obj.Name, obj.Level);
    }

    /// <summary>
    /// The framework's own guard catches the lossy codec, so the comparer is what disables it
    /// — not the codec being subtle.
    /// </summary>
    [Fact]
    public async Task TheDefaultComparer_CatchesALossyCodec()
    {
        using var harness = new WorkflowHarness("comparer-default-catches");
        harness.Codec.PreservesUnknownData = false;
        harness.Codec.SerializeOverride = (doc, destination, cancellationToken) =>
            destination.WriteAsync(TestCodec.Encode(doc with { Trailer = string.Empty }), cancellationToken);

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);
        var targetBytes = File.ReadAllBytes(target);

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        Assert.Equal(SaveFailureReason.RoundTripMismatch, outcome.Reason);
        Assert.Equal(RoundTripVerification.Mismatched, outcome.RoundTrip);
        Assert.Equal(targetBytes, File.ReadAllBytes(target));
    }

    /// <summary>
    /// The same codec, with a comparer that does not look at the dropped field: the save
    /// succeeds and the trailer is gone from the file.
    /// </summary>
    /// <remarks>
    /// This pins a documented limitation, not desired behaviour. The framework reports
    /// <c>RoundTripVerification.Verified</c> because the check it was configured to run did
    /// run and did pass — which is a true statement about what was checked, and exactly why
    /// the comparer has to be right. Nothing in the outcome is false; it is simply weaker than
    /// a reader might assume.
    /// </remarks>
    [Fact]
    public async Task ATooLooseComparer_LetsALossyCodecThroughAndNothingDetectsIt()
    {
        using var harness = new WorkflowHarness("comparer-too-loose");
        harness.Codec.PreservesUnknownData = false;
        harness.Codec.SerializeOverride = (doc, destination, cancellationToken) =>
            destination.WriteAsync(TestCodec.Encode(doc with { Trailer = string.Empty }), cancellationToken);

        var document = SampleDocument;
        var target = harness.WriteSave("save.sav", document);

        using var open = await harness.OpenAsync(target, Token);

        var options = harness.Options with { DocumentComparer = new IgnoresTheTrailer() };
        var written = document with { Level = 9 };

        var outcome = await new SafeFileWorkflow<TestDocument>(options)
            .OverwriteWithBackupAsync(written, open, cancellationToken: Token);

        WorkflowHarness.AssertSucceeded(outcome);

        // The trailer the user had is gone, and no verdict anywhere says so.
        var landed = TestCodec.Parse(File.ReadAllBytes(target));
        Assert.Equal(string.Empty, landed.Trailer);
        Assert.NotEqual(written.Trailer, landed.Trailer);

        // Verified means "the configured check ran and passed", which it did. That is the
        // whole of the guarantee, and it is only as good as the comparer.
        Assert.Equal(RoundTripVerification.Verified, outcome.RoundTrip);
    }
}
