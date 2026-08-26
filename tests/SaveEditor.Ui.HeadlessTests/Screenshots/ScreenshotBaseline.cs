using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SaveEditor.ScreenshotDiff;

namespace SaveEditor.Ui.HeadlessTests.Screenshots;

/// <summary>
/// Compares a capture against a committed reference, and seeds references when
/// explicitly asked.
/// </summary>
/// <remarks>
/// <para>
/// References are Ubuntu-golden per <c>PLAN.md</c> §12: the platforms rasterise text
/// differently, so one golden set cannot serve both, and a reference generated on a
/// Windows development machine would fail CI on its first run. Comparison therefore
/// runs on Linux only; elsewhere the test skips with a reason.
/// </para>
/// <para>
/// Seeding is opt-in through <c>SAVEEDITOR_UPDATE_BASELINES=1</c> so a reference can
/// only be written deliberately. Auto-writing a missing reference would make the gate
/// self-certifying — the first run of a broken screen would enshrine it as correct.
/// </para>
/// </remarks>
public static class ScreenshotBaseline
{
    private const string UpdateVariable = "SAVEEDITOR_UPDATE_BASELINES";

    /// <summary>Whether references are being seeded rather than compared.</summary>
    public static bool IsUpdating =>
        Environment.GetEnvironmentVariable(UpdateVariable) is "1" or "true";

    /// <summary>Whether this platform owns the golden references.</summary>
    public static bool IsGoldenPlatform => OperatingSystem.IsLinux();

    /// <summary>Directory holding the committed references.</summary>
    /// <param name="callerPath">Resolved by the compiler; do not pass.</param>
    /// <returns>The baselines directory, created if seeding.</returns>
    public static string Directory([CallerFilePath] string callerPath = "")
    {
        var directory = System.IO.Directory.GetParent(callerPath);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PLAN.md")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
                   ?? throw new InvalidOperationException("Could not locate the repository root.");

        return Path.Combine(root, "tests", "SaveEditor.Ui.HeadlessTests", "baselines");
    }

    /// <summary>Compares a capture against its reference, or seeds it.</summary>
    /// <param name="name">Reference name, without extension.</param>
    /// <param name="pixels">The captured BGRA buffer.</param>
    /// <remarks>
    /// Skips with a stated reason off the golden platform, and when a reference is
    /// missing and seeding was not requested — never silently passes.
    /// </remarks>
    public static void Verify(string name, byte[] pixels)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(pixels);

        Assert.SkipUnless(
            IsGoldenPlatform,
            "Screenshot references are Ubuntu-golden; the platforms rasterise text " +
            "differently and one golden set cannot serve both. Behavioural tests still run here.");

        var path = Path.Combine(Directory(), $"{name}.png");

        if (IsUpdating)
        {
            System.IO.Directory.CreateDirectory(Directory());
            WritePng(path, pixels);
            return;
        }

        Assert.SkipUnless(
            File.Exists(path),
            $"No committed reference for '{name}'. Seed it from a CI run with " +
            $"{UpdateVariable}=1 rather than from a development machine.");

        var expected = ReadPng(path);
        var diff = PixelComparator.Compare(expected, pixels);

        if (diff.IsIdentical)
        {
            return;
        }

        // Write the actual capture and a diff mask beside the reference so a failed
        // run leaves something a reviewer can look at rather than only a number.
        WritePng(Path.Combine(Directory(), $"{name}.actual.png"), pixels);
        WritePng(
            Path.Combine(Directory(), $"{name}.diff.png"),
            PixelComparator.BuildDiffMask(expected, pixels));

        Assert.Fail(
            $"'{name}' differs from its reference: {diff.DifferingPixels} of {diff.TotalPixels} " +
            $"pixels, first at index {diff.FirstDifferenceAt}. Review the change deliberately; " +
            $"if it is intended, reseed with {UpdateVariable}=1.");
    }

    /// <summary>Encodes a BGRA buffer as PNG.</summary>
    /// <remarks>
    /// References are stored as PNG rather than raw pixels. A 1600x1000 BGRA frame is
    /// 6.4 MB, so six references would put 38 MB into the repository and add another
    /// 38 MB on every reseed. PNG is lossless, so the comparison stays byte-exact.
    /// </remarks>
    private static void WritePng(string path, byte[] pixels)
    {
        using var bitmap = new WriteableBitmap(
            new PixelSize(ScreenshotHarness.Width, ScreenshotHarness.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using (var frame = bitmap.Lock())
        {
            var rowBytes = ScreenshotHarness.Width * 4;
            for (var row = 0; row < ScreenshotHarness.Height; row++)
            {
                Marshal.Copy(pixels, row * rowBytes, frame.Address + (row * frame.RowBytes), rowBytes);
            }
        }

        bitmap.Save(path, new PngBitmapEncoderOptions());
    }

    /// <summary>Decodes a PNG reference back to a BGRA buffer.</summary>
    private static byte[] ReadPng(string path)
    {
        using var bitmap = new Bitmap(path);

        var width = bitmap.PixelSize.Width;
        var height = bitmap.PixelSize.Height;
        var rowBytes = width * 4;
        var buffer = new byte[rowBytes * height];

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(0, 0, width, height),
                handle.AddrOfPinnedObject(),
                buffer.Length,
                rowBytes);
        }
        finally
        {
            handle.Free();
        }

        return buffer;
    }
}
