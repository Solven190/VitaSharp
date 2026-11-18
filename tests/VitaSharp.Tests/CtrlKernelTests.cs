using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceCtrlForDriver's settings (SceCtrl 3.74): the idle cancel keys and thresholds, the two button masks and the
/// Blu-ray remote flag; and the bridge's vshCtrl* forms, which check for a system program (0x80010058) and set the
/// same kernel state (before, the bridge answered 0 to anyone and kept nothing).
/// </summary>
[Collection("guest")]
public sealed class CtrlKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80340001), Perm = unchecked((int)0x80010058);

    private readonly HleHarness _harness = new(fixture, "ctrlk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private VshBridge Bridge => _harness.Module<VshBridge>();

    [Theory]
    [InlineData(0x30801AE9u)]
    [InlineData(0xB9402CEFu)]
    [InlineData(0x0A679512u)]
    [InlineData(0x345E74FDu)]
    [InlineData(0x46ABDDE8u)]
    [InlineData(0xB25BD8FDu)]
    [InlineData(0x12C13A72u)]
    [InlineData(0x03777131u)]
    [InlineData(0x5B849B22u)]
    [InlineData(0xAA36F578u)]
    public void EveryEntryIsBound(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceCtrlForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void TheIdleCancelSettingsRoundTrip()
    {
        Assert.Equal(0, Call(Services, "ksceCtrlSetIdleCancelKey", 1u, 2u, 3u, 4u));
        uint words = _harness.Alloc(16);
        Assert.Equal(0, Call(Services, "ksceCtrlGetIdleCancelKey", words, 0u, words + 8, words + 12));
        Assert.Equal(1u, GuestMemory.R32(words));
        Assert.Equal(0u, GuestMemory.R32(words + 4)); // that output was not asked
        Assert.Equal(4u, GuestMemory.R32(words + 12));

        Assert.Equal(Invalid, Call(Services, "ksceCtrlSetIdleCancelThreshold", -2, 0));
        Assert.Equal(Invalid, Call(Services, "ksceCtrlSetIdleCancelThreshold", 0, 0x81));
        Assert.Equal(0, Call(Services, "ksceCtrlSetIdleCancelThreshold", -1, 0x80));
        Assert.Equal(Invalid, Call(Services, "ksceCtrlGetIdleCancelThreshold", words, 0u));
        Assert.Equal(0, Call(Services, "ksceCtrlGetIdleCancelThreshold", words, words + 4));
        Assert.Equal(uint.MaxValue, GuestMemory.R32(words));
        Assert.Equal(0x80u, GuestMemory.R32(words + 4));
    }

    [Fact]
    public void TheMasksClearThenSet()
    {
        uint mask = _harness.Alloc(4);
        Assert.Equal(Invalid, Call(Services, "ksceCtrlGetMaskForAll", 0u));
        Call(Services, "ksceCtrlUpdateMaskForAll", 0u, 0xF0u);
        Call(Services, "ksceCtrlUpdateMaskForAll", 0x30u, 0x01u);
        Assert.Equal(0, Call(Services, "ksceCtrlGetMaskForAll", mask));
        Assert.Equal(0xC1u, GuestMemory.R32(mask));
        Call(Services, "ksceCtrlUpdateMaskForNonShell", 0u, 0x8u);
        Assert.Equal(0, Call(Services, "ksceCtrlGetMaskForNonShell", mask));
        Assert.Equal(0x8u, GuestMemory.R32(mask));
    }

    [Fact]
    public void TheBridgeIsForSystemProgramsAndSetsTheKernelState()
    {
        Assert.Equal(Perm, Call(Bridge, "vshCtrlSetIdleCancelThreshold", 5, 5));
        Assert.Equal(Perm, Call(Bridge, "vshCtrlSetIdleCancelKey", 1u, 2u, 3u, 4u));
        Assert.Equal(Perm, Call(Bridge, "vshCtrlIsBdRemoconConnected"));
        _harness.Emu.IsSystemProgram = true;
        Assert.Equal(Invalid, Call(Bridge, "vshCtrlSetIdleCancelThreshold", 0x90, 5));
        Assert.Equal(0, Call(Bridge, "vshCtrlSetIdleCancelThreshold", 7, 9));
        Assert.Equal(0, Call(Bridge, "vshCtrlSetIdleCancelKey", 0x10u, 0u, 0u, 0u));
        uint words = _harness.Alloc(8);
        Call(Services, "ksceCtrlGetIdleCancelThreshold", words, words + 4);
        Assert.Equal(7u, GuestMemory.R32(words));
        Call(Services, "ksceCtrlGetIdleCancelKey", words, 0u, 0u, 0u);
        Assert.Equal(0x10u, GuestMemory.R32(words));
        Assert.Equal(0, Call(Bridge, "vshCtrlIsBdRemoconConnected"));
        Call(Services, "ksceCtrlSetBdRemoconConnected", 1);
        Assert.Equal(1, Call(Bridge, "vshCtrlIsBdRemoconConnected"));
    }
}
