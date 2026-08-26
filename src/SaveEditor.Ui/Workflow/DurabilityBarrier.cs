using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Workflow;

/// <summary>Whether the destination was replaced atomically.</summary>
public enum ReplaceStatus
{
    /// <summary>The destination now names the new bytes, in one indivisible step.</summary>
    Replaced,

    /// <summary>
    /// The destination cannot be replaced atomically here.
    /// </summary>
    /// <remarks>
    /// This is a terminal answer, not a prompt to try something else. There is no
    /// delete-then-move fallback: a filesystem or sharing mode that cannot support atomic
    /// replacement aborts the save with a message naming the limitation, because the
    /// fallback would open a window in which the save exists nowhere at all.
    /// </remarks>
    NotAtomic,

    /// <summary>The replace was attempted atomically and failed.</summary>
    Failed,
}

/// <summary>The result of an atomic replace attempt.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Detail">Framework-authored explanation, naming the limitation when there is one.</param>
public readonly record struct ReplaceResult(ReplaceStatus Status, string Detail);

/// <summary>Whether the containing directory entry was made durable.</summary>
public enum DirectoryFlushStatus
{
    /// <summary>The directory was fsync'd after the rename.</summary>
    Flushed,

    /// <summary>The platform has no equivalent operation and does not need one.</summary>
    NotApplicable,

    /// <summary>The flush was attempted and failed.</summary>
    Failed,
}

/// <summary>The result of flushing the directory containing a replaced file.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Detail">Framework-authored explanation.</param>
public readonly record struct DirectoryFlushResult(DirectoryFlushStatus Status, string Detail);

/// <summary>
/// The three durability operations the save workflow performs, in the order it performs
/// them (<c>PLAN.md</c> §7 step 8, finding B2).
/// </summary>
/// <remarks>
/// <para>
/// The ordering is the point: flush and fsync the temporary file, <em>then</em> replace,
/// <em>then</em> fsync the containing directory. Skipping the last step can lose the
/// rename itself on power failure — the new file is durable and the directory entry
/// naming it is not.
/// </para>
/// <para>
/// This is an interface so the ordering is observable in a test without a power failure,
/// and so an unsupported-replacement platform can be simulated. It is not an extension
/// point for supplying a non-atomic replacement: the workflow treats
/// <see cref="ReplaceStatus.NotAtomic"/> as a definitive failure.
/// </para>
/// </remarks>
public interface IDurabilityBarrier
{
    /// <summary>Flushes user-space buffers and then fsyncs the file itself.</summary>
    /// <param name="stream">The temporary file, still open.</param>
    /// <param name="cancellationToken">Cancels the flush.</param>
    ValueTask FlushFileAsync(FileStream stream, CancellationToken cancellationToken = default);

    /// <summary>Replaces the destination with the temporary file, atomically or not at all.</summary>
    /// <param name="temporaryPath">The fully written, fsync'd temporary file.</param>
    /// <param name="temporaryIdentity">
    /// Identity recorded when the temporary file was exclusively created, re-asserted before
    /// the rename so that a temporary path swapped in between is refused rather than renamed
    /// over the save.
    /// <para>
    /// <strong>The two platforms are not equally strong here, and the difference is stated
    /// rather than left as a platform detail (finding F-7).</strong> Windows renames the
    /// <em>handle</em>: the temporary file is re-opened, its identity compared, and the rename
    /// issued against that same handle, so the object checked and the object renamed are the
    /// same object by construction. Linux has no rename-by-descriptor, so the identity is
    /// re-read from the path and <c>rename(2)</c> then acts on that name — two operations,
    /// with a window between them that is narrowed to a few instructions but not closed.
    /// Both refuse an identity mismatch; only Windows can prove there was no swap.
    /// </para>
    /// </param>
    /// <param name="destinationPath">Where the bytes belong.</param>
    /// <param name="destinationExists">
    /// Whether the destination was observed to exist. When <see langword="false"/> the
    /// replace refuses to clobber an entry that appeared in the meantime.
    /// </param>
    /// <param name="cancellationToken">Cancels the replace.</param>
    /// <returns>The result.</returns>
    ValueTask<ReplaceResult> ReplaceAsync(
        string temporaryPath,
        FileIdentity temporaryIdentity,
        string destinationPath,
        bool destinationExists,
        CancellationToken cancellationToken = default);

