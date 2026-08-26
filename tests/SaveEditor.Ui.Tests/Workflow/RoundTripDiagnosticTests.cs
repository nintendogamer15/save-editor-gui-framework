using SaveEditor.Ui.Codecs;
using SaveEditor.Ui.Interaction;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// A document type without value equality makes the round-trip check compare
/// references, so every save fails identically. The message has to say so.
/// </summary>
/// <remarks>
/// Found while building the template: the generated app's document was a plain
/// mutable class, and every save failed with "something was lost in
/// serialization" — pointing at a codec that was working perfectly. The check is
/// fail-safe, so nothing was ever at risk; it was the diagnosis that was wrong,
/// and a wrong diagnosis on a save failure costs an author hours.
/// </remarks>
public class RoundTripDiagnosticTests
{
    /// <summary>A mutable document with no value equality — the trap's exact shape.</summary>
    private sealed class MutableDocument
    {
        public string Name { get; set; } = "Aerith";
    }

    private sealed class PassThroughCodec : ISaveCodec<MutableDocument>
    {
        public SaveFormatDescriptor Format { get; } = new("test.mutable", "Mutable", ["sav"]);

        public bool PreservesUnknownData => true;

        public async ValueTask<MutableDocument> DecodeAsync(Stream source, CancellationToken ct = default)
        {
            using var reader = new StreamReader(source, leaveOpen: true);
            return new MutableDocument { Name = await reader.ReadToEndAsync(ct).ConfigureAwait(false) };
        }

        public async ValueTask SerializeAsync(MutableDocument d, Stream destination, CancellationToken ct = default)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(d.Name);
            await destination.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        public ValueTask<ValidationReport> ValidateAsync(MutableDocument d, CancellationToken ct = default) =>
            ValueTask.FromResult(ValidationReport.Empty);
    }

    [Fact]
    public async Task A_Document_Without_Value_Equality_Gets_An_Actionable_Message()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"se-rt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var codec = new PassThroughCodec();
            var workflow = new SafeFileWorkflow<MutableDocument>(new SafeFileWorkflowOptions<MutableDocument>
            {
                Registry = new SaveCodecRegistry<MutableDocument>([]),
                Interaction = new SilentInteraction(Path.Combine(directory, "out.sav")),
            });

            var outcome = await workflow.SaveAsAsync(
                new MutableDocument(), codec, null, null, TestContext.Current.CancellationToken);

            Assert.False(outcome.IsSuccess);
            Assert.Equal(SaveFailureReason.RoundTripMismatch, outcome.Reason);

            // The serializer is perfect. The message must not blame it alone.
            Assert.Contains("does not define value equality", outcome.Message, StringComparison.Ordinal);
            Assert.Contains("DocumentComparer", outcome.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_Record_Document_Does_Not_Get_The_Equality_Hint()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"se-rt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            // A record has value equality, so a mismatch really is a lossy codec and
            // the hint would be a red herring.
            var codec = new LossyRecordCodec();
            var workflow = new SafeFileWorkflow<RecordDocument>(new SafeFileWorkflowOptions<RecordDocument>
            {
                Registry = new SaveCodecRegistry<RecordDocument>([]),
                Interaction = new SilentInteraction(Path.Combine(directory, "out.sav")),
            });

            var outcome = await workflow.SaveAsAsync(
                new RecordDocument("Aerith", 42), codec, null, null, TestContext.Current.CancellationToken);

            Assert.False(outcome.IsSuccess);
            Assert.Equal(SaveFailureReason.RoundTripMismatch, outcome.Reason);
            Assert.DoesNotContain("value equality", outcome.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record RecordDocument(string Name, int Level);

    /// <summary>Genuinely lossy: drops the level.</summary>
    private sealed class LossyRecordCodec : ISaveCodec<RecordDocument>
    {
        public SaveFormatDescriptor Format { get; } = new("test.record", "Record", ["sav"]);

        public bool PreservesUnknownData => false;

        public async ValueTask<RecordDocument> DecodeAsync(Stream source, CancellationToken ct = default)
        {
            using var reader = new StreamReader(source, leaveOpen: true);
            return new RecordDocument(await reader.ReadToEndAsync(ct).ConfigureAwait(false), 0);
        }

        public async ValueTask SerializeAsync(RecordDocument d, Stream destination, CancellationToken ct = default)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(d.Name);
            await destination.WriteAsync(bytes, ct).ConfigureAwait(false);
        }

        public ValueTask<ValidationReport> ValidateAsync(RecordDocument d, CancellationToken ct = default) =>
            ValueTask.FromResult(ValidationReport.Empty);
    }

    private sealed class SilentInteraction(string savePath) : IUserInteraction
    {
        public ValueTask<string?> PickOpenFileAsync(FilePickerRequest r, CancellationToken c = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<SaveFilePickResult?> PickSaveFileAsync(FilePickerRequest r, CancellationToken c = default) =>
            ValueTask.FromResult<SaveFilePickResult?>(new SaveFilePickResult(savePath, false));

        public ValueTask<string?> PickFolderAsync(string t, string? s = null, CancellationToken c = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<bool> ConfirmAsync(ConfirmationRequest r, CancellationToken c = default) =>
            ValueTask.FromResult(true);

        public ValueTask ShowMessageAsync(MessageRequest r, CancellationToken c = default) =>
            ValueTask.CompletedTask;

        public ValueTask<string?> ChooseAsync(ChoicePrompt p, CancellationToken c = default) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask ShowDocumentAsync(DocumentRequest r, CancellationToken c = default) =>
            ValueTask.CompletedTask;
    }
}
