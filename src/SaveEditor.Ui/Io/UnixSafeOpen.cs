using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SaveEditor.Ui.Io;

/// <summary>
/// Linux platform layer for <see cref="SafePathResolver"/>.
/// </summary>
/// <remarks>
/// <para>
/// Ancestors are walked with an <c>openat</c> chain rooted at <c>/</c>, each step
/// carrying <c>O_PATH | O_NOFOLLOW | O_CLOEXEC</c>. <c>O_PATH</c> is what makes a
/// symlinked component detectable rather than merely fatal: without it
/// <c>O_NOFOLLOW</c> fails with <c>ELOOP</c> and the caller cannot tell a link from
/// an unrelated error. Because each step is relative to the descriptor of the
/// component already checked, no earlier component is re-resolved by name, so the
/// ancestor walk carries no check-then-use window of its own.
/// </para>
/// <para>
/// The leaf is opened with <c>O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK</c>. The
/// <c>O_NONBLOCK</c> is the guard against a FIFO: opening one for reading otherwise
/// blocks until a writer appears, which would hang decode behind a cancel button
/// that cannot help. The file type is then read from the descriptor, not the path,
/// and anything that is not a regular file is refused.
/// </para>
/// <para>
/// Identity, hard-link count, and size come from <c>statx</c> against the open
/// descriptor. <c>statx</c> is used rather than <c>fstat</c> because its structure
/// layout is identical on every architecture, whereas glibc's <c>struct stat</c>
/// differs between x86-64 and AArch64. It requires Linux 4.11 and glibc 2.28; where
/// it is unavailable the resolver fails closed with <see cref="PathRefusalReason.Unreadable"/>.
/// </para>
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal static class UnixSafeOpen
{
    private const int O_RDONLY = 0x0;
    private const int O_RDWR = 0x2;
    private const int O_CREAT = 0x40;
    private const int O_EXCL = 0x80;
    private const int O_NONBLOCK = 0x800;
    private const int O_DIRECTORY = 0x10000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_CLOEXEC = 0x80000;
    private const int O_PATH = 0x200000;

    private const int AT_FDCWD = -100;
    private const int AT_SYMLINK_NOFOLLOW = 0x100;
    private const int AT_EMPTY_PATH = 0x1000;

    private const uint STATX_BASIC_STATS = 0x0000_07FF;

    private const ushort S_IFMT = 0xF000;
    private const ushort S_IFREG = 0x8000;
    private const ushort S_IFDIR = 0x4000;
    private const ushort S_IFLNK = 0xA000;

    private const uint CreateMode = 0b110_000_000; // 0600

    private const int EPERM = 1;
    private const int ENOENT = 2;
    private const int ENXIO = 6;
    private const int EACCES = 13;
    private const int EEXIST = 17;
    private const int ENOTDIR = 20;
    private const int EISDIR = 21;
    private const int EROFS = 30;
    private const int ENAMETOOLONG = 36;
    private const int ENOSYS = 38;
    private const int ELOOP = 40;

    /// <summary>Walks every ancestor, then opens the leaf with link following disabled.</summary>
    internal static NativeOpenOutcome Open(
        string root,
        IReadOnlyList<string> components,
        PathResolutionMode mode,
        PathResolutionOptions options)
    {
        var directory = open(Utf8(root), O_PATH | O_NOFOLLOW | O_CLOEXEC | O_DIRECTORY);
        if (directory < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.Unreadable,
                $"The volume root could not be opened (errno {errno}).");
        }

        try
        {
            for (var i = 0; i < components.Count - 1; i++)
            {
                var next = openat(directory, Utf8(components[i]), O_PATH | O_NOFOLLOW | O_CLOEXEC);
                if (next < 0)
                {
                    var errno = Marshal.GetLastPInvokeError();
                    var mapped = MapAncestorError(errno);
                    return NativeOpenOutcome.Refuse(
                        mapped.Reason,
                        $"A directory component could not be checked (errno {errno}). {mapped.Detail}");
                }

                close(directory);
                directory = next;

                var statResult = TryStatDescriptor(directory, out var stat);
                if (statResult is not null)
                {
                    return statResult;
                }

                var kind = (ushort)(stat.Mode & S_IFMT);
                if (kind == S_IFLNK)
                {
                    return NativeOpenOutcome.Refuse(
                        PathRefusalReason.LinkInAncestor,
                        "A directory component between the volume root and the leaf is a symbolic link. It redirects the write even when the leaf itself looks like a plain file.");
                }

                if (kind != S_IFDIR)
                {
                    return NativeOpenOutcome.Refuse(
                        PathRefusalReason.NotFound,
                        "A path component that must be a directory is not one.");
                }
            }

            var leaf = components[^1];

            return mode == PathResolutionMode.CreateNew
                ? CreateNewLeaf(directory, leaf)
                : OpenExistingLeaf(directory, leaf, options);
        }
        finally
        {
            if (directory >= 0)
            {
                close(directory);
            }
        }
    }

    /// <summary>Reads identity for a path, without following a final symbolic link.</summary>
    /// <param name="path">An absolute path.</param>
    /// <returns>The identity, or <see langword="null"/> if it could not be read.</returns>
    /// <remarks>
    /// <para>
    /// Exists so the Unix replace can re-assert the temporary file's identity immediately
    /// before <c>rename(2)</c>, which the Windows replace has always done and this one did
    /// not (finding F-7). <c>AT_SYMLINK_NOFOLLOW</c> means a symbolic link planted at the
    /// path yields the link's own identity, which will not match the regular file recorded at
    /// exclusive creation, so the comparison fails and the replace is abandoned.
    /// </para>
    /// <para>
    /// <strong>This narrows the window; it does not close it.</strong> Linux offers no
    /// rename-by-descriptor, so the check and the rename are two operations on the same name
    /// and something can still be swapped between them. Windows renames the handle itself, so
    /// there the checked object and the renamed object are the same object by construction.
    /// The asymmetry is real and is stated rather than glossed.
    /// </para>
    /// </remarks>
    internal static FileIdentity? ReadIdentityOfPath(string path) =>
        StatxAt(AT_FDCWD, path, out var stat) ? IdentityOf(stat) : null;

    /// <summary>Re-reads identity from a handle the caller already holds.</summary>
    internal static FileIdentity? ReadIdentity(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed)
        {
            return null;
        }

        // The descriptor is reference-counted for the duration of the stat. Without this
        // a concurrent close could let the number be recycled onto an unrelated object
        // and produce a false identity match — the one answer this probe must never give.
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            if (!added)
            {
                return null;
            }

            var descriptor = (int)handle.DangerousGetHandle();
            if (descriptor < 0)
            {
                return null;
            }

            return Statx(descriptor, out var stat) ? IdentityOf(stat) : null;
        }
        finally
        {
            if (added)
            {
                handle.DangerousRelease();
            }
        }
    }

    private static NativeOpenOutcome OpenExistingLeaf(int directory, string leaf, PathResolutionOptions options)
    {
        var flags = (options.ForWriting ? O_RDWR : O_RDONLY) | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK;
        var descriptor = openat(directory, Utf8(leaf), flags);

        if (descriptor < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            var mapped = MapLeafError(errno, options.ForWriting);
            return NativeOpenOutcome.Refuse(
                mapped.Reason,
                $"The target could not be opened (errno {errno}). {mapped.Detail}");
        }

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);

        var statResult = TryStatDescriptor(descriptor, out var stat);
        if (statResult is not null)
        {
            handle.Dispose();
            return statResult;
        }

        var kind = (ushort)(stat.Mode & S_IFMT);
        if (kind != S_IFREG)
        {
            handle.Dispose();
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.NotARegularFile,
                $"The target is not a regular file (st_mode file type 0x{kind:X4}). FIFOs, devices, sockets, and directories are refused.");
        }

        return new NativeOpenOutcome.Opened(handle, FactsOf(stat));
    }

    private static NativeOpenOutcome CreateNewLeaf(int directory, string leaf)
    {
        var flags = O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC;
        var descriptor = openat(directory, Utf8(leaf), flags, CreateMode);

        if (descriptor < 0)
        {
            var errno = Marshal.GetLastPInvokeError();

            if (errno == EEXIST)
            {
                return ClassifyPrePlantedEntry(directory, leaf);
            }

            var mapped = MapLeafError(errno, forWriting: true);
            return NativeOpenOutcome.Refuse(
                mapped.Reason,
                $"The file could not be created exclusively (errno {errno}). {mapped.Detail}");
        }

        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);

        var statResult = TryStatDescriptor(descriptor, out var stat);
        if (statResult is not null)
        {
            handle.Dispose();
            return statResult;
        }

        return new NativeOpenOutcome.Opened(handle, FactsOf(stat));
    }

    private static NativeOpenOutcome ClassifyPrePlantedEntry(int directory, string leaf)
    {
        if (StatxAt(directory, leaf, out var stat) && (stat.Mode & S_IFMT) == S_IFLNK)
        {
            return NativeOpenOutcome.Refuse(
                PathRefusalReason.LinkTarget,
                "A symbolic link already exists at the exclusive-create path. Creation is refused; it is never retried through a link-following open.");
        }

        return NativeOpenOutcome.Refuse(
            PathRefusalReason.AlreadyExists,
            "An entry already exists at the exclusive-create path. Creation is refused; it is never retried through a link-following open.");
    }

    private static NativeOpenOutcome.Refused? TryStatDescriptor(int descriptor, out StatxBuffer stat)
    {
        if (Statx(descriptor, out stat))
        {
            return null;
        }

        var errno = Marshal.GetLastPInvokeError();
        return new NativeOpenOutcome.Refused(
            PathRefusalReason.Unreadable,
            errno == ENOSYS
                ? "statx is unavailable on this kernel, so file identity cannot be recorded. The resolver fails closed rather than proceeding without identity."
                : $"The identity of the target could not be read (errno {errno}).");
    }

    private static bool Statx(int descriptor, out StatxBuffer stat)
    {
        try
        {
            return statx(descriptor, Utf8(string.Empty), AT_EMPTY_PATH | AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, out stat) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            stat = default;
            return false;
        }
        catch (DllNotFoundException)
        {
            stat = default;
            return false;
        }
    }

    private static bool StatxAt(int directory, string name, out StatxBuffer stat)
    {
        try
        {
            return statx(directory, Utf8(name), AT_SYMLINK_NOFOLLOW, STATX_BASIC_STATS, out stat) == 0;
        }
        catch (EntryPointNotFoundException)
        {
            stat = default;
            return false;
        }
        catch (DllNotFoundException)
        {
            stat = default;
            return false;
        }
    }

    private static NativeFileFacts FactsOf(StatxBuffer stat)
    {
        var links = stat.Nlink > int.MaxValue ? int.MaxValue : (int)stat.Nlink;
        var size = stat.Size > (ulong)long.MaxValue ? long.MaxValue : (long)stat.Size;
        return new NativeFileFacts(IdentityOf(stat), links, size);
    }

    private static FileIdentity IdentityOf(StatxBuffer stat) =>
        new(((ulong)stat.DevMajor << 32) | stat.DevMinor, stat.Ino);

    private static (PathRefusalReason Reason, string Detail) MapAncestorError(int errno) => errno switch
    {
        ENOENT or ENOTDIR => (PathRefusalReason.NotFound, "The path, or a component of it, does not exist."),
        ELOOP => (PathRefusalReason.LinkInAncestor, "A directory component is a symbolic link."),
        EACCES or EPERM => (PathRefusalReason.Unreadable, "A directory component could not be traversed."),
        ENAMETOOLONG => (PathRefusalReason.InvalidPath, "A path component is too long."),
        _ => (PathRefusalReason.Unreadable, "A directory component could not be checked."),
    };

    private static (PathRefusalReason Reason, string Detail) MapLeafError(int errno, bool forWriting) => errno switch
    {
        ENOENT or ENOTDIR => (PathRefusalReason.NotFound, "The path, or a component of it, does not exist."),
        ELOOP => (PathRefusalReason.LinkTarget, "The final path component is a symbolic link. It is refused, not followed."),
        EISDIR => (PathRefusalReason.NotARegularFile, "The final path component is a directory."),
        ENXIO => (PathRefusalReason.NotARegularFile, "The target is a FIFO with no reader, a socket, or a device with no backing. Non-regular files are refused."),
        EROFS => (PathRefusalReason.WriteProtected, "The filesystem is mounted read-only."),
        EACCES when forWriting => (PathRefusalReason.WriteProtected, "Write access was denied. No mode bit is changed and no elevation is attempted."),
        EACCES or EPERM => (PathRefusalReason.Unreadable, "Access was denied."),
        ENAMETOOLONG => (PathRefusalReason.InvalidPath, "The path is too long."),
        _ => (PathRefusalReason.Unreadable, "The target exists but could not be opened for the requested access."),
    };

    private static byte[] Utf8(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        bytes[^1] = 0;
        return bytes;
    }

#pragma warning disable SYSLIB1054 // DllImport keeps this file free of the AllowUnsafeBlocks requirement that LibraryImport introduces.
#pragma warning disable CA1401
#pragma warning disable IDE1006 // libc entry points keep their C names.

    [DllImport("libc", SetLastError = true)]
    private static extern int open(byte[] pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int openat(int dirfd, byte[] pathname, int flags);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int openat(int dirfd, byte[] pathname, int flags, uint mode);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int dirfd, byte[] pathname, int flags, uint mask, out StatxBuffer buffer);

#pragma warning restore IDE1006
#pragma warning restore CA1401
#pragma warning restore SYSLIB1054

    /// <summary>
    /// The kernel's <c>struct statx</c>. Only the fields the resolver uses are named;
    /// the offsets are fixed by the kernel ABI and are identical on every architecture.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxBuffer
    {
        [FieldOffset(16)]
        public uint Nlink;

        [FieldOffset(28)]
        public ushort Mode;

        [FieldOffset(32)]
        public ulong Ino;

        [FieldOffset(40)]
        public ulong Size;

        [FieldOffset(136)]
        public uint DevMajor;

        [FieldOffset(140)]
        public uint DevMinor;
    }
}
