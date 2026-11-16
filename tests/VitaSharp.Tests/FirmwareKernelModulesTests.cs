using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The firmware's kernel modules as another kernel module sees them, as on the Vita: any module of the plugin
/// configuration's *KERNEL section loads into the kernel, the firmware's modules are already in the kernel process's
/// module list (ksceKernelGetModuleList(KERNEL_PID), ksceKernelGetModuleCB) with their export and import tables, the
/// new module's imports bind to the firmware's export entries, and those entries are real code: code written over an
/// export entry (or over another module's import stub) with ksceKernelCpuUnrestrictedMemcpy and the cache flushes
/// runs for the callers that go through it, as kernel code that rewrites kernel memory does on the console. Everything
/// is synthetic and generic: a firmware SceSysmem in os0:kd that exports SceCpuForDriver ksceKernelAtomicAddAndGet32
/// (the FW 3.74 NIDs), modules that call it, and a function written over its entry that counts the calls and goes on
/// to the original.
/// </summary>
[Collection("guest")]
public sealed class FirmwareKernelModulesTests : IDisposable
{
    private const uint CpuForDriver = 0x40ECDB0E, AtomicAddAndGet32 = 0x5F6A8743;
    private const uint LdrPcMinus4 = 0xE51FF004;

    /// <summary>The synthetic SceSysmem's segment sizes (the firmware's own are 0x2F280 and 0x7604 on FW 3.74).</summary>
    private const uint FirmwareCodeSize = 0x3000, FirmwareDataSize = 0x604;

    /// <summary>What fills a firmware code segment VitaSharp does not run: UDF in A32, UDF and a branch back to it in
    /// Thumb.</summary>
    private const uint TrapFillWord = 0xE7FDDEFE;

    private readonly HleHarness _harness;

