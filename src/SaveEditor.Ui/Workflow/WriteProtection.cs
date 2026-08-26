using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SaveEditor.Ui.Workflow;

/// <summary>
/// Decides whether a resolved file is marked write-protected (<c>PLAN.md</c> §7 step 5,
/// finding A12).
/// </summary>
/// <remarks>
/// <para>
/// The check exists because the platform will not make it for us. On Windows the read-only
/// attribute blocks a write through the file but not necessarily a rename over it; on
/// Linux <c>rename(2)</c> needs write permission on the <em>directory</em> and none at all
/// on the file, so a <c>0444</c> save can be replaced without a single permission error.
/// A framework whose whole promise is "refusal, never modification" cannot let the
/// read-only marking be bypassed by an implementation detail of how it happens to write.
/// </para>
/// <para>
/// Refusal is the entire behaviour. The attribute is never cleared, the mode is never
/// changed, no elevation is attempted, and there is no delete-and-recreate workaround.
/// </para>
/// </remarks>
internal static class WriteProtection
{
    private const int FS_IMMUTABLE_FL = 0x0000_0010;
    private const int FS_APPEND_FL = 0x0000_0020;
    private const uint FS_IOC_GETFLAGS = 0x8008_6601;

    /// <summary>Describes why a file must not be written, or returns null when it may be.</summary>
    /// <param name="stream">The retained handle for the target.</param>
    /// <returns>A framework-authored refusal, or <see langword="null"/>.</returns>
    internal static string? Describe(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            return DescribeWindows(stream);
        }

        return DescribeUnix(stream);
    }

    [SupportedOSPlatform("windows")]
    private static string? DescribeWindows(FileStream stream)
    {
        try
        {
            var attributes = File.GetAttributes(stream.SafeFileHandle);
            return attributes.HasFlag(FileAttributes.ReadOnly)
                ? "The target carries the read-only attribute. It is reported, never cleared, and the save was refused rather than working around it."
                : null;
        }
        catch (Exception)
        {
            // An unreadable attribute is not evidence that writing is allowed, but it is
            // also not evidence that it is forbidden. The resolver has already established
            // that this handle names a regular file the process opened; the replace will
            // report its own failure if the platform refuses it.
            return null;
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static string? DescribeUnix(FileStream stream)
    {
        try
        {
            var mode = File.GetUnixFileMode(stream.SafeFileHandle);
            if ((mode & UnixFileMode.UserWrite) == 0)
            {
                return "The target has its owner-write bit clear, which marks it read-only. It is reported, never changed, and the save was refused rather than renaming over it.";
            }
        }
        catch (Exception)
        {
        }

        var immutable = DescribeUnixAttributes(stream);
        return immutable;
    }

    [UnsupportedOSPlatform("windows")]
    private static string? DescribeUnixAttributes(FileStream stream)
    {
        try
        {
            var handle = stream.SafeFileHandle;
            if (handle.IsInvalid || handle.IsClosed)
            {
                return null;
            }

            var descriptor = (int)handle.DangerousGetHandle();
            if (descriptor < 0)
            {
                return null;
            }

            if (ioctl(descriptor, FS_IOC_GETFLAGS, out var flags) != 0)
            {
                // ENOTTY on filesystems without inode flags. Best-effort by design.
                return null;
            }

            if ((flags & FS_IMMUTABLE_FL) != 0)
            {
                return "The target is marked immutable. The attribute is reported, never cleared, and no elevation is attempted.";
            }

            return (flags & FS_APPEND_FL) != 0
                ? "The target is marked append-only, which a replacement would violate. The attribute is reported, never cleared."
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

#pragma warning disable SYSLIB1054 // The Io layer uses DllImport throughout; staying consistent avoids AllowUnsafeBlocks.
#pragma warning disable IDE1006 // libc entry points keep their own names.

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, out int argument);

#pragma warning restore IDE1006
#pragma warning restore SYSLIB1054
}
