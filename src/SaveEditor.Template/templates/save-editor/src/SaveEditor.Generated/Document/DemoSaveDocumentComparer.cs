namespace SaveEditor.Generated.Document;

// ============================================================================
// REPLACE ME FIRST. See DemoSaveDocument.cs.
// ============================================================================

/// <summary>
/// Value-compares two <see cref="DemoSaveDocument"/> instances field by field.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SaveEditor.Ui.Workflow.SafeFileWorkflowOptions{TDocument}.DocumentComparer"/>
/// defaults to <see cref="EqualityComparer{T}.Default"/>, which is exactly
/// right for a document modelled as a record and silently wrong for one
/// modelled as a mutable class like <see cref="DemoSaveDocument"/>: without
/// an overridden <c>Equals</c>, the default comparer falls back to reference
/// equality, and the pre-replace round-trip check — decode the freshly
/// serialized bytes and compare against the document in memory — would then
/// fail on every single save, because the freshly decoded instance is never
/// reference-equal to the one that is open. This is the framework's own
/// documented gotcha; it is not a demo-format quirk. If your document type
/// is a mutable class, you need one of these too.
/// </para>
/// <para>
/// Keep this in sync with every field <see cref="DemoSaveDocument"/> gains.
/// A comparer that silently ignores a new field would let the round-trip
/// check pass even when that field's data was actually lost.
/// </para>
/// </remarks>
public sealed class DemoSaveDocumentComparer : IEqualityComparer<DemoSaveDocument>
{
    /// <summary>The shared instance. This comparer holds no state.</summary>
    public static readonly DemoSaveDocumentComparer Instance = new();

    /// <inheritdoc />
    public bool Equals(DemoSaveDocument? x, DemoSaveDocument? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return string.Equals(x.HeroName, y.HeroName, StringComparison.Ordinal)
            && x.Level == y.Level
            && x.HardcoreMode == y.HardcoreMode
            && string.Equals(x.Difficulty, y.Difficulty, StringComparison.Ordinal)
            && x.Gold == y.Gold
            && string.Equals(x.SaveId, y.SaveId, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public int GetHashCode(DemoSaveDocument obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var hash = default(HashCode);
        hash.Add(obj.HeroName, StringComparer.Ordinal);
        hash.Add(obj.Level);
        hash.Add(obj.HardcoreMode);
        hash.Add(obj.Difficulty, StringComparer.Ordinal);
        hash.Add(obj.Gold);
        hash.Add(obj.SaveId, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}
