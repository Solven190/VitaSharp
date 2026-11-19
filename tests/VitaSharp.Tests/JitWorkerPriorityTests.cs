using VitaSharp.Core.Cpu;
using VitaSharp.Core.Util;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The JIT workers are on the guest's critical path: a guest thread runs a hot block in the interpreter until a worker
/// has compiled it. Below the guest threads' host priority, guest threads that keep their CPUs busy (polling loops)
/// leave the workers no time at all, and the guest crawls in the interpreter for tens of seconds.
/// </summary>
public sealed class JitWorkerPriorityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void JitWorkersAreNeverBelowTheGuestThreads(bool hostPriorityOn)
    {
        if (Environment.GetEnvironmentVariable(EnvVars.HostPrio) != null)
        {
            return; // the environment variable overrides the setting under test
        }

        bool saved = HostThreads.Enabled;
        HostThreads.Enabled = hostPriorityOn;
        try
        {
            ThreadPriority guest = HostThreads.PriorityOf(HostThreads.Role.Guest);
            Assert.True(Jit.WorkerPriority >= guest, $"JIT workers at {Jit.WorkerPriority}, guest threads at {guest}");
            Assert.True(Jit.WorkerPriority >= ThreadPriority.Normal, $"JIT workers at {Jit.WorkerPriority}");
        }
        finally
        {
            HostThreads.Enabled = saved;
        }
    }

    /// <summary>What a worker thread really runs at after <see cref="HostThreads.Apply"/>, next to a guest thread.</summary>
    [Fact]
    public void AJitWorkerThreadRunsAtLeastAtTheGuestThreadPriority()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(EnvVars.HostPrio) != null)
        {
            return; // managed thread priorities only take effect on Windows
        }

        ThreadPriority RunAs(HostThreads.Role role, ThreadPriority initial)
        {
            ThreadPriority seen = ThreadPriority.Lowest;
            var thread = new Thread(() =>
            {
                HostThreads.Apply(role);
                seen = Thread.CurrentThread.Priority;
            })
            {
                IsBackground = true,
                Priority = initial,
            };
            thread.Start();
            thread.Join();
            return seen;
        }

        ThreadPriority guest = RunAs(HostThreads.Role.Guest, ThreadPriority.Normal);
        ThreadPriority worker = RunAs(HostThreads.Role.JitWorker, Jit.WorkerPriority);
        Assert.True(worker >= guest, $"JIT worker at {worker}, guest thread at {guest}");
    }
}
