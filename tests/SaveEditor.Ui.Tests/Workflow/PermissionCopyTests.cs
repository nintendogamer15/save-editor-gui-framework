using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

using SaveEditor.Ui.Io;
using SaveEditor.Ui.Tests.Io;
using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// What the Windows permission copy actually writes onto the temporary file (finding F-16).
/// </summary>
/// <remarks>
/// <para>
/// Every case here reads the temporary file's discretionary ACL back through a fresh
/// descriptor read and compares ACEs. <see cref="PermissionCopyStatus"/> is deliberately
/// never the evidence: the copy this file exists to pin down reported
/// <see cref="PermissionCopyStatus.Copied"/> on every save while writing nothing at all,
/// so a test that asserts on the status is a test that would have passed against the
/// defect. Only the descriptor on disk can tell the two apart.
/// </para>
/// <para>
/// The fixtures stage the shape that was unfixable from the user's side: a destination
/// whose own ACL has inheritance disabled and is narrower than the inheritable set of the
/// directory the temporary file is created in. The temporary file inherits the broader
/// directory ACL, the copy silently did not narrow it, and the widening gate then failed
/// the save with <c>PermissionWidening</c>.
/// </para>
/// </remarks>
public sealed class PermissionCopyTests
{
    private const string WindowsOnly =
        "Discretionary ACLs are a Windows concept; the Linux half of this copy is mode and extended attributes, covered by Workflow_PreservesModeSoZeroSixHundredStaysZeroSixHundred.";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PermissionCopy_WritesTheOriginalsProtectedDaclOntoTheTemporaryFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TempWorkspace("perm-copy-core");

        var originalPath = WriteFile(workspace, "save.sav");
        RestrictDacl(originalPath);

        using var original = await OpenExistingAsync(originalPath);
        var temporaryPath = workspace.Path("save.sav.part");
        using var temporary = await CreateExclusivelyAsync(temporaryPath);

        var inherited = AccessRules(temporaryPath);
        var expected = AccessRules(originalPath);

        // Without this the case could pass vacuously on a machine whose temporary
        // directory happens to hand out exactly the original's ACL by inheritance.
        Assert.NotEqual(expected, inherited);
        Assert.False(IsProtected(temporaryPath));

        _ = new PlatformFilePermissionPolicy()
            .CopyOnto(original.Stream, temporary.Stream, temporary.CanonicalPath, temporary.Identity);

        Assert.Equal(expected, AccessRules(temporaryPath));

