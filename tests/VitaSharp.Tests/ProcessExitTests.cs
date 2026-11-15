using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;

namespace VitaSharp.Tests;

/// <summary>
/// A process ending by itself (sceKernelExitProcess, SceLibc exit/_Exit/abort). On the Vita the process is gone and the
/// system takes the screen back (the shell's LiveArea). When the game is the only process (launched directly, no
/// shell), the end of the process is the end of the session: <see cref="Emulator.ProcessExited"/> tells the host, which
/// closes the window (desktop), ends the run (headless) or leaves the game screen (Android). Before this the emulator
/// only stopped its threads and the window stayed open on the last picture (vkQuake2's Quit, 14/11/2026).
/// </summary>
[Collection("guest")]
public sealed class ProcessExitTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "process-exit");

    public void Dispose() => _harness.Dispose();

    /// <summary>Calls the exit function on a guest thread, which it ends (GuestThreadExitException).</summary>
    private void ExitOnGuest(object module, string function, object?[] args)
    {
        _harness.OnGuest(_ =>
        {
            Assert.Throws<GuestThreadExitException>(() => HleHarness.Invoke(module, function, args));
        });
    }

    [Theory]
    [InlineData("sceKernelExitProcess", 0)]
    [InlineData("sceKernelExitProcess", 3)]
    [InlineData("_sceKernelExitProcessForUser", 7)]
    public void TheLastProcessExitingEndsTheSessionWithItsStatus(string function, int status)
    {
        Emulator emu = _harness.Emu;
        emu.Running = true;
        var statuses = new List<int>();
        emu.ProcessExited += s => statuses.Add(s);

        ExitOnGuest(_harness.Module<ProcessMgr>(), function, [status]);

        Assert.Equal([status], statuses);
        Assert.Equal(status, emu.LastProcessExitStatus);
        Assert.False(emu.Running);
        Assert.Null(emu.ExecRequest);
    }

    [Theory]
    [InlineData("exit", 0)]
    [InlineData("exit", 2)]
    [InlineData("_Exit", 4)]
    [InlineData("abort", 1)]
    public void LibcExitPathsEndTheSessionToo(string function, int expected)
    {
        Emulator emu = _harness.Emu;
        emu.Running = true;
        var statuses = new List<int>();
        emu.ProcessExited += s => statuses.Add(s);
        object?[] args = function == "abort" ? [] : [expected];

        ExitOnGuest(_harness.Module<Libc>(), function, args);

        Assert.Equal([expected], statuses);
        Assert.False(emu.Running);
    }

    [Fact]
    public void ASecondExitOfTheSameProcessEndsNothingMore()
    {
        // two threads calling exit (a crash handler and the main loop): the session ends once, with the first status
        Emulator emu = _harness.Emu;
        emu.Running = true;
        int raised = 0;
        emu.ProcessExited += _ => Interlocked.Increment(ref raised);
        ProcessMgr proc = _harness.Module<ProcessMgr>();

        ExitOnGuest(proc, "sceKernelExitProcess", [5]);
        ExitOnGuest(proc, "sceKernelExitProcess", [6]);

        Assert.Equal(1, raised);
        Assert.Equal(5, emu.LastProcessExitStatus);
    }

    [Fact]
    public void ASuspendedProgramResumesInsteadOfEndingTheSession()
    {
        // an application launched over the program that started it (sceAppMgrLaunchAppByUri and the like, no shell):
        // its exit restarts that program, the session goes on
        Emulator emu = _harness.Emu;
        emu.Running = true;
        string appDir = Path.Combine(_harness.Root, "ux0", "app", "TEST00001");
        emu.SuspendedPrograms.Push((appDir, "eboot.bin"));
        int raised = 0;
        int execs = 0;
        emu.ProcessExited += _ => raised++;
        emu.ExecRequested += () => execs++;

        ExitOnGuest(_harness.Module<ProcessMgr>(), "sceKernelExitProcess", [0]);

        Assert.Equal(0, raised);
        Assert.Null(emu.LastProcessExitStatus);
        Assert.Equal(1, execs);
        Assert.Equal(("app0:eboot.bin", appDir), (emu.ExecRequest?.Path, emu.ExecRequest?.AppDir));
        Assert.Empty(emu.SuspendedPrograms);
    }

    [Fact]
    public void StoppingTheEmulatorIsNotAProcessExit()
    {
        // the host closing the window (X, the launcher's Stop) stops the emulator: that is not the game ending
        Emulator emu = _harness.Emu;
        emu.Running = true;
        int raised = 0;
        emu.ProcessExited += _ => raised++;

        emu.Stop();

        Assert.Equal(0, raised);
        Assert.Null(emu.LastProcessExitStatus);
    }
}
