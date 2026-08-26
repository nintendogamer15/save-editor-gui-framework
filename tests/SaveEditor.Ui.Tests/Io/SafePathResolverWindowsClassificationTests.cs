using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Tests.Io;

/// <summary>
/// The two Windows classification rules that decide a refusal, exercised directly.
/// </summary>
/// <remarks>
/// A cloud placeholder cannot be fabricated and a mapped network drive cannot be assumed
/// to exist on a build agent, so these rules are tested against the constants themselves
/// rather than through a fixture. The end-to-end tests that do have fixtures — junctions,
/// hard links, symbolic links — live in the other files in this directory.
/// </remarks>
public sealed class SafePathResolverWindowsClassificationTests
{
    private static readonly SafePathResolver Resolver = new();

    private static ValueTask<PathResolution> Resolve(string path, PathResolutionOptions options) =>
        Resolver.ResolveAsync(path, options, TestContext.Current.CancellationToken);

    [Theory]
    // Name-surrogate tags: the name stands in for an object elsewhere, so a planted
    // component redirects the write. These are the ones the resolver refuses.
    [InlineData(WindowsPathFacts.ReparseTagSymlink, true)]
    [InlineData(WindowsPathFacts.ReparseTagMountPoint, true)]
    // Non-surrogate tags: the same object, stored or fetched differently. Refusing these
    // would lock the user out of a synced Documents folder and buy nothing.
    [InlineData(WindowsPathFacts.ReparseTagCloud, false)]
    [InlineData(WindowsPathFacts.ReparseTagCloud1, false)]
    [InlineData(WindowsPathFacts.ReparseTagDedup, false)]
    [InlineData(WindowsPathFacts.ReparseTagWofCompression, false)]
    [InlineData(WindowsPathFacts.ReparseTagAppExecLink, false)]
    [InlineData(0u, false)]
    public void SafePath_RefusesOnlyNamespaceRedirectingReparseTags(uint reparseTag, bool expectedRefusal)
    {
        Assert.Equal(expectedRefusal, WindowsPathFacts.IsNamespaceRedirectingReparseTag(reparseTag));
    }

    [Fact]
    public void SafePath_ReparseTagRuleMatchesTheNameSurrogateBit()
    {
        // The rule is IsReparseTagNameSurrogate, not a hand-maintained tag list, so an
        // unknown future tag is classified by the same bit Windows itself uses.
        Assert.Equal(0x20000000u, WindowsPathFacts.ReparseTagNameSurrogateBit);

        foreach (var tag in new[] { 0xA0000003u, 0xA000000Cu, 0x2000_0001u, 0xBFFF_FFFFu })
        {
            Assert.True(WindowsPathFacts.IsNamespaceRedirectingReparseTag(tag));
        }

        foreach (var tag in new[] { 0x8000_0013u, 0x9000_001Au, 0x0000_0001u, 0xDFFF_FFFFu & ~0x2000_0000u })
        {
            Assert.False(WindowsPathFacts.IsNamespaceRedirectingReparseTag(tag));
        }
    }

    [Theory]
    [InlineData(WindowsPathFacts.DriveRemote, true)]
    [InlineData(WindowsPathFacts.DriveFixed, false)]
    [InlineData(WindowsPathFacts.DriveRemovable, false)]
    [InlineData(WindowsPathFacts.DriveCdrom, false)]
    [InlineData(WindowsPathFacts.DriveRamdisk, false)]
    [InlineData(WindowsPathFacts.DriveNoRootDir, false)]
    [InlineData(WindowsPathFacts.DriveUnknown, false)]
    public void SafePath_TreatsOnlyRemoteDrivesAsNonLocal(uint driveType, bool expectedNonLocal)
    {
        // Removable and CD-ROM volumes are local: they belong to the local-unprivileged
        // -writer adversary, not to the network opt-in.
        Assert.Equal(expectedNonLocal, WindowsPathFacts.IsRemoteDriveType(driveType));
    }

    [Fact]
    public void SafePath_ClassifiesTheTemporaryVolumeAsLocal()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Drive types are Windows-specific.");

        using var workspace = new TempWorkspace("drive-type");

        var root = Path.GetPathRoot(workspace.Root)!;
        var driveType = WindowsPathFacts.GetDriveType(root);

        Assert.False(
            WindowsPathFacts.IsRemoteDriveType(driveType),
            $"The temporary directory's volume '{root}' reported drive type {driveType}, which would make every other test in this directory resolve against a non-local path.");
    }

    [Fact]
    public async Task SafePath_RefusesAMappedNetworkDriveUnlessNonLocalPathsAreAllowed()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Mapped drives are Windows-specific.");

        // A real mapping cannot be created here — it needs an SMB server — so this runs
        // only where one already exists, and says so plainly when it does not.
        string? remoteRoot = null;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (WindowsPathFacts.IsRemoteDriveType(WindowsPathFacts.GetDriveType(drive.Name)))
            {
                remoteRoot = drive.Name;
                break;
            }
        }

        Assert.SkipWhen(
            remoteRoot is null,
            "No mapped network drive is present on this machine, so the end-to-end mapped-drive refusal cannot be exercised. The classification rule itself is covered by SafePath_TreatsOnlyRemoteDrivesAsNonLocal.");

        var path = Path.Combine(remoteRoot!, "saves", "slot1.dat");

        var refused = Assert.IsType<PathResolution.Refused>(
            await Resolve(path, new PathResolutionOptions()));

        Assert.Equal(PathRefusalReason.NonLocalPath, refused.Reason);

        // With the opt-in set the non-local gate is what lifts, and nothing else.
        var afterOptIn = await Resolve(path, new PathResolutionOptions { AllowNonLocalPaths = true });
        if (afterOptIn is PathResolution.Refused stillRefused)
        {
            Assert.NotEqual(PathRefusalReason.NonLocalPath, stillRefused.Reason);
        }
    }
}
