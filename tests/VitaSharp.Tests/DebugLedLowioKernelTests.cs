using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceDebugLed (the GPO/GPI words and the handler slots, shared by the syscalls and the kernel entries),
/// ScePwmForDriver (the two channels' state machine), SceCdramForDriver and SceUartForKernel, as the FW 3.74 modules
/// answer them on a unit whose devices take every update at once and whose serial ports have no cable.
/// </summary>
[Collection("guest")]
public sealed class DebugLedLowioKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int PwmChannel = unchecked((int)0x803F0200), PwmInvalid = unchecked((int)0x803F0201);

    private readonly HleHarness _harness = new(fixture, "lowio");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x24173819u, "SceDebugLedForDriver", false)]
    [InlineData(0xF62154E7u, "SceDebugLedForDriver", false)]
    [InlineData(0x3BB289F7u, "SceDebugLedForDriver", false)]
    [InlineData(0x51C5325Au, "SceDebugLedForDriver", false)]
    [InlineData(0x098473B0u, "SceDebugLedForDriver", false)]
    [InlineData(0x0374213Cu, "ScePwmForDriver", false)]
    [InlineData(0x13161CCCu, "ScePwmForDriver", false)]
    [InlineData(0x22C17D24u, "ScePwmForDriver", false)]
    [InlineData(0x514773B1u, "ScePwmForDriver", false)]
    [InlineData(0x99911920u, "ScePwmForDriver", false)]
    [InlineData(0x0657FC1Bu, "SceCdramForDriver", true)]
    [InlineData(0x2D728EBFu, "SceCdramForDriver", true)]
    [InlineData(0x3EFA7540u, "SceCdramForDriver", false)]
    [InlineData(0x4C02AA05u, "SceUartForKernel", true)]
    [InlineData(0x4E97D3ADu, "SceUartForKernel", true)]
    [InlineData(0x16780BC3u, "SceUartForKernel", true)]
    [InlineData(0x430C48F1u, "SceUartForKernel", false)]
    public void EveryExportHasAnEntry(uint nid, string library, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == library).ToList();
        Assert.Single(bindings);
        Assert.Equal(isDefault, bindings[0].Default);
    }

    [Fact]
    public void TheGpoAndGpiWordsAreKept()
    {
        Assert.Equal(0, Call(Services, "sceKernelGetGPI"));
        Call(Services, "ksceKernelSetGPI", 0x5Au);
        Assert.Equal(0x5A, Call(Services, "sceKernelGetGPI"));
        Assert.Equal(0, Call(Services, "sceKernelSetGPO", 0xA5u));
        Assert.Equal(0xA5, Call(Services, "ksceKernelGetGPO"));
        Assert.Equal(0, Call(Services, "ksceKernelSetGPOMask", 0x0Fu, 0x100u));
    }

    [Fact]
    public void AnEmptyHandlerSlotRunsNothing()
    {
        Call(Services, "ksceDebugLedRegisterHandle0", 0u);
        Assert.Equal(0, Call(Services, "sceDebugLedInvokeHandle0", 1u, 2u, 3u));
        Assert.Equal(0, Call(Services, "sceDebugLedInvokeHandle1", 1u, 2u, 3u));
    }

    [Fact]
    public void APwmChannelStartsUpdatesAndStops()
    {
        uint period = _harness.Alloc(2), duty = _harness.Alloc(2), level = _harness.Word(0xFF);
        Assert.Equal(PwmChannel, Call(Services, "PwmStart", 2u, 0u, 10u, 5u));
        Assert.Equal(PwmInvalid, Call(Services, "PwmStart", 0u, 0x400u, 10u, 5u));
        Assert.Equal(PwmInvalid, Call(Services, "PwmStart", 0u, 0u, 10u, 11u));
        Assert.Equal(0, Call(Services, "PwmUpdate", 0u, 20u, 10u)); // idle: nothing

        Assert.Equal(0, Call(Services, "PwmStart", 0u, 0x100u, 10u, 5u)); // previous state: idle
        Assert.Equal(1, Call(Services, "PwmGetState", 0u, period, duty, level));
        Assert.Equal(10, GuestMemory.Read<ushort>(period));
        Assert.Equal(5, GuestMemory.Read<ushort>(duty));
        Assert.Equal(2u, GuestMemory.R32(level)); // a running output
        Assert.Equal(0, Call(Services, "PwmIsUpdatePending", 0u));

        Assert.Equal(0, Call(Services, "PwmUpdate", 0u, 10u, 10u));
        Assert.Equal(1, Call(Services, "PwmGetState", 0u, 0u, 0u, level));
        Assert.Equal(0u, GuestMemory.R32(level)); // full duty: the inverse of bit 8

        Assert.Equal(1, Call(Services, "PwmStop", 0u)); // previous state: running
        Assert.Equal(0, Call(Services, "PwmGetState", 0u, period, 0u, 0u));
        Assert.Equal(0, GuestMemory.Read<ushort>(period));
        Assert.Equal(0, Call(Services, "PwmStop", 0u)); // already idle
        Assert.Equal(0, Call(Services, "PwmGetState", 1u, 0u, 0u, 0u)); // the other channel is untouched
    }

    [Fact]
    public void CdramAndTheSerialPorts()
    {
        Call(Services, "CdramSecureMonitorCall");
        Assert.Equal(0, Call(Services, "CdramWaitReady"));
        Assert.Equal(0, Call(Services, "ksceUartInit", 0));
        Assert.Equal(0, Call(Services, "ksceUartWrite", 0, (int)'o'));
        Assert.Equal(0, Call(Services, "ksceUartWrite", 0, (int)'\n'));
        Assert.Equal(0, Call(Services, "ksceUartReadAvailable", 0));
        Assert.Equal(0, Call(Services, "ksceUartRead", 0));
    }
}
