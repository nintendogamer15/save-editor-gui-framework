namespace SaveEditor.Ui.Io;

/// <summary>
/// Identifies a file as an object on a volume, independently of any path that
/// may currently name it.
/// </summary>
/// <remarks>
/// <para>
/// On Linux this is <c>st_dev</c> and <c>st_ino</c>; on Windows it is the volume
/// serial number and the 64-bit file index. The framework records this at
/// resolution time and re-asserts it before every destructive step, so that a
/// path swapped between the safety check and the write is detected rather than
/// followed.
/// </para>
/// <para>
/// Identity is deliberately not a path. Comparing paths cannot detect a
/// component being replaced underneath the caller.
/// </para>
/// </remarks>
/// <param name="VolumeId">Volume serial number, or <c>st_dev</c>.</param>
/// <param name="FileId">File index, or <c>st_ino</c>.</param>
public readonly record struct FileIdentity(ulong VolumeId, ulong FileId);
