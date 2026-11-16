using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The last kernel exports of small FW 3.74 libraries (SceCpuForDriver, SceDipswForDriver, ScePmMgrForDriver,
/// SceLedForDriver, SceHidForDriver, SceBusErrorForKernel, SceCodecEngineWrapperForDebugger, SceCompatForDriver and
/// two SceSblGcAuthMgr entries), each as its module answers on a retail unit without the device or secure service; and
/// the debug switches, which the kernel entries read and write without the syscalls' checks.
/// </summary>
[Collection("guest")]
public sealed class KernelSmallLibrariesTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint UserRw = 0x0C20D060, UserNcRw = 0x0C208060;
    private const int DipswError = unchecked((int)0x80021001);

    private readonly HleHarness _harness = new(fixture, "ksmall");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x337CBDF3u, false)] // SceCpuForDriver
    [InlineData(0xB2AD48BEu, false)] // SceDipswForDriver
    [InlineData(0x2AC815A2u, false)] // ScePmMgrForDriver
    [InlineData(0xBD1F193Bu, false)]
    [InlineData(0xEA24BE03u, true)] // SceLedForDriver
    [InlineData(0x0B7D27CFu, true)] // SceHidForDriver
    [InlineData(0xEB323EC6u, false)]
    [InlineData(0xEBB1F135u, true)] // SceBusErrorForKernel
    [InlineData(0x771190E7u, true)] // SceCodecEngineWrapperForDebugger
    [InlineData(0x6EBD9E01u, false)] // SceCompatForDriver
    [InlineData(0x68781760u, true)] // SceSblGcAuthMgrGcAuthForDriver
    [InlineData(0x39222A58u, true)] // SceSblGcAuthMgrPsmactForDriver
    public void TheLibrarysLastFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    private uint Block(uint type)
    {
        uint basePtr = _harness.Alloc(4);
        int uid = Call(Sysmem, "ksceKernelAllocMemBlock", _harness.Str("ksmall"), type, 0x1000u, 0u);
        Assert.True(uid > 0, $"0x{uid:X8}");
        Assert.Equal(0, Call(Sysmem, "ksceKernelGetMemBlockBase", uid, basePtr));
        return GuestMemory.R32(basePtr);
    }

    [Fact]
    public void UncachedAddressFollowsTheMemoryType()
    {
        using ProcessScope.Scope scope = ProcessScope.Enter(_harness.Emu);
        Assert.Equal(1, Call(Sysmem, "ksceKernelIsUncacheAddressInTmpFsGame", Block(UserNcRw)));
        Assert.Equal(0, Call(Sysmem, "ksceKernelIsUncacheAddressInTmpFsGame", Block(UserRw)));
        Assert.Equal(0, Call(Sysmem, "ksceKernelIsUncacheAddressInTmpFsGame", 0x00001000u)); // no translation
    }

    [Fact]
    public void PmMgrModesAreOffOnARetailUnit()
    {
        uint mode = _harness.Word(0xAA);
        Assert.Equal(unchecked((int)0x800F0A16), Call(Sysmem, "kscePmMgrGetProductMode", 0u));
        Assert.Equal(0, Call(Sysmem, "kscePmMgrGetProductMode", mode));
        Assert.Equal(0u, GuestMemory.R32(mode) & 0xFF);
        Assert.Equal(0, Call(Sysmem, "kscePmMgrIsExternalBootMode"));
    }

    /// <summary>The syscalls check the range (0x100 switches) and, from bit 0x40, the caller's privilege; the kernel
    /// entries do neither; all of them share the one set of switches, which GetDipswInfo reads by word.</summary>
    [Fact]
    public void DebugSwitchesAreSharedByTheSyscallsAndTheKernelEntries()
    {
        _harness.Emu.IsSystemProgram = false;
        Assert.Equal(0, Call(Services, "sceKernelSetDipsw", 0x21u));
        Assert.Equal(1, Call(Services, "sceKernelCheckDipsw", 0x21u));
        Assert.Equal(1, Call(Services, "ksceKernelCheckDipsw", 0x21u));
        Assert.Equal(2u, (uint)Call(Services, "ksceKernelGetDipswInfo", 1u));
        Assert.Equal(DipswError, Call(Services, "sceKernelSetDipsw", 0x41u));
        Assert.Equal(DipswError, Call(Services, "sceKernelCheckDipsw", 0x100u));
        Assert.Equal(DipswError, Call(Services, "sceKernelClearDipsw", 0x41u));

        HleHarness.Invoke(Services, "ksceKernelSetDipsw", 0xE5u);
        Assert.Equal(1, Call(Services, "ksceKernelCheckDipsw", 0xE5u));
        Assert.Equal(0x20u, (uint)Call(Services, "ksceKernelGetDipswInfo", 7u));
        Assert.Equal(DipswError, Call(Services, "sceKernelCheckDipsw", 0xE5u));
        _harness.Emu.IsSystemProgram = true;
        Assert.Equal(1, Call(Services, "sceKernelCheckDipsw", 0xE5u));
        Assert.Equal(0, Call(Services, "sceKernelClearDipsw", 0xE5u));
        Assert.Equal(0, Call(Services, "ksceKernelCheckDipsw", 0xE5u));
        HleHarness.Invoke(Services, "ksceKernelClearDipsw", 0x21u);
        Assert.Equal(0, Call(Services, "sceKernelCheckDipsw", 0x21u));
        Assert.Equal(0u, (uint)Call(Services, "ksceKernelGetDipswInfo", 9u));
    }

    [Fact]
    public void LedModesAreCheckedPerLed()
    {
        Assert.Equal(unchecked((int)0x803F0140), Call(Services, "ksceLedSetMode", 4u, 1u, 0u));
        Assert.Equal(unchecked((int)0x803F0141), Call(Services, "ksceLedSetMode", 0u, 5u, 0u));
        Assert.Equal(unchecked((int)0x803F0141), Call(Services, "ksceLedSetMode", 1u, 4u, 0u));
        Assert.Equal(0, Call(Services, "ksceLedSetMode", 2u, 4u, 0u));
        Assert.Equal(0, Call(Services, "ksceLedSetMode", 3u, 1u, 0u));
    }

    [Fact]
    public void DevicesLogsAndSecureServicesAreAbsent()
    {
        Hid hid = _harness.Module<Hid>();
        uint handle = _harness.Word(0xAAAAAAAA);
        Assert.Equal(unchecked((int)0x803B0006), Call(hid, "HidSelectDevice", 1));
        Assert.Equal(unchecked((int)0x803B0001), Call(hid, "HidSelectedDevice", 0u));
        Assert.Equal(0, Call(hid, "HidSelectedDevice", handle));
        Assert.Equal(0u, GuestMemory.R32(handle));

        uint buffer = _harness.Word(0xAAAAAAAA);
        Assert.Equal(0, Call(Services, "BusErrorCopyLog", buffer, 4u));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(buffer));
        Assert.Equal(unchecked((int)0x80600000), Call(Services, "CodecEngineDebugPoolInfo", 0x1000u, 0u));
        Assert.Equal(unchecked((int)0x80600009), Call(Services, "CodecEngineDebugPoolInfo", 0x800u, buffer));
        Assert.Equal(0, Call(Services, "CodecEngineDebugPoolInfo", 0x1000u, buffer));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(buffer));
        Assert.Equal(unchecked((int)0x80020004), Call(Services, "GcAuthSecureService", 0u));
    }

    /// <summary>SceAVConfig hands SceCompat the colour space mode it set on the display, which the PSP emulator's
    /// display path puts back when it leaves.</summary>
    [Fact]
    public void CompatKeepsTheDisplayColorSpaceMode()
    {
        Compat compat = _harness.Module<Compat>();
        Assert.Equal(0, Call(compat, "ksceCompatSetDisplayColorSpaceMode", 2));
        Assert.Equal(2, compat.DisplayColorSpaceMode);
        Assert.Equal(0, Call(compat, "ksceCompatSetDisplayColorSpaceMode", 0));
        Assert.Equal(0, compat.DisplayColorSpaceMode);
    }
}
