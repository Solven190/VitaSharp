using VitaSharp.Core.Kernel;

namespace VitaSharp.Core.Util;

/// <summary>
/// "PERF states" in the periodic summary (VITASHARP_THREAD_STATES=1): what each guest thread did since the previous
/// summary, sampled every 5 ms on a background thread: waiting (on what), running guest code or an HLE call (which),
/// blocked in the host inside a call, waiting for an emulated core, or held by CPU pacing. The phone has no sampler like
/// the desktop's --profile, and a slowdown that grows over a session (Minecraft on Android, 45 to 2 fps in 8 min) needs
/// to show which threads spin, wait or starve, window by window.
/// </summary>
public static class ThreadStateSampler
{
    private static readonly object s_lock = new();
    private static readonly Dictionary<string, Dictionary<string, int>> s_window = new();
    private static readonly Dictionary<(string State, Hle.HleFunction Function), string> s_names = new();
    private static Thread? s_thread;
    private static int s_samples;

    /// <summary>True when VITASHARP_THREAD_STATES=1.</summary>
    public static bool Enabled { get; } = Environment.GetEnvironmentVariable(EnvVars.ThreadStates) == "1";

    /// <summary>The window's lines (and a new window); starts the sampler on the first call.</summary>
    public static IEnumerable<string> Lines(Emulator emu)
    {
        if (!Enabled)
        {
            return [];
        }

        lock (s_lock)
        {
            if (s_thread == null)
            {
                s_thread = new Thread(() => Run(emu)) { IsBackground = true, Name = "Thread state sampler", Priority = ThreadPriority.BelowNormal };
                s_thread.Start();
                return [];
            }

            var lines = new List<string>();
            int samples = Math.Max(1, s_samples);
            foreach ((string thread, Dictionary<string, int> states) in s_window
                .OrderByDescending(kv => kv.Value.Where(s => !s.Key.StartsWith("wait:", StringComparison.Ordinal) && s.Key != "Dormant").Sum(s => s.Value)).Take(10))
            {
                IEnumerable<string> top = states.OrderByDescending(s => s.Value).Take(5).Select(s => $"{s.Key} {s.Value * 100.0 / samples:F0}%");
                lines.Add($"PERF states {thread}: {string.Join(", ", top)}");
            }

            s_window.Clear();
            s_samples = 0;
            return lines;
        }
    }

    private static void Run(Emulator emu)
    {
        while (emu.Running || emu.MainModule == null)
        {
            Thread.Sleep(5);
            if (EmulationPause.IsPaused)
            {
                continue;
            }

            lock (s_lock)
            {
                s_samples++;
                foreach (GuestThread t in emu.Kernel.Threads)
                {
                    string state = Classify(t);
                    if (!s_window.TryGetValue(t.Name, out Dictionary<string, int>? counts))
                    {
                        s_window[t.Name] = counts = new Dictionary<string, int>();
                    }

                    counts[state] = counts.GetValueOrDefault(state) + 1;
                }
            }
        }
    }

    /// <summary>The desktop sampler's states (Desktop/Sampler.cs), with the HLE call a running or host-blocked thread is in.</summary>
    private static string Classify(GuestThread t)
    {
        string state;
        if (t.Status == ThreadStatus.Waiting)
        {
            state = "wait:" + t.WaitingOn;
        }
        else if (t.Core.InThrottle)
        {
            state = "throttle";
        }
        else if (t.Status == ThreadStatus.Running && !t.HasCore && Scheduler.Cores > 0)
        {
            state = t.BlockedInHost ? "host-block" : "core-wait";
        }
        else
        {
            state = t.Status.ToString();
        }

        if (t.CurrentHle is { } function && state is "host-block" or "Running")
        {
            if (!s_names.TryGetValue((state, function), out string? named))
            {
                s_names[(state, function)] = named = state + ":" + function.Name;
            }

            state = named;
        }

        return state;
    }
}
