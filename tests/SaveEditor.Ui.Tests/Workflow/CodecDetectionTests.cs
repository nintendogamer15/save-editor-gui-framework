using System.Text;
using SaveEditor.Ui.Codecs;

namespace SaveEditor.Ui.Tests.Workflow;

/// <summary>
/// Detection is the one place a hostile save file reaches every installed codec at once,
/// so the parsing surface it exposes is the union of all of them. These cover the three
/// bounds that narrow it: isolation, a bounded slice, and a time box.
/// </summary>
public sealed class CodecDetectionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Detection_ThrowingDetectorIsDeclinedNotFatal()
    {
        var thrower = new TestDetector
        {
            Format = new SaveFormatDescriptor("broken", "Broken Format", ["bad"]),
            HeaderBytesRequired = 8,
            DetectOverride = _ => throw new InvalidOperationException("this detector is broken"),
        };

        var healthy = new TestDetector();
        var healthyCodec = new TestCodec();

        var registry = new SaveCodecRegistry<TestDocument>(
        [
            new CodecRegistration<TestDocument>(thrower, new TestCodec { Format = thrower.Format }),
            new CodecRegistration<TestDocument>(healthy, healthyCodec),
        ]);

        var bytes = TestCodec.Encode(new TestDocument("hero", 3, "tail"));

        var result = await registry.DetectAsync(bytes, Token);

        // The broken detector did not abort detection for the format that does work.
        Assert.True(result.IsResolved);
        Assert.Same(healthyCodec, result.Codec);

        var brokenReport = Assert.Single(result.Reports, report => report.Format.Id == "broken");
        Assert.Equal(DetectionVerdict.Declined, brokenReport.Verdict);
        Assert.True(brokenReport.Faulted);
        Assert.Contains("InvalidOperationException", brokenReport.Detail);

        // And the throw is contained rather than swallowed: it is visible in the report.
        Assert.False(brokenReport.TimedOut);
    }

    [Fact]
    public async Task Detection_DetectorReceivesBoundedHeaderSliceOnly()
    {
        var payload = new byte[4096];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        Encoding.UTF8.GetBytes(TestCodec.Magic).CopyTo(payload, 0);

        var modest = new TestDetector { HeaderBytesRequired = 16 };
        var greedy = new TestDetector
        {
            Format = new SaveFormatDescriptor("greedy", "Greedy Format", ["greedy"]),
            HeaderBytesRequired = 1_000_000,
            DetectOverride = _ => DetectionVerdict.Declined,
        };

        var registry = new SaveCodecRegistry<TestDocument>(
            [
                new CodecRegistration<TestDocument>(modest, new TestCodec()),
                new CodecRegistration<TestDocument>(greedy, new TestCodec { Format = greedy.Format }),
            ],
            new SaveCodecRegistryOptions { MaxHeaderBytes = 64 });

        var result = await registry.DetectAsync(payload, Token);

        // Asked for sixteen, shown sixteen — not the whole 4 KiB payload.
        var modestHeader = Assert.Single(modest.Headers);
        Assert.Equal(16, modestHeader.Length);
        Assert.Equal(payload.Take(16), modestHeader);

        // Asked for a megabyte, clamped to the registry bound rather than to the file size.
        var greedyHeader = Assert.Single(greedy.Headers);
        Assert.Equal(64, greedyHeader.Length);

        Assert.Equal(64, registry.HeaderBytesRequired);
        Assert.True(result.IsResolved);
    }

    [Fact]
    public async Task Detection_ShortFileOffersFewerBytesThanRequested()
    {
        var detector = new TestDetector { HeaderBytesRequired = 512 };
        var registry = new SaveCodecRegistry<TestDocument>(
            [new CodecRegistration<TestDocument>(detector, new TestCodec())]);

        var bytes = TestCodec.Encode(new TestDocument("a", 1, string.Empty));
        Assert.True(bytes.Length < 512);

        await registry.DetectAsync(bytes, Token);

        Assert.Equal(bytes.Length, Assert.Single(detector.Headers).Length);
    }

    [Fact]
    public async Task Detection_AmbiguityIsReportedRatherThanResolvedByRegistrationOrder()
    {
        var first = new TestDetector { Format = new SaveFormatDescriptor("one", "Format One", ["sav"]) };
        var second = new TestDetector { Format = new SaveFormatDescriptor("two", "Format Two", ["sav"]) };

        var registry = new SaveCodecRegistry<TestDocument>(
        [
            new CodecRegistration<TestDocument>(first, new TestCodec { Format = first.Format }),
            new CodecRegistration<TestDocument>(second, new TestCodec { Format = second.Format }),
        ]);

        var result = await registry.DetectAsync(TestCodec.Encode(new TestDocument("hero", 1, "t")), Token);

        Assert.True(result.IsAmbiguous);
        Assert.Null(result.Codec);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task Detection_SlowDetectorIsTimeBoxedAndRecordedAsDeclined()
    {
        using var release = new ManualResetEventSlim(false);

        var slow = new TestDetector
        {
            Format = new SaveFormatDescriptor("slow", "Slow Format", ["slow"]),
            DetectOverride = _ =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return DetectionVerdict.Confident;
            },
        };

        var healthy = new TestDetector();
        var healthyCodec = new TestCodec();

        var registry = new SaveCodecRegistry<TestDocument>(
            [
                new CodecRegistration<TestDocument>(slow, new TestCodec { Format = slow.Format }),
                new CodecRegistration<TestDocument>(healthy, healthyCodec),
            ],
            new SaveCodecRegistryOptions { DetectorTimeout = TimeSpan.FromMilliseconds(100) });

        try
        {
            var result = await registry.DetectAsync(TestCodec.Encode(new TestDocument("hero", 1, "t")), Token);

            var slowReport = Assert.Single(result.Reports, report => report.Format.Id == "slow");
            Assert.True(slowReport.TimedOut);
            Assert.Equal(DetectionVerdict.Declined, slowReport.Verdict);

            // The time box does not stop the detector; it stops detection waiting for it.
            Assert.Same(healthyCodec, result.Codec);
        }
        finally
        {
            release.Set();
        }
    }
}
