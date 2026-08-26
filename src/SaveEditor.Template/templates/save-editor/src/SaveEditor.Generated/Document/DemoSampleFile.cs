using SaveEditor.Generated.Codecs;

namespace SaveEditor.Generated.Document;

// ============================================================================
// REPLACE ME FIRST. See DemoSaveCodec.cs.
// ============================================================================

/// <summary>
/// Writes a freshly generated demo save to disk, so the shipped app and its
/// tests have something real to open on a machine that has never run it
/// before.
/// </summary>
/// <remarks>
/// A real editor points users at wherever their game actually keeps its
/// saves; it does not need this. This exists only because the demo format
/// is invented for this template and nobody's disk has one already.
/// </remarks>
public static class DemoSampleFile
{
    /// <summary>Writes a sample demo save into a directory, creating it if needed.</summary>
    /// <param name="directory">Where to write the sample.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The full path written.</returns>
    public static async Task<string> WriteAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sample.demosave");

        var document = new DemoSaveDocument();
        var codec = new DemoSaveCodec();

        await using var stream = File.Create(path);
        await codec.SerializeAsync(document, stream, cancellationToken).ConfigureAwait(false);

        return path;
    }
}
