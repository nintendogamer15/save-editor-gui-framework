using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SaveEditor.Ui.Io;

/// <summary>
/// Windows platform layer for <see cref="SafePathResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// .NET exposes neither <c>FILE_FLAG_OPEN_REPARSE_POINT</c> nor the reparse tag, so
/// every open here goes through <c>CreateFileW</c> directly. Identity and hard-link
/// count come from <c>GetFileInformationByHandle</c> on the already-open handle, never
/// from a second path lookup.
/// </para>
/// <para>
/// Ancestors are checked root-first by absolute path. The kernel resolves the earlier
/// components of each of those opens, but every earlier component has already been
/// proven not to be a reparse point by the time it is traversed. The residual is the
/// narrow window in which a component is swapped between its check and its later
/// traversal; Windows has no <c>openat</c> equivalent short of <c>NtCreateFile</c>
/// relative opens, so this is narrowed rather than closed, consistent with the
/// FIX (narrow) disposition of finding A5.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsSafeOpen
{
    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint FileReadAttributes = 0x0000_0080;

    private const uint FileShareRead = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint FileShareDelete = 0x0000_0004;
    private const uint FileShareAll = FileShareRead | FileShareWrite | FileShareDelete;

    private const uint DispositionCreateNew = 1;
    private const uint DispositionOpenExisting = 3;

    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;

    private const uint FileAttributeReadonly = 0x0000_0001;
    private const uint FileAttributeDirectory = 0x0000_0010;
    private const uint FileAttributeReparsePoint = 0x0000_0400;

    private const int FileAttributeTagInfoClass = 9;
    private const uint FileTypeDisk = 1;

    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotReady = 21;
    private const int ErrorSharingViolation = 32;
    private const int ErrorFileExists = 80;
    private const int ErrorInvalidName = 123;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorBadPathname = 161;
    private const int ErrorDirectory = 267;
    private const int ErrorCantAccessFile = 1920;

    /// <summary>Walks every ancestor, then opens the leaf with link following disabled.</summary>
    internal static NativeOpenOutcome Open(
        string root,
        IReadOnlyList<string> components,
        PathResolutionMode mode,
        PathResolutionOptions options)
    {
        for (var i = 0; i < components.Count - 1; i++)
        {
            var ancestor = PathSyntaxGuard.BuildCanonicalPath(root, components, i + 1);
            var refusal = CheckAncestor(ancestor);
            if (refusal is not null)
            {
                return refusal;
            }
        }

        var leaf = PathSyntaxGuard.BuildCanonicalPath(root, components, components.Count);

        return mode == PathResolutionMode.CreateNew
            ? CreateNewLeaf(leaf)
            : OpenExistingLeaf(leaf, options);
    }

    /// <summary>Re-reads identity from a handle the caller already holds.</summary>
    internal static FileIdentity? ReadIdentity(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed)
        {
            return null;
        }

        return GetFileInformationByHandle(handle, out var info)
            ? IdentityOf(info)
            : null;
    }

    private static NativeOpenOutcome.Refused? CheckAncestor(string ancestorPath)
    {
        using var handle = CreateFileW(
            ancestorPath,
            FileReadAttributes,
            FileShareAll,
            IntPtr.Zero,
            DispositionOpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            var mapped = MapOpenError(error, forWriting: false);
            return new NativeOpenOutcome.Refused(
                mapped.Reason,
                $"The directory component could not be checked (Win32 error {error}). {mapped.Detail}");
        }

        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out var tagInfo, 8))
        {
            var error = Marshal.GetLastPInvokeError();
            return new NativeOpenOutcome.Refused(
                PathRefusalReason.Unreadable,
                $"The attributes of a directory component could not be read (Win32 error {error}).");
        }

        if ((tagInfo.FileAttributes & FileAttributeReparsePoint) != 0 &&
            WindowsPathFacts.IsNamespaceRedirectingReparseTag(tagInfo.ReparseTag))
        {
            return new NativeOpenOutcome.Refused(
                PathRefusalReason.LinkInAncestor,
                $"A directory component between the volume root and the leaf is a name-surrogate reparse point (tag 0x{tagInfo.ReparseTag:X8}). Junctions, mount points, and directory symlinks redirect the write even when the leaf looks like a plain file.");
        }

        if ((tagInfo.FileAttributes & FileAttributeDirectory) == 0)
        {
            return new NativeOpenOutcome.Refused(
                PathRefusalReason.NotFound,
                "A path component that must be a directory is not one.");
        }

        return null;
    }

    private static NativeOpenOutcome OpenExistingLeaf(string leafPath, PathResolutionOptions options)
    {
        // Phase 1: attributes-only. This open succeeds for reparse points, directories,
        // and devices alike, so the refusal reason can be precise instead of a blanket
        // access-denied.
        using var probe = CreateFileW(
            leafPath,
            FileReadAttributes,
            FileShareAll,
            IntPtr.Zero,
            DispositionOpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (probe.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            var mapped = MapOpenError(error, options.ForWriting);
            return NativeOpenOutcome.Refuse(
                mapped.Reason,
                $"The target could not be opened (Win32 error {error}). {mapped.Detail}");
        }

        if (!GetFileInformationByHandleEx(probe, FileAttributeTagInfoClass, out var tagInfo, 8))
        {
            var error = Marshal.GetLastPInvokeError();
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.Unreadable,
                $"The attributes of the target could not be read (Win32 error {error}).");
        }

        var isReparsePoint = (tagInfo.FileAttributes & FileAttributeReparsePoint) != 0;

        if (isReparsePoint && WindowsPathFacts.IsNamespaceRedirectingReparseTag(tagInfo.ReparseTag))
        {
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.LinkTarget,
                $"The final path component is a name-surrogate reparse point (tag 0x{tagInfo.ReparseTag:X8}). Symbolic links, junctions, and mount points are refused rather than followed.");
        }

        if ((tagInfo.FileAttributes & FileAttributeDirectory) != 0)
        {
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.NotARegularFile,
                "The final path component is a directory, not a regular file.");
        }

        if (options.ForWriting && (tagInfo.FileAttributes & FileAttributeReadonly) != 0)
        {
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.WriteProtected,
                "The target carries the read-only attribute. It is reported, never cleared.");
        }

        if (!GetFileInformationByHandle(probe, out var probeInfo))
        {
            var error = Marshal.GetLastPInvokeError();
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.Unreadable,
                $"The identity of the target could not be read (Win32 error {error}).");
        }

        // Phase 2: the handle that is actually retained.
        var access = GenericRead | FileReadAttributes;
        if (options.ForWriting)
        {
            access |= GenericWrite;
        }

        // A non-surrogate reparse point — a cloud placeholder, a deduplicated or
        // WOF-compressed file — must be opened *through* its filter, not as the
        // placeholder itself, or the retained handle would read raw reparse data instead
        // of the file's bytes. Dropping the flag cannot silently follow a link: phase 1
        // already established this tag does not redirect the namespace, and the identity
        // comparison below refuses if the entry changed between the two opens.
        var retainedFlags = isReparsePoint ? 0u : FileFlagOpenReparsePoint;

        var handle = CreateFileW(
            leafPath,
            access,
            FileShareRead | FileShareDelete,
            IntPtr.Zero,
            DispositionOpenExisting,
            retainedFlags,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            var mapped = MapOpenError(error, options.ForWriting);
            return NativeOpenOutcome.Refuse(
                mapped.Reason,
                $"The target could not be opened for the requested access (Win32 error {error}). {mapped.Detail}");
        }

        return Finish(handle, probeInfo, leafPath, allowNonSurrogateReparsePoint: isReparsePoint);
    }

    private static NativeOpenOutcome CreateNewLeaf(string leafPath)
    {
        var handle = CreateFileW(
            leafPath,
            GenericRead | GenericWrite | FileReadAttributes,
            FileShareRead | FileShareDelete,
            IntPtr.Zero,
            DispositionCreateNew,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();

            if (error is ErrorFileExists or ErrorAlreadyExists)
            {
                return ClassifyPrePlantedEntry(leafPath, error);
            }

            var mapped = MapOpenError(error, forWriting: true);
            return NativeOpenOutcome.Refuse(
                mapped.Reason,
                $"The file could not be created exclusively (Win32 error {error}). {mapped.Detail}");
        }

        if (!GetFileInformationByHandle(handle, out var info))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.Unreadable,
                $"The identity of the created file could not be read (Win32 error {error}).");
        }

        return Finish(handle, info, leafPath, allowNonSurrogateReparsePoint: false);
    }

    private static NativeOpenOutcome ClassifyPrePlantedEntry(string leafPath, int originalError)
    {
        using var probe = CreateFileW(
            leafPath,
            FileReadAttributes,
            FileShareAll,
            IntPtr.Zero,
            DispositionOpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (!probe.IsInvalid &&
            GetFileInformationByHandleEx(probe, FileAttributeTagInfoClass, out var tagInfo, 8) &&
            (tagInfo.FileAttributes & FileAttributeReparsePoint) != 0 &&
            WindowsPathFacts.IsNamespaceRedirectingReparseTag(tagInfo.ReparseTag))
        {
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.LinkTarget,
                $"A name-surrogate reparse point (tag 0x{tagInfo.ReparseTag:X8}) already exists at the exclusive-create path. Creation is refused; it is never retried through a link-following open.");
        }

        return NativeOpenOutcome.Refuse(
            PathRefusalReason.AlreadyExists,
            $"An entry already exists at the exclusive-create path (Win32 error {originalError}). Creation is refused; it is never retried through a link-following open.");
    }

    private static NativeOpenOutcome Finish(
        SafeFileHandle handle,
        BY_HANDLE_FILE_INFORMATION probeInfo,
        string leafPath,
        bool allowNonSurrogateReparsePoint)
    {
        var fileType = GetFileType(handle);
        if (fileType != FileTypeDisk)
        {
            handle.Dispose();
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.NotARegularFile,
                $"The target is not a disk file (GetFileType returned {fileType.ToString(CultureInfo.InvariantCulture)}). Character devices and named pipes are refused.");
        }

        if (!GetFileInformationByHandle(handle, out var info))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.Unreadable,
                $"The identity of the retained handle could not be read (Win32 error {error}).");
        }

        if ((info.FileAttributes & FileAttributeDirectory) != 0)
        {
            handle.Dispose();
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.NotARegularFile,
                "The retained handle refers to a directory.");
        }

        // Belt and braces against a swap between the two opens. A reparse point is only
        // tolerated here when phase 1 classified it as non-surrogate; the identity
        // comparison below is what proves it is still the same object.
        if (!allowNonSurrogateReparsePoint && (info.FileAttributes & FileAttributeReparsePoint) != 0)
        {
            handle.Dispose();
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.LinkTarget,
                "The retained handle refers to a reparse point that was not present when the target was checked.");
        }

        var probeIdentity = IdentityOf(probeInfo);
        var identity = IdentityOf(info);

        if (probeIdentity != identity)
        {
            handle.Dispose();
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.Unreadable,
                $"The object at '{leafPath}' changed identity between the safety check and the retained open. The operation is abandoned rather than retried.");
        }

        var size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
        var links = info.NumberOfLinks > int.MaxValue ? int.MaxValue : (int)info.NumberOfLinks;

        return new NativeOpenOutcome.Opened(handle, new NativeFileFacts(identity, links, size));
    }

    private static FileIdentity IdentityOf(BY_HANDLE_FILE_INFORMATION info) =>
        new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);

    private static (PathRefusalReason Reason, string Detail) MapOpenError(int error, bool forWriting) => error switch
    {
        ErrorFileNotFound or ErrorPathNotFound => (PathRefusalReason.NotFound, "The path, or a component of it, does not exist."),
        ErrorAccessDenied when forWriting => (PathRefusalReason.WriteProtected, "Write access was denied. No attribute is cleared and no elevation is attempted."),
        ErrorAccessDenied => (PathRefusalReason.Unreadable, "Access was denied."),
        ErrorSharingViolation => (PathRefusalReason.Unreadable, "Another process holds the file with incompatible sharing."),
        ErrorInvalidName or ErrorBadPathname => (PathRefusalReason.InvalidPath, "The path is syntactically unusable."),
        ErrorDirectory => (PathRefusalReason.NotARegularFile, "A component is not a directory, or the target is one."),
        ErrorNotReady => (PathRefusalReason.Unreadable, "The volume is not ready."),
        ErrorCantAccessFile => (PathRefusalReason.LinkTarget, "The target is a reparse point whose contents cannot be accessed directly."),
        _ => (PathRefusalReason.Unreadable, "The target exists but could not be opened for the requested access."),
    };

#pragma warning disable SYSLIB1054 // DllImport keeps this file free of the AllowUnsafeBlocks requirement that LibraryImport introduces.

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile,
        int fileInformationClass,
        out FILE_ATTRIBUTE_TAG_INFO lpFileInformation,
        uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle hFile);

#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ATTRIBUTE_TAG_INFO
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }
}
