using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblFwLoaderForDriver (post_ss_mgr.skprx 3.74): the lock / load / unlock protocol with the module's checks;
/// the load itself (the secure processor decrypting a firmware blob) is not available on the emulated unit.
/// </summary>
[Collection("guest")]
public sealed class SblFwLoaderKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x800F0E16), NotLocked = unchecked((int)0x800F0E29);
    private const int Unavailable = unchecked((int)0x80020004);

    private readonly HleHarness _harness = new(fixture, "fwload");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x91C73A54u, true)]
    [InlineData(0xA6278D27u, false)]
    [InlineData(0xBB59FC7Au, false)]
    public void EveryExportHasAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceSblFwLoaderForDriver").ToList();
        Assert.Single(bindings);
        Assert.Equal(isDefault, bindings[0].Default);
    }

    [Fact]
    public void LockLoadUnlock()
    {
        uint buffer = _harness.Alloc(0x100), loaded = _harness.Word(0);
        Assert.Equal(NotLocked, Call(Services, "ksceSblFwLoaderUnlock"));
        Assert.Equal(NotLocked, Call(Services, "ksceSblFwLoaderLoad", 1, buffer, 0x100u, loaded));
        Assert.Equal(Invalid, Call(Services, "ksceSblFwLoaderLock", 0u, 0));
        Assert.Equal(Invalid, Call(Services, "ksceSblFwLoaderLock", _harness.Str("os0:kd/wlanbt.bin"), 1));
        Assert.Equal(Invalid, Call(Services, "ksceSblFwLoaderLock", _harness.Str(new string('a', 0x100)), 0));
        Assert.Equal(0, Call(Services, "ksceSblFwLoaderLock", _harness.Str("os0:kd/wlanbt_robin_img_ax.skprx"), 0));
        Assert.Equal(Invalid, Call(Services, "ksceSblFwLoaderLoad", 2, buffer, 0x100u, loaded));
        Assert.Equal(Invalid, Call(Services, "ksceSblFwLoaderLoad", 1, 0u, 0x100u, loaded));
        Assert.Equal(Unavailable, Call(Services, "ksceSblFwLoaderLoad", 1, buffer, 0x100u, loaded));
        Assert.Equal(0, Call(Services, "ksceSblFwLoaderUnlock"));
        Assert.Equal(NotLocked, Call(Services, "ksceSblFwLoaderUnlock"));
    }
}
