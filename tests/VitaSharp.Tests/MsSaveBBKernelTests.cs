using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblGcAuthMgrMsSaveBBForDriver (gcauthmgr 3.74): the MAC context set-up and the cipher context clearing run in
/// the module (for the kernel entries, the user syscalls and libkernel's forms alike, with no permission check); the
/// MAC and cipher work needs the secure processor (0x80020004).
/// </summary>
[Collection("guest")]
public sealed class MsSaveBBKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x808A0003), Unsupported = unchecked((int)0x80020004);

    private readonly HleHarness _harness = new(fixture, "msbb");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData("SceSblGcAuthMgrMsSaveBBForDriver", 0x4A249828u, false)]
    [InlineData("SceSblGcAuthMgrMsSaveBBForDriver", 0x805C860Bu, false)]
    [InlineData("SceSblGcAuthMgr", 0x3A92780Du, false)]
    [InlineData("SceSblGcAuthMgr", 0x18EAAD73u, false)]
    [InlineData("SceLibKernel", 0x1693032Eu, false)]
    [InlineData("SceLibKernel", 0xEB02F15Du, false)]
    [InlineData("SceSblGcAuthMgrMsSaveBBForDriver", 0x3C185A67u, true)]
    [InlineData("SceSblGcAuthMgrMsSaveBBForDriver", 0x79A9BE44u, true)]
    [InlineData("SceSblGcAuthMgrMsSaveBBForDriver", 0x8E6626A0u, true)]
    [InlineData("SceSblGcAuthMgrMsSaveBBForDriver", 0xF66D5F76u, true)]
    public void EveryEntryIsBound(string library, uint nid, bool secure)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == library).ToList();
        Assert.Single(bindings);
        Assert.Equal(secure, bindings[0].Default);
    }

    [Fact]
    public void TheMacContextIsSetUpForItsType()
    {
        uint context = _harness.Alloc(0x30);
        GuestMemory.Fill(context, 0xAA, 0x30);
        Assert.Equal(Invalid, Call(Services, "MsSaveBBMacInit", 0u, 2));
        Assert.Equal(0, Call(Services, "MsSaveBBMacInit", context, 2)); // a game, not a system program
        Assert.Equal(2u, GuestMemory.R32(context));
        Assert.All(GuestMemory.ReadSpan(context + 4, 0x24).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0xAA, GuestMemory.ReadSpan(context + 0x28, 1)[0]); // nothing past the context
    }

    [Fact]
    public void TheCipherContextIsClearedAtTheEnd()
    {
        uint context = _harness.Alloc(0x20);
        GuestMemory.Fill(context, 0x55, 0x20);
        Assert.Equal(Invalid, Call(Services, "MsSaveBBCipherFinal", 0u));
        Assert.Equal(0, Call(Services, "MsSaveBBCipherFinal", context));
        Assert.All(GuestMemory.ReadSpan(context, 0x18).ToArray(), b => Assert.Equal(0, b));
        Assert.Equal(0x55, GuestMemory.ReadSpan(context + 0x18, 1)[0]);
    }

    [Fact]
    public void TheMacAndCipherWorkNeedsTheSecureProcessor()
    {
        Assert.Equal(Unsupported, Call(Services, "MsSaveBBSecure"));
    }

    [Fact]
    public void TheUserFormsRunTheSameSetUpForAnyCaller()
    {
        uint context = _harness.Alloc(0x28);
        Assert.Equal(0, Call(Services, "_sceSblGcAuthMgrMsSaveBBMacInit", context, 1)); // a game, not a system program
        Assert.Equal(1u, GuestMemory.R32(context));
        Assert.Equal(0, Call(Services, "sceSblGcAuthMgrMsSaveBBMacInit", context, 3));
        Assert.Equal(3u, GuestMemory.R32(context));
        Assert.Equal(0, Call(Services, "_sceSblGcAuthMgrMsSaveBBCipherFinal", context));
        Assert.Equal(0u, GuestMemory.R32(context));
        Assert.Equal(Invalid, Call(Services, "sceSblGcAuthMgrMsSaveBBCipherFinal", 0u));
    }
}
