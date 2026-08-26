using System.Net.Sockets;
using SaveEditor.Ui.Io;

namespace SaveEditor.Ui.Tests.Io;

/// <summary>
/// The named security tests from PLAN.md section 12 for findings A1 and A4.
/// </summary>
/// <remarks>
/// Where a fixture cannot be created on this machine — a Windows symbolic link without
/// elevation or Developer Mode, a hard link across volumes — the test skips with the
/// concrete failure reason. It never passes vacuously: a security test that asserts
/// nothing is worse than one that is honestly skipped.
/// </remarks>
public sealed class SafePathResolverSecurityTests
{
    private static readonly SafePathResolver Resolver = new();

    private static ValueTask<PathResolution> Resolve(string path, PathResolutionOptions options) =>
        Resolver.ResolveAsync(path, options, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SafePath_RejectsLinkInIntermediateComponent()
    {
        using var workspace = new TempWorkspace("ancestor-link");

        var realDirectory = workspace.CreateDirectory("real");
        Directory.CreateDirectory(Path.Combine(realDirectory, "inner"));
        var target = Path.Combine(realDirectory, "inner", "save.dat");
        File.WriteAllBytes(target, new byte[16]);

        var linkDirectory = workspace.Path("via");

        var junctionFailure = PlatformFixtures.TryCreateJunction(linkDirectory, realDirectory);
        if (junctionFailure is not null)
        {
            var symlinkFailure = PlatformFixtures.TryCreateDirectorySymbolicLink(linkDirectory, realDirectory);
            Assert.SkipWhen(
                symlinkFailure is not null,
                $"Could not plant a linked intermediate directory. Junction: {junctionFailure} Symbolic link: {symlinkFailure}");
        }

        // The leaf here is a perfectly ordinary file. Only the middle component is a link,
        // which is exactly the case a leaf-only check misses.
        var throughLink = Path.Combine(linkDirectory, "inner", "save.dat");

        var resolution = await Resolve(throughLink, new PathResolutionOptions());

        var refused = Assert.IsType<PathResolution.Refused>(resolution);
        Assert.Equal(PathRefusalReason.LinkInAncestor, refused.Reason);

        // Control: the same file through its real path resolves.
        using var direct = await ResolveToFileAsync(target);
        Assert.Equal(16, direct.Stream.Length);
    }

    [Fact]
    public async Task SafePath_RejectsJunctionAndNonSymlinkReparseTags()
    {
        Assert.SkipUnless(
            OperatingSystem.IsWindows(),
            "Reparse tags are a Windows NTFS concept. Linux link coverage is in SafePath_RejectsLinkInIntermediateComponent and SafePath_RefusesSymbolicLinkAtTheLeaf.");

        using var workspace = new TempWorkspace("reparse-tags");

        var realDirectory = workspace.CreateDirectory("real");
        var target = Path.Combine(realDirectory, "save.dat");
        File.WriteAllBytes(target, new byte[8]);

        // IO_REPARSE_TAG_MOUNT_POINT (0xA0000003) is a reparse point that is not a
        // symlink, and it carries the name-surrogate bit, which is the property the
        // resolver actually keys off. Tags without that bit are deliberately permitted;
        // see SafePathResolverWindowsClassificationTests.
        Assert.True(WindowsPathFacts.IsNamespaceRedirectingReparseTag(WindowsPathFacts.ReparseTagMountPoint));

        var junction = workspace.Path("junction");
        var failure = PlatformFixtures.TryCreateJunction(junction, realDirectory);
        Assert.SkipWhen(failure is not null, $"Could not create a junction: {failure}");

        // As the final component: the junction itself is named.
        var leafResolution = await Resolve(junction, new PathResolutionOptions());
        var leafRefused = Assert.IsType<PathResolution.Refused>(leafResolution);
        Assert.Equal(PathRefusalReason.LinkTarget, leafRefused.Reason);
        Assert.Contains("A0000003", leafRefused.Detail, StringComparison.OrdinalIgnoreCase);

        // As an intermediate component: the leaf looks like a plain file.
        var throughJunction = Path.Combine(junction, "save.dat");
        var ancestorResolution = await Resolve(throughJunction, new PathResolutionOptions());
        var ancestorRefused = Assert.IsType<PathResolution.Refused>(ancestorResolution);
        Assert.Equal(PathRefusalReason.LinkInAncestor, ancestorRefused.Reason);
        Assert.Contains("A0000003", ancestorRefused.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SafePath_ConfirmsWhenHardlinkCountExceedsOne()
    {
        using var workspace = new TempWorkspace("hardlinks");

        var original = workspace.CreateFile("save.dat", 32);
        var alias = workspace.Path("alias.dat");

        var failure = PlatformFixtures.TryCreateHardLink(alias, original);
        Assert.SkipWhen(failure is not null, $"Could not create a hard link: {failure}");

        var resolution = await Resolve(original, new PathResolutionOptions());

        var confirmation = Assert.IsType<PathResolution.NeedsConfirmation>(resolution);
        using var file = confirmation.File;

        Assert.Equal(PathConfirmationKind.MultipleHardLinks, confirmation.Kind);
        Assert.Equal(2, file.HardLinkCount);

        // Both names are the same object, so the alias reports the same identity.
        var aliasResolution = await Resolve(alias, new PathResolutionOptions());
        var aliasConfirmation = Assert.IsType<PathResolution.NeedsConfirmation>(aliasResolution);
        using var aliasFile = aliasConfirmation.File;

        Assert.Equal(PathConfirmationKind.MultipleHardLinks, aliasConfirmation.Kind);
        Assert.Equal(file.Identity, aliasFile.Identity);

        // Removing the alias drops the count back to one and the confirmation with it.
        File.Delete(alias);
        using var plain = await ResolveToFileAsync(original);
        Assert.Equal(1, plain.HardLinkCount);
    }

    [Fact]
    public async Task SafePath_RefusesFifoDeviceAndNamedPipe()
    {
        using var workspace = new TempWorkspace("non-regular");

        var checkedAtLeastOne = false;

        if (OperatingSystem.IsWindows())
        {
            // A real named pipe server, so the pipe path names something that exists.
            var pipeName = $"saveeditor-safepath-{Guid.NewGuid():N}";
            using var server = new System.IO.Pipes.NamedPipeServerStream(pipeName);

            await AssertRefusedAsync($@"\\.\pipe\{pipeName}", PathRefusalReason.InvalidPath);
            await AssertRefusedAsync(workspace.Path("NUL"), PathRefusalReason.InvalidPath);
            await AssertRefusedAsync(workspace.Path("CON"), PathRefusalReason.InvalidPath);
            await AssertRefusedAsync(workspace.Path("COM1.dat"), PathRefusalReason.InvalidPath);
            await AssertRefusedAsync(@"\\.\PhysicalDrive0", PathRefusalReason.InvalidPath);
            await AssertRefusedAsync(@"\\?\GLOBALROOT\Device\HarddiskVolume1\save.dat", PathRefusalReason.InvalidPath);
            checkedAtLeastOne = true;
        }
        else
        {
            // A FIFO opened for reading blocks until a writer appears unless the resolver
            // passes O_NONBLOCK, so a regression here shows up as a hung test.
            var fifo = workspace.Path("pipe.fifo");
            var fifoFailure = PlatformFixtures.TryCreateFifo(fifo);
            if (fifoFailure is null)
            {
                await AssertRefusedAsync(fifo, PathRefusalReason.NotARegularFile);
                checkedAtLeastOne = true;
            }

            // Character devices.
            if (File.Exists("/dev/null"))
            {
                await AssertRefusedAsync("/dev/null", PathRefusalReason.NotARegularFile);
                await AssertRefusedAsync("/dev/zero", PathRefusalReason.NotARegularFile);
                checkedAtLeastOne = true;
            }

            // A Unix domain socket.
            var socketPath = workspace.Path("endpoint.sock");
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Bind(new UnixDomainSocketEndPoint(socketPath));

                var resolution = await Resolve(socketPath, new PathResolutionOptions());
                var refused = Assert.IsType<PathResolution.Refused>(resolution);
                Assert.Equal(PathRefusalReason.NotARegularFile, refused.Reason);
                checkedAtLeastOne = true;
            }
            catch (SocketException)
            {
            }
        }

        Assert.SkipUnless(checkedAtLeastOne, "No non-regular-file fixture could be created on this machine.");
    }

    [Fact]
    public async Task SafePath_RefusesInputAboveConfiguredSizeCap()
    {
        using var workspace = new TempWorkspace("size-cap");

        const int fileBytes = 4096;
        var target = workspace.CreateFile("save.dat", fileBytes);

        // Above MaxBytes: refused outright, and no handle is produced.
        var refusedResolution = await Resolve(
            target,
            new PathResolutionOptions { MaxBytes = fileBytes - 1, ConfirmAboveBytes = fileBytes - 1 });

        var refused = Assert.IsType<PathResolution.Refused>(refusedResolution);
        Assert.Equal(PathRefusalReason.TooLarge, refused.Reason);

        // Above ConfirmAboveBytes but under MaxBytes: the user is asked first.
        var confirmResolution = await Resolve(
            target,
            new PathResolutionOptions { MaxBytes = fileBytes * 4, ConfirmAboveBytes = fileBytes - 1 });

        var confirmation = Assert.IsType<PathResolution.NeedsConfirmation>(confirmResolution);
        using (confirmation.File)
        {
            Assert.Equal(PathConfirmationKind.UnusuallyLarge, confirmation.Kind);
        }

        // Exactly at the cap is not above it.
        var exact = await Resolve(
            target,
            new PathResolutionOptions { MaxBytes = fileBytes, ConfirmAboveBytes = fileBytes });

        var resolved = Assert.IsType<PathResolution.Resolved>(exact);
        using (resolved.File)
        {
            Assert.Equal(fileBytes, resolved.File.Stream.Length);
        }
    }

    [Fact]
    public async Task SafePath_RefusesSymbolicLinkAtTheLeaf()
    {
        using var workspace = new TempWorkspace("leaf-symlink");

        var target = workspace.CreateFile("save.dat", 8);
        var link = workspace.Path("save.link");

        var failure = PlatformFixtures.TryCreateFileSymbolicLink(link, target);
        Assert.SkipWhen(failure is not null, $"Could not create a file symbolic link: {failure}");

        var resolution = await Resolve(link, new PathResolutionOptions());

        var refused = Assert.IsType<PathResolution.Refused>(resolution);
        Assert.Equal(PathRefusalReason.LinkTarget, refused.Reason);
    }

    private static async Task AssertRefusedAsync(string path, PathRefusalReason expected)
    {
        var resolution = await Resolve(path, new PathResolutionOptions());
        var refused = Assert.IsType<PathResolution.Refused>(resolution);
        Assert.Equal(expected, refused.Reason);
        Assert.NotEmpty(refused.Detail);
    }

    private static async Task<ResolvedFile> ResolveToFileAsync(string path)
    {
        var resolution = await Resolve(path, new PathResolutionOptions());
        return Assert.IsType<PathResolution.Resolved>(resolution).File;
    }
}
