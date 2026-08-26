using System.Runtime.InteropServices;

namespace SaveEditor.Ui.Io;

/// <summary>
/// Windows classification rules that decide a refusal, kept free of any open so they
/// can be exercised directly against known constants.
/// </summary>
/// <remarks>
/// A cloud placeholder cannot be fabricated in a test and a mapped network drive cannot
/// be assumed to exist on a build agent, so the rules that key off a reparse tag and a
/// drive type live here as pure functions and are unit-tested against the constants
/// themselves. The callers that actually open handles stay in <see cref="WindowsSafeOpen"/>.
/// </remarks>
internal static class WindowsPathFacts
{
    /// <summary>
    /// The bit Windows sets on a reparse tag whose target replaces the name in the
    /// namespace, as tested by the SDK's <c>IsReparseTagNameSurrogate</c> macro.
    /// </summary>
    internal const uint ReparseTagNameSurrogateBit = 0x2000_0000;

    /// <summary><c>IO_REPARSE_TAG_MOUNT_POINT</c> — junctions and volume mount points.</summary>
    internal const uint ReparseTagMountPoint = 0xA000_0003;

    /// <summary><c>IO_REPARSE_TAG_SYMLINK</c>.</summary>
    internal const uint ReparseTagSymlink = 0xA000_000C;

    /// <summary><c>IO_REPARSE_TAG_CLOUD</c> — a OneDrive-class placeholder.</summary>
    internal const uint ReparseTagCloud = 0x9000_001A;

    /// <summary><c>IO_REPARSE_TAG_CLOUD_1</c>, the tag family's second member.</summary>
    internal const uint ReparseTagCloud1 = 0x9000_101A;

    /// <summary><c>IO_REPARSE_TAG_DEDUP</c> — data-deduplication backed content.</summary>
    internal const uint ReparseTagDedup = 0x8000_0013;

    /// <summary><c>IO_REPARSE_TAG_WOF</c> — Windows Overlay Filter compression.</summary>
    internal const uint ReparseTagWofCompression = 0x8000_0017;

    /// <summary><c>IO_REPARSE_TAG_APPEXECLINK</c> — an execution alias, not a namespace redirect.</summary>
    internal const uint ReparseTagAppExecLink = 0x8000_001B;

    /// <summary><c>DRIVE_UNKNOWN</c>.</summary>
    internal const uint DriveUnknown = 0;

    /// <summary><c>DRIVE_NO_ROOT_DIR</c>.</summary>
    internal const uint DriveNoRootDir = 1;

    /// <summary><c>DRIVE_REMOVABLE</c>.</summary>
    internal const uint DriveRemovable = 2;

    /// <summary><c>DRIVE_FIXED</c>.</summary>
    internal const uint DriveFixed = 3;

    /// <summary><c>DRIVE_REMOTE</c> — a mapped network drive or a UNC root.</summary>
    internal const uint DriveRemote = 4;

    /// <summary><c>DRIVE_CDROM</c>.</summary>
    internal const uint DriveCdrom = 5;

    /// <summary><c>DRIVE_RAMDISK</c>.</summary>
    internal const uint DriveRamdisk = 6;

    /// <summary>
    /// Reports whether a reparse tag redirects the namespace, which is the property the
    /// resolver refuses on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Symbolic links and mount points carry the name-surrogate bit: the name they
    /// occupy stands in for a different object somewhere else, which is exactly how a
    /// planted component redirects a write. Cloud placeholders, deduplicated content,
    /// and WOF-compressed files do not carry it — they name the same file and only
    /// change how its bytes are stored or fetched, so refusing them buys nothing and
    /// costs the user every save under a synced Documents folder.
    /// </para>
    /// </remarks>
    internal static bool IsNamespaceRedirectingReparseTag(uint reparseTag) =>
        (reparseTag & ReparseTagNameSurrogateBit) != 0;

    /// <summary>Reports whether a drive type carries the exposure that gates non-local paths.</summary>
    /// <remarks>
    /// A drive letter mapped to an SMB share produces the same outbound connection and
    /// the same NTLM authentication attempt as the UNC path it stands for, so it is
    /// treated identically. Removable and CD-ROM volumes are local: they are covered by
    /// the local-unprivileged-writer adversary, not by the non-local opt-in.
    /// </remarks>
    internal static bool IsRemoteDriveType(uint driveType) => driveType == DriveRemote;

    /// <summary>
    /// Reads the drive type of a path root, or <see cref="DriveUnknown"/> off Windows or
    /// on failure.
    /// </summary>
    /// <remarks>
    /// This is a lookup against the local drive table. It reports the type of a mapping
    /// that already exists and does not itself establish one, so it is safe to call
    /// before the non-local decision has been made.
    /// </remarks>
    internal static uint GetDriveType(string pathRoot)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(pathRoot))
        {
            return DriveUnknown;
        }

        try
        {
            return GetDriveTypeW(pathRoot);
        }
        catch (Exception)
        {
            return DriveUnknown;
        }
    }

#pragma warning disable SYSLIB1054 // DllImport keeps this file free of the AllowUnsafeBlocks requirement that LibraryImport introduces.

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern uint GetDriveTypeW(string lpRootPathName);

#pragma warning restore SYSLIB1054
}
