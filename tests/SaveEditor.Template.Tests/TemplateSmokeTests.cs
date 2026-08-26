using static SaveEditor.Template.Tests.ProcessRunner;

namespace SaveEditor.Template.Tests;

/// <summary>
/// P5 acceptance: <c>dotnet new save-editor</c> produces a running editor with
/// no manual framework wiring. The generated template smoke test generates,
/// builds, and runs the app headlessly, exercises a sample field edit and a
/// theme switch, and both packages install and restore from a local feed.
/// </summary>
/// <remarks>
/// <para>
/// This drives the real <c>dotnet</c> CLI end to end, entirely under a
/// temporary directory: pack <c>SaveEditor.Ui</c> and
/// <c>SaveEditor.Template</c> into a local feed, install the template from
/// that feed, instantiate it, point the generated project's own
/// <c>NuGet.config</c> at that same feed (never at a project reference), and
/// build and test the result. That local-feed round trip is what proves the
/// template package is actually consumable rather than merely present on
/// disk — a generated app that only builds because it secretly references
/// this repository's source would prove nothing about the shipped package.
/// </para>
/// <para>
/// The "sample field edit and theme switch" half of P5's acceptance is
/// exercised by the generated project's own headless test project — this
/// test proves it exists, builds, and passes; the assertions themselves live
/// in <c>templates/save-editor/tests/SaveEditor.Generated.Tests/SmokeTests.cs</c>,
/// which every editor this template generates inherits and can extend.
/// </para>
/// </remarks>
public sealed class TemplateSmokeTests
{
    private const string TemplatePackageId = "SaveEditor.Template";
    private const string GeneratedProjectName = "SmokeGeneratedEditor";

    [Fact(Timeout = 10 * 60 * 1000)]
    public async Task Generated_Project_Restores_From_A_Local_Feed_Builds_Clean_And_Its_Tests_Pass()
    {
        var repoRoot = FindRepoRoot();
        var feedDirectory = Directory.CreateTempSubdirectory("save-editor-template-feed-").FullName;
        var workDirectory = Directory.CreateTempSubdirectory("save-editor-template-work-").FullName;
        var generatedRoot = Path.Combine(workDirectory, GeneratedProjectName);
        var templateInstalled = false;

        try
        {
            // 1. Pack both framework packages into a filesystem feed. This is
            // exactly what a release does, minus publishing anywhere.
            await RunAsync(
                "dotnet",
                $"pack \"{Path.Combine(repoRoot, "src", "SaveEditor.Ui", "SaveEditor.Ui.csproj")}\" -c Release -o \"{feedDirectory}\"",
                repoRoot);

            await RunAsync(
                "dotnet",
                $"pack \"{Path.Combine(repoRoot, "src", "SaveEditor.Template", "SaveEditor.Template.csproj")}\" -c Release -o \"{feedDirectory}\"",
                repoRoot);

            // A template left installed by a previous, interrupted run must not make
            // this run see two conflicting registrations of the same identity.
            await RunAsync("dotnet", $"new uninstall {TemplatePackageId}", workDirectory, allowFailure: true);

            var templatePackage = Directory
                .GetFiles(feedDirectory, "SaveEditor.Template.*.nupkg")
                .Single();

            // 2. Install the template from the packed nupkg, not from source.
            await RunAsync("dotnet", $"new install \"{templatePackage}\"", workDirectory);
            templateInstalled = true;

            // 3. Instantiate it, parameterising the project name.
            await RunAsync(
                "dotnet",
                $"new save-editor -n {GeneratedProjectName} -o \"{generatedRoot}\"",
                workDirectory);

            var cancellationToken = TestContext.Current.CancellationToken;

            var generatedCsproj = Directory
                .GetFiles(generatedRoot, $"{GeneratedProjectName}.csproj", SearchOption.AllDirectories)
                .Single();
            Assert.Contains(
                "PackageReference Include=\"SaveEditor.Ui\"",
                await File.ReadAllTextAsync(generatedCsproj, cancellationToken));

            // 4. Point the generated project at the local feed instead of any
            // project reference. This is the step that proves the package, not
            // the repository, is what the generated app actually consumes.
            await File.WriteAllTextAsync(
                Path.Combine(generatedRoot, "NuGet.config"),
                BuildNuGetConfig(feedDirectory),
                cancellationToken);

            var slnx = Directory.GetFiles(generatedRoot, "*.slnx").Single();

            // 5. Build clean.
            var buildOutput = await RunAsync("dotnet", $"build \"{slnx}\" -c Release", generatedRoot);
            Assert.Contains("Build succeeded.", buildOutput);
            Assert.Contains("0 Warning(s)", buildOutput);
            Assert.Contains("0 Error(s)", buildOutput);

            // 6. Run the generated project's own headless tests: a sample field
            // edit and a theme switch, exercised inside the generated app itself.
            var generatedTestProject = Directory
                .GetFiles(generatedRoot, "*.Tests.csproj", SearchOption.AllDirectories)
                .Single();

            // dotnet test exits non-zero on any test failure, which RunAsync
            // above already turns into a thrown exception with the captured
            // output attached — reaching this line at all means every
            // generated test, including the field-edit and theme-switch
            // smoke tests, passed. The explicit check is a belt-and-braces
            // sanity assertion against a runner that reports success wrongly.
            var testOutput = await RunAsync("dotnet", $"test \"{generatedTestProject}\" -c Release", generatedRoot);
            Assert.DoesNotContain("[FAIL]", testOutput);

            // 7. The app itself starts and stays up — proof the composition root
            // in MainWindow.axaml.cs actually resolves at runtime, not just at
            // compile time.
            // Searched under src/ specifically: the test project references the
            // app project, so building it also copies the app's own apphost
            // into the *test* project's output directory as a dependency,
            // which would otherwise make this an ambiguous match.
            var generatedExe = Directory
                .GetFiles(Path.Combine(generatedRoot, "src"), $"{GeneratedProjectName}.exe", SearchOption.AllDirectories)
                .Where(path => path.Contains(Path.Combine("bin", "Release"), StringComparison.OrdinalIgnoreCase))
                .Single();

            await AssertStartsAndStaysUpAsync(generatedExe, generatedRoot);
        }
        finally
        {
            if (templateInstalled)
            {
                await RunAsync("dotnet", $"new uninstall {TemplatePackageId}", workDirectory, allowFailure: true);
            }

            TryDeleteDirectory(workDirectory);
            TryDeleteDirectory(feedDirectory);
        }
    }

    private static async Task AssertStartsAndStaysUpAsync(string exePath, string workingDirectory)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(exePath)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{exePath}'.");

        try
        {
            // Long enough to get past composition (settings load, theme
            // initialization, shell construction) without turning this into a
            // real-time UI test.
            await Task.Delay(TimeSpan.FromSeconds(3));

            if (process.HasExited)
            {
                Assert.Fail($"The generated app exited early with code {process.ExitCode}.");
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static string BuildNuGetConfig(string feedDirectory) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="local-feed" value="{feedDirectory}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
        </configuration>
        """;

    /// <summary>Walks upward from the test assembly to find the repository root.</summary>
    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SaveEditor.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find the repository root (SaveEditor.slnx) above '{AppContext.BaseDirectory}'.");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
            // Best-effort cleanup. A file the OS still has a handle open on
            // (an antivirus scan, a lingering build lock) must not fail an
            // otherwise-passing test; the OS temp directory gets swept
            // eventually regardless.
        }
    }
}
