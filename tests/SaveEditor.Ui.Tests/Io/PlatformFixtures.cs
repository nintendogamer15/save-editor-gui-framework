using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SaveEditor.Ui.Tests.Io;

/// <summary>
/// A per-test temporary directory. Fixtures are never written into the repository.
/// </summary>
internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace(string label)
    {
        Root = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "SaveEditorSafePathTests",
            $"{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path(params string[] parts)
    {
        var combined = Root;
        foreach (var part in parts)
        {
            combined = System.IO.Path.Combine(combined, part);
        }

        return combined;
    }

    public string CreateFile(string name, int bytes)
    {
        var path = Path(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public string CreateDirectory(params string[] parts)
    {
        var path = Path(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        TryDelete(Root);
    }

    private static void TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    // Recursive delete must not descend through a junction that a test
                    // planted, so links are unlinked before the tree is removed.
                    UnlinkReparsePoints(directory);
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void UnlinkReparsePoints(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var info = new FileInfo(entry);
            if (info.LinkTarget is not null)
            {
                if (Directory.Exists(entry) && !File.Exists(entry))
                {
                    Directory.Delete(entry);
                }
                else
                {
                    File.Delete(entry);
                }

                continue;
            }

            if (Directory.Exists(entry))
            {
                UnlinkReparsePoints(entry);
            }
        }
    }
}

/// <summary>
/// Creation of the filesystem objects the security tests need, each returning an
/// explicit failure reason instead of throwing, so a test that cannot build its
/// fixture skips with a stated reason rather than passing vacuously.
/// </summary>
internal static class PlatformFixtures
{
    /// <summary>Creates an NTFS junction (Windows) — no elevation or Developer Mode required.</summary>
    public static string? TryCreateJunction(string junctionPath, string targetDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Junctions are a Windows-only concept.";
        }

        try
        {
            var startInfo = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(junctionPath);
            startInfo.ArgumentList.Add(targetDirectory);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return "cmd.exe could not be started to run mklink /J.";
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);

            if (process.ExitCode != 0 || !Directory.Exists(junctionPath))
            {
                return $"mklink /J failed (exit {process.ExitCode}): {stdout.Trim()} {stderr.Trim()}".Trim();
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"mklink /J could not be run: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Creates a directory symbolic link. Needs elevation or Developer Mode on Windows.</summary>
    public static string? TryCreateDirectorySymbolicLink(string linkPath, string targetDirectory)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetDirectory);
            return null;
        }
        catch (Exception ex)
        {
            return SymlinkFailureReason(ex);
        }
    }

    /// <summary>Creates a file symbolic link. Needs elevation or Developer Mode on Windows.</summary>
    public static string? TryCreateFileSymbolicLink(string linkPath, string targetFile)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetFile);
            return null;
        }
        catch (Exception ex)
        {
            return SymlinkFailureReason(ex);
        }
    }

    /// <summary>Creates a hard link. Allowed without elevation on NTFS and on Linux.</summary>
    public static string? TryCreateHardLink(string linkPath, string existingFile)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!CreateHardLinkW(linkPath, existingFile, IntPtr.Zero))
                {
                    return $"CreateHardLinkW failed with Win32 error {Marshal.GetLastPInvokeError()}. Hard links require both paths on the same NTFS volume.";
                }

                return null;
            }

            if (link(NullTerminated(existingFile), NullTerminated(linkPath)) != 0)
            {
                return $"link(2) failed with errno {Marshal.GetLastPInvokeError()}.";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"Hard link creation is unavailable here: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Creates a FIFO (Linux only).</summary>
    public static string? TryCreateFifo(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return "FIFOs do not exist on Windows.";
        }

        try
        {
            if (mkfifo(NullTerminated(path), 0b110_000_000) != 0)
            {
                return $"mkfifo(3) failed with errno {Marshal.GetLastPInvokeError()}.";
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"mkfifo is unavailable here: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string SymlinkFailureReason(Exception ex) =>
        OperatingSystem.IsWindows()
            ? $"Creating a symbolic link on Windows requires elevation or Developer Mode; it failed here with {ex.GetType().Name}: {ex.Message}"
            : $"Symbolic link creation failed: {ex.GetType().Name}: {ex.Message}";

    private static byte[] NullTerminated(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        bytes[^1] = 0;
        return bytes;
    }

#pragma warning disable SYSLIB1054

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

#pragma warning disable IDE1006

    [DllImport("libc", SetLastError = true)]
    private static extern int link(byte[] oldpath, byte[] newpath);

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo(byte[] pathname, uint mode);

#pragma warning restore IDE1006
#pragma warning restore SYSLIB1054
}
