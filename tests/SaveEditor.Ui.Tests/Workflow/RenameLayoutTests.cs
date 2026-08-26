using SaveEditor.Ui.Workflow;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// The <c>FILE_RENAME_INFO</c> field offsets the Windows atomic replace marshals by hand.
/// </summary>
/// <remarks>
/// <para>
/// The offsets were hardcoded to their 64-bit values. On 32-bit Windows that made every
/// overwrite of an existing file fail with <c>ERROR_INVALID_PARAMETER</c>, which the error
/// classifier then reported as "this filesystem or Windows build does not support a
/// POSIX-semantics rename" — a confusing dead end for what was a struct-layout bug. It fails
/// closed, so it was never a data risk (finding F-14).
/// </para>
/// <para>
/// <strong>What this does and does not prove.</strong> It pins the arithmetic for both
/// pointer sizes, which is the part that was wrong and the only part reachable from a test.
/// It does not exercise an actual 32-bit rename: this suite runs on x64, and nothing here
/// can stand in for running the P/Invoke against a 32-bit runtime.
/// </para>
/// </remarks>
public sealed class RenameLayoutTests
{
    /// <summary>
    /// <c>Flags</c> occupies the first four bytes; <c>RootDirectory</c> is a pointer and is
    /// aligned to the pointer size; <c>FileNameLength</c> and the <c>FileName</c> array follow
    /// from it.
    /// </summary>
    [Theory]
    [InlineData(8, 8, 16, 20)]
    [InlineData(4, 4, 8, 12)]
    public void Layout_PlacesEveryFieldByPointerSize(
        int pointerSize,
        int rootDirectory,
        int fileNameLength,
        int fileName)
    {
        var layout = PlatformDurabilityBarrier.RenameInfoLayout.For(pointerSize);

        Assert.Equal(rootDirectory, layout.RootDirectory);
        Assert.Equal(fileNameLength, layout.FileNameLength);
        Assert.Equal(fileName, layout.FileName);
    }

    /// <summary>
    /// The header must leave no gap the kernel would read as part of the name, and no overlap
    /// between the length field and the name.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    public void Layout_LeavesNoOverlapBetweenTheLengthFieldAndTheName(int pointerSize)
    {
        var layout = PlatformDurabilityBarrier.RenameInfoLayout.For(pointerSize);

        // Flags is four bytes at offset 0 and must not run into the aligned pointer.
        Assert.True(layout.RootDirectory >= sizeof(uint), "RootDirectory overlaps the Flags union.");

        // The pointer must not run into the length field.
        Assert.Equal(layout.RootDirectory + pointerSize, layout.FileNameLength);

        // The length field is a DWORD and the name starts immediately after it.
        Assert.Equal(layout.FileNameLength + sizeof(uint), layout.FileName);
    }

    /// <summary>The running process's own layout is the 64-bit one, and is what ships here.</summary>
    [Fact]
    public void Layout_MatchesThisProcessPointerSize()
    {
        var layout = PlatformDurabilityBarrier.RenameInfoLayout.For(IntPtr.Size);

        Assert.Equal(IntPtr.Size, layout.RootDirectory);
        Assert.Equal(IntPtr.Size * 2, layout.FileNameLength);
    }
}
