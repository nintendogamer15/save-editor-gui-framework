using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.Win32.SafeHandles;
using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Workflow;

/// <summary>
/// The permission set of one file, in a form two files can be compared in.
/// </summary>
/// <param name="UnixMode">POSIX mode bits, or <see langword="null"/> off Linux.</param>
/// <param name="WindowsEffectiveRights">
/// Effective granted rights per trustee — allow mask with the deny mask removed — keyed by
/// SDDL SID string. <see langword="null"/> off Windows or when the descriptor is unreadable.
/// </param>
/// <param name="Summary">Framework-authored description, for status text and diagnostics.</param>
public sealed record PermissionSnapshot(
    UnixFileMode? UnixMode,
    IReadOnlyDictionary<string, int>? WindowsEffectiveRights,
    string Summary);

/// <summary>How completely a permission set was carried onto the temporary file.</summary>
public enum PermissionCopyStatus
{
    /// <summary>Everything the platform offers was copied.</summary>
    Copied,

    /// <summary>
    /// The load-bearing part was copied and something optional was not.
    /// </summary>
    /// <remarks>
    /// ACL and extended-attribute copying are best-effort by design; the hard gate is the
    /// widening comparison that runs afterwards, not the success of the copy.
    /// </remarks>
    PartiallyCopied,

    /// <summary>The platform offers nothing to copy.</summary>
    Unsupported,

    /// <summary>The copy failed outright.</summary>
    Failed,
}

/// <summary>The result of copying a permission set onto a temporary file.</summary>
/// <param name="Status">How completely it was copied.</param>
/// <param name="Detail">Framework-authored explanation.</param>
public readonly record struct PermissionCopyResult(PermissionCopyStatus Status, string Detail);

/// <summary>
/// Reads, copies, and compares file permission sets (<c>PLAN.md</c> §7 step 10, finding A6).
/// </summary>
/// <remarks>
/// An interface so that the workflow's widening gate can be exercised on a platform where
/// a genuinely broader permission set cannot be staged.
/// </remarks>
public interface IFilePermissionPolicy
{
    /// <summary>Reads the permission set from an open handle.</summary>
    /// <param name="stream">The open file.</param>
    /// <returns>The snapshot.</returns>
    PermissionSnapshot Capture(FileStream stream);

    /// <summary>Copies a permission set from the original onto the temporary file.</summary>
    /// <param name="original">The retained original handle.</param>
    /// <param name="target">The exclusively-created temporary file, still open.</param>
    /// <param name="targetPath">The temporary file path, used only for the Windows DACL re-open.</param>
    /// <param name="targetIdentity">
    /// Identity recorded when the temporary file was created, re-asserted before any
    /// path-named operation touches it.
    /// </param>
    /// <returns>How completely the copy succeeded.</returns>
    PermissionCopyResult CopyOnto(
        FileStream original,
        FileStream target,
        string targetPath,
        FileIdentity targetIdentity);

    /// <summary>Reports whether one permission set grants anything the other does not.</summary>
    /// <param name="candidate">What the destination would end up with.</param>
    /// <param name="original">What the destination has today.</param>
    /// <param name="detail">Framework-authored explanation of the widening, when there is one.</param>
    /// <returns><see langword="true"/> when the candidate is broader.</returns>
    bool IsBroaderThan(PermissionSnapshot candidate, PermissionSnapshot original, out string detail);
}

