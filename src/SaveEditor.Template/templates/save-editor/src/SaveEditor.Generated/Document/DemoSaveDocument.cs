namespace SaveEditor.Generated.Document;

// ============================================================================
// REPLACE ME FIRST.
//
// This is the in-memory shape of the fake "demo save" format the template
// ships so it has something to open, edit, and write on day one. It is not
// modelled on any real game. Your first act as a new editor author should be
// deleting this type (and Codecs/DemoSaveCodec.cs, Codecs/DemoSaveDetector.cs,
// and Sections/DemoSectionFactory.cs alongside it) and replacing it with your
// own document type and your own codec for the format you are actually
// editing. See README.md, "Replacing the demo format".
// ============================================================================

/// <summary>An obviously fake save document, entirely in memory.</summary>
/// <remarks>
/// Mutable and plain, on purpose: the framework's field descriptors read and
/// write through <c>Func</c>/<c>Action</c> accessors, so nothing about this
/// type needs to implement an interface, inherit from a base class, or use
/// <c>CommunityToolkit.Mvvm</c>. Whatever type your own save format decodes
/// into can be substituted here directly.
/// </remarks>
public sealed class DemoSaveDocument
{
    /// <summary>A player-editable name.</summary>
    public string HeroName { get; set; } = "Adventurer";

    /// <summary>A player-editable level, demonstrating a numeric field with a spinner.</summary>
    public long Level { get; set; } = 1;

    /// <summary>A player-editable flag, demonstrating a boolean field.</summary>
    public bool HardcoreMode { get; set; }

    /// <summary>A player-editable choice, demonstrating a choice field.</summary>
    public string Difficulty { get; set; } = "Normal";

    /// <summary>
    /// A player-editable currency value, demonstrating a field with a caution
    /// shown alongside it rather than blocking the edit outright.
    /// </summary>
    public long Gold { get; set; } = 350;

    /// <summary>An identifier the save was created with, demonstrating a read-only field.</summary>
    public string SaveId { get; init; } = Guid.NewGuid().ToString("N")[..8];
}