        // SE_DACL_PROTECTED is half the point: without it the inherited ACEs above are
        // still on the file and the copied ACEs have merely joined them, which is exactly
        // the state the widening gate refuses.
        Assert.True(IsProtected(temporaryPath));
    }

    [Fact]
    public async Task PermissionCopy_CarriesAnExplicitDenyAceOntoTheTemporaryFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TempWorkspace("perm-copy-deny");

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value;

        var originalPath = WriteFile(workspace, "save.sav");

        // Execute is denied rather than anything the test itself needs, so the fixture
        // stays openable while still carrying the ACE type most likely to be the real
        // reason a save file's ACL is narrower than its directory's.
        RestrictDacl(originalPath, WellKnownSidType.WorldSid, FileSystemRights.ExecuteFile, AccessControlType.Deny);

        using var original = await OpenExistingAsync(originalPath);
        var temporaryPath = workspace.Path("save.sav.part");
        using var temporary = await CreateExclusivelyAsync(temporaryPath);

        Assert.DoesNotContain(AccessRules(temporaryPath), rule => rule.Contains("Deny", StringComparison.Ordinal));

        _ = new PlatformFilePermissionPolicy()
            .CopyOnto(original.Stream, temporary.Stream, temporary.CanonicalPath, temporary.Identity);

        var actual = AccessRules(temporaryPath);
        Assert.Contains(actual, rule => rule.StartsWith($"{everyone} Deny", StringComparison.Ordinal));
        Assert.Equal(AccessRules(originalPath), actual);
    }

    [Fact]
    public async Task PermissionCopy_CarriesAnAceForAPrincipalTheDirectoryDoesNotGrant()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TempWorkspace("perm-copy-principal");

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null).Value;

        var originalPath = WriteFile(workspace, "save.sav");
        RestrictDacl(originalPath, WellKnownSidType.WorldSid, FileSystemRights.ReadAttributes, AccessControlType.Allow);

        using var original = await OpenExistingAsync(originalPath);
        var temporaryPath = workspace.Path("save.sav.part");
        using var temporary = await CreateExclusivelyAsync(temporaryPath);

        var inherited = AccessRules(temporaryPath);
        Assert.SkipWhen(
            inherited.Any(rule => rule.StartsWith(everyone, StringComparison.Ordinal)),
            "The temporary directory already grants Everyone by inheritance, so this case could not distinguish a copied ACE from an inherited one.");

        _ = new PlatformFilePermissionPolicy()
            .CopyOnto(original.Stream, temporary.Stream, temporary.CanonicalPath, temporary.Identity);

        // A trustee the directory never mentions can only be on the temporary file because
        // the copy put it there.
        Assert.Contains(
            AccessRules(temporaryPath),
            rule => rule.StartsWith($"{everyone} Allow", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PermissionCopy_WritesNothingWhenTheReopenedFileIsNotTheFileItCreated()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = new TempWorkspace("perm-copy-identity");

        var originalPath = WriteFile(workspace, "save.sav");
        RestrictDacl(originalPath);

        using var original = await OpenExistingAsync(originalPath);
        var temporaryPath = workspace.Path("save.sav.part");
        using var temporary = await CreateExclusivelyAsync(temporaryPath);

        var before = AccessRules(temporaryPath);

        // The re-open is the only path that writes a descriptor by path, so the identity
        // re-assertion is the whole of the control that stops it becoming an
        // arbitrary-ACL-write primitive on an attacker-named file. Recording a different
        // identity is the same thing the re-open would see if the temporary file had been
        // swapped, without having to win the race.
        var swapped = temporary.Identity with { FileId = temporary.Identity.FileId ^ 1UL };

        var result = new PlatformFilePermissionPolicy()
            .CopyOnto(original.Stream, temporary.Stream, temporary.CanonicalPath, swapped);

        Assert.Equal(PermissionCopyStatus.Failed, result.Status);
        Assert.Equal(before, AccessRules(temporaryPath));
        Assert.False(IsProtected(temporaryPath));
    }

    [Fact]
    public async Task PermissionCopy_SavesOntoADestinationNarrowerThanItsOwnDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnly);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var harness = new WorkflowHarness("perm-copy-workflow");

        var document = new TestDocument("hero", 3, "trailer-bytes");
        var target = harness.WriteSave("save.sav", document);
        RestrictDacl(target);

        var expected = AccessRules(target);

        using var open = await harness.OpenAsync(target, Token);

        var outcome = await harness.Create()
            .OverwriteWithBackupAsync(document with { Level = 9 }, open, cancellationToken: Token);

        // The user-visible half of finding F-16. The temporary file inherited the broader
        // directory ACL, the copy that claimed to have narrowed it had written nothing, and
        // the widening gate then failed this save with PermissionWidening — from the user's
        // side unfixably, because the destination's own ACL was the thing being obeyed.
        WorkflowHarness.AssertSucceeded(outcome);

        Assert.Equal(expected, AccessRules(target));
        Assert.True(IsProtected(target));

        var backup = Assert.Single(WorkflowHarness.Backups(harness.Workspace.Root));
        Assert.Equal(expected, AccessRules(backup));
    }

    [Fact]
    public void PermissionCopy_WideningGateRefusesWhenTheCandidateCannotBeRead()
    {
        var policy = new PlatformFilePermissionPolicy();

        var readable = new PermissionSnapshot(null, new Dictionary<string, int> { ["S-1-5-21-1"] = 0x1 }, "discretionary ACL");
        var unreadable = new PermissionSnapshot(null, null, "discretionary ACL unreadable (UnauthorizedAccessException)");

        // A candidate nothing is known about used to fall through to "the replacement
        // grants nothing the original does not", which deleted the hard gate at the one
        // moment it could not be evaluated (finding F-16).
        Assert.True(policy.IsBroaderThan(unreadable, readable, out var detail));
        Assert.Contains("could not be read", detail, StringComparison.Ordinal);
        Assert.Contains("unreadable", detail, StringComparison.Ordinal);

        // The mirror image is deliberately not refused here: Capture and CopyOnto read the
        // original through the same call on the same handle, so an original whose
        // permissions cannot be read has already abandoned the save with
        // PermissionCopyStatus.Failed before the comparison is reached.
        Assert.False(policy.IsBroaderThan(readable, unreadable, out _));

        // A snapshot carrying only the dimension its own platform populates is readable,
        // not unreadable. Confusing the two would refuse every Linux save.
        var mode = new PermissionSnapshot(UnixFileMode.UserRead | UnixFileMode.UserWrite, null, "mode 0600");
        Assert.False(policy.IsBroaderThan(mode, mode, out _));
        Assert.False(policy.IsBroaderThan(readable, readable, out _));
    }

    private static string WriteFile(TempWorkspace workspace, string name)
    {
        var path = workspace.Path(name);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("SEDT|hero|3|trailer-bytes"));
        return path;
    }

    private static async Task<ResolvedFile> OpenExistingAsync(string path)
    {
        var resolution = await new SafePathResolver()
            .ResolveAsync(path, new PathResolutionOptions { ForWriting = true }, Token);

        return Assert.IsType<PathResolution.Resolved>(resolution).File;
    }

    private static async Task<ResolvedFile> CreateExclusivelyAsync(string path)
    {
        var resolution = await new SafePathResolver().CreateNewAsync(
            path,
            new PathResolutionOptions { Mode = PathResolutionMode.CreateNew, ForWriting = true },
            Token);

        return Assert.IsType<PathResolution.Resolved>(resolution).File;
    }

    /// <summary>
    /// Disables inheritance on a file and leaves it with one explicit ACE for the account
    /// running the test, plus one optional ACE for a well-known trustee.
    /// </summary>
    /// <remarks>
    /// The extra ACE is passed as values rather than as a callback because a lambda is its
    /// own call site for platform analysis, and every type involved here is Windows-only.
    /// The owner of a file always holds <c>READ_CONTROL</c> and <c>WRITE_DAC</c> whatever
    /// the DACL says, so a fixture this restrictive is still writable by the test that
    /// created it.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static void RestrictDacl(
        string path,
        WellKnownSidType? extraTrustee = null,
        FileSystemRights extraRights = 0,
        AccessControlType extraType = AccessControlType.Allow)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var self = identity.User ?? throw new InvalidOperationException("The current Windows identity has no user SID.");

        var file = new FileInfo(path);
        var security = file.GetAccessControl(AccessControlSections.Access);

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(self, FileSystemRights.FullControl, AccessControlType.Allow));

        if (extraTrustee is { } trustee)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(trustee, null),
                extraRights,
                extraType));
        }

        file.SetAccessControl(security);
    }

    /// <summary>Reads a file's ACEs back through a fresh descriptor read, as text.</summary>
    /// <remarks>
    /// Rights are rendered as the raw mask and trustees as SID strings so that the
    /// comparison does not depend on account names resolving, and inheritance flags are
    /// included so a copied ACE cannot be mistaken for an inherited one.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static List<string> AccessRules(string path)
    {
        var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);

        var rendered = new List<string>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            rendered.Add(
                $"{rule.IdentityReference.Value} {rule.AccessControlType} 0x{(int)rule.FileSystemRights:X8} " +
                $"inherited={rule.IsInherited} inheritance={rule.InheritanceFlags} propagation={rule.PropagationFlags}");
        }

        rendered.Sort(StringComparer.Ordinal);
        return rendered;
    }

    [SupportedOSPlatform("windows")]
    private static bool IsProtected(string path) =>
        new FileInfo(path).GetAccessControl(AccessControlSections.Access).AreAccessRulesProtected;
}