/// <summary>
/// The framework's permission policy for Windows and Linux.
/// </summary>
/// <remarks>
/// <para>
/// <c>rename(2)</c> gives the destination the temporary file's mode and ownership, so a
/// save that arrived as <c>0600</c> would silently come back as <c>0644</c> — the exact
/// opposite of the promise in §7 step 5 that the framework never widens what it touches.
/// The mode is therefore copied from the retained original handle onto the temporary file
/// <em>before</em> the replace, and the resulting set is compared against the original's.
/// A candidate that grants anything the original does not aborts the save.
/// </para>
/// <para>
/// <strong>ACL and extended-attribute copying is best-effort; the widening comparison is
/// the hard gate.</strong> On Linux the copy walks the extended attributes of the open
/// descriptor, which carries POSIX ACLs along with it because they are stored as
/// <c>system.posix_acl_access</c>; attributes in namespaces the process may not write are
/// skipped. On Windows the discretionary ACL is written onto the temporary file through
/// exactly one re-open of the temporary path, whose file identity is re-asserted against
/// the identity recorded at exclusive-create time before anything is written. Writing
/// through the handle the workflow already holds is not attempted at all: a descriptor
/// that was only read persists nothing, so that call was a silent no-op that reported
/// success on every save (finding F-16). A re-open that lands on a different object is
/// abandoned rather than used, so the re-open cannot become a primitive for stamping the
/// original save's descriptor onto an attacker-named file.
/// </para>
/// </remarks>
public sealed class PlatformFilePermissionPolicy : IFilePermissionPolicy
{
    private const UnixFileMode AllPermissionBits =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute |
        UnixFileMode.SetUser | UnixFileMode.SetGroup | UnixFileMode.StickyBit;

    private const int XattrNameBufferBytes = 64 * 1024;
    private const int XattrValueBufferBytes = 64 * 1024;

    /// <summary>Creates a policy. The type holds no state and is safe to share.</summary>
    public PlatformFilePermissionPolicy()
    {
    }

    /// <inheritdoc />
    public PermissionSnapshot Capture(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (OperatingSystem.IsWindows())
        {
            return CaptureWindows(stream);
        }

        return CaptureUnix(stream);
    }

    /// <inheritdoc />
    public PermissionCopyResult CopyOnto(
        FileStream original,
        FileStream target,
        string targetPath,
        FileIdentity targetIdentity)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(targetPath);

        if (OperatingSystem.IsWindows())
        {
            return CopyWindows(original, targetPath, targetIdentity);
        }

