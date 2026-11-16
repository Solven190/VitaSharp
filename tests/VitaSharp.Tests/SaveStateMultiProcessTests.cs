using VitaSharp.Core;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// Save states under the Vita shell: the system's processes (the shell and an application it opened) are saved and
/// loaded together, each with its own address space, threads and HLE state, around the one kernel they share. On the
/// Vita each process has its own user address space (the shell and a game are both linked at 0x81000000) and the kernel
/// object table is the system's. A save with a second process running was refused ("save states support a game run
/// directly (one process)"). Synthetic: two <see cref="SyntheticTitle"/>s (the first process as the shell, the second
/// spawned next to it), each with a counting thread (an A32 loop: count, sceKernelDelayThread, again).
/// </summary>
[Collection("guest")]
public sealed class SaveStateMultiProcessTests : IDisposable
{
    private const uint DelayThread = 0x4B675D05;

    /// <summary>A word of the titles' segment no code or data uses: each process writes its own value there.</summary>
    private const uint MarkerAddress = SyntheticTitle.Base + 0xF00;

    private readonly HleHarness _harness;
    private readonly Emulator _shell;
    private readonly List<Emulator> _apps = [];

    public SaveStateMultiProcessTests(GuestMemoryFixture fixture)
    {
        // the titles' segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "ssmulti");
        _shell = _harness.Emu;
        _shell.LoadTitle(SyntheticTitle.Install(_harness.Root, "VSTS00001"));
        _shell.Start();
    }

    public void Dispose()
    {
        foreach (Emulator app in _apps)
        {
            app.Stop();
        }

        _shell.Stop();
        _harness.Dispose();
    }

    /// <summary>The second process, spawned by the first as the shell opens an application; returns once it runs.</summary>
    private Emulator OpenApp()
    {
        using var started = new ManualResetEventSlim();
        Emulator app = _shell.SpawnProcess(SyntheticTitle.Install(_harness.Root, "VSTS00002"), "eboot.bin", [], started: _ => started.Set());
        _apps.Add(app);
        Assert.True(started.Wait(60_000), "the application did not start");
        HleHarness.Until(() => app.MainModule != null && app.Kernel.Threads.Any(), "the application's main thread");
        return app;
    }

    /// <summary>Starts a thread of <paramref name="process"/> that counts in its user memory and sleeps between counts;
    /// returns the counter's address (in that process's address space).</summary>
    private static uint StartCounter(Emulator process, string name)
    {
        uint counter, code;
        using (ProcessScope.Enter(process))
        {
            counter = process.Memory.Alloc(0x10, "test:counter");
            code = process.Memory.Alloc(0x40, "test:counter code");
            GuestMemory.Fill(counter, 0, 0x10);
            uint stub = code + 0x30;
            Linker.WriteHleStub(stub, process.Hle.GetHleFunctionIndex(DelayThread, 0, "SceThreadmgr"), DelayThread);
            uint[] words =
            [
                0xE59F4018, // ldr r4, [pc, #24]   (the counter)
                0xE5945000, // ldr r5, [r4]
                0xE2855001, // add r5, r5, #1
                0xE5845000, // str r5, [r4]
                0xE3A00E3E, // mov r0, #0x3E0      (992 us)
                0xE59FC008, // ldr r12, [pc, #8]   (the delay stub)
                0xE12FFF3C, // blx r12
                0xEAFFFFF7, // b to the start
                counter,
                stub,
            ];
            for (int i = 0; i < words.Length; i++)
            {
                GuestMemory.W32(code + (uint)i * 4, words[i]);
            }

            GuestThread thread = process.Kernel.CreateThread(name, code, 0x40, 0x4000, 0, 0x70000);
            thread.Start(0, 0);
        }

        return counter;
    }

    private static uint Read(Emulator process, uint address)
    {
        using (AddressSpace.Use(process.Space))
        {
            return GuestMemory.R32(address);
        }
    }

