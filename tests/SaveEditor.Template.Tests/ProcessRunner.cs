using System.Diagnostics;
using System.Text;

namespace SaveEditor.Template.Tests;

/// <summary>Runs an external process to completion and captures its output.</summary>
/// <remarks>
/// These tests drive the real <c>dotnet</c> CLI — packing, template
/// installation, project generation, build, and test — because that is the
/// only way to prove the template package is actually consumable rather than
/// merely present on disk.
/// </remarks>
internal static class ProcessRunner
{
    /// <summary>Runs a process and returns its combined stdout/stderr.</summary>
    /// <param name="fileName">Executable to run.</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="timeout">How long to wait before giving up.</param>
    /// <param name="allowFailure">
    /// When <see langword="false"/> (the default), a non-zero exit code throws with
    /// the captured output attached, so a failing step fails loudly at the point it
    /// failed rather than three steps later with no context.
    /// </param>
    public static async Task<string> RunAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        TimeSpan? timeout = null,
        bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var sync = new Lock();

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));

        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException(
                $"'{fileName} {arguments}' in '{workingDirectory}' did not finish within {timeout}.\n" +
                "Output so far:\n" + Snapshot());
        }

        var captured = Snapshot();

        if (!allowFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{fileName} {arguments}' in '{workingDirectory}' exited {process.ExitCode}.\nOutput:\n{captured}");
        }

        return captured;

        void Append(string? line)
        {
            if (line is null)
            {
                return;
            }

            lock (sync)
            {
                output.AppendLine(line);
            }
        }

        string Snapshot()
        {
            lock (sync)
            {
                return output.ToString();
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Best-effort: the process may already have exited.
        }
    }
}
