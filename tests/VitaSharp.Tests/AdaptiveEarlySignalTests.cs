using VitaSharp.Core.Gpu;

namespace VitaSharp.Tests;

/// <summary>VITASHARP_DISPLAY_EARLY_SIGNAL=auto (<see cref="AdaptiveEarlySignal"/>): early is tried only while the game
/// misses its cadence and waits on the display queue, kept only when frames got faster, and dropped again when the
/// completion signal is no slower.</summary>
public sealed class AdaptiveEarlySignalTests
{
    /// <summary>Feeds <paramref name="frames"/> frames from a model of the game: frame time and display wait as a
    /// function of the current signal. Alternating 2 and 3 vblank frames model a game that misses its cadence.</summary>
    private static void Run(AdaptiveEarlySignal a, int frames, Func<bool, int, (double interval, double wait)> game)
    {
        for (int i = 0; i < frames; i++)
        {
            (double interval, double wait) = game(a.Early, i);
            a.OnFrame(interval, wait, out _);
        }
    }

    // Killzone-like: GPU-latency bound, 24 fps (2-3 vblanks), 8 ms on the display queue; early -> ~27 fps
    private static (double, double) Kz(bool early, int i) => early ? (i % 8 == 0 ? 50 : 33.3, 8) : (i % 2 == 0 ? 50 : 33.3, 8.5);
    // F1-like: CPU bound and locked at 30 fps with the completion signal; early runs ahead and drops to ~25 fps
    private static (double, double) F1(bool early, int i) => early ? (i % 2 == 0 ? 50 : 33.3, 2) : (i % 20 == 0 ? 50 : 33.3, 6);

    [Fact]
    public void GpuLatencyBoundGameKeepsEarly()
    {
        var a = new AdaptiveEarlySignal();
        Run(a, 600, Kz);
        Assert.Equal(AdaptiveEarlySignal.Phase.On, a.State);
        Run(a, 3000, Kz); // the periodic re-checks find the completion signal slower and keep early
        Assert.True(a.Early);
    }

    // ---------------------------------------------------------------- early never beats the console's pace

    /// <summary>A game with no frame limiter (F1 2011's race): with the completion signal it flips every 2 vblanks with
    /// some long frames and waits on the display queue; early would let it flip every vblank.</summary>
    private static (double, double) Uncapped(bool early, int i) => early ? (AdaptiveEarlySignal.VblankMs, 1) : (i % 4 == 0 ? 50 : 33.3, 6);

    [Fact]
    public void EarlyHoldsTheFlipToTheCompletionSignalsMedianInterval()
    {
        var a = new AdaptiveEarlySignal();
        int earlyFrames = 0;
        Run(a, 300, (e, i) =>
        {
            if (e)
            {
                earlyFrames++;
                // the hold is on whenever early is: never a flip interval below the 2 vblanks of the completion signal
                Assert.Equal(2, a.HoldVblanks);
            }

            return Uncapped(e, i);
        });
        Assert.True(earlyFrames > 0, "early was never tried");
        Assert.Equal(2, a.BaseVblanks);
        Assert.Equal(0, new AdaptiveEarlySignal().HoldVblanks); // nothing measured, early off: no hold
    }

    [Fact]
    public void GpuLatencyBoundGameKeepsItsGainUnderTheHold()
    {
        // Killzone: median 2 vblanks with the completion signal; early only removes long frames, so the 2-vblank hold
        // leaves its gain (its early frames are 2 vblanks or more)
        var a = new AdaptiveEarlySignal();
        Run(a, 600, Kz);
        Assert.True(a.Early);
        Assert.Equal(2, a.HoldVblanks);
    }

    [Fact]
    public void TheBaseIntervalIsMeasuredAgainWhenTheSceneChanges()
    {
        // menu at 2 vblanks, then a heavier scene at 3 vblanks with the completion signal: the re-check measures it again
        var a = new AdaptiveEarlySignal();
        Run(a, 600, Kz);
        Assert.Equal(2, a.BaseVblanks);
        Run(a, AdaptiveEarlySignal.RecheckFrames + 400, (e, i) => e ? (50, 8) : (i % 4 == 0 ? 66.7 : 50, 8.5));
        Assert.Equal(3, a.BaseVblanks);
    }

    [Theory]
    [InlineData(VitaSharp.Core.Hle.Modules.FrameRateMode.Native, 2, 2)]   // native: the early hold
    [InlineData(VitaSharp.Core.Hle.Modules.FrameRateMode.Native, 0, 0)]   // native, early off: the game's own pace
    [InlineData(VitaSharp.Core.Hle.Modules.FrameRateMode.Unlimited, 2, 0)] // unlimited: no hold, faster than the console is the point
    [InlineData(VitaSharp.Core.Hle.Modules.FrameRateMode.Fixed30, 0, 2)]
    [InlineData(VitaSharp.Core.Hle.Modules.FrameRateMode.Fixed60, 2, 1)]  // the user's fixed rate wins over the hold
    public void FlipIntervalByFrameRateMode(VitaSharp.Core.Hle.Modules.FrameRateMode mode, int earlyHold, int expected)
    {
        Assert.Equal(expected, VitaSharp.Core.Hle.Modules.FrameRate.FlipIntervalFor(mode, earlyHold));
    }

    [Fact]
    public void LockedGameIsNeverTried()
    {
        var a = new AdaptiveEarlySignal();
        bool everEarly = false;
        Run(a, 3000, (e, i) => { everEarly |= e; return F1(e, i); });
        Assert.False(everEarly);
    }

    [Fact]
    public void TryThatDoesNotHelpIsDroppedAndBacksOff()
    {
        // misses its cadence and waits, but early is slower (CPU bound): tried once, dropped, then tried rarely
        var a = new AdaptiveEarlySignal();
        int earlyFrames = 0;
        Run(a, 6000, (e, i) =>
        {
            if (e)
            {
                earlyFrames++;
            }

            return e ? (i % 2 == 0 ? 50 : 40, 2) : (i % 2 == 0 ? 50 : 33.3, 6);
        });
        Assert.False(a.Early);
        Assert.InRange(earlyFrames, 1, 6000 / 10); // a few probe windows, never kept
    }

    [Fact]
    public void NoDisplayWaitNoTry()
    {
        var a = new AdaptiveEarlySignal();
        bool everEarly = false;
        Run(a, 3000, (e, i) => { everEarly |= e; return (i % 2 == 0 ? 50 : 33.3, 0.5); });
        Assert.False(everEarly);
    }

    [Fact]
    public void SceneThatTurnsCpuBoundFallsBack()
    {
        var a = new AdaptiveEarlySignal();
        Run(a, 600, Kz);
        Assert.True(a.Early);
        // the scene changes: now the completion signal is as fast as early -> the re-check takes it back
        Run(a, AdaptiveEarlySignal.RecheckFrames + 400, (e, i) => (i % 2 == 0 ? 50 : 33.3, 6));
        Assert.NotEqual(AdaptiveEarlySignal.Phase.On, a.State);
        int earlyFrames = 0;
        Run(a, 1200, (e, i) =>
        {
            if (e)
            {
                earlyFrames++;
            }
            return (i % 2 == 0 ? 50 : 33.3, 6);
        });
        Assert.InRange(earlyFrames, 0, 200); // at most a probe window, backing off
        Assert.NotEqual(AdaptiveEarlySignal.Phase.On, a.State);
    }
}
