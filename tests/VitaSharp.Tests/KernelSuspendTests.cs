using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceKernelSuspendForDriver (sysmem 3.74) before the power service registers its operations table: the power lock
/// is sysmem's counter (SetOps returns it), the table entries answer 0 or, for the four that need the power service,
/// 0x80020004, and the two handler lists take 32 entries each.
/// </summary>
[Collection("guest")]
public sealed class KernelSuspendTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "suspend");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x0106C0F0u)]
    [InlineData(0x0A6CA124u)]
    [InlineData(0x0DE3CC02u)]
    [InlineData(0x105C5752u)]
    [InlineData(0x1FA2F8F1u)]
    [InlineData(0x230495EDu)]
    [InlineData(0x250ACD90u)]
    [InlineData(0x254525F8u)]
    [InlineData(0x2BB92967u)]
    [InlineData(0x4DF40893u)]
    [InlineData(0x4E5A3A23u)]
    [InlineData(0x6A503956u)]
    [InlineData(0x81D9E41Cu)]
    [InlineData(0x8B3F02B8u)]
    [InlineData(0xAEA9440Du)]
    [InlineData(0xB4B13615u)]
    [InlineData(0xB5C58EE8u)]
    [InlineData(0xC00826ACu)]
    [InlineData(0xCE7A2207u)]
    [InlineData(0xD4958E6Fu)]
    [InlineData(0xD6124071u)]
    [InlineData(0xE677B343u)]
    [InlineData(0xF2B07167u)]
    [InlineData(0xFE2118BDu)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceKernelSuspendForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void ThePowerLockCountsUntilThePowerServiceRegisters()
    {
        Assert.Equal(0, Call(Services, "SuspendPowerLock", 0u));
        Assert.Equal(0, Call(Services, "SuspendPowerLock", 0u));
        Assert.Equal(0, Call(Services, "SuspendPowerUnlock", 0u));
        Assert.Equal(1, Call(Services, "SuspendSetOps", 0u)); // the count taken before the table
        Assert.Equal(0, Call(Services, "SuspendPowerUnlock", 0u));
        Assert.Equal(0, Call(Services, "SuspendPowerUnlock", 0u)); // never below zero
        Assert.Equal(0, Call(Services, "SuspendSetOps", 0u));
    }

    [Fact]
    public void WithoutATableTheEntriesAnswerAsTheModule()
    {
        Assert.Equal(0, Call(Services, "SuspendOp0C"));
        Assert.Equal(0, Call(Services, "SuspendOp34"));
        Assert.Equal(0, Call(Services, "SuspendNotify", 1u, 2u));
        Assert.Equal(unchecked((int)0x80020004), Call(Services, "SuspendOp14"));
        Assert.Equal(unchecked((int)0x80020004), Call(Services, "SuspendOp20"));
    }

    [Fact]
    public void TheHandlerListsTake32Entries()
    {
        Assert.Equal(0, Call(Services, "SuspendRegisterHandler", 0x1Fu, 0u, 0u));
        Assert.Equal(-1, Call(Services, "SuspendRegisterHandler", 0x20u, 0u, 0u));
        Assert.Equal(-1, Call(Services, "SuspendRegisterResumeHandler", 0x20u, 0u, 0u));
        Assert.Equal(0, Call(Services, "SuspendCallHandlers", 1u));
        Assert.Equal(0, Call(Services, "SuspendCallResumeHandlers", 1u));
    }
}
