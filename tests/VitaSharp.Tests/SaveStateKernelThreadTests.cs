using VitaSharp.Core;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// A save state holds the kernel process's threads too: a kernel module's own thread (a plugin's worker, say) is
/// saved with the game and runs on after a load, from the saved registers, as a kernel thread. Since the kernel
/// became one process for the whole system, a save with such a thread running was refused ("save states do not hold
/// the kernel's threads yet"). Runs <see cref="SyntheticTitle"/> in the process and a synthetic kernel thread (an A32
/// loop in kernel memory: count, ksceKernelDelayThread, again).
/// </summary>
[Collection("guest")]
public sealed class SaveStateKernelThreadTests : IDisposable
{
    private const uint ThreadmgrForDriver = 0xE2C40624, DelayThread = 0x4B675D05;
    private readonly HleHarness _harness;
    private readonly Emulator _emu;

    public SaveStateKernelThreadTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "sskthread");
        _emu = _harness.Emu;
        _emu.LoadTitle(SyntheticTitle.Install(_harness.Root));
        _emu.Start();
    }

    public void Dispose()
    {
        _emu.Stop();
        _harness.Dispose();
    }

    /// <summary>Starts a kernel thread of the kernel process that counts in kernel memory and sleeps between counts;
    /// returns the counter's address.</summary>
    private uint StartKernelCounter(Emulator kernel)
    {
        uint counter, code;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            counter = _emu.Memory.Alloc(0x10, "test:counter", MemoryRegion.Kernel);
            code = _emu.Memory.Alloc(0x40, "test:counter code", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(counter, 0, 0x10);
        uint stub = code + 0x30;
        Linker.WriteHleStub(stub, kernel.Hle.GetHleFunctionIndex(DelayThread, ThreadmgrForDriver, "SceThreadmgrForDriver"), DelayThread);
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

        using (ProcessScope.Enter(kernel))
        {
            GuestThread thread = kernel.Kernel.CreateThread("VsTestKernelWorker", code, 0x40, 0x4000, 0, 0x70000);
            thread.KernelMode = true;
            thread.Start(0, 0);
        }

        return counter;
    }

    [Fact]
    public void AKernelModulesThreadIsSavedAndRunsOnAfterALoad()
    {
        Emulator kernel = _emu.Vita.KernelProcessFor(_emu);
        uint counter = StartKernelCounter(kernel);
        HleHarness.Until(() => GuestMemory.R32(counter) > 5, "the kernel thread's first counts");

        string path = Path.Combine(_harness.Root, "state.vss");
        SaveStates.Save(_emu, path, timeoutMs: 60_000);
        HleHarness.Until(() => GuestMemory.R32(counter) > 200, "counts after the save");
        uint beforeLoad = GuestMemory.R32(counter);

        SaveStates.Load(_emu, path);
        uint afterLoad = GuestMemory.R32(counter);
        Assert.True(afterLoad < beforeLoad, $"the kernel memory was not restored ({afterLoad} after the load, {beforeLoad} before)");

        // the restored thread is the kernel process's, a kernel thread, and counts on from the saved value
        GuestThread worker = Assert.Single(kernel.Kernel.Threads, t => t.Name == "VsTestKernelWorker");
        Assert.True(worker.KernelMode);
        Assert.Equal(KernelSpace.KernelPid, worker.Pid);
        Assert.DoesNotContain(_emu.Kernel.Threads, t => t.Name == "VsTestKernelWorker");
        HleHarness.Until(() => GuestMemory.R32(counter) > afterLoad + 5, "counts after the load");
    }

    /// <summary>
    /// A title thread blocked in sceKernelWaitSema when the state is saved is saved in its wait: after a load it is
    /// still waiting (the semaphore's count and the counter in its memory are the saved ones, whatever happened after
    /// the save), and a signal wakes it from the saved registers, after which it waits again. Guest code: count the
    /// wake-ups in user memory, wait on the semaphore, again.
    /// </summary>
    [Fact]
    public void AThreadWaitingOnASemaphoreIsSavedInItsWaitAndWakesAfterALoad()
    {
        const uint WaitSema = 0x0C7B834B;
        ThreadMgr threadMgr = _harness.Module<ThreadMgr>();
        uint counter, code;
        int sema;
        using (ProcessScope.Enter(_emu))
        {
            counter = _emu.Memory.Alloc(0x10, "test:wake counter");
            code = _emu.Memory.Alloc(0x60, "test:sema waiter code");
            sema = threadMgr.sceKernelCreateSema("VsTestWaitSema", 0, 0, 1, 0);
        }

        Assert.True(sema > 0, $"sceKernelCreateSema: 0x{sema:X8}");
        GuestMemory.Fill(counter, 0, 0x10);
        uint stub = code + 0x40;
        Linker.WriteHleStub(stub, _emu.Hle.GetHleFunctionIndex(WaitSema, 0, "SceLibKernel"), WaitSema);
        uint[] words =
        [
            0xE59F4020, // ldr r4, [pc, #32]   (the counter)
            0xE59F0020, // ldr r0, [pc, #32]   (the semaphore)
            0xE3A01001, // mov r1, #1
            0xE3A02000, // mov r2, #0          (no timeout)
            0xE59FC018, // ldr r12, [pc, #24]  (the wait stub)
            0xE12FFF3C, // blx r12
            0xE5945000, // ldr r5, [r4]
            0xE2855001, // add r5, r5, #1
            0xE5845000, // str r5, [r4]
            0xEAFFFFF6, // b to the semaphore load
            counter,
            (uint)sema,
            stub,
        ];
        for (int i = 0; i < words.Length; i++)
        {
            GuestMemory.W32(code + (uint)i * 4, words[i]);
        }

        GuestThread waiter;
        using (ProcessScope.Enter(_emu))
        {
            waiter = _emu.Kernel.CreateThread("VsTestSemaWaiter", code, 0x40, 0x4000, 0, 0x70000);
            waiter.Start(0, 0);
        }

        HleHarness.Until(() => waiter.Status == ThreadStatus.Waiting, "the thread waits on the semaphore");
        Assert.Equal(0u, GuestMemory.R32(counter));

        string path = Path.Combine(_harness.Root, "state.vss");
        SaveStates.Save(_emu, path, timeoutMs: 60_000);
        Assert.Equal(0, Signal(threadMgr, sema));
        HleHarness.Until(() => GuestMemory.R32(counter) == 1, "the wake-up after the save");

        SaveStates.Load(_emu, path);
        Assert.Equal(0u, GuestMemory.R32(counter));
        GuestThread restored = Assert.Single(_emu.Kernel.Threads, t => t.Name == "VsTestSemaWaiter");
        HleHarness.Until(() => restored.Status == ThreadStatus.Waiting, "the loaded thread waits on the semaphore");
        Assert.Equal(0u, GuestMemory.R32(counter));
        Assert.Equal(0, Signal(threadMgr, sema));
        HleHarness.Until(() => GuestMemory.R32(counter) == 1, "the wake-up after the load");
        HleHarness.Until(() => restored.Status == ThreadStatus.Waiting, "the thread waits again");
        Assert.Equal(1u, GuestMemory.R32(counter));
    }

    private int Signal(ThreadMgr threadMgr, int sema)
    {
        using (ProcessScope.Enter(_emu))
        {
            return threadMgr.sceKernelSignalSema(sema, 1);
        }
    }

    /// <summary>A kernel module that started (module_start on a kernel thread of the kernel process, gone once it
    /// returned) leaves nothing that keeps a state from being saved: the thread's stack and TLS were kernel memory and
    /// went with it. They were blocks of the kernel process's own address space, and a save refused "another address
    /// space is in use" as soon as a kernel plugin had loaded.</summary>
    [Fact]
    public void AStateIsSavedAfterAKernelModuleStarted()
    {
        string tai = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        File.WriteAllBytes(Path.Combine(tai, "started.skprx"), new SceModuleBuilder("StartedKernelModule").Start(0).Build());
        LoadedModule module = _emu.LoadModule("ur0:tai/started.skprx", kernel: true);
        Assert.Equal(Emulator.ModuleStartSuccess, _emu.StartKernelModule(module, 0, 0));
        Assert.DoesNotContain(_emu.Vita.KernelProcess!.Kernel.Threads, t => t.Name.StartsWith("start:", StringComparison.Ordinal));

        string path = Path.Combine(_harness.Root, "state.vss");
        SaveStates.Save(_emu, path, timeoutMs: 60_000);
        SaveStates.Load(_emu, path);
        Assert.Contains(HleHarness.KernelModules(_emu).Values, m => m.Name == "StartedKernelModule");
    }
}