        return CopyUnix(original, target);
    }

    /// <inheritdoc />
    public bool IsBroaderThan(PermissionSnapshot candidate, PermissionSnapshot original, out string detail)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(original);

        // Fail closed when nothing at all could be read about the candidate. Both
        // dimensions null is precisely what Capture returns when the mode or the
        // descriptor was unreadable, and it used to fall through to "the replacement
        // grants nothing the original does not" — the hard widening gate quietly
        // disappearing at the one moment nothing is known about what the destination
        // would end up with (finding F-16). Only the candidate side needs this: Capture
        // and CopyOnto read the original through the same call on the same handle, so an
        // unreadable original has already aborted the save with
        // PermissionCopyStatus.Failed before this comparison is reached.
        if (candidate.UnixMode is null && candidate.WindowsEffectiveRights is null)
        {
            detail =
                $"The permission set the destination would end up with could not be read ({candidate.Summary}), " +
                $"so it cannot be shown to grant nothing beyond the original's ({original.Summary}). " +
                "The save was refused rather than proceeding past a comparison that cannot be made.";
            return true;
        }

        if (candidate.UnixMode is { } candidateMode && original.UnixMode is { } originalMode)
        {
            var widened = (candidateMode & AllPermissionBits) & ~(originalMode & AllPermissionBits);
            if (widened != 0)
            {
                detail =
                    $"Replacing would set mode {Format(candidateMode)} on a file that is {Format(originalMode)}, " +
                    $"granting {Format(widened)} that it does not have today.";
                return true;
            }
        }

        if (candidate.WindowsEffectiveRights is { } candidateRights && original.WindowsEffectiveRights is { } originalRights)
        {
            foreach (var (sid, rights) in candidateRights)
            {
                var granted = originalRights.TryGetValue(sid, out var existing) ? existing : 0;
                var extra = rights & ~granted;
                if (extra != 0)
                {
                    detail =
                        $"Replacing would grant rights 0x{extra:X8} to {sid}, which the file does not grant today.";
                    return true;
                }
            }
        }

        detail = "The replacement grants nothing the original does not.";
        return false;
    }

    private static string Format(UnixFileMode mode) => $"0{Convert.ToString((int)(mode & AllPermissionBits), 8)}";

    [UnsupportedOSPlatform("windows")]
    private static PermissionSnapshot CaptureUnix(FileStream stream)
    {
        try
        {
            var mode = File.GetUnixFileMode(stream.SafeFileHandle);
            return new PermissionSnapshot(mode, null, $"mode {Format(mode)}");
        }
        catch (Exception ex)
        {
            return new PermissionSnapshot(null, null, $"mode unreadable ({ex.GetType().Name})");
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static PermissionCopyResult CopyUnix(FileStream original, FileStream target)
    {
        UnixFileMode mode;
        try
        {
            mode = File.GetUnixFileMode(original.SafeFileHandle);
        }
        catch (Exception ex)
        {
            return new PermissionCopyResult(
                PermissionCopyStatus.Failed,
                $"The mode of the original could not be read ({ex.GetType().Name}: {ex.Message}).");
        }

        try
        {
            File.SetUnixFileMode(target.SafeFileHandle, mode);
        }
        catch (Exception ex)
        {
            return new PermissionCopyResult(
                PermissionCopyStatus.Failed,
                $"The mode of the original could not be applied to the temporary file ({ex.GetType().Name}: {ex.Message}).");
        }

        var skipped = CopyExtendedAttributes(original.SafeFileHandle, target.SafeFileHandle);

        return skipped == 0
            ? new PermissionCopyResult(PermissionCopyStatus.Copied, $"Mode {Format(mode)} and all extended attributes were carried over.")
            : new PermissionCopyResult(
                PermissionCopyStatus.PartiallyCopied,
                $"Mode {Format(mode)} was carried over; {skipped} extended attribute(s) could not be written and were skipped.");
    }

    /// <summary>Copies extended attributes, which carry POSIX ACLs with them.</summary>
    /// <returns>How many attributes could not be written.</returns>
    [UnsupportedOSPlatform("windows")]
    private static int CopyExtendedAttributes(SafeFileHandle source, SafeFileHandle destination)
    {
        var sourceDescriptor = DescriptorOf(source);
        var destinationDescriptor = DescriptorOf(destination);
        if (sourceDescriptor < 0 || destinationDescriptor < 0)
        {
            return 0;
        }

        var names = new byte[XattrNameBufferBytes];
        long listed;
        try
        {
            listed = flistxattr(sourceDescriptor, names, (nint)names.Length);
        }
        catch (Exception)
        {
            // No xattr support in this libc or on this filesystem. Best-effort means
            // best-effort: the mode copy above is what the widening gate depends on.
            return 0;
        }

        if (listed <= 0)
        {
            return 0;
        }

        var skipped = 0;
        var value = new byte[XattrValueBufferBytes];
        var start = 0;

        for (var i = 0; i < listed; i++)
        {
            if (names[i] != 0)
            {
                continue;
            }

            var length = i - start;
            if (length > 0)
            {
                var name = new byte[length + 1];
                Array.Copy(names, start, name, 0, length);

                var size = fgetxattr(sourceDescriptor, name, value, (nint)value.Length);
                if (size < 0)
                {
                    skipped++;
                }
                else if (fsetxattr(destinationDescriptor, name, value, (nint)size, 0) != 0)
                {
                    skipped++;
                }
            }

            start = i + 1;
        }

        return skipped;
    }

    [UnsupportedOSPlatform("windows")]
    private static int DescriptorOf(SafeFileHandle handle)
    {
        if (handle.IsInvalid || handle.IsClosed)
        {
            return -1;
        }

        var descriptor = (int)handle.DangerousGetHandle();
        return descriptor < 0 ? -1 : descriptor;
    }

    [SupportedOSPlatform("windows")]
    private static PermissionSnapshot CaptureWindows(FileStream stream)
    {
        try
        {
            var security = stream.GetAccessControl();
            return new PermissionSnapshot(null, EffectiveRights(security), "discretionary ACL");
        }
        catch (Exception ex)
        {
            return new PermissionSnapshot(null, null, $"discretionary ACL unreadable ({ex.GetType().Name})");
        }
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, int> EffectiveRights(FileSecurity security)
    {
        var allow = new Dictionary<string, int>(StringComparer.Ordinal);
        var deny = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var sid = rule.IdentityReference.Value;
            var bucket = rule.AccessControlType == AccessControlType.Deny ? deny : allow;
            bucket[sid] = (bucket.TryGetValue(sid, out var existing) ? existing : 0) | (int)rule.FileSystemRights;
        }

        var effective = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (sid, rights) in allow)
        {
            var denied = deny.TryGetValue(sid, out var value) ? value : 0;
            var granted = rights & ~denied;
            if (granted != 0)
            {
                effective[sid] = granted;
            }
        }

        return effective;
    }

    [SupportedOSPlatform("windows")]
    private static PermissionCopyResult CopyWindows(
        FileStream original,
        string targetPath,
        FileIdentity targetIdentity)
    {
        FileSecurity security;
        try
        {
            security = original.GetAccessControl();
        }
        catch (Exception ex)
        {
            return new PermissionCopyResult(
                PermissionCopyStatus.Failed,
                $"The discretionary ACL of the original could not be read ({ex.GetType().Name}: {ex.Message}).");
        }

        // There is deliberately no first attempt through the retained temporary handle.
        // Finding F-16: a FileSecurity that has only been read has none of its
        // AccessRulesModified / AuditRulesModified / OwnerModified / GroupModified flags
        // set, so FileSystemSecurity.Persist computes an empty SECURITY_INFORMATION and
        // NativeObjectSecurity.Persist returns before it ever reaches SetSecurityInfo. The
        // target.SetAccessControl(security) that used to stand here therefore wrote
        // nothing, threw nothing, and reported Copied on every single save. Forcing the
        // descriptor to count as modified does not rescue that route either: the handle the
        // exclusive create returned carries data access, which on Windows does not include
        // WRITE_DAC, and the same call then fails with UnauthorizedAccessException. Keeping
        // it as a first tier would only raise and swallow one exception per save for a
        // route that cannot succeed, so the identity-checked re-open is the only route.
        return CopyWindowsThroughReopen(security, targetPath, targetIdentity);
    }

    /// <summary>
    /// Writes the original's discretionary ACL onto the temporary file through one
    /// identity-checked re-open of its path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The access mask is <c>FILE_READ_ATTRIBUTES | READ_CONTROL | WRITE_DAC</c> and
    /// nothing else, and the share mode allows read, write and delete. Only data, execute
    /// and delete rights take part in the pairwise sharing check Windows runs against every
    /// other open handle to the same file, so a mask built solely from attribute and
    /// security rights is share-neutral and opens alongside the exclusive-create handle the
    /// workflow is still holding; asking for <c>GENERIC_READ | GENERIC_WRITE</c> as well
    /// fails that check with <c>ERROR_SHARING_VIOLATION</c> (32) instead.
    /// <see cref="WindowsSafeOpen"/> already depends on the same property for its ancestor
    /// attribute probes.
    /// </para>
    /// <para>
    /// The descriptor is written with <c>SetKernelObjectSecurity</c> rather than through a
    /// <see cref="FileStream"/>, because a <c>WRITE_DAC</c>-only handle has no data access
    /// for a stream to wrap and because <see cref="FileSystemSecurity"/> decides for itself
    /// which sections to persist — the exact mechanism behind finding F-16.
    /// </para>
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static PermissionCopyResult CopyWindowsThroughReopen(
        FileSecurity security,
        string targetPath,
        FileIdentity targetIdentity)
    {
        const uint FileReadAttributes = 0x0000_0080;
        const uint ReadControl = 0x0002_0000;
        const uint WriteDac = 0x0004_0000;
        const uint FileShareRead = 0x0000_0001;
        const uint FileShareWrite = 0x0000_0002;
        const uint FileShareDelete = 0x0000_0004;
        const uint OpenExisting = 3;
        const uint FileFlagOpenReparsePoint = 0x0020_0000;
        const uint DaclSecurityInformation = 0x0000_0004;
        const uint UnprotectedDaclSecurityInformation = 0x2000_0000;
        const uint ProtectedDaclSecurityInformation = 0x8000_0000;

        byte[] descriptor;
        bool protectedRules;
        try
        {
            // Already self-relative, which is the form SetKernelObjectSecurity wants.
            descriptor = security.GetSecurityDescriptorBinaryForm();
            protectedRules = security.AreAccessRulesProtected;
        }
        catch (Exception ex)
        {
            return new PermissionCopyResult(
                PermissionCopyStatus.PartiallyCopied,
                $"The discretionary ACL could not be carried over: the original's security descriptor could not be serialized ({ex.GetType().Name}: {ex.Message}). The widening comparison still applies.");
        }

        // SE_DACL_PROTECTED has to be asked for explicitly, and it is load-bearing. Without
        // it the temporary file keeps the inheritable ACEs it picked up from the containing
        // directory at create time and the copied ACEs merely join them, which is how a
        // destination whose own ACL is narrower than its parent directory's inheritable set
        // used to fail its own save with PermissionWidening (finding F-16).
        var securityInformation = DaclSecurityInformation |
            (protectedRules ? ProtectedDaclSecurityInformation : UnprotectedDaclSecurityInformation);

        var handle = CreateFileW(
            targetPath,
            FileReadAttributes | ReadControl | WriteDac,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return new PermissionCopyResult(
                PermissionCopyStatus.PartiallyCopied,
                $"The discretionary ACL could not be carried over: re-opening the temporary file for WRITE_DAC failed with Win32 error {error}. The widening comparison still applies.");
        }

        try
        {
            // GetFileInformationByHandle needs no data access, so the identity check still
            // functions on a mask that deliberately grants none.
            if (!GetFileInformationByHandle(handle, out var info))
            {
                return new PermissionCopyResult(
                    PermissionCopyStatus.PartiallyCopied,
                    "The discretionary ACL could not be carried over: the identity of the re-opened temporary file could not be read. The widening comparison still applies.");
            }

            var reopened = new FileIdentity(
                info.VolumeSerialNumber,
                ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);

            if (reopened != targetIdentity)
            {
                // Something replaced the temporary file between its exclusive creation and
                // this re-open. Writing the original save's descriptor onto whatever is
                // there now would be an arbitrary-ACL-write primitive, so it is abandoned.
                // This re-open is now the only path that writes a descriptor by path, which
                // makes this check the whole of that control rather than a second layer.
                return new PermissionCopyResult(
                    PermissionCopyStatus.Failed,
                    "The temporary file was replaced between its exclusive creation and the permission copy. The save was abandoned rather than writing a security descriptor to an unknown object.");
            }

            if (!SetKernelObjectSecurity(handle, securityInformation, descriptor))
            {
                var error = Marshal.GetLastPInvokeError();
                return new PermissionCopyResult(
                    PermissionCopyStatus.PartiallyCopied,
                    $"The discretionary ACL could not be carried over: writing the security descriptor onto the temporary file failed with Win32 error {error}. The widening comparison still applies.");
            }

            return new PermissionCopyResult(
                PermissionCopyStatus.Copied,
                protectedRules
                    ? "The original's discretionary ACL was written onto the temporary file through an identity-checked re-open, with inheritance left disabled as it is on the original."
                    : "The original's discretionary ACL was written onto the temporary file through an identity-checked re-open.");
        }
        catch (Exception ex)
        {
            return new PermissionCopyResult(
                PermissionCopyStatus.PartiallyCopied,
                $"The discretionary ACL could not be carried over ({ex.GetType().Name}: {ex.Message}). The widening comparison still applies.");
        }
        finally
        {
            // No FileStream is wrapped around the handle any more, so this is its single
            // owner and the disposal cannot be a double release.
            handle.Dispose();
        }
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

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(
        SafeFileHandle handle,
        uint securityInformation,
        byte[] securityDescriptor);

#pragma warning disable IDE1006 // libc entry points keep their own names.

    [DllImport("libc", SetLastError = true)]
    private static extern nint flistxattr(int fd, byte[] list, nint size);

    [DllImport("libc", SetLastError = true)]
    private static extern nint fgetxattr(int fd, byte[] name, byte[] value, nint size);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsetxattr(int fd, byte[] name, byte[] value, nint size, int flags);

#pragma warning restore IDE1006
#pragma warning restore SYSLIB1054
}