    public FirmwareKernelModulesTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "fwkmod", kernelPlugins: true);
        SceModuleBuilder sysmem = new SceModuleBuilder("SceSysmem").SegmentSizes(FirmwareCodeSize, FirmwareDataSize);
        int body = sysmem.ReturnConstant(0);
        sysmem.Export("SceCpuForDriver", CpuForDriver, 0x0001, (AtomicAddAndGet32, body));
        string kd = Path.Combine(_harness.Root, "os0", "kd");
        Directory.CreateDirectory(kd);
        File.WriteAllBytes(Path.Combine(kd, "sysmem.skprx"), sysmem.Build());
        FirmwareKernelImage.Forget(_harness.Root);
    }

    public void Dispose()
    {
        FirmwareKernelImage.Forget(_harness.Root);
        _harness.Dispose();
    }

    private string WritePlugin(string file, byte[] elf)
    {
        string dir = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, file), elf);
        return "ur0:tai/" + file;
    }

    /// <summary>A plugin that imports ksceKernelAtomicAddAndGet32 and exports nothing; returns it and the index of
    /// its function that calls the import with its own arguments.</summary>
    private static (SceModuleBuilder Plugin, int Caller) CallerPlugin(string name)
    {
        SceModuleBuilder plugin = new SceModuleBuilder(name).Start(0).Import("SceCpuForDriver", CpuForDriver, AtomicAddAndGet32);
        int caller = plugin.CallImport(0, 0);
        return (plugin, caller);
    }

    /// <summary>Loads the plugin configuration with <paramref name="plugins"/> as its *KERNEL modules (in order);
    /// every one must load and start.</summary>
    private List<LoadedModule> LoadKernelPlugins(params (string File, SceModuleBuilder Plugin)[] plugins)
    {
        var paths = plugins.Select(p => WritePlugin(p.File, p.Plugin.Build())).ToList();
        File.WriteAllText(Path.Combine(_harness.Root, "ur0", "tai", "config.txt"), "*KERNEL\n" + string.Join("\n", paths) + "\n");
        List<string> results = _harness.Emu.LoadPlugins();
        Assert.Equal(paths.Count, results.Count);
        Assert.All(results, r => Assert.Contains(": loaded and started (", r));
        return paths.Select(p => _harness.Emu.Kernel.Modules.Values.Single(m => m.Path == p)).ToList();
    }

    /// <summary>A module's control block as kernel code reads it: its name, export and import table ranges.</summary>
    private uint ControlBlockOf(string moduleName)
    {
        ModulemgrKernel modulemgr = _harness.Module<ModulemgrKernel>();
        uint list = _harness.Alloc(256 * 4);
        uint count = _harness.Word(256);
        uint block = _harness.Alloc(4);
        Assert.Equal(0, Call(modulemgr, "ksceKernelGetModuleList", KernelSpace.KernelPid, 0xFF, 1, list, count));
        for (uint i = 0; i < GuestMemory.R32(count); i++)
        {
            Assert.Equal(0, Call(modulemgr, "ksceKernelGetModuleCB", (int)GuestMemory.R32(list + i * 4), block));
            uint info = GuestMemory.R32(block);
            if (GuestMemory.ReadCString(GuestMemory.R32(info + 0x1C), 27) == moduleName)
            {
                return info;
            }
        }

        throw new Xunit.Sdk.XunitException($"{moduleName} is not in the kernel process's module list");
    }

    /// <summary>A function's entry in a module's export table, read from its control block as kernel code reads it.</summary>
    private uint FindExport(string moduleName, uint libNid, uint nid)
    {
        uint info = ControlBlockOf(moduleName);
        for (uint export = GuestMemory.R32(info + 0x20); export < GuestMemory.R32(info + 0x24); export += GuestMemory.Read<ushort>(export))
        {
            if (GuestMemory.R32(export + 0x10) != libNid)
            {
                continue;
            }

            uint nids = GuestMemory.R32(export + 0x18);
            uint entries = GuestMemory.R32(export + 0x1C);
            for (uint k = 0; k < GuestMemory.Read<ushort>(export + 6); k++)
            {
                if (GuestMemory.R32(nids + k * 4) == nid)
                {
                    return GuestMemory.R32(entries + k * 4);
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"{moduleName} 0x{libNid:X8} 0x{nid:X8} not in its export table");
    }

    /// <summary>A function's import stub in a module's import table (0x34-byte entries:
    /// +0x10 library NID, +0x1C NID table, +0x20 stub table).</summary>
    private uint FindImport(string moduleName, uint libNid, uint nid)
    {
        uint info = ControlBlockOf(moduleName);
        for (uint import = GuestMemory.R32(info + 0x28); import < GuestMemory.R32(info + 0x2C); import += GuestMemory.Read<ushort>(import))
        {
            if (GuestMemory.R32(import + 0x10) != libNid)
            {
                continue;
            }

            uint nids = GuestMemory.R32(import + 0x1C);
            uint stubs = GuestMemory.R32(import + 0x20);
            for (uint k = 0; k < GuestMemory.Read<ushort>(import + 6); k++)
            {
                if (GuestMemory.R32(nids + k * 4) == nid)
                {
                    return GuestMemory.R32(stubs + k * 4);
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"{moduleName} 0x{libNid:X8} 0x{nid:X8} not in its import table");
    }

    /// <summary>A jump, "ldr pc, [pc, #-4]; target", written over <paramref name="at"/> with
    /// ksceKernelCpuUnrestrictedMemcpy, then the data and instruction caches flushed for the range.</summary>
    private void WriteJump(uint at, uint hook)
    {
        SysmemKernel sysmem = _harness.Module<SysmemKernel>();
        uint patch = _harness.Alloc(8);
        GuestMemory.W32(patch, LdrPcMinus4);
        GuestMemory.W32(patch + 4, hook);
        Assert.Equal(0, Call(sysmem, "ksceKernelCpuUnrestrictedMemcpy", at, patch, 8u));
        Invoke(sysmem, "ksceKernelCpuDcacheWritebackInvalidateRange", at, 8u);
        Invoke(sysmem, "ksceKernelCpuIcacheAndL2WritebackInvalidateRange", at, 8u);
    }

    /// <summary>A jump written over an export entry, with a trampoline in kernel memory (the two instructions the patch
    /// replaces, then a jump to the rest of the function), then the patch over the entry. Returns the trampoline.</summary>
    private uint HookExport(uint entry, uint hook)
    {
        uint trampoline;
        using (ProcessScope.Enter(KernelSpace.KernelPid))
        {
            trampoline = _harness.Emu.Memory.Alloc(16, "test:trampoline", MemoryRegion.Kernel, 16);
        }

        GuestMemory.W32(trampoline, GuestMemory.R32(entry));
        GuestMemory.W32(trampoline + 4, GuestMemory.R32(entry + 4));
        GuestMemory.W32(trampoline + 8, LdrPcMinus4);
        GuestMemory.W32(trampoline + 12, entry + 8);
        WriteJump(entry, hook);
        return trampoline;
    }

    /// <summary>
    /// A counting hook in A32: adds 1 to the word at its first literal, then a call of its second literal (the
    /// trampoline or the original function) with the caller's arguments, and returns what that returned. Returns the
    /// function's index and the byte offsets of the two literals in it.
    /// </summary>
    private static (int Function, uint CounterLiteral, uint ContinueLiteral) CountingHook(SceModuleBuilder plugin)
    {
        uint[] code =
        [
            0xE92D4010, // 0 push {r4, lr}
            0xE59FC014, // 1 ldr r12, [pc, #20]   the counter (word 8)
            0xE59C4000, // 2 ldr r4, [r12]
            0xE2844001, // 3 add r4, r4, #1
            0xE58C4000, // 4 str r4, [r12]
            0xE59FC008, // 5 ldr r12, [pc, #8]    where to continue (word 9)
            0xE12FFF3C, // 6 blx r12              call it with r0-r3
            0xE8BD8010, // 7 pop {r4, pc}
            0x00000000, // 8 the counter's address
            0x00000000, // 9 the trampoline / original
        ];
        return (plugin.Arm(code), 8 * 4, 9 * 4);
    }

    /// <summary>Calls <paramref name="function"/> (a plugin's caller of ksceKernelAtomicAddAndGet32) as kernel code:
    /// adds <paramref name="value"/> to <paramref name="word"/>; returns what the call returned.</summary>
    private uint CallAsKernel(uint function, uint word, uint value)
    {
        uint result = 0;
        _harness.OnGuest(t =>
        {
            t.KernelMode = true;
            result = t.CallGuest(function, word, value);
        });
        return result;
    }

    [Fact]
    public void TheFirmwareKernelModulesAreListedWithTheirExportTablesBeforeAnyOtherKernelModule()
    {
        (SceModuleBuilder plugin, _) = CallerPlugin("CallerPlugin");
        LoadKernelPlugins(("caller.skprx", plugin));
        uint entry = FindExport("SceSysmem", CpuForDriver, AtomicAddAndGet32);
        Assert.Equal(entry, _harness.Emu.FirmwareKernel.EntryOf(CpuForDriver, AtomicAddAndGet32));
        Assert.InRange(entry, GuestMemory.KernelStart, GuestMemory.KernelEnd - 1);
        // the entry is ARM code into the HLE function: an SVC, then a return
        Assert.Equal(0xEFu, GuestMemory.R32(entry) >> 24);
        Assert.Equal(0xE12FFF1Eu, GuestMemory.R32(entry + 4));
        Assert.False(_harness.Emu.FirmwareKernel.IsPatched(CpuForDriver, AtomicAddAndGet32));
        // the firmware module belongs to the kernel process, as the Vita's kernel modules do
        Assert.Contains(_harness.Emu.Kernel.Modules.Values, m => m.IsKernel && m.Name == "SceSysmem");
    }

    [Fact]
    public void AKernelModulesImportOfAFirmwareExportRunsTheFirmwareFunction()
    {
        (SceModuleBuilder plugin, int caller) = CallerPlugin("CallerPlugin");
        LoadedModule loaded = LoadKernelPlugins(("caller.skprx", plugin))[0];
        uint word = _harness.Word(40);
        uint call = SceModuleBuilder.FunctionAddress(loaded.Segments[0].Address, caller);
        Assert.Equal(42u, CallAsKernel(call, word, 2));
        Assert.Equal(42u, GuestMemory.R32(word));
    }

    [Fact]
    public void CodeWrittenOverAnExportEntryRunsForEveryCallerAndTheMovedInstructionsStillReachTheFirmware()
    {
        (SceModuleBuilder first, int firstCaller) = CallerPlugin("CallerPlugin");
        (SceModuleBuilder second, int secondCaller) = CallerPlugin("OtherCaller");
        SceModuleBuilder hooker = new SceModuleBuilder("CountingModule").Start(0);
        (int hookFunction, uint counterLiteral, uint continueLiteral) = CountingHook(hooker);
        List<LoadedModule> loaded = LoadKernelPlugins(("caller.skprx", first), ("other.skprx", second), ("hook.skprx", hooker));

        uint hook = hooker.AddressOf(loaded[2].Segments[0].Address, hookFunction);
        uint counter = _harness.Word(0);
        uint trampoline = HookExport(FindExport("SceSysmem", CpuForDriver, AtomicAddAndGet32), hook);
        GuestMemory.W32(hook + counterLiteral, counter);
        GuestMemory.W32(hook + continueLiteral, trampoline);
        _harness.Emu.Code.Invalidate(hook, 0x28);
        Assert.True(_harness.Emu.FirmwareKernel.IsPatched(CpuForDriver, AtomicAddAndGet32));

        uint word = _harness.Word(10);
        Assert.Equal(13u, CallAsKernel(SceModuleBuilder.FunctionAddress(loaded[0].Segments[0].Address, firstCaller), word, 3));
        Assert.Equal(20u, CallAsKernel(SceModuleBuilder.FunctionAddress(loaded[1].Segments[0].Address, secondCaller), word, 7));
        Assert.Equal(20u, GuestMemory.R32(word));
        Assert.Equal(2u, GuestMemory.R32(counter));
    }

    [Fact]
    public void CodeWrittenOverAnImportStubRunsOnlyForThatModulesCalls()
    {
        (SceModuleBuilder first, int firstCaller) = CallerPlugin("CallerPlugin");
        (SceModuleBuilder second, int secondCaller) = CallerPlugin("OtherCaller");
        SceModuleBuilder hooker = new SceModuleBuilder("CountingModule").Start(0);
        (int hookFunction, uint counterLiteral, uint continueLiteral) = CountingHook(hooker);
        List<LoadedModule> loaded = LoadKernelPlugins(("caller.skprx", first), ("other.skprx", second), ("hook.skprx", hooker));

        // the import stub of CallerPlugin is rewritten; the counting function continues to the export
        uint hook = hooker.AddressOf(loaded[2].Segments[0].Address, hookFunction);
        uint counter = _harness.Word(0);
        uint stub = FindImport("CallerPlugin", CpuForDriver, AtomicAddAndGet32);
        GuestMemory.W32(hook + counterLiteral, counter);
        GuestMemory.W32(hook + continueLiteral, FindExport("SceSysmem", CpuForDriver, AtomicAddAndGet32));
        _harness.Emu.Code.Invalidate(hook, 0x28);
        WriteJump(stub, hook);

        uint word = _harness.Word(0);
        Assert.Equal(1u, CallAsKernel(SceModuleBuilder.FunctionAddress(loaded[0].Segments[0].Address, firstCaller), word, 1));
        Assert.Equal(1u, GuestMemory.R32(counter));
        Assert.Equal(3u, CallAsKernel(SceModuleBuilder.FunctionAddress(loaded[1].Segments[0].Address, secondCaller), word, 2));
        Assert.Equal(1u, GuestMemory.R32(counter));
        Assert.False(_harness.Emu.FirmwareKernel.IsPatched(CpuForDriver, AtomicAddAndGet32));
    }

    /// <summary>
    /// Kernel code that reaches a firmware module's code or data by segment offset (module_get_offset on its module
    /// id, what a kernel plugin does to find a function the firmware does not export) gets the address the console
    /// gives: the module's segments have the firmware's sizes and order, the export tables come after them.
    /// </summary>
    [Fact]
    public void AFirmwareModulesSegmentsHaveTheFirmwaresSizesSoModuleGetOffsetAcceptsItsOffsets()
    {
        LoadKernelPlugins(("empty.skprx", new SceModuleBuilder("EmptyPlugin").Start(0)));
        LoadedModule shell = _harness.Emu.FirmwareKernel.Modules.Single(m => m.Name == "SceSysmem");
        Assert.Equal(FirmwareCodeSize, shell.Segments[0].Size);
        Assert.Equal(FirmwareDataSize, shell.Segments[1].Size);
        uint entry = FindExport("SceSysmem", CpuForDriver, AtomicAddAndGet32);
        Assert.InRange(entry, shell.Segments[2].Address, shell.Segments[2].Address + shell.Segments[2].Size - 1);

        PluginFramework framework = _harness.Module<PluginFramework>();
        uint address = _harness.Alloc(4);
        Assert.Equal(0, Call(framework, "ModuleGetOffset", KernelSpace.KernelPid, shell.Uid, 0, FirmwareCodeSize - 0x31Fu, address));
        Assert.Equal(shell.Segments[0].Address + FirmwareCodeSize - 0x31F, GuestMemory.R32(address));
        Assert.Equal(0, Call(framework, "ModuleGetOffset", KernelSpace.KernelPid, shell.Uid, 1, 0x48u, address));
        Assert.Equal(shell.Segments[1].Address + 0x48, GuestMemory.R32(address));
        // the code the firmware has there is not copied: the segment traps whatever way it is entered
        Assert.Equal(TrapFillWord, GuestMemory.R32(shell.Segments[0].Address + FirmwareCodeSize - 0x320));
        Assert.NotEqual(0, Call(framework, "ModuleGetOffset", KernelSpace.KernelPid, shell.Uid, 0, FirmwareCodeSize + 1, address));
    }

    private sealed class CollectingSink : ILogSink
    {
        public readonly List<string> Messages = [];

        public void Write(LogLevel level, string thread, string message)
        {
            lock (Messages)
            {
                Messages.Add(message);
            }
        }
    }

    /// <summary>The address module_get_offset gives for <paramref name="offset"/> of the firmware SceSysmem's code
    /// segment, as a kernel module asks for it.</summary>
    private uint FirmwareCodeAddress(uint offset)
    {
        LoadedModule shell = _harness.Emu.FirmwareKernel.Modules.Single(m => m.Name == "SceSysmem");
        uint address = _harness.Alloc(4);
        Assert.Equal(0, Call(_harness.Module<PluginFramework>(), "ModuleGetOffset", KernelSpace.KernelPid, shell.Uid, 0, offset, address));
        return GuestMemory.R32(address);
    }

    /// <summary>Calls <paramref name="function"/> as kernel code with <paramref name="args"/>; returns r0.</summary>
    private uint CallKernelFunction(uint function, params uint[] args)
    {
        uint result = 0;
        _harness.OnGuest(t =>
        {
            t.KernelMode = true;
            result = t.CallGuest(function, args);
        });
        return result;
    }

    /// <summary>
    /// A kernel module that calls a function the firmware module has at an offset of its code segment, which VitaSharp
    /// has no HLE for, gets an error back at its return address (Thumb or A32 entry, either halfword of a word), and
    /// the log names the module and offset, instead of the thread dying on code that is not there.
    /// </summary>
    [Theory]
    [InlineData(0x1CE1u)]
    [InlineData(0x1CE3u)]
    [InlineData(0x1CE0u)]
    public void ACallOfAFirmwareInternalFunctionWithoutHleReturnsAnErrorAndNamesIt(uint offset)
    {
        LoadKernelPlugins(("empty.skprx", new SceModuleBuilder("EmptyPlugin").Start(0)));
        var sink = new CollectingSink();
        ILogSink? saved = Log.Sink;
        Log.Sink = sink;
        uint result;
        try
        {
            result = CallKernelFunction(FirmwareCodeAddress(offset), 1, 2, 3);
        }
        finally
        {
            Log.Sink = saved;
        }

        Assert.Equal(0x80020002u, result);
        uint named = (offset & 1) != 0 ? (offset & ~3u) | 1 : offset;
        lock (sink.Messages)
        {
            Assert.Contains(sink.Messages, m => m.StartsWith($"SceSysmem+0x{named:X}: internal firmware function not emulated", StringComparison.Ordinal));
        }
    }

    /// <summary>A firmware internal function with an HLE runs it with the call's arguments (the fifth on the stack)
    /// and returns its result to the caller; the next firmware build's module NID does not reach it.</summary>
    [Fact]
    public void ACallOfAFirmwareInternalFunctionWithHleRunsItWithTheCallsArguments()
    {
        LoadKernelPlugins(("empty.skprx", new SceModuleBuilder("EmptyPlugin").Start(0)));
        const uint ModuleNid = 0x12345678; // the synthetic module's
        _harness.Emu.FirmwareKernel.RegisterInternal(ModuleNid, 0x1A15, (_, a) => a[0] + a[1] * 2 + a[4] * 100);
        _harness.Emu.FirmwareKernel.RegisterInternal(ModuleNid + 1, 0x1B15, (_, _) => 7);
        Assert.Equal(1u + 4u + 500u, CallKernelFunction(FirmwareCodeAddress(0x1A15), 1, 2, 3, 4, 5));
        Assert.Equal(0x80020002u, CallKernelFunction(FirmwareCodeAddress(0x1B15)));
    }

    [Fact]
    public void WithoutAFirmwareInstalledNoFirmwareModuleIsShown()


    {
        Directory.Delete(Path.Combine(_harness.Root, "os0", "kd"), true);
        FirmwareKernelImage.Forget(_harness.Root);
        SceModuleBuilder plugin = new SceModuleBuilder("EmptyPlugin").Start(0);
        LoadKernelPlugins(("empty.skprx", plugin));
        Assert.Empty(_harness.Emu.FirmwareKernel.Modules);
        Assert.Equal(0u, _harness.Emu.FirmwareKernel.EntryOf(CpuForDriver, AtomicAddAndGet32));
    }
}
