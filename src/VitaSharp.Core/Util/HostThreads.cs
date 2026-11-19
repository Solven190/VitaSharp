using System.Runtime.InteropServices;

namespace VitaSharp.Core.Util;

/// <summary>
/// Host scheduling priority of the emulator's latency-critical threads: the guest threads (emulated cores), the JIT
/// workers and the GPU worker run above normal priority, so other programs on the PC do not take their time slices.
/// Measured in docs/notes/thread-priority.md: no change on an idle machine, but with every logical CPU busy elsewhere
/// Sonic Mania went from 19-20 to 31.6 fps (445 to 19 frames over 50 ms).
/// The JIT workers are never below the guest threads: a guest thread runs a block in the interpreter until a worker
/// has compiled it, so a starved worker stalls the guest (Killzone Mercenary's four guest threads polling
/// sceKernelDelayThread kept below-normal workers off the CPU for 14-65 s at boot). Dolphin, FEX and Box64 compile on
/// the guest thread itself, at its priority.
/// Each thread calls <see cref="Apply"/> on itself when it starts.
/// Windows: THREAD_PRIORITY_ABOVE_NORMAL. macOS: QOS_CLASS_USER_INTERACTIVE. Linux: unchanged (raising a thread's
/// priority needs CAP_SYS_NICE).
/// Off: <c>--host-prio=off</c> or <c>VITASHARP_HOST_PRIO=0</c> (the environment variable wins).
/// </summary>
public static class HostThreads
{
    public enum Role
    {
        Guest, Gpu, JitWorker
    }

    /// <summary>Set by the front end from <c>--host-prio=on|off</c> before the emulator starts threads.</summary>
    public static bool Enabled = true;

    private static bool On => Environment.GetEnvironmentVariable(EnvVars.HostPrio) switch
    {
        "0" or "off" or "false" or "no" => false,
        "1" or "on" or "true" or "yes" => true,
        _ => Enabled,
    };

    /// <summary>The managed priority a thread of <paramref name="role"/> runs at (Windows; every role gets the same
    /// boost today, and normal when the boost is off).</summary>
    public static ThreadPriority PriorityOf(Role role) => On ? ThreadPriority.AboveNormal : ThreadPriority.Normal;

    /// <summary>Raises the calling thread's host priority for its role.</summary>
    public static void Apply(Role role)
    {
        if (!On)
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0);
                return;
            }
            if (OperatingSystem.IsWindows())
            {
                Thread.CurrentThread.Priority = PriorityOf(role);
            }
        }
        catch (Exception e)
        {
            Log.WarnOnce("host-prio-" + role, $"host thread priority for {role} not set: {e.Message}");
        }
    }

    private const uint QOS_CLASS_USER_INTERACTIVE = 0x21;
    [DllImport("libc")] private static extern int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);
}
