using System.Buffers.Binary;
using System.Text;
using VitaSharp.Core;
using VitaSharp.Core.Loader;

namespace VitaSharp.Tests;

/// <summary>
/// A kernel plugin (the plugin configuration's <c>*KERNEL</c>, e.g. kubridge.skprx) stays in the kernel across a
/// LoadExec: the program the application starts with sceAppMgrLoadExec imports its user-callable exports the same as
/// the first one. Real Racing 3 (a homebrew port) starts configurador.bin, which LoadExecs eboot.bin: the emulator
/// rebuilt the process's kernel for the new image but kept the "kernel plugins loaded" mark, so kubridge was gone and
/// its imports (kuKernelMemProtect, kuKernelFlushCaches, kuKernelCpuUnrestrictedMemcpy) became unimplemented HLE stubs
/// that did nothing; the port's code hooks were never written and it stayed on "Preparando los shaders... 99%"
/// (16/11/2026). Runs a synthetic kernel module (one syscall-exported function, module_start returning 0) and the
/// synthetic title.
/// </summary>
[Collection("guest")]
public sealed class KernelPluginExecTests : IDisposable
{
    private const uint LibNid = 0x5EA7B71D, FuncNid = 0x0BADF00D;
    private readonly HleHarness _harness;
    private readonly Emulator _emu;

    public KernelPluginExecTests(GuestMemoryFixture fixture)
    {
        // the title's segment is fixed at the start of user memory: blocks other tests of the shared memory left there
        for (uint address = SyntheticTitle.Base; address < SyntheticTitle.Base + 0x0010_0000; address += 0x1000)
        {
            if (fixture.Memory.FindBlock(address) is { } leftover)
            {
                fixture.Memory.Free(leftover.Address);
            }
        }

        _harness = new HleHarness(fixture, "kpexec", kernelPlugins: true);
        _emu = _harness.Emu;
        string tai = Path.Combine(_harness.Root, "ur0", "tai");
        Directory.CreateDirectory(tai);
        File.WriteAllBytes(Path.Combine(tai, "vstest.skprx"), KernelModule());
        File.WriteAllText(Path.Combine(tai, "config.txt"), "*KERNEL\nur0:tai/vstest.skprx\n");
    }

    public void Dispose()
    {
        _emu.Stop();
        _harness.Dispose();
    }

    private bool PluginLoaded => _emu.Kernel.Modules.Values.Any(m => m.IsKernel && m.Name == "VsTestKernelPlugin" && m.Started);

    /// <summary>What a user import of the plugin's function binds to: its code, not an HLE stub.</summary>
    private bool ExportVisibleToUsers => _emu.Linker.TryGetVisibleExport(FuncNid, LibNid, kernelImporter: false, out _);

    [Fact]
    public void AKernelPluginStaysAcrossALoadExec()
    {
        string app = SyntheticTitle.Install(_harness.Root);
        _emu.LoadTitle(app);
        Assert.True(PluginLoaded, "the kernel plugin did not load with the first program");
        Assert.True(ExportVisibleToUsers);
        _emu.Start();

        // the program asks for another executable of its application (sceAppMgrLoadExec: app0:eboot.bin -jugar)
        _emu.RequestExec("app0:eboot.bin", ["-jugar"]);
        HleHarness.Until(() => !_emu.Running || _emu.ExecRequest != null, "the exec request");
        _emu.PerformExec(null);

        Assert.True(PluginLoaded, "the kernel plugin is gone after the LoadExec");
        Assert.True(ExportVisibleToUsers, "the new program's imports of the kernel plugin have nothing to bind to");
        Assert.Single(_emu.Kernel.Modules.Values, m => m.IsKernel && m.Name == "VsTestKernelPlugin");

        // and a second LoadExec the same (Real Racing 3: eboot.bin, configurador.bin, eboot.bin)
        _emu.RequestExec("app0:eboot.bin", []);
        _emu.PerformExec(null);
        Assert.True(PluginLoaded);
        Assert.True(ExportVisibleToUsers);
    }

    /// <summary>The kernel plugins load once per kernel: a second plugin load for the same process does not load
    /// (or start) them again.</summary>
    [Fact]
    public void KernelPluginsLoadOncePerProcess()
    {
        string app = SyntheticTitle.Install(_harness.Root);
        _emu.LoadTitle(app);
        Assert.True(PluginLoaded);
        Assert.Empty(_emu.LoadKernelPlugins());
        Assert.Single(_emu.Kernel.Modules.Values, m => m.IsKernel && m.Name == "VsTestKernelPlugin");
    }

