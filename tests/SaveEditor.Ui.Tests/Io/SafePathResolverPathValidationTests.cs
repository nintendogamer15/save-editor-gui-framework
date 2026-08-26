using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Tests.Io;

/// <summary>
/// Syntactic screening: device namespaces, UNC, reserved names, traversal, and the
/// characters Win32 silently strips.
/// </summary>
public sealed class SafePathResolverPathValidationTests
{
    private static readonly SafePathResolver Resolver = new();

    private static ValueTask<PathResolution> Resolve(string path, PathResolutionOptions options) =>
        Resolver.ResolveAsync(path, options, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SafePath_RefusesUncPathsUnlessNonLocalPathsAreAllowed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC syntax is Windows-specific.");

        const string unc = @"\\example-host\share\saves\slot1.dat";

        var refused = Assert.IsType<PathResolution.Refused>(
            await Resolve(unc, new PathResolutionOptions()));

        Assert.Equal(PathRefusalReason.NonLocalPath, refused.Reason);

        // With the opt-in set, the path is no longer refused for being non-local. The
        // host does not exist, so the refusal moves to a filesystem-level reason — which
        // is the point: the syntactic gate is what was lifted, nothing else.
        var opened = await Resolve(
            unc,
            new PathResolutionOptions { AllowNonLocalPaths = true });

        var afterOptIn = Assert.IsType<PathResolution.Refused>(opened);
        Assert.NotEqual(PathRefusalReason.NonLocalPath, afterOptIn.Reason);
    }

    [Fact]
    public async Task SafePath_RefusesDeviceNamespacesRegardlessOfNonLocalOptIn()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Device namespaces are Windows-specific.");

        string[] devicePaths =
        [
            @"\\.\PhysicalDrive0",
            @"\\.\C:",
            @"\\?\C:\saves\slot1.dat",
            @"\\?\UNC\example-host\share\slot1.dat",
            @"\\?\GLOBALROOT\Device\HarddiskVolume2\slot1.dat",
        ];

        foreach (var path in devicePaths)
        {
            // AllowNonLocalPaths is documented as an opt-in for network shares. It must
            // not become an opt-in for raw device access.
            var refused = Assert.IsType<PathResolution.Refused>(
                await Resolve(path, new PathResolutionOptions { AllowNonLocalPaths = true }));

            Assert.Equal(PathRefusalReason.InvalidPath, refused.Reason);
        }
    }

    [Fact]
    public async Task SafePath_RefusesWindowsReservedDeviceNamesInEveryComponent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Reserved device names are Windows-specific.");

        using var workspace = new TempWorkspace("reserved-names");

        string[] names =
        [
            "NUL", "nul", "Con", "aux", "PRN",
            "COM1", "com9", "LPT3",
            "CON.txt", "nul.sav", "LPT1.dat.bak",
        ];

        foreach (var name in names)
        {
            var refused = Assert.IsType<PathResolution.Refused>(
                await Resolve(workspace.Path(name), new PathResolutionOptions()));

            Assert.Equal(PathRefusalReason.InvalidPath, refused.Reason);
        }

        // A reserved name in an intermediate component is refused too.
        var nested = Assert.IsType<PathResolution.Refused>(
            await Resolve(workspace.Path("aux", "slot1.dat"), new PathResolutionOptions()));

        Assert.Equal(PathRefusalReason.InvalidPath, nested.Reason);

        // Names that merely start with a reserved prefix stay usable.
        var usable = workspace.CreateFile("console.dat", 4);
        using var file = Assert.IsType<PathResolution.Resolved>(
            await Resolve(usable, new PathResolutionOptions())).File;

        Assert.Equal(4, file.Stream.Length);
    }

    [Fact]
    public async Task SafePath_RefusesTrailingDotsSpacesAndAlternateDataStreams()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "Trailing dots and spaces are legal filename characters on Linux; Win32 is what silently strips them.");

        using var workspace = new TempWorkspace("trailing");

        string[] paths =
        [
            workspace.Path("slot1.dat."),
            workspace.Path("slot1.dat "),
            workspace.Path("folder ", "slot1.dat"),
            workspace.Path("slot1.dat:hidden"),
        ];

        foreach (var path in paths)
        {
            var refused = Assert.IsType<PathResolution.Refused>(
                await Resolve(path, new PathResolutionOptions()));

            Assert.Equal(PathRefusalReason.InvalidPath, refused.Reason);
        }
    }

    [Fact]
    public async Task SafePath_RefusesTraversalAndUnqualifiedPaths()
    {
        using var workspace = new TempWorkspace("traversal");

        var directory = workspace.CreateDirectory("saves");
        var traversal = Path.Combine(directory, "..", "escaped.dat");

        var refusedTraversal = Assert.IsType<PathResolution.Refused>(
            await Resolve(traversal, new PathResolutionOptions()));

        Assert.Equal(PathRefusalReason.InvalidPath, refusedTraversal.Reason);

        foreach (var unqualified in new[] { "slot1.dat", Path.Combine("saves", "slot1.dat") })
        {
            var refused = Assert.IsType<PathResolution.Refused>(
                await Resolve(unqualified, new PathResolutionOptions()));

            Assert.Equal(PathRefusalReason.InvalidPath, refused.Reason);
        }
    }

    [Fact]
    public async Task SafePath_RefusesHostilePathsWithoutThrowing()
    {
        using var workspace = new TempWorkspace("hostile");

        string[] paths =
        [
            string.Empty,
            "   ",
            "\0",
            workspace.Path("slot\0.dat"),
            workspace.Path(new string('x', 4096)),
            workspace.Path("missing", "slot1.dat"),
            workspace.Root,
            Path.GetPathRoot(workspace.Root)!,
        ];

        foreach (var path in paths)
        {
            var resolution = await Resolve(path, new PathResolutionOptions());
            var refused = Assert.IsType<PathResolution.Refused>(resolution);
            Assert.NotEmpty(refused.Detail);
        }

        // A directory is refused as not being a regular file, not as an access failure.
        var directoryRefusal = Assert.IsType<PathResolution.Refused>(
            await Resolve(workspace.Root, new PathResolutionOptions()));

        Assert.Equal(PathRefusalReason.NotARegularFile, directoryRefusal.Reason);
    }

    [Fact]
    public async Task SafePath_RefusesMissingLeafWithNotFound()
    {
        using var workspace = new TempWorkspace("missing-leaf");

        var refused = Assert.IsType<PathResolution.Refused>(
            await Resolve(workspace.Path("slot1.dat"), new PathResolutionOptions()));

        Assert.Equal(PathRefusalReason.NotFound, refused.Reason);
    }
}
