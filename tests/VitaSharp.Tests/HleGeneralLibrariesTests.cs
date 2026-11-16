using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The last general-group functions of SceLibKernel, SceUsbDevSerial and SceNetCtl (FW 3.74 modules read in their
/// code): libkernel's setjmp/longjmp pair, its library-variable, exception-index and backtrace-printer entries over
/// the process's modules, the libkernel forwards of the ad hoc / MS save secure services, the USB serial device
/// function on a unit without a USB cable and the netctl configuration, scan and ad hoc authentication entries. The NID
/// test closes each library: every function the firmware exports there has an [Hle] entry.
/// </summary>
[Collection("guest")]
public sealed unsafe class HleGeneralLibrariesTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NetCtlNotInitialized = unchecked((int)0x80412101);
    private const int NetCtlInvalidAddress = unchecked((int)0x80412107);
    private const int NetCtlInvalidSize = unchecked((int)0x80412111);

    private readonly HleHarness _harness = new(fixture, "general-libs");

    public void Dispose() => _harness.Dispose();

    private SystemMisc Misc => _harness.Module<SystemMisc>();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private Net Network => _harness.Module<Net>();

    private HleModule Threads => _harness.Emu.Hle.Modules.Single(m => m.GetType().Name == "ThreadMgr");

    /// <summary>Every NID the FW 3.74 modules export in these libraries and the HLE was missing has an entry now.</summary>
    [Theory]
    [InlineData(0x0A4DF821u, false)] // SceLibKernel
    [InlineData(0x20E2D4B7u, false)]
    [InlineData(0x37F4ED04u, false)]
    [InlineData(0x6C7365C4u, false)]
    [InlineData(0x84C75DC3u, true)]
    [InlineData(0x9B28E1AFu, true)]
    [InlineData(0x2E05B2DCu, true)]
    [InlineData(0x56A59D4Fu, true)]
    [InlineData(0x4437FF2Du, true)] // SceUsbDevSerial
    [InlineData(0x57FBB08Cu, true)]
    [InlineData(0x6F2B3960u, true)]
    [InlineData(0x918D359Eu, true)]
    [InlineData(0x9F497D4Eu, true)]
    [InlineData(0xC0FDECAFu, true)]
    [InlineData(0xDAE5C362u, true)]
    [InlineData(0xF0C3F3D4u, true)]
    [InlineData(0x15BEEEF5u, false)] // SceNetCtl
    [InlineData(0x54EBA0FBu, false)]
    [InlineData(0x586C4DD7u, false)]
    [InlineData(0x67926E00u, false)]
    [InlineData(0x83184B6Bu, false)]
    [InlineData(0x9AFCDAC4u, false)]
    [InlineData(0xA0085E9Fu, false)]
    [InlineData(0xBB163832u, false)]
    [InlineData(0xBBA99D34u, false)]
    [InlineData(0xC03B9655u, false)]
    [InlineData(0xEA423A96u, false)]
    public void TheLibrarysLastFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    // ------------------------------------------------------------------ SceLibKernel: setjmp / longjmp

    /// <summary>libkernel's NID_DC277B4D is longjmp, not a count helper: it restores what NID_37F4ED04 saved (sp and lr
    /// without a key, d8-d15, the FPSCR) and returns the value, 1 for 0; both poison the scratch registers.</summary>
    [Fact]
    public void LongjmpRestoresWhatSetjmpSaved()
    {
        CpuState* cpu = CpuState.Allocate();
        try
        {
            var context = new HleContext { Cpu = cpu, Emu = _harness.Emu };
            for (int i = 4; i < 12; i++)
            {
                cpu->R[i] = 0x1000u + (uint)i;
            }

            cpu->R[13] = 0x81234560;
            cpu->R[14] = 0x81000101;
            for (int i = 8; i < 16; i++)
            {
                cpu->D[i] = 0x4000000000000000UL + (ulong)i;
            }

            cpu->Fpscr = 0xFFFFFFFF;
            uint env = _harness.Alloc(0x6C);
            Assert.Equal(0, Convert.ToInt32(Invoke(Threads, "LibKernelSetjmp", context, env)));
            Assert.Equal(0xF80000FFu, cpu->Fpscr);
            Assert.Equal(0xF80000FFu, GuestMemory.R32(env + 104));
            Assert.Equal(0x81234560u, GuestMemory.R32(env + 32));
            Assert.Equal(0x81000101u, GuestMemory.R32(env + 36));
            Assert.Equal(0xDEADBEEFu, cpu->R[1]);
            Assert.Equal(0xDEADBEEFu, cpu->R[12]);
            Assert.Equal(0x7FF8DEAD_7F80DEADUL, cpu->D[0]);
            Assert.Equal(0x7FF8DEAD_7F80DEADUL, cpu->D[31]);
            Assert.Equal(0x4000000000000008UL, cpu->D[8]);

            for (int i = 4; i < 12; i++)
            {
                cpu->R[i] = 0;
            }

            cpu->R[13] = 0x81300000;
            cpu->R[14] = 0x81000201;
            cpu->D[15] = 0;
            cpu->Fpscr = 0;
            Invoke(Threads, "LibKernelLongjmp", context, env, 0);
            Assert.Equal(1u, cpu->R[0]);
            Assert.Equal(0x81234560u, cpu->R[13]);
            Assert.Equal(0x81000101u, cpu->R[14]);
            Assert.Equal(0x100Bu, cpu->R[11]);
            Assert.Equal(0x400000000000000FUL, cpu->D[15]);
            Assert.Equal(0xF80000FFu, cpu->Fpscr);
            Invoke(Threads, "LibKernelLongjmp", context, env, 7);
            Assert.Equal(7u, cpu->R[0]);
        }
        finally
        {
            CpuState.Free(cpu);
        }
    }

    // ------------------------------------------------------------------ SceLibKernel: the process's modules

    /// <summary>A module with one library (two functions, two variables) whose export table and exception index live in
    /// guest memory, as the loader leaves them.</summary>
    private (LoadedModule Module, uint Code, uint NidTable, uint EntryTable) AddSyntheticModule()
    {
        uint code = _harness.Alloc(0x1000);
        uint nids = _harness.Alloc(16);
        uint entries = _harness.Alloc(16);
        uint[] nidValues = [0x11111111, 0x22222222, 0xAAAA0001, 0xAAAA0002];
        uint[] entryValues = [code + 1, code + 0x21, code + 0x800, 0];
        for (uint i = 0; i < 4; i++)
        {
            GuestMemory.W32(nids + i * 4, nidValues[i]);
            GuestMemory.W32(entries + i * 4, entryValues[i]);
        }

        uint export = _harness.Alloc(0x20);
        GuestMemory.Write(export, (ushort)0x20);
        GuestMemory.Write(export + 2, (ushort)0x0103);
        GuestMemory.Write(export + 4, (ushort)0x0001);
        GuestMemory.Write(export + 6, (ushort)2);
        GuestMemory.W32(export + 8, 2);
        GuestMemory.W32(export + 0x10, 0xCAFE0001);
        GuestMemory.W32(export + 0x18, nids);
        GuestMemory.W32(export + 0x1C, entries);

        // three exidx entries: [code, CANTUNWIND], [code + 0x20, inline 0x80B0B0B0], [code + 0x40, prel31 -> code + 0x900]
        uint exidx = _harness.Alloc(24);
        uint[] functions = [code, code + 0x20, code + 0x40];
        for (uint i = 0; i < 3; i++)
        {
            GuestMemory.W32(exidx + i * 8, (functions[i] - (exidx + i * 8)) & 0x7FFFFFFF);
        }

        GuestMemory.W32(exidx + 4, 1);
        GuestMemory.W32(exidx + 12, 0x80B0B0B0);
        GuestMemory.W32(exidx + 20, (code + 0x900 - (exidx + 20)) & 0x7FFFFFFF);

        var module = new LoadedModule
        {
            Uid = 0x7FFF1001,
            Name = "SynthModule",
            Path = "ux0:synth.suprx",
            ExportsStart = export,
            ExportsEnd = export + 0x20,
            ExidxTop = exidx,
            ExidxEnd = exidx + 24,
        };
        module.Segments[0] = new SegmentInfo(code, code, 0x1000);
        module.Exports.Add(("SynthLib", 0xCAFE0001, [0x11111111, 0x22222222], [0xAAAA0001, 0xAAAA0002]));
        _harness.Emu.Kernel.Modules[module.Uid] = module;
        return (module, code, nids, entries);
    }

    /// <summary>sceKernelGetLibraryInfoByNID writes the 0x1C-byte SceKernelLibraryInfo libkernel reads: counts at
    /// +0x10/+0x12, the NID and entry tables at +0x14/+0x18 (it wrote a library name there).</summary>
    [Fact]
    public void LibraryInfoGivesTheNidAndEntryTables()
    {
        (LoadedModule module, _, uint nids, uint entries) = AddSyntheticModule();
        uint info = _harness.Alloc(0x20);
        GuestMemory.W32(info, 0x1C);
        Assert.Equal(0, Call(Misc, "sceKernelGetLibraryInfoByNID", module.Uid, 0xCAFE0001u, info));
        Assert.Equal(0x1Cu, GuestMemory.R32(info));
        Assert.Equal(0x0103, GuestMemory.Read<ushort>(info + 0xC));
        Assert.Equal(2, GuestMemory.Read<ushort>(info + 0x10));
        Assert.Equal(2, GuestMemory.Read<ushort>(info + 0x12));
        Assert.Equal(nids, GuestMemory.R32(info + 0x14));
        Assert.Equal(entries, GuestMemory.R32(info + 0x18));
    }

    /// <summary>NID_0A4DF821 finds a variable among the library's variables only (a function NID is not one), and a
    /// NULL entry or an unknown NID is 0x8002D00C.</summary>
    [Fact]
    public void LibraryVariableIsLookedUpAmongTheVariables()
    {
        (LoadedModule module, uint code, _, _) = AddSyntheticModule();
        uint address = _harness.Word(0x55555555);
        Assert.Equal(0, Call(Misc, "LibKernelGetLibraryVariable", module.Uid, 0xCAFE0001u, 0xAAAA0001u, address));
        Assert.Equal(code + 0x800, GuestMemory.R32(address));
        GuestMemory.W32(address, 0x55555555);
        Assert.Equal(unchecked((int)0x8002D00C), Call(Misc, "LibKernelGetLibraryVariable", module.Uid, 0xCAFE0001u, 0xAAAA0002u, address));
        Assert.Equal(unchecked((int)0x8002D00C), Call(Misc, "LibKernelGetLibraryVariable", module.Uid, 0xCAFE0001u, 0x11111111u, address));
        Assert.Equal(0x55555555u, GuestMemory.R32(address));
        Assert.Equal(unchecked((int)0x8002D011), Call(Misc, "LibKernelGetLibraryVariable", 0x7FFF0F0F, 0xCAFE0001u, 0xAAAA0001u, address));
    }

    /// <summary>NID_6C7365C4 gives the exidx entry of the function holding the address: the entry word, and the unwind
    /// data address only for a prel31 word (0xFFFFFFFF for CANTUNWIND and inline data). The last function covers the
    /// rest of the module; before the first or outside a module is 0x8002D01C, outside the user range 0x80020008.</summary>
    [Fact]
    public void ExidxEntryOfAnAddressFollowsTheIndexTable()
    {
        (_, uint code, _, _) = AddSyntheticModule();
        Assert.True(code >= 0x81000000 && code < 0xF0000000, $"test memory at 0x{code:X8}");
        uint output = _harness.Alloc(8);
        Assert.Equal(0, Call(Misc, "LibKernelFindExidxEntry", code + 0x11, output));
        Assert.Equal(1u, GuestMemory.R32(output));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(output + 4));
        Assert.Equal(0, Call(Misc, "LibKernelFindExidxEntry", code + 0x20, output));
        Assert.Equal(0x80B0B0B0u, GuestMemory.R32(output));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(output + 4));
        Assert.Equal(0, Call(Misc, "LibKernelFindExidxEntry", code + 0xFF0, output));
        Assert.Equal(code + 0x900, GuestMemory.R32(output + 4));
        Assert.Equal(unchecked((int)0x80020008), Call(Misc, "LibKernelFindExidxEntry", 0x80000000u, output));
        Assert.Equal(unchecked((int)0x8002D01C), Call(Misc, "LibKernelFindExidxEntry", 0xEFFFF000u, output));
    }

    /// <summary>NID_20E2D4B7 prints the frames between the two rulers, naming the module and the offset from its first
    /// segment, the stack size between frames; only for the calling process, and never for NULL frames.</summary>
    [Fact]
    public void BacktracePrinterNamesTheModuleOfEachFrame()
    {
        (_, uint code, _, _) = AddSyntheticModule();
        uint frames = _harness.Alloc(16);
        GuestMemory.W32(frames, 0x81F00000);
        GuestMemory.W32(frames + 4, code + 0x44);
        GuestMemory.W32(frames + 8, 0x81F00040);
        GuestMemory.W32(frames + 12, 0x00001234);
        var lines = (List<string>)Invoke(Misc, "BacktraceLines", frames, 2)!;
        Assert.Equal("=== Print Backtrace =================", lines[0]);
        Assert.Equal($"(0) PC:0x{code + 0x44:X8}, SP:0x81F00000(size=0x00000040 byte) [SynthModule + 0x00000044]", lines[1]);
        Assert.Equal("(1) PC:0x00001234, SP:0x81F00040(size=0x00000000 byte) [no module info]", lines[2]);
        Assert.Equal("=====================================", lines[3]);
        Assert.Equal(0, Call(Misc, "LibKernelPrintBacktrace", 0, frames, 2));
        Assert.Equal(0, Call(Misc, "LibKernelPrintBacktrace", _harness.Emu.Pid, frames, 2));
        Assert.Equal(unchecked((int)0x80029001), Call(Misc, "LibKernelPrintBacktrace", _harness.Emu.Pid + 1, frames, 2));
        Assert.Equal(unchecked((int)0x80020006), Call(Misc, "LibKernelPrintBacktrace", 0, 0u, 2));
    }

    /// <summary>libkernel forwards the ad hoc BB160 and MS save cipher calls to their SceSblGcAuthMgr syscalls: the
    /// same entry, so the same answer (the session opens; keys and the cipher need the secure module: unavailable).</summary>
    [Theory]
    [InlineData(0x84C75DC3u, 0x09E1E270u)]
    [InlineData(0x9B28E1AFu, 0xE4AA1BB2u)]
    [InlineData(0x2E05B2DCu, 0x064BA38Au)]
    [InlineData(0x56A59D4Fu, 0xF6FECCE0u)]
    public void LibKernelSecureForwardsShareTheSyscallsEntry(uint userNid, uint syscallNid)
    {
        Assert.Equal(MethodOf(syscallNid), MethodOf(userNid));
    }

    /// <summary>The C# method bound to a NID, by its number or (a name-only entry) by its name in the NID list.</summary>
    private static string MethodOf(uint nid)
    {
        string? name = NidDatabase.Lookup(nid)?.Name;
        return HleBindings.Modules.SelectMany(m => m.Bindings())
            .Single(b => b.Nid == nid || (b.Nid == 0 && b.Name != null && b.Name == name)).Method;
    }

    // ------------------------------------------------------------------ SceUsbDevSerial: no USB cable

    [Fact]
    public void UsbSerialDeviceStartsStopsAndCountsItsPort()
    {
        Assert.Equal(unchecked((int)0x80243300), Call(Services, "UsbDevSerialStart", 2u));
        Assert.Equal(1, Call(Services, "UsbDevSerialStop"));
        Assert.Equal(0x110, Call(Services, "UsbDevSerialGetState"));
        Assert.Equal(0, Call(Services, "UsbDevSerialStart", 1u));
        Assert.Equal(unchecked((int)0x80243002), Call(Services, "UsbDevSerialStart", 0u));
        Assert.Equal(0x210, Call(Services, "UsbDevSerialGetState"));
        Assert.Equal(0, Call(Services, "UsbDevSerialStop"));
        Assert.Equal(1, Call(Services, "UsbDevSerialStop"));

        Assert.Equal(unchecked((int)0x80010009), Call(Services, "UsbDevSerialClose"));
        Assert.Equal(0, Call(Services, "UsbDevSerialOpen"));
        Assert.Equal(0, Call(Services, "UsbDevSerialOpen"));
        Assert.Equal(0, Call(Services, "UsbDevSerialClose"));
        Assert.Equal(0, Call(Services, "UsbDevSerialClose"));
        Assert.Equal(unchecked((int)0x80010009), Call(Services, "UsbDevSerialClose"));
    }

    /// <summary>Transfers check the buffer, accept an empty one, and otherwise need a host that never comes; the
    /// control call sets the receive timeout without a host.</summary>
    [Fact]
    public void UsbSerialDeviceTransfersNeedAHost()
    {
        uint buffer = _harness.Alloc(16);
        Assert.Equal(0, Call(Services, "UsbDevSerialOpen"));
        Assert.Equal(unchecked((int)0x80010016), Call(Services, "UsbDevSerialReceive", 0u, 16u));
        Assert.Equal(0, Call(Services, "UsbDevSerialReceive", buffer, 0u));
        Assert.Equal(unchecked((int)0x80010013), Call(Services, "UsbDevSerialReceive", buffer, 16u));
        Assert.Equal(unchecked((int)0x80010016), Call(Services, "UsbDevSerialSend", 0u, 16u));
        Assert.Equal(unchecked((int)0x80010013), Call(Services, "UsbDevSerialSend", buffer, 16u));

        Assert.Equal(-1, Call(Services, "get_UsbDevSerialReceiveTimeout"));
        uint value = _harness.Word(250000);
        Assert.Equal(0, Call(Services, "UsbDevSerialControl", 0x3517001, value, 4));
        Assert.Equal(250000, Call(Services, "get_UsbDevSerialReceiveTimeout"));
        Assert.Equal(unchecked((int)0x80010016), Call(Services, "UsbDevSerialControl", 0x3517001, value, 8));
        Assert.Equal(unchecked((int)0x80010016), Call(Services, "UsbDevSerialControl", 0x3517008, value, 4));
        Assert.Equal(unchecked((int)0x80010013), Call(Services, "UsbDevSerialControl", 0x3517002, value, 4));
    }

    // ------------------------------------------------------------------ SceNetCtl

    [Fact]
    public void NetCtlEntriesCheckTheLibraryFirst()
    {
        Assert.Equal(NetCtlNotInitialized, Call(Network, "NetCtlSetConfiguration", 0u));
        Assert.Equal(NetCtlNotInitialized, Call(Network, "NetCtlSetSsid", 0u, 0u));
        Assert.Equal(NetCtlNotInitialized, Call(Network, "NetCtlAdhocAuthStop"));
        Assert.Equal(NetCtlNotInitialized, Call(Network, "NetCtlPeriodicalScanStart", 0x100, 0, 0u));
        Assert.Equal(NetCtlNotInitialized, Call(Network, "NetCtlPeriodicalScanStop"));
        Assert.Equal(NetCtlNotInitialized, Call(Network, "NetCtlPeriodicalScanGetResults", 0u, 0u));
        Assert.Equal(NetCtlNotInitialized, Call(Network, "NetCtlClearEvents"));
    }

    /// <summary>The configuration's reserved parts must be zero; a clean block goes to the shell's service (not
    /// reachable without libshellsvc in the process: not initialised, as the real library would have been).</summary>
    [Fact]
    public void NetCtlConfigurationKeepsItsReservedPartsZero()
    {
        Assert.Equal(0, Call(Network, "sceNetCtlInit"));
        Assert.Equal(NetCtlInvalidAddress, Call(Network, "NetCtlSetConfiguration", 0u));
        uint config = _harness.Alloc(0x608);
        Assert.Equal(NetCtlNotInitialized, Call(Network, "NetCtlSetConfiguration", config));
        foreach (uint offset in new uint[] { 0x71, 0x420, 0x4E4, 0x5A8, 0x5AC, 0x5B4, 0x5C0, 0x5D8, 0x604 })
        {
            GuestMemory.Write(config + offset, (byte)1);
            Assert.Equal(unchecked((int)0x804121CD), Call(Network, "NetCtlSetConfiguration", config));
            GuestMemory.Write(config + offset, (byte)0);
        }
    }

    [Fact]
    public void NetCtlOneSsidChecksItsParameterBlock()
    {
        Assert.Equal(0, Call(Network, "sceNetCtlInit"));
        uint ssid = _harness.Str("VitaNet");
        uint param = _harness.Alloc(0x38);
        Assert.Equal(unchecked((int)0x80412190), Call(Network, "NetCtlSetSsid", 0u, 0u));
        Assert.Equal(0, Call(Network, "NetCtlSetSsid", ssid, 0u));
        GuestMemory.W32(param + 4, 3);
        Assert.Equal(0, Call(Network, "NetCtlSetSsid", ssid, param));
        GuestMemory.W32(param + 4, 4);
        Assert.Equal(unchecked((int)0x804121CE), Call(Network, "NetCtlSetSsid", ssid, param));
        GuestMemory.W32(param + 4, 0);
        GuestMemory.Write(param + 0x30, (byte)1);
        Assert.Equal(unchecked((int)0x804121CD), Call(Network, "NetCtlSetSsid", ssid, param));
        Assert.Equal(unchecked((int)0x80412190), Call(Network, "NetCtlSetSsid", _harness.Str(new string('s', 33)), 0u));
    }

    /// <summary>The periodical scan starts with an optional 0x30-byte block, stops, and finds no access point.</summary>
    [Fact]
    public void NetCtlPeriodicalScanFindsNoAccessPoint()
    {
        Assert.Equal(0, Call(Network, "sceNetCtlInit"));
        uint param = _harness.Word(0x2C);
        Assert.Equal(NetCtlInvalidSize, Call(Network, "NetCtlPeriodicalScanStart", 0x100, 0, param));
        GuestMemory.W32(param, 0x30);
        Assert.Equal(0, Call(Network, "NetCtlPeriodicalScanStart", 0x100, 0, param));
        Assert.Equal(0, Call(Network, "NetCtlPeriodicalScanStart", 0x100, 0, 0u));
        uint results = _harness.Alloc(0x70);
        Assert.Equal(NetCtlInvalidAddress, Call(Network, "NetCtlPeriodicalScanGetResults", 0u, 1u));
        Assert.Equal(NetCtlInvalidSize, Call(Network, "NetCtlPeriodicalScanGetResults", results, 0u));
        Assert.Equal(0, Call(Network, "NetCtlPeriodicalScanGetResults", results, 1u));
        Assert.Equal(0, Call(Network, "NetCtlPeriodicalScanStop"));
        Assert.Equal(0, Call(Network, "NetCtlPeriodicalScanStop"));
        Assert.Equal(unchecked((int)0x80412108), Call(Network, "NetCtlAdhocAuthStop"));
    }

    /// <summary>NID_9AFCDAC4 maps one port like NID_9CB6FEE1: the context checks, the busy bit and the cleared address,
    /// and no gateway answering.</summary>
    [Fact]
    public void NetAuthOnePortMappingMatchesTheTwoPortOne()
    {
        uint context = _harness.Alloc(0x2000);
        Assert.Equal(unchecked((int)0x80414105), Call(Network, "NetAuthCtxMapPort", 0u, 0u, 80u));
        Assert.Equal(unchecked((int)0x80414101), Call(Network, "NetAuthCtxMapPort", context, 0u, 80u));
        GuestMemory.Write(context, (byte)1);
        GuestMemory.W32(context + 0x12A4, 0x0A000001);
        Assert.Equal(NetCtlNotInitialized, Call(Network, "NetAuthCtxMapPort", context, 0u, 80u));
        Assert.Equal(1u, GuestMemory.R32(context + 4) & 1);
        Assert.Equal(0u, GuestMemory.R32(context + 0x12A4));
        Assert.Equal(0, Call(Network, "sceNetCtlInit"));
        Assert.Equal(unchecked((int)0x80414187), Call(Network, "NetAuthCtxMapPort", context, 0u, 80u));
    }
}
