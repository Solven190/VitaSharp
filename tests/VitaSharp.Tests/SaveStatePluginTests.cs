using VitaSharp.Core;
using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Save states with the emulator option "Enable Kernel Plugins" on: the plugin framework's hooks and data injections
/// (the kernel process's, where kernel plugins call it) are part of the state. A load puts back the hooks the saved
/// session had, as they were (the patched code, the thunks, the reference records, the import stubs a hook on an HLE
/// function took, also for stubs bound after the load) and drops the ones made after the save, without loading any
/// plugin again. Before, a save with a kernel plugin's hook in place was refused ("module kernel PluginFramework: its
/// state is not supported by save states yet"). Synthetic kernel modules (SceModuleBuilder) and <see cref="SyntheticTitle"/>.
/// </summary>
[Collection("guest")]
public sealed unsafe class SaveStatePluginTests : IDisposable
{
    private const uint TargetLib = 0x0BAD7A60, TargetNid = 0x2468ACE0;
    private const uint ThreadmgrLib = 0x859A24B1, GetThreadId = 0x0FB972F9;
    private readonly HleHarness _harness;
    private readonly Emulator _emu;

    public SaveStatePluginTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "ssplugin", kernelPlugins: true);
        _emu = _harness.Emu;
        _emu.LoadTitle(SyntheticTitle.Install(_harness.Root));
        _emu.Start();
    }

    public void Dispose()
    {
        _emu.Stop();
        _harness.Dispose();
    }

    private Emulator Kernel => _emu.Vita.KernelProcessFor(_emu);

    /// <summary>The kernel process's framework: the one kernel plugins call.</summary>
    private PluginFramework Framework => Kernel.Hle.Modules.OfType<PluginFramework>().Single();

    private string StatePath => Path.Combine(_harness.Root, "state.vss");

    private LoadedModule Load(string file, SceModuleBuilder builder, bool kernel)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), builder.Build());
        return _emu.LoadModule("ur0:tai/" + file, kernel);
    }

    /// <summary>A kernel module exporting a function that returns 7.</summary>
    private uint LoadTarget()
    {
        var builder = new SceModuleBuilder("SsHookTarget");
        int returns7 = builder.ReturnConstant(7);
        builder.Export("SsHookTargetLib", TargetLib, 0x0001, (TargetNid, returns7));
        LoadedModule module = Load("sstarget.skprx", builder, kernel: true);
        return SceModuleBuilder.FunctionAddress(module.Segments[0].Address, returns7);
    }

    /// <summary>A kernel plugin with functions returning <paramref name="values"/> (the hook functions).</summary>
    private uint[] LoadHooks(params byte[] values)
    {
        var builder = new SceModuleBuilder("SsHookProvider");
        int[] functions = values.Select(builder.ReturnConstant).ToArray();
        LoadedModule module = Load("sshooks.skprx", builder, kernel: true);
        return functions.Select(f => SceModuleBuilder.FunctionAddress(module.Segments[0].Address, f)).ToArray();
    }

    /// <summary>A user module of the game's process that calls sceKernelGetThreadId through its import stub.</summary>
    private uint LoadGameCaller(string name)
    {
        SceModuleBuilder builder = new SceModuleBuilder(name).Import("SceThreadmgr", ThreadmgrLib, GetThreadId);
        int call = builder.CallImport(0, 0);
        LoadedModule module = Load(name + ".suprx", builder, kernel: false);
        return SceModuleBuilder.FunctionAddress(module.Segments[0].Address, call);
    }

    /// <summary>The firmware export entry of sceKernelGetThreadId as the kernel has it (an SVC stub in kernel memory).</summary>
    private uint KernelExportEntry()
    {
        uint entry;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            entry = _emu.Memory.Alloc(0x10, "test:export entry", MemoryRegion.Kernel);
        }

        Linker.WriteHleStub(entry, Kernel.Hle.GetHleFunctionIndex(GetThreadId, ThreadmgrLib, "SceThreadmgr"), GetThreadId);
        return entry;
    }

    private uint KernelWord(uint value)
    {
        uint word;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            word = _emu.Memory.Alloc(0x10, "test:kernel word", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(word, 0, 0x10);
        GuestMemory.W32(word, value);
        return word;
    }

    /// <summary>A string in kernel memory (what a kernel plugin passes).</summary>
    private uint KernelString(string text)
    {
        uint address;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            address = _emu.Memory.Alloc(0x40, "test:kernel string", MemoryRegion.Kernel);
        }

        GuestMemory.Fill(address, 0, 0x40);
        GuestMemory.WriteCString(address, text);
        return address;
    }

    /// <summary>Calls a framework function as a kernel plugin does (in the kernel process).</summary>
    private int KernelCall(string name, params object?[] args)
    {
        using (ProcessScope.Enter(Kernel))
        {
            return Call(Framework, name, args);
        }
    }

    private uint RunGuest(uint function)
    {
        uint result = 0;
        _harness.OnGuest(t => result = t.CallGuest(function));
        return result;
    }

    /// <summary>
    /// A kernel plugin's export hook and data injection are saved: after the save the hook is released, the injection
    /// undone and another hook installed; the load puts back the saved hook (the function runs it, its reference and
    /// UID release it, which puts the original code back) and the saved injection (its UID writes the original bytes
    /// back), and the hook made after the save is gone.
    /// </summary>
    [Fact]
    public void AKernelPluginsHookAndInjectionComeBackWithALoad()
    {
        // the module had no save state support: a save after a plugin used it was refused naming it
        Assert.False(SnapshotSchema.IsStateful(typeof(PluginFramework)));
        uint target = LoadTarget();
        uint[] hooks = LoadHooks(42, 43);
        uint data = KernelWord(1);
        uint source = KernelWord(99);
        uint hookRef = KernelWord(0);
        uint laterRef = KernelWord(0);

        int uid = KernelCall("HookFunctionExportForKernel", KernelSpace.KernelPid, hookRef, KernelString("SsHookTarget"), TargetLib, TargetNid, hooks[0]);
        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        int injection = KernelCall("InjectAbsForKernel", KernelSpace.KernelPid, data, source, 4u);
        Assert.True(injection > 0, $"injection returned 0x{injection:X8}");
        Assert.Equal(42u, RunGuest(target));
        Assert.Equal(99u, GuestMemory.R32(data));
        uint record = GuestMemory.R32(hookRef);

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);

        // the session goes on: the hook and the injection are undone, another hook is installed
        Assert.Equal(0, KernelCall("HookReleaseForKernel", uid, record));
        Assert.Equal(0, KernelCall("InjectReleaseForKernel", injection));
        Assert.Equal(1u, GuestMemory.R32(data));
        int later = KernelCall("HookFunctionExportForKernel", KernelSpace.KernelPid, laterRef, KernelString("SsHookTarget"), TargetLib, TargetNid, hooks[1]);
        Assert.True(later > 0, $"second hook returned 0x{later:X8}");
        Assert.Equal(43u, RunGuest(target));

        SaveStates.Load(_emu, StatePath);

        Assert.Equal(42u, RunGuest(target));
        Assert.Equal(99u, GuestMemory.R32(data));
        Assert.Equal(record, GuestMemory.R32(hookRef));
        // the framework's records are the saved ones: the saved UIDs undo the saved patches
        Assert.Equal(0, KernelCall("InjectReleaseForKernel", injection));
        Assert.Equal(1u, GuestMemory.R32(data));
        Assert.Equal(0, KernelCall("HookReleaseForKernel", uid, record));
        Assert.Equal(7u, RunGuest(target));
        Assert.NotEqual(0, KernelCall("HookReleaseForKernel", later, GuestMemory.R32(laterRef)));
    }

    /// <summary>
    /// A hook on an HLE function's firmware export takes the game's import stubs of it: saved with the hook in place
    /// and loaded after the hook was released, the game's call runs the hook again, and so does a module of the game
    /// loaded after the load (the redirection of stubs bound later is part of the state).
    /// </summary>
    [Fact]
    public void TheGamesImportStubOfAHookedHleFunctionRunsTheHookAfterALoad()
    {
        uint gameCall = LoadGameCaller("SsGameCaller");
        uint[] hooks = LoadHooks(42);
        uint entry = KernelExportEntry();
        uint hookRef = KernelWord(0);
        uint tid = RunGuest(gameCall);
        Assert.NotEqual(42u, tid);

        int uid;
        using (ProcessScope.Enter(Kernel))
        {
            uid = Convert.ToInt32(Invoke(Framework, "HookAbsolute", KernelSpace.KernelPid, hookRef, entry, hooks[0], GetThreadId));
        }

        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        Assert.Equal(42u, RunGuest(gameCall));
        uint record = GuestMemory.R32(hookRef);

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        Assert.Equal(0, KernelCall("HookReleaseForKernel", uid, record));
        Assert.NotEqual(42u, RunGuest(gameCall));

        SaveStates.Load(_emu, StatePath);

        Assert.Equal(42u, RunGuest(gameCall));
        uint laterCall = LoadGameCaller("SsLaterCaller");
        Assert.Equal(42u, RunGuest(laterCall));
        Assert.Equal(0, KernelCall("HookReleaseForKernel", uid, record));
        Assert.NotEqual(42u, RunGuest(gameCall));
        Assert.NotEqual(42u, RunGuest(laterCall));
    }

    /// <summary>
    /// The other way round: saved before the hook, loaded with the hook in place, the game's import stub calls the HLE
    /// function again and so does a module of the game loaded after the load (the running session's redirection goes).
    /// </summary>
    [Fact]
    public void AHookMadeAfterTheSaveIsGoneAfterALoad()
    {
        uint gameCall = LoadGameCaller("SsGameCaller");
        uint[] hooks = LoadHooks(42);
        uint entry = KernelExportEntry();
        uint hookRef = KernelWord(0);
        uint tid = RunGuest(gameCall);
        Assert.NotEqual(42u, tid);

        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        int uid;
        using (ProcessScope.Enter(Kernel))
        {
            uid = Convert.ToInt32(Invoke(Framework, "HookAbsolute", KernelSpace.KernelPid, hookRef, entry, hooks[0], GetThreadId));
        }

        Assert.True(uid > 0, $"hook returned 0x{uid:X8}");
        Assert.Equal(42u, RunGuest(gameCall));

        SaveStates.Load(_emu, StatePath);

        Assert.NotEqual(42u, RunGuest(gameCall));
        uint laterCall = LoadGameCaller("SsLaterCaller");
        Assert.NotEqual(42u, RunGuest(laterCall));
    }

    /// <summary>The exception handlers kernel modules registered with SceExcpmgr (the kernel process's) are the saved
    /// ones after a load: one registered after the save is gone.</summary>
    [Fact]
    public void TheKernelsExceptionHandlersAreTheSavedOnesAfterALoad()
    {
        ProcessmgrKernel manager = Kernel.Hle.Modules.OfType<ProcessmgrKernel>().Single();
        uint first = KernelWord(0);
        uint second = KernelWord(0);
        using (ProcessScope.Enter(Kernel))
        {
            Assert.Equal(0, Call(manager, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionUndefined, 1u, first));
        }

        List<uint> saved = manager.ExceptionHandlers(GuestThread.ExceptionUndefined);
        SaveStates.Save(_emu, StatePath, timeoutMs: 60_000);
        using (ProcessScope.Enter(Kernel))
        {
            Assert.Equal(0, Call(manager, "sceKernelRegisterExceptionHandler", GuestThread.ExceptionUndefined, 0u, second));
        }

        Assert.NotEqual(saved, manager.ExceptionHandlers(GuestThread.ExceptionUndefined));
        SaveStates.Load(_emu, StatePath);
        Assert.Equal(saved, manager.ExceptionHandlers(GuestThread.ExceptionUndefined));
        Assert.Equal((first & ~3u) + 8, manager.ExceptionVector(GuestThread.ExceptionUndefined));
    }

    /// <summary>A thread saved inside a kernel exception handler keeps its mode, banked registers, saved program status
    /// and fault registers: restored in user mode with the handler's stack pointer, its return (SUBS PC, LR / RFE)
    /// would have gone wrong.</summary>
    [Fact]
    public void AThreadInAnExceptionModeKeepsItsPrivilegedStateInTheRecord()
    {
        GuestThread thread;
        using (ProcessScope.Enter(Kernel))
        {
            thread = Kernel.Kernel.CreateThread("VsTestExceptionMode", 0, 0x40, 0x4000, 0, 0x70000);
        }

        CpuState* state = thread.Core.State;
        ExceptionModes.EnterException(state, ExceptionModes.Abort, 0x8100_0004, 0x8200_0008);
        state->BankedSp[ExceptionModes.Bank(ExceptionModes.User)] = 0x8130_0000;
        state->Ifar = 0x8100_0000;
        state->Ifsr = GuestThread.FaultTranslationPage;
        uint spsr = state->Spsr[ExceptionModes.Bank(ExceptionModes.Abort)];

        var writer = new SnapshotWriter();
        thread.WriteSnapshot(writer, GuestThread.SafeKind.Dormant);
        var restored = GuestThread.ReadSnapshot(Kernel, new SnapshotReader(writer.Data.ToArray()), out _, out _, out _);
        try
        {
            CpuState* loaded = restored.Core.State;
            Assert.Equal(ExceptionModes.Abort, restored.CpuMode);
            Assert.Equal(0x8100_0004u, loaded->R[14]);
            Assert.Equal(0x8130_0000u, loaded->BankedSp[ExceptionModes.Bank(ExceptionModes.User)]);
            Assert.Equal(spsr, loaded->Spsr[ExceptionModes.Bank(ExceptionModes.Abort)]);
            Assert.Equal(0x8100_0000u, loaded->Ifar);
            Assert.Equal(GuestThread.FaultTranslationPage, loaded->Ifsr);
        }
        finally
        {
            restored.Dispose();
        }
    }
}
