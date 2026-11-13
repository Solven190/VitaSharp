using VitaSharp.Core.Cpu;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// The periodic PERF cycles lines report each thread's counters since the previous report (<see cref="CpuCore.TakePerfWindow"/>),
/// not averages since boot: those were dominated by the JIT warm-up after a load (Killzone's "1,521 native entries per
/// frame" were 240 in steady state).
/// </summary>
public sealed unsafe class PerfWindowTests
{
    [Fact]
    public void PeriodicLinesShowTheLastWindowOnly()
    {
        using var core = new CpuCore(new BlockCache());
        // a heavy warm-up: 100 frames with 10 M guest cycles and 5,000 native entries each
        core.BlockCycles = 1_000_000_000;
        core.NativeEntries = 500_000;
        core.NativeTicks = System.Diagnostics.Stopwatch.Frequency; // 1 s in native code
        CpuCore.PerfSample first = core.TakePerfWindow(100);
        Assert.Equal(100, first.Frames);
        Assert.Equal(1_000_000_000, first.BlockCycles);
        core.NativeReturnReport(100); // the periodic report of the warm-up window

        // steady state: the next 10 frames add 1 M cycles and 240 entries each, 20 ms of native code in all
        core.BlockCycles += 10_000_000;
        core.NativeEntries += 2_400;
        core.NativeTicks += System.Diagnostics.Stopwatch.Frequency / 50;
        CpuCore.PerfSample window = core.TakePerfWindow(110);
        Assert.Equal(new CpuCore.PerfSample(10, 10_000_000, 0, 0, System.Diagnostics.Stopwatch.Frequency / 50, 0, 2_400, 0, 0, 0), window);

        string line = SessionReport.CycleLine("main", core, window, "since the last report", -1).Replace(',', '.');
        Assert.StartsWith("PERF cycles main: since the last report (10 frames). guest code 1.00 M/frame", line);
        Assert.Contains("host time in native code 2.00 ms/frame", line);
        Assert.Contains("entries 240.0", core.NativeReturnReport(110).Replace(',', '.'));

        // the whole run, for the end-of-run summary, is still available
        CpuCore.PerfSample totals = core.PerfTotals(110);
        Assert.Equal(1_010_000_000, totals.BlockCycles);
        Assert.Equal(502_400, totals.NativeEntries);
    }
}
