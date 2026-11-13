using VitaSharp.Core.Hle;
using VitaSharp.Core.Kernel;

namespace VitaSharp.Core.Util;

/// <summary>
/// <see cref="EnvVars.PerfHleTime"/>=1: the periodic "PERF hle time" lines. For the guest threads that spent the most
/// wall time in HLE calls since the previous summary: that time per presented frame and the functions it went to
/// (blocking waits included: notification and display queue waits, lwcond/semaphore/event flag waits, delays), next to
/// the window's frame count. A thread's frame time minus its HLE time is the time it ran guest code or waited for a
/// host core, so the lines say where a serial frame waits without a system trace. A call made from inside another
/// call's guest callback is counted in both.
/// </summary>
public static class HleTimeReport
{
    /// <summary>Threads reported per summary.</summary>
    public const int Threads = 5;

    /// <summary>Functions listed per thread.</summary>
    public const int FunctionsPerThread = 8;

    private static readonly object s_lock = new();
    private static readonly Dictionary<GuestThread, long[]> s_last = new();
    private static long s_lastFrame = -1;
    private static long s_lastTicks;

    /// <summary>The lines for the window since the previous call (none on the first call, which starts the window, or
    /// when HLE calls are not timed).</summary>
    public static IEnumerable<string> Lines(Emulator emu)
    {
        if (!HleFunction.Profile)
        {
            return [];
        }

        long frame = VitaSharp.Core.Hle.Modules.Display.FramesPresented;
        long ticksNow = System.Diagnostics.Stopwatch.GetTimestamp();
        var lines = new List<string>();
        lock (s_lock)
        {
            GuestThread[] threads = emu.Kernel.Threads.ToArray();
            var now = new Dictionary<GuestThread, long[]>();
            foreach (GuestThread t in threads)
            {
                now[t] = (long[])t.HleProfile.Clone();
            }

            long frames = frame - s_lastFrame;
            if (s_lastFrame >= 0 && frames > 0)
            {
                double tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                lines.Add($"PERF hle time window: frames {s_lastFrame}-{frame}, {(ticksNow - s_lastTicks) * tickMs / frames:F2} ms/frame");
                var windows = new List<(string Name, (string Function, long Calls, long Ticks)[] Rows, long Ticks)>();
                foreach ((GuestThread t, long[] profile) in now)
                {
                    (string, long, long)[] rows = Window(s_last.GetValueOrDefault(t), profile, index => emu.Hle.GetFunction(index).Name);
                    long total = rows.Sum(r => r.Item3);
                    if (total > 0)
                    {
                        windows.Add((t.Name, rows, total));
                    }
                }

                foreach ((string name, (string, long, long)[] rows, long _) in windows.OrderByDescending(w => w.Ticks).Take(Threads))
                {
                    lines.Add(ThreadLine(name, rows, frames, tickMs, s_lastFrame, frame));
                }
            }

            s_last.Clear();
            foreach ((GuestThread t, long[] profile) in now)
            {
                s_last[t] = profile;
            }

            s_lastFrame = frame;
            s_lastTicks = ticksNow;
        }

        return lines;
    }

    /// <summary>The calls and wall ticks per function between two copies of a thread's <see cref="GuestThread.HleProfile"/>
    /// (calls, wall ticks, CPU cycles per function index), busiest first; <paramref name="before"/> null = from zero.</summary>
    public static (string Function, long Calls, long Ticks)[] Window(long[]? before, long[] now, Func<uint, string> functionName)
    {
        var rows = new List<(string, long, long)>();
        for (int i = 0; i + 2 < now.Length; i += 3)
        {
            long calls = now[i] - (before != null && i + 2 < before.Length ? before[i] : 0);
            long ticks = now[i + 1] - (before != null && i + 2 < before.Length ? before[i + 1] : 0);
            if (calls > 0 || ticks > 0)
            {
                rows.Add((functionName((uint)(i / 3)), calls, ticks));
            }
        }

        return rows.OrderByDescending(r => r.Item3).ToArray();
    }

    /// <summary>One "PERF hle time" line: the thread's HLE wall time per frame and its busiest functions (ms/frame,
    /// calls/frame).</summary>
    public static string ThreadLine(string thread, IReadOnlyList<(string Function, long Calls, long Ticks)> rows, long frames, double tickMs, long fromFrame, long toFrame)
    {
        double total = rows.Sum(r => r.Ticks) * tickMs / frames;
        IEnumerable<string> top = rows.Take(FunctionsPerThread)
            .Select(r => $"{r.Function} {r.Ticks * tickMs / frames:F2} ({r.Calls / (double)frames:F1})");
        return $"PERF hle time {thread} (frames {fromFrame}-{toFrame}): in HLE {total:F2} ms/frame; {string.Join(", ", top)}";
    }
}