    /// <summary>
    /// A relocatable kernel module (ET_SCE_RELEXEC, linked at 0): module_info, one export entry (library
    /// <see cref="LibNid"/> "VsTestBridge", syscall-exported to user code, function <see cref="FuncNid"/>), ARM code
    /// "mov r0, #0; bx lr" as module_start and as the function, and the SCE relocations (format 0, R_ARM_ABS32) of the
    /// export entry's pointers.
    /// </summary>
    private static byte[] KernelModule()
    {
        const uint InfoOff = 0x00, ExportOff = 0x60, NameOff = 0xA0, NidOff = 0xB0, EntryOff = 0xB4, CodeOff = 0xC0, SegSize = 0x100;
        byte[] seg = new byte[SegSize];
        void W32(uint off, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(seg.AsSpan((int)off), v);
        void W16(uint off, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(seg.AsSpan((int)off), v);

        W16(InfoOff + 2, 0x0101);
        Encoding.ASCII.GetBytes("VsTestKernelPlugin").CopyTo(seg, (int)InfoOff + 4);
        W32(InfoOff + 0x24, ExportOff);
        W32(InfoOff + 0x28, ExportOff + 0x20);
        W32(InfoOff + 0x2C, ExportOff + 0x20); // no imports
        W32(InfoOff + 0x30, ExportOff + 0x20);
        W32(InfoOff + 0x34, 0x12345678);       // module NID
        W32(InfoOff + 0x44, CodeOff);          // module_start
        W32(InfoOff + 0x48, 0xFFFFFFFF);

        W16(ExportOff, 0x20);                  // size
        W16(ExportOff + 2, 1);                 // version
        W16(ExportOff + 4, Linker.SyscallExportAttr | 1);
        W16(ExportOff + 6, 1);                 // one function
        W32(ExportOff + 0x10, LibNid);
        Encoding.ASCII.GetBytes("VsTestBridge").CopyTo(seg, (int)NameOff);
        W32(NidOff, FuncNid);
        W32(CodeOff, 0xE3A00000);              // mov r0, #0
        W32(CodeOff + 4, 0xE12FFF1E);          // bx lr

        // relocations: the pointers of the export entry and its function table (segment 0 + addend)
        (uint Patch, uint Addend)[] abs32 = [(ExportOff + 0x14, NameOff), (ExportOff + 0x18, NidOff), (ExportOff + 0x1C, EntryOff), (EntryOff, CodeOff)];
        byte[] rela = new byte[abs32.Length * 12];
        for (int i = 0; i < abs32.Length; i++)
        {
            Span<byte> r = rela.AsSpan(i * 12, 12);
            BinaryPrimitives.WriteUInt32LittleEndian(r, 2u << 8); // format 0, symbol segment 0, R_ARM_ABS32, patch segment 0
            BinaryPrimitives.WriteUInt32LittleEndian(r[4..], abs32[i].Addend);
            BinaryPrimitives.WriteUInt32LittleEndian(r[8..], abs32[i].Patch);
        }

        const int ProgramHeaders = 0x34, SegData = 0x100, RelaData = SegData + (int)SegSize;
        byte[] elf = new byte[RelaData + rela.Length];
        Span<byte> e = elf.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(e, 0x464C457F);
        e[4] = 1;
        e[5] = 1;
        e[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(e[16..], 0xFE04); // ET_SCE_RELEXEC
        BinaryPrimitives.WriteUInt16LittleEndian(e[18..], 40);
        BinaryPrimitives.WriteUInt32LittleEndian(e[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(e[24..], InfoOff); // segment 0, module_info offset
        BinaryPrimitives.WriteUInt32LittleEndian(e[28..], ProgramHeaders);
        BinaryPrimitives.WriteUInt16LittleEndian(e[40..], 0x34);
        BinaryPrimitives.WriteUInt16LittleEndian(e[42..], 32);
        BinaryPrimitives.WriteUInt16LittleEndian(e[44..], 2);
        Span<byte> load = e.Slice(ProgramHeaders, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(load, 1);          // PT_LOAD
        BinaryPrimitives.WriteUInt32LittleEndian(load[4..], SegData);
        BinaryPrimitives.WriteUInt32LittleEndian(load[16..], SegSize);
        BinaryPrimitives.WriteUInt32LittleEndian(load[20..], SegSize);
        BinaryPrimitives.WriteUInt32LittleEndian(load[24..], 7);
        BinaryPrimitives.WriteUInt32LittleEndian(load[28..], 0x10);
        Span<byte> relaHeader = e.Slice(ProgramHeaders + 32, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(relaHeader, 0x60000000); // PT_SCE_RELA
        BinaryPrimitives.WriteUInt32LittleEndian(relaHeader[4..], RelaData);
        BinaryPrimitives.WriteUInt32LittleEndian(relaHeader[16..], (uint)rela.Length);
        seg.CopyTo(elf, SegData);
        rela.CopyTo(elf, RelaData);
        return elf;
    }
}
