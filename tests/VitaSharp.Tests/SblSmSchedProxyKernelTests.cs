using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblSmSchedProxyForKernel (smsc_proxy.skprx 3.74) on a unit without the secure processor: the proxy is up
/// (down after Uninitialize: 0x800F0426), nothing can be started in the secure processor (0x80020004), and every
/// session call checks its arguments (0x800F0416) and then finds no session (0x800F042B).
/// </summary>
[Collection("guest")]
public sealed class SblSmSchedProxyKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x800F0416), Unsupported = unchecked((int)0x800F0425);
    private const int Down = unchecked((int)0x800F0426), NoSession = unchecked((int)0x800F042B);
    private const int Unavailable = unchecked((int)0x80020004);

    private readonly HleHarness _harness = new(fixture, "smsched");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0xA488D604u, false)]
    [InlineData(0x33A3A1E2u, false)]
    [InlineData(0x1916509Bu, true)]
    [InlineData(0x7894B6F0u, true)]
    [InlineData(0x723B382Fu, false)]
    [InlineData(0x15B0E4DFu, false)]
    [InlineData(0x3CE17233u, false)]
    [InlineData(0xF70C04ECu, false)]
    [InlineData(0x973A4A7Du, false)]
    [InlineData(0x27EB92F1u, false)]
    [InlineData(0xF35EFC1Au, false)]
    [InlineData(0xDE4EAC3Cu, false)]
    [InlineData(0x8B84AC2Au, false)]
    [InlineData(0x85EDA5FCu, false)]
    [InlineData(0x1DFC8624u, false)]
    [InlineData(0x984EC9D1u, false)]
    public void EveryExportHasAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceSblSmSchedProxyForKernel").ToList();
        Assert.Single(bindings);
        Assert.Equal(isDefault, bindings[0].Default);
    }

    [Fact]
    public void SessionCallsCheckTheirArgumentsThenFindNoSession()
    {
        uint output = _harness.Word(0);
        Assert.Equal(Unavailable, Call(Services, "ksceSblSmSchedProxyInvoke", 0u, 0u, 0u, 0u, 0u, output));
        Assert.Equal(Unavailable, Call(Services, "ksceSblSmSchedProxyExecuteSKCommand"));
        Assert.Equal(Invalid, Call(Services, "SmSchedMailbox", 1, 0u, 0u));
        Assert.Equal(Invalid, Call(Services, "SmSchedMailbox", 1, 4u, 0u));
        Assert.Equal(NoSession, Call(Services, "SmSchedMailbox", 1, 3u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceSblSmSchedProxyGetStatus", 1, 0u));
        Assert.Equal(NoSession, Call(Services, "ksceSblSmSchedProxyGetStatus", 1, output));
        Assert.Equal(NoSession, Call(Services, "ksceSblSmSchedProxyWait", 1, output));
        Assert.Equal(NoSession, Call(Services, "ksceSblSmSchedProxyForceStop", 1));
        Assert.Equal(Invalid, Call(Services, "ksceSblSmSchedProxyRegisterIntrHandler", 1, 4u, 0x81000000u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceSblSmSchedProxyRegisterIntrHandler", 1, 0u, 0u, 0u));
        Assert.Equal(NoSession, Call(Services, "ksceSblSmSchedProxyRegisterIntrHandler", 1, 0u, 0x81000000u, 0u));
        Assert.Equal(NoSession, Call(Services, "ksceSblSmSchedProxyReleaseIntrHandler", 1, 3u));
        Assert.Equal(Unsupported, Call(Services, "SmSchedUnsupported"));
    }

    [Fact]
    public void TheProxyCanBeTakenDownAndBroughtBack()
    {
        Assert.Equal(0, Call(Services, "ksceSblSmSchedProxyUninitialize"));
        Assert.Equal(Down, Call(Services, "ksceSblSmSchedProxyForceStop", 1));
        Assert.Equal(Down, Call(Services, "SmSchedMailbox", 1, 0u, 0u));
        Assert.Equal(Down, Call(Services, "ksceSblSmSchedProxyInvoke", 0u, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceSblSmSchedProxyInitialize"));
        Assert.Equal(NoSession, Call(Services, "ksceSblSmSchedProxyForceStop", 1));
    }
}
