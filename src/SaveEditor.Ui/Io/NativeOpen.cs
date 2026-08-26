using Microsoft.Win32.SafeHandles;

namespace SaveEditor.Ui.Io;

/// <summary>
/// Facts read from an already-open handle, never from a path string.
/// </summary>
/// <param name="Identity">Volume and file identity.</param>
/// <param name="HardLinkCount">Number of directory entries referring to the object.</param>
/// <param name="Size">Length in bytes.</param>
internal readonly record struct NativeFileFacts(FileIdentity Identity, int HardLinkCount, long Size);

/// <summary>
/// The result of a platform open. Either a handle plus the facts read from it, or
/// a refusal. The platform layer never throws for a hostile path.
/// </summary>
internal abstract record NativeOpenOutcome
{
    private NativeOpenOutcome()
    {
    }

    /// <summary>The target was opened with link following disabled.</summary>
    internal sealed record Opened(SafeFileHandle Handle, NativeFileFacts Facts) : NativeOpenOutcome;

    /// <summary>The target was refused; no handle is produced.</summary>
    internal sealed record Refused(PathRefusalReason Reason, string Detail) : NativeOpenOutcome;

    internal static NativeOpenOutcome Refuse(PathRefusalReason reason, string detail) =>
        new Refused(reason, detail);
}