    /// <summary>Fsyncs the directory containing a replaced file.</summary>
    /// <param name="directoryPath">The containing directory.</param>
    /// <param name="cancellationToken">Cancels the flush.</param>
    /// <returns>The result.</returns>
    ValueTask<DirectoryFlushResult> FlushDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The framework's durability barrier for Windows and Linux.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Windows replaces an existing file with a POSIX-semantics rename</strong> —
/// <c>SetFileInformationByHandle</c> with <c>FileRenameInfoEx</c> and
/// <c>FILE_RENAME_FLAG_POSIX_SEMANTICS | FILE_RENAME_FLAG_REPLACE_IF_EXISTS</c> — rather
/// than with <c>MoveFileEx</c> or <c>ReplaceFile</c>. This is not a stylistic choice. The
/// workflow holds the original open with write sharing denied, which is what closes the
/// external-change window on this platform, and neither of the other two APIs can replace
/// a file that anybody holds open: <c>MoveFileEx</c> with
/// <c>MOVEFILE_REPLACE_EXISTING</c> fails with <c>ERROR_ACCESS_DENIED</c> against a
/// destination held open even with <c>FILE_SHARE_DELETE</c>, and <c>ReplaceFile</c> needs
/// write access the framework's own handle denies. Choosing either would have forced the
/// original's handle to be released before the replace, reopening exactly the window step
/// 9 exists to close. POSIX-semantics rename supersedes a destination whose other handles
/// permit delete sharing, which is precisely how the resolver opens it, and unlinks the
/// old object the way <c>rename(2)</c> does.
/// </para>
/// <para>
/// The rename needs a handle to the temporary file carrying <c>DELETE</c> access, which
/// the exclusive-create handle does not have, so it is re-opened once by path with
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> and its file identity is compared against the
/// identity recorded at creation before anything is renamed. A mismatch aborts. Creating a
/// file that is not supposed to exist yet still uses <c>MoveFileExW</c> without
/// <c>MOVEFILE_REPLACE_EXISTING</c>, so an entry that appeared in the meantime is refused
/// rather than clobbered. A cross-volume move returns <c>ERROR_NOT_SAME_DEVICE</c> and is
/// reported as non-atomic rather than being retried with <c>MOVEFILE_COPY_ALLOWED</c>.
/// </para>
/// <para>
/// <strong>Linux</strong> replaces with <c>rename(2)</c>, or with
/// <c>link(2)</c>+<c>unlink(2)</c> when the destination is not supposed to exist, so that
/// an entry appearing in between is refused with <c>EEXIST</c> instead of clobbered.
/// <c>EXDEV</c> is reported as non-atomic. The temporary file's identity is re-read with
/// <c>statx</c> and <c>AT_SYMLINK_NOFOLLOW</c> immediately beforehand, which narrows the
/// race on the temporary name without closing it — see the remarks on
/// <see cref="IDurabilityBarrier.ReplaceAsync"/> for why Windows can close it and Linux
/// cannot.
/// </para>
/// <para>
/// <strong>The directory flush is Linux-only</strong> and says so rather than pretending.
/// Windows offers no directory handle to fsync; the rename is an NTFS metadata change
/// carried by the volume journal, and the file's own contents were fsync'd before it.
/// </para>
/// </remarks>
public sealed class PlatformDurabilityBarrier : IDurabilityBarrier
{
    private const uint MoveFileWriteThrough = 0x0000_0008;

    private const uint Delete = 0x0001_0000;
    private const uint GenericRead = 0x8000_0000;
    private const uint FileReadAttributes = 0x0000_0080;
    private const uint FileShareRead = 0x0000_0001;
    private const uint FileShareWrite = 0x0000_0002;
    private const uint FileShareDelete = 0x0000_0004;
    private const uint DispositionOpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;

