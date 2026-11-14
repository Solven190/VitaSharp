using VitaSharp.Core.Firmware;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
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

    private readonly HleHarness _harness;

    public FirmwareKernelModulesTests(GuestMemoryFixture fixture)
    {
        _harness = new HleHarness(fixture, "fwkmod");
        var sysmem = new SceModuleBuilder("SceSysmem");
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
