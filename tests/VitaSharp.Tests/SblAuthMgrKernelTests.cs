using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblAuthMgrForKernel / SceSblAuthMgrForDriver (authmgr.skprx 3.74): the module's own checks of every entry, the
/// secure processor's commands answering "not available" (0x80020004) on a unit without it, and the two entries the
/// module answers itself (CompareSwVersion, NID_2A83A012).
/// </summary>
[Collection("guest")]
public sealed class SblAuthMgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x800F0516), BadContext = unchecked((int)0x800F0509);
    private const int NewerFirmware = unchecked((int)0x800F0537), Unavailable = unchecked((int)0x80020004);

    private readonly HleHarness _harness = new(fixture, "authmgr");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0xA9CD2A09u, "SceSblAuthMgrForKernel", true)]
    [InlineData(0x026ACBADu, "SceSblAuthMgrForKernel", false)]
    [InlineData(0xF3411881u, "SceSblAuthMgrForKernel", true)]
    [InlineData(0x89CCDA2Cu, "SceSblAuthMgrForKernel", true)]
    [InlineData(0xBC422443u, "SceSblAuthMgrForKernel", true)]
    [InlineData(0xABAB8466u, "SceSblAuthMgrForKernel", false)]
    [InlineData(0x2A83A012u, "SceSblAuthMgrForKernel", false)]
    [InlineData(0x122ACDEAu, "SceSblAuthMgrForKernel", true)]
    [InlineData(0xF2BB723Eu, "SceSblAuthMgrForKernel", true)]
    [InlineData(0x868B9E9Au, "SceSblAuthMgrForDriver", true)]
    [InlineData(0x41DAEA12u, "SceSblAuthMgrForDriver", true)]
    [InlineData(0x24C4CE64u, "SceSblAuthMgrForDriver", true)]
    public void EveryExportHasAnEntry(uint nid, string library, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == library).ToList();
        Assert.Single(bindings);
        Assert.Equal(isDefault, bindings[0].Default);
    }

    [Fact]
    public void TheContextCalls()
    {
        uint context = _harness.Word(0);
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrOpen", 0u));
        Assert.Equal(Unavailable, Call(Services, "ksceSblAuthMgrOpen", context));
        Assert.Equal(0, Call(Services, "ksceSblAuthMgrClose", 1));
        Assert.Equal(BadContext, Call(Services, "ksceSblAuthMgrClose", 2));
    }

    [Fact]
    public void TheSelfAuthenticationChecksItsArgumentsThenNeedsTheSecureProcessor()
    {
        uint header = _harness.Alloc(0x1000), info = _harness.Alloc(0x130);
        Assert.Equal(BadContext, Call(Services, "ksceSblAuthMgrAuthHeader", 0, header, 0x1000u, info));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrAuthHeader", 1, 0u, 0x1000u, info));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrAuthHeader", 1, header, 0x1000u, 0u));
        Assert.Equal(Unavailable, Call(Services, "ksceSblAuthMgrAuthHeader", 1, header, 0x1000u, info));

        Assert.Equal(BadContext, Call(Services, "ksceSblAuthMgrSetupAuthSegment", 3, 0));
        Assert.Equal(Unavailable, Call(Services, "ksceSblAuthMgrSetupAuthSegment", 1, 0));

        uint block = (_harness.Alloc(0x80) + 0x1F) & ~0x1Fu;
        Assert.Equal(BadContext, Call(Services, "ksceSblAuthMgrLoadBlock", 0, block, 0x40u));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrLoadBlock", 1, block + 4, 0x40u));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrLoadBlock", 1, block, 0u));
        Assert.Equal(Unavailable, Call(Services, "ksceSblAuthMgrLoadBlock", 1, block, 0x40u));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, 0)]
    [InlineData(0x03740000u, 0)]
    [InlineData(0x03740FFFu, 0)] // the build bits do not count
    [InlineData(0x03600000u, 0)]
    [InlineData(0x03741000u, NewerFirmware)]
    [InlineData(0x03800000u, NewerFirmware)]
    public void CompareSwVersionAcceptsUpTo374(uint version, int expected)
    {
        Assert.Equal(expected, Call(Services, "ksceSblAuthMgrCompareSwVersion", version));
    }

    [Fact]
    public void TheKeyServicesCheckTheirArgumentsThenNeedTheSecureProcessor()
    {
        uint key = _harness.Alloc(0x20);
        Assert.Equal(0, Call(Services, "AuthMgrNid2A83A012"));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrSetDmac5Key", key, 0x18u, 0, 0));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrSetDmac5Key", key, 0x10u, 0, 3));
        Assert.Equal(Unavailable, Call(Services, "ksceSblAuthMgrSetDmac5Key", key, 0x20u, 0, 0x20001));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrClearDmac5Key", 0, 1));
        Assert.Equal(Unavailable, Call(Services, "ksceSblAuthMgrClearDmac5Key", 0, 0));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrGetEKc", key, 0x10u, 3u));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrGetEKc", key, 0x110u, 0u));
        Assert.Equal(Unavailable, Call(Services, "ksceSblAuthMgrGetEKc", key, 0x100u, 2u));

        uint data = (_harness.Alloc(0x100) + 0x3F) & ~0x3Fu, bind = (_harness.Alloc(0x100) + 0x3F) & ~0x3Fu;
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrDecBindData", data + 0x10, 0x10u, bind, 0x90u, 0));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrDecBindData", data, 0x10u, bind, 0u, 0));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrDecBindData", data, 0x10u, bind, 0x90u, 1));
        Assert.Equal(Unavailable, Call(Services, "ksceSblAuthMgrDecBindData", data, 0x10u, bind, 0x90u, 0));

        uint context = _harness.Alloc(0x10), file = (_harness.Alloc(0x40) + 0x1F) & ~0x1Fu;
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrVerifySpfsoCtx", 0u));
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrVerifySpfsoCtx", context)); // no buffer
        GuestMemory.W32(context + 4, file);
        GuestMemory.W32(context + 8, 0x8001);
        Assert.Equal(InvalidArgument, Call(Services, "ksceSblAuthMgrVerifySpfsoCtx", context));
        GuestMemory.W32(context + 8, 0x200);
        Assert.Equal(Unavailable, Call(Services, "ksceSblAuthMgrVerifySpfsoCtx", context));
    }
}
