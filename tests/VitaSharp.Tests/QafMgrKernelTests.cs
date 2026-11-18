using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceQafMgrForDriver (sysmem 3.74): each entry is one bit of the boot loader's QA flags; a retail unit has none, so
/// every query answers 0, and every export of the library has its entry.
/// </summary>
[Collection("guest")]
public sealed class QafMgrKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "qafmgr");

    public void Dispose() => _harness.Dispose();

    [Theory]
    [InlineData(0x382C71E8u, "QafMgrFlag382C71E8")]
    [InlineData(0xCAD47130u, "ksceSblQafMgrIsAllowSystemAppDebug")]
    [InlineData(0x694D1096u, "QafMgrFlag694D1096")]
    [InlineData(0x41E04800u, "QafMgrFlag41E04800")]
    [InlineData(0x36E5312Eu, "ksceSblQafMgrIsAllowLoadMagicGate")]
    [InlineData(0x3CB55F98u, "QafMgrFlag3CB55F98")]
    [InlineData(0x8C423C18u, "QafMgrFlag8C423C18")]
    [InlineData(0x0E588747u, "QafMgrFlag0E588747")]
    [InlineData(0x082A4FC2u, "ksceSblQafMgrIsAllowHost0Access")]
    [InlineData(0xB9770A13u, "QafMgrFlagB9770A13")]
    [InlineData(0x883E9465u, "ksceSblQafMgrIsAllowDecryptedBootConfigLoad")]
    [InlineData(0x52B4E164u, "QafMgrFlag52B4E164")]
    [InlineData(0xBFD5E463u, "ksceSblQafMgrIsAllowRemotePlayDebug")]
    [InlineData(0x4BC1883Fu, "QafMgrFlag4BC1883F")]
    [InlineData(0xB7B195B2u, "ksceSblQafMgrIsAllowPSPEmuShowQAInfo")]
    [InlineData(0x9644171Du, "ksceSblQafMgrIsAllowNearTest")]
    [InlineData(0xE573F124u, "QafMgrFlagE573F124")]
    [InlineData(0x7B14DC45u, "QafMgrFlag7B14DC45")]
    [InlineData(0xAE033133u, "QafMgrFlagAE033133")]
    [InlineData(0x10283EB8u, "ksceSblQafMgrIsAllowMarlinTest")]
    [InlineData(0x70A67A4Bu, "QafMgrFlag70A67A4B")]
    [InlineData(0xC1EA75C8u, "ksceSblQafMgrIsAllowKeepCoreFile")]
    [InlineData(0xE8B8F31Fu, "ksceSblQafMgrIsAllowDtcpIpReset")]
    [InlineData(0xDEC6DF4Eu, "QafMgrFlagDEC6DF4E")]
    [InlineData(0xF8BFEE48u, "ksceSblQafMgrIsAllowControlIduAutoUpdate")]
    public void EveryQueryIsBoundAndARetailUnitAnswersNo(uint nid, string method)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceQafMgrForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
        Assert.Equal(0, Call(_harness.Module<SystemServicesVsh>(), method));
    }
}
