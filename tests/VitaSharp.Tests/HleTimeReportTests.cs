using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>The "PERF hle time" lines (VITASHARP_PERF_HLE_TIME, Core/Util/HleTimeReport.cs): per-window deltas of a
/// thread's HLE profile (calls, wall ticks, CPU cycles per function index) and the line written from them.</summary>
public sealed class HleTimeReportTests
{
    private static string Name(uint index) => $"fn{index}";

    [Fact]
    public void Window_SubtractsThePreviousCopyAndSortsByWallTime()
    {
        long[] before = [10, 100, 0, 5, 50, 0];
        long[] now = [12, 130, 0, 9, 450, 0, 3, 60, 0];

        (string Function, long Calls, long Ticks)[] rows = HleTimeReport.Window(before, now, Name);

        Assert.Equal(["fn1", "fn2", "fn0"], rows.Select(r => r.Function));
        Assert.Equal((4L, 400L), (rows[0].Calls, rows[0].Ticks));
        Assert.Equal((3L, 60L), (rows[1].Calls, rows[1].Ticks)); // a function first called in the window
        Assert.Equal((2L, 30L), (rows[2].Calls, rows[2].Ticks));
    }

    [Fact]
    public void Window_LeavesOutFunctionsNotCalledInTheWindow()
    {
        long[] profile = [7, 70, 0, 1, 10, 0];

        Assert.Empty(HleTimeReport.Window(profile, (long[])profile.Clone(), Name));
        Assert.Equal(2, HleTimeReport.Window(null, profile, Name).Length);
    }

    [Fact]
    public void ThreadLine_GivesMillisecondsAndCallsPerFrame()
    {
        (string, long, long)[] rows = [("sceGxmNotificationWait", 20, 200), ("sceKernelWaitLwCond", 60, 100)];

        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            // 10 frames, one tick = 0.5 ms: 300 ticks = 15 ms per frame in HLE
            string line = HleTimeReport.ThreadLine("main", rows, 10, 0.5, 100, 110);

            Assert.Equal("PERF hle time main (frames 100-110): in HLE 15.00 ms/frame; sceGxmNotificationWait 10.00 (2.0), sceKernelWaitLwCond 5.00 (6.0)", line);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }
}