    private static void Write(Emulator process, uint address, uint value)
    {
        using (AddressSpace.Use(process.Space))
        {
            GuestMemory.W32(address, value);
        }
    }

    [Fact]
    public void TheShellAndAnAppItOpenedAreSavedTogetherAndBothRunOnAfterALoad()
    {
        Emulator app = OpenApp();
        Assert.NotSame(_shell.Space, app.Space);
        uint shellCounter = StartCounter(_shell, "VsTestShellCounter");
        uint appCounter = StartCounter(app, "VsTestAppCounter");
        // the same address of both executables' segment holds each process's own value
        Write(_shell, MarkerAddress, 0x5E11_0001);
        Write(app, MarkerAddress, 0xA990_0002);
        HleHarness.Until(() => Read(_shell, shellCounter) > 5 && Read(app, appCounter) > 5, "the first counts of both threads");

        string path = Path.Combine(_harness.Root, "state.vss");
        SaveStates.Save(_shell, path, timeoutMs: 60_000);
        Write(_shell, MarkerAddress, 0x0BAD_0001);
        Write(app, MarkerAddress, 0x0BAD_0002);
        HleHarness.Until(() => Read(_shell, shellCounter) > 200 && Read(app, appCounter) > 200, "counts after the save");
        uint shellBefore = Read(_shell, shellCounter), appBefore = Read(app, appCounter);

        SaveStates.Load(_shell, path);

        // each address space has its own saved memory back
        Assert.Equal(0x5E11_0001u, Read(_shell, MarkerAddress));
        Assert.Equal(0xA990_0002u, Read(app, MarkerAddress));
        uint shellAfter = Read(_shell, shellCounter), appAfter = Read(app, appCounter);
        Assert.True(shellAfter < shellBefore, $"the shell's memory was not restored ({shellAfter} after the load, {shellBefore} before)");
        Assert.True(appAfter < appBefore, $"the application's memory was not restored ({appAfter} after the load, {appBefore} before)");

        // each thread went back to its own process and both count on from the saved values
        GuestThread shellThread = Assert.Single(_shell.Kernel.Threads, t => t.Name == "VsTestShellCounter");
        GuestThread appThread = Assert.Single(app.Kernel.Threads, t => t.Name == "VsTestAppCounter");
        Assert.Equal(_shell.Pid, shellThread.Pid);
        Assert.Equal(app.Pid, appThread.Pid);
        Assert.DoesNotContain(_shell.Kernel.Threads, t => t.Name == "VsTestAppCounter");
        Assert.NotNull(app.Kernel.MainThread);
        Assert.Same(app, app.Kernel.MainThread.Process);
        HleHarness.Until(() => Read(_shell, shellCounter) > shellAfter + 5 && Read(app, appCounter) > appAfter + 5, "counts of both threads after the load");

        // and the loaded system saves and loads again
        SaveStates.Save(_shell, path, timeoutMs: 60_000);
        SaveStates.Load(_shell, path);
        HleHarness.Until(() => Read(app, appCounter) > appAfter + 10, "the application counts after a second load");
    }

    [Fact]
    public void AStateIsRefusedWhenOtherProcessesRunThanWhenItWasSaved()
    {
        string path = Path.Combine(_harness.Root, "alone.vss");
        SaveStates.Save(_shell, path, timeoutMs: 60_000);

        Emulator app = OpenApp();
        uint appCounter = StartCounter(app, "VsTestAppCounter");
        HleHarness.Until(() => Read(app, appCounter) > 5, "the application counts");

        SnapshotException refused = Assert.Throws<SnapshotException>(() => SaveStates.Load(_shell, path));
        Assert.Contains("open the same applications", refused.Message, StringComparison.Ordinal);

        // refused before anything stopped: the application runs on
        uint before = Read(app, appCounter);
        HleHarness.Until(() => Read(app, appCounter) > before + 5, "the application counts after the refused load");
    }
}
