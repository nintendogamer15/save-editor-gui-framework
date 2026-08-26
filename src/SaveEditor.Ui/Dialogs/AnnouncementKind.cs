namespace SaveEditor.Ui.Dialogs;

/// <summary>How urgently an <see cref="AnnouncementRegion"/> message should read.</summary>
public enum AnnouncementKind
{
    /// <summary>Neutral information.</summary>
    Info,

    /// <summary>A completed, successful outcome.</summary>
    Success,

    /// <summary>A condition worth noticing that did not itself fail.</summary>
    Warning,

    /// <summary>A failure.</summary>
    Error,
}