    private const int FileRenameInfoExClass = 22;
    private const uint RenameReplaceIfExists = 0x0000_0001;
    private const uint RenamePosixSemantics = 0x0000_0002;

    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorNotSameDevice = 17;
    private const int ErrorSharingViolation = 32;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    private const int O_RDONLY = 0x0;
    private const int O_DIRECTORY = 0x10000;
    private const int O_CLOEXEC = 0x80000;

    private const int EEXIST = 17;
    private const int EXDEV = 18;

    /// <summary>Creates a barrier. The type holds no state and is safe to share.</summary>
    public PlatformDurabilityBarrier()
    {
    }

    /// <inheritdoc />
    public async ValueTask FlushFileAsync(FileStream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Flush(flushToDisk: true) is fsync on Unix and FlushFileBuffers on Windows, and
        // both block for as long as the device takes.
        await Task.Run(() => stream.Flush(flushToDisk: true), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<ReplaceResult> ReplaceAsync(
        string temporaryPath,
        FileIdentity temporaryIdentity,
        string destinationPath,
        bool destinationExists,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(temporaryPath);
        ArgumentException.ThrowIfNullOrEmpty(destinationPath);

        return new ValueTask<ReplaceResult>(Task.Run(
            () => OperatingSystem.IsWindows()
                ? ReplaceOnWindows(temporaryPath, temporaryIdentity, destinationPath, destinationExists)
                : ReplaceOnUnix(temporaryPath, temporaryIdentity, destinationPath, destinationExists),
            cancellationToken));
    }

    /// <inheritdoc />
    public ValueTask<DirectoryFlushResult> FlushDirectoryAsync(
        string directoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(directoryPath);

        if (OperatingSystem.IsWindows())
        {
            return new ValueTask<DirectoryFlushResult>(new DirectoryFlushResult(
                DirectoryFlushStatus.NotApplicable,
                "Windows exposes no directory handle to flush. The rename is an NTFS metadata change carried by the volume journal, and the file itself was already flushed to disk before it."));
        }

        return new ValueTask<DirectoryFlushResult>(Task.Run(() => FlushDirectoryOnUnix(directoryPath), cancellationToken));
    }

    [SupportedOSPlatform("windows")]
    private static ReplaceResult ReplaceOnWindows(
        string temporaryPath,
        FileIdentity temporaryIdentity,
        string destinationPath,
        bool destinationExists)
    {
        if (!destinationExists)
        {
            return MoveFileExW(temporaryPath, destinationPath, MoveFileWriteThrough)
                ? new ReplaceResult(ReplaceStatus.Replaced, "Created atomically with MoveFileExW.")
                : ClassifyWindowsError(Marshal.GetLastPInvokeError(), "MoveFileExW");
        }

        using var source = CreateFileW(
            temporaryPath,
            Delete | GenericRead | FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            DispositionOpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (source.IsInvalid)
        {
            return ClassifyWindowsError(Marshal.GetLastPInvokeError(), "opening the temporary file for rename");
        }

        if (!GetFileInformationByHandle(source, out var info))
        {
            return new ReplaceResult(
                ReplaceStatus.Failed,
                $"The identity of the temporary file could not be read before renaming it (Win32 error {Marshal.GetLastPInvokeError()}).");
        }

        var reopened = new FileIdentity(
            info.VolumeSerialNumber,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);

        if (reopened != temporaryIdentity)
        {
            return new ReplaceResult(
                ReplaceStatus.Failed,
                "The temporary file was replaced between its exclusive creation and the rename. The save was abandoned rather than renaming an unknown object over the target.");
        }

        if (PosixRename(source, destinationPath, out var renameError))
        {
            return new ReplaceResult(
                ReplaceStatus.Replaced,
                "Replaced atomically with a POSIX-semantics rename, which supersedes a destination the framework itself still holds open.");
        }

        return ClassifyWindowsError(renameError, "FileRenameInfoEx with POSIX semantics");
    }

    /// <summary>Field offsets within <c>FILE_RENAME_INFO</c> for a given pointer size.</summary>
    /// <param name="RootDirectory">Offset of the <c>HANDLE RootDirectory</c> field.</param>
    /// <param name="FileNameLength">Offset of the <c>DWORD FileNameLength</c> field.</param>
    /// <param name="FileName">Offset of the <c>WCHAR FileName[]</c> array, and the header size.</param>
    /// <remarks>
    /// The <c>Flags</c> union sits at 0 and is four bytes. <c>RootDirectory</c> is a pointer,
    /// so it is aligned to the pointer size — offset 8 on 64-bit, 4 on 32-bit — and the two
    /// fields after it follow from that. Hardcoding the 64-bit values made every overwrite of
    /// an existing file on 32-bit Windows fail with <c>ERROR_INVALID_PARAMETER</c>, which
    /// <see cref="ClassifyWindowsError"/> then reported as the filesystem not supporting a
    /// POSIX-semantics rename: a confusing dead end for a layout bug (finding F-14).
    /// </remarks>
    internal readonly record struct RenameInfoLayout(int RootDirectory, int FileNameLength, int FileName)
    {
        internal static RenameInfoLayout For(int pointerSize)
        {
            var rootDirectory = pointerSize;
            var fileNameLength = rootDirectory + pointerSize;
            return new RenameInfoLayout(rootDirectory, fileNameLength, fileNameLength + sizeof(uint));
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool PosixRename(SafeFileHandle source, string destinationPath, out int error)
    {
        var layout = RenameInfoLayout.For(IntPtr.Size);

        var nameBytes = Encoding.Unicode.GetByteCount(destinationPath);
        var size = layout.FileName + nameBytes + 2;
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            for (var i = 0; i < size; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            Marshal.WriteInt32(buffer, 0, unchecked((int)(RenameReplaceIfExists | RenamePosixSemantics)));
            Marshal.WriteIntPtr(buffer, layout.RootDirectory, IntPtr.Zero);
            Marshal.WriteInt32(buffer, layout.FileNameLength, nameBytes);

            for (var i = 0; i < destinationPath.Length; i++)
            {
                Marshal.WriteInt16(buffer, layout.FileName + (i * 2), (short)destinationPath[i]);
            }

            if (SetFileInformationByHandle(source, FileRenameInfoExClass, buffer, size))
            {
                error = 0;
                return true;
            }

            error = Marshal.GetLastPInvokeError();
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ReplaceResult ClassifyWindowsError(int error, string operation)
    {
        return error switch
        {
            ErrorNotSameDevice => new ReplaceResult(
                ReplaceStatus.NotAtomic,
                "The temporary file and the destination are on different volumes, so no atomic replacement is possible. The save was abandoned rather than degraded to a delete-then-copy."),
            ErrorSharingViolation => new ReplaceResult(
                ReplaceStatus.NotAtomic,
                "The destination is held open by another process without share-delete, so it cannot be superseded atomically. The save was abandoned rather than degraded to a delete-then-move."),
            ErrorFileExists or ErrorAlreadyExists => new ReplaceResult(
                ReplaceStatus.Failed,
                "An entry appeared at the destination after it was checked, and the replace refused to overwrite it."),
            ErrorAccessDenied => new ReplaceResult(
                ReplaceStatus.NotAtomic,
                "The destination could not be superseded atomically; it is most likely held open by another process without share-delete. The save was abandoned rather than degraded to a delete-then-move."),
            ErrorInvalidParameter => new ReplaceResult(
                ReplaceStatus.NotAtomic,
                "This filesystem or Windows build does not support a POSIX-semantics rename, so a destination the framework holds open cannot be replaced atomically. The save was abandoned rather than degraded to a delete-then-move."),
            _ => new ReplaceResult(
                ReplaceStatus.Failed,
                $"The atomic replace failed during {operation} with Win32 error {error}."),
        };
    }

    [UnsupportedOSPlatform("windows")]
    private static ReplaceResult ReplaceOnUnix(
        string temporaryPath,
        FileIdentity temporaryIdentity,
        string destinationPath,
        bool destinationExists)
    {
        // Re-assert the temporary file's identity immediately before acting on its name.
        // ReplaceOnWindows has always done this by re-opening the handle; this path took only
        // paths, so a local attacker who won the race on the temp name got arbitrary content
        // renamed over the save on Linux and not on Windows (finding F-7).
        //
        // This narrows the window rather than closing it: there is no rename-by-descriptor on
        // Linux, so the check and the rename remain two operations on one name.
        var current = UnixSafeOpen.ReadIdentityOfPath(temporaryPath);
        if (current is null)
        {
            return new ReplaceResult(
                ReplaceStatus.Failed,
                "The identity of the temporary file could not be re-read before renaming it, so the replace was abandoned rather than acting on a name it could not vouch for.");
        }

        if (current.Value != temporaryIdentity)
        {
            return new ReplaceResult(
                ReplaceStatus.Failed,
                "The temporary file was replaced between its exclusive creation and the rename. The save was abandoned rather than renaming an unknown object over the target.");
        }

        var source = NullTerminated(temporaryPath);
        var destination = NullTerminated(destinationPath);

        if (!destinationExists)
        {
            // link(2) fails with EEXIST rather than clobbering, which is the behaviour a
            // "this file does not exist yet" write needs.
            if (link(source, destination) == 0)
            {
                unlink(source);
                return new ReplaceResult(ReplaceStatus.Replaced, "Created atomically with link(2).");
            }

            var linkErrno = Marshal.GetLastPInvokeError();
            return linkErrno switch
            {
                EEXIST => new ReplaceResult(
                    ReplaceStatus.Failed,
                    "An entry appeared at the destination after it was checked, and the write refused to overwrite it."),
                EXDEV => new ReplaceResult(
                    ReplaceStatus.NotAtomic,
                    "The temporary file and the destination are on different filesystems, so no atomic creation is possible. The save was abandoned rather than degraded to a copy."),
                _ => new ReplaceResult(ReplaceStatus.Failed, $"link(2) failed with errno {linkErrno}."),
            };
        }

        if (rename(source, destination) == 0)
        {
            return new ReplaceResult(ReplaceStatus.Replaced, "Replaced atomically with rename(2).");
        }

        var errno = Marshal.GetLastPInvokeError();
        return errno == EXDEV
            ? new ReplaceResult(
                ReplaceStatus.NotAtomic,
                "The temporary file and the destination are on different filesystems, so rename(2) cannot replace atomically. The save was abandoned rather than degraded to a delete-then-move.")
            : new ReplaceResult(ReplaceStatus.Failed, $"rename(2) failed with errno {errno}.");
    }

    private static DirectoryFlushResult FlushDirectoryOnUnix(string directoryPath)
    {
        var descriptor = open(NullTerminated(directoryPath), O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        if (descriptor < 0)
        {
            return new DirectoryFlushResult(
                DirectoryFlushStatus.Failed,
                $"The containing directory could not be opened for flushing (errno {Marshal.GetLastPInvokeError()}).");
        }

        try
        {
            return fsync(descriptor) == 0
                ? new DirectoryFlushResult(DirectoryFlushStatus.Flushed, "The containing directory was fsync'd after the rename.")
                : new DirectoryFlushResult(
                    DirectoryFlushStatus.Failed,
                    $"fsync(2) on the containing directory failed with errno {Marshal.GetLastPInvokeError()}.");
        }
        finally
        {
            close(descriptor);
        }
    }

    private static byte[] NullTerminated(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        bytes[^1] = 0;
        return bytes;
    }

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

#pragma warning disable SYSLIB1054 // The Io layer uses DllImport throughout; staying consistent avoids AllowUnsafeBlocks.

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string lpExistingFileName, string lpNewFileName, uint dwFlags);

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
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, IntPtr lpFileInformation, int dwBufferSize);

#pragma warning disable IDE1006 // libc entry points keep their own names.

    [DllImport("libc", SetLastError = true)]
    private static extern int rename(byte[] oldpath, byte[] newpath);

    [DllImport("libc", SetLastError = true)]
    private static extern int link(byte[] oldpath, byte[] newpath);

    [DllImport("libc", SetLastError = true)]
    private static extern int unlink(byte[] pathname);

    [DllImport("libc", SetLastError = true)]
    private static extern int open(byte[] pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

#pragma warning restore IDE1006
#pragma warning restore SYSLIB1054
}
