using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Tests.Io;

/// <summary>
/// Exclusive-create behaviour and identity retention. Backup and temporary files are
/// created only through this path, so a pre-planted entry at a predictable name has to
/// abort rather than fall back to a link-following open.
/// </summary>
public sealed class SafePathResolverCreateNewTests
{
    private static readonly SafePathResolver Resolver = new();

    private static ValueTask<PathResolution> Resolve(string path, PathResolutionOptions options) =>
        Resolver.ResolveAsync(path, options, TestContext.Current.CancellationToken);

    private static ValueTask<PathResolution> CreateNew(string path, PathResolutionOptions options) =>
        Resolver.CreateNewAsync(path, options, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SafePath_CreateNewProducesAWritableFileWithRecordedIdentity()
    {
        using var workspace = new TempWorkspace("create-new");

        var target = workspace.Path("slot1.tmp");

        var resolution = await CreateNew(target, new PathResolutionOptions());
        var resolved = Assert.IsType<PathResolution.Resolved>(resolution);

        using (var file = resolved.File)
        {
            Assert.Equal(0, file.Stream.Length);
            Assert.Equal(1, file.HardLinkCount);
            Assert.NotEqual(default, file.Identity);
            Assert.True(file.ReassertIdentity());

            file.Stream.Write([1, 2, 3, 4]);
            file.Stream.Flush();

            // Identity is stable across the write, and re-asserting it reads from the
            // retained handle rather than re-resolving the path.
            Assert.True(file.ReassertIdentity());
        }

        Assert.Equal(4, new FileInfo(target).Length);
    }

    [Fact]
    public async Task SafePath_CreateNewRefusesAPrePlantedRegularFile()
    {
        using var workspace = new TempWorkspace("plant-file");

        var target = workspace.CreateFile("slot1.tmp", 3);

        var refused = Assert.IsType<PathResolution.Refused>(
            await CreateNew(target, new PathResolutionOptions()));

        Assert.Equal(PathRefusalReason.AlreadyExists, refused.Reason);

        // The pre-existing bytes are untouched: refusal never becomes a truncating retry.
        Assert.Equal(3, new FileInfo(target).Length);
    }

    [Fact]
    public async Task SafePath_CreateNewRefusesAPrePlantedHardLink()
    {
        using var workspace = new TempWorkspace("plant-hardlink");

        var victim = workspace.CreateFile("victim.dat", 64);
        var plantedTempPath = workspace.Path("slot1.tmp");

        var failure = PlatformFixtures.TryCreateHardLink(plantedTempPath, victim);
        Assert.SkipWhen(failure is not null, $"Could not create a hard link: {failure}");

        var refused = Assert.IsType<PathResolution.Refused>(
            await CreateNew(plantedTempPath, new PathResolutionOptions()));

        Assert.Equal(PathRefusalReason.AlreadyExists, refused.Reason);

        // The aliased victim still holds its original bytes.
        Assert.Equal(64, new FileInfo(victim).Length);
    }

    [Fact]
    public async Task SafePath_CreateNewRefusesAPrePlantedSymbolicLink()
    {
        using var workspace = new TempWorkspace("plant-symlink");

        var victim = workspace.CreateFile("victim.dat", 64);
        var plantedTempPath = workspace.Path("slot1.tmp");

        var failure = PlatformFixtures.TryCreateFileSymbolicLink(plantedTempPath, victim);
        Assert.SkipWhen(failure is not null, $"Could not create a file symbolic link: {failure}");

        var refused = Assert.IsType<PathResolution.Refused>(
            await CreateNew(plantedTempPath, new PathResolutionOptions()));

        // A link is reported as the more specific cause than a bare "already exists".
        Assert.Equal(PathRefusalReason.LinkTarget, refused.Reason);
        Assert.Equal(64, new FileInfo(victim).Length);
    }

    [Fact]
    public async Task SafePath_CreateNewRefusesALinkedAncestor()
    {
        using var workspace = new TempWorkspace("plant-ancestor");

        var realDirectory = workspace.CreateDirectory("real");
        var linkDirectory = workspace.Path("via");

        var junctionFailure = PlatformFixtures.TryCreateJunction(linkDirectory, realDirectory);
        if (junctionFailure is not null)
        {
            var symlinkFailure = PlatformFixtures.TryCreateDirectorySymbolicLink(linkDirectory, realDirectory);
            Assert.SkipWhen(
                symlinkFailure is not null,
                $"Could not plant a linked intermediate directory. Junction: {junctionFailure} Symbolic link: {symlinkFailure}");
        }

        var refused = Assert.IsType<PathResolution.Refused>(
            await CreateNew(Path.Combine(linkDirectory, "slot1.tmp"), new PathResolutionOptions()));

        Assert.Equal(PathRefusalReason.LinkInAncestor, refused.Reason);
        Assert.False(File.Exists(Path.Combine(realDirectory, "slot1.tmp")));
    }

    [Fact]
    public async Task SafePath_CreateNewIgnoresAnOpenExistingModeInOptions()
    {
        using var workspace = new TempWorkspace("mode-override");

        var target = workspace.CreateFile("slot1.tmp", 5);

        // The method name is authoritative: passing OpenExisting here must not downgrade
        // the call into an open that would follow a planted link.
        var refused = Assert.IsType<PathResolution.Refused>(
            await CreateNew(
                target,
                new PathResolutionOptions { Mode = PathResolutionMode.OpenExisting }));

        Assert.Equal(PathRefusalReason.AlreadyExists, refused.Reason);
    }

    [Fact]
    public async Task SafePath_ResolveWithCreateNewModeCreatesExclusively()
    {
        using var workspace = new TempWorkspace("resolve-create-new");

        var target = workspace.Path("slot1.tmp");
        var options = new PathResolutionOptions { Mode = PathResolutionMode.CreateNew };

        var resolution = await Resolve(target, options);
        using (Assert.IsType<PathResolution.Resolved>(resolution).File)
        {
            Assert.True(File.Exists(target));
        }

        // A second attempt at the same path is a refusal, not an overwrite.
        var second = Assert.IsType<PathResolution.Refused>(await Resolve(target, options));
        Assert.Equal(PathRefusalReason.AlreadyExists, second.Reason);
    }

    [Fact]
    public async Task SafePath_ReassertIdentityFailsClosedOnAClosedHandle()
    {
        using var workspace = new TempWorkspace("identity-probe");

        var target = workspace.CreateFile("slot1.dat", 8);

        var resolved = Assert.IsType<PathResolution.Resolved>(
            await Resolve(target, new PathResolutionOptions()));

        var file = resolved.File;
        Assert.True(file.ReassertIdentity());

        // Renaming the path out from under the handle does not change the object the
        // handle names: identity follows the object, not the name.
        var renamed = workspace.Path("renamed.dat");
        File.Move(target, renamed);
        Assert.True(file.ReassertIdentity());

        file.Dispose();
        Assert.Throws<ObjectDisposedException>(() => file.ReassertIdentity());
    }
}
