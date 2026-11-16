using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The last functions of SceNet (and their SceNetInternal twins) and ScePspnetAdhoc, each answered as libnet.suprx and
/// pspnet_adhoc.suprx 3.74 answer on a unit with only the loopback interface and no ad hoc network.
/// </summary>
[Collection("guest")]
public sealed class HleSmallLibraries4Tests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotInit = unchecked((int)0x804101C8);
    private const int Invalid = unchecked((int)0x80410116);
    private const int CtlNotInit = unchecked((int)0x80410B08);

    private readonly HleHarness _harness = new(fixture, "small-libs4");

    public void Dispose() => _harness.Dispose();

    private Net NetModule => _harness.Module<Net>();

    private void NetInit()
    {
        uint param = _harness.Alloc(12);
        GuestMemory.W32(param, _harness.Alloc(0x4000));
        GuestMemory.W32(param + 4, 0x4000);
        Assert.Equal(0, Call(NetModule, "sceNetInit", param));
    }

    [Theory]
    [InlineData(0x522D2118u, false)] // SceNet
    [InlineData(0x9DBD2F60u, false)] // SceNetInternal (same entry)
    [InlineData(0x1F0080B3u, false)]
    [InlineData(0x46E28336u, false)]
    [InlineData(0xD886F498u, false)]
    [InlineData(0x5C83C5C8u, false)] // ScePspnetAdhoc
    [InlineData(0xF87F9518u, false)]
    [InlineData(0xA3EC45B9u, true)]
    [InlineData(0x8938A4D0u, true)]
    [InlineData(0x5B3DF8DFu, true)] // SceUsbstor
    public void TheLibrarysLastFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void NetStackSettingAndListNeedInitAndTheirReservedWordZero()
    {
        uint entries = _harness.Alloc(16);
        Assert.Equal(NotInit, Call(NetModule, "NetStackSetting19", 1u, 0u, 0u));
        Assert.Equal(NotInit, Call(NetModule, "NetStackFlaggedList", entries, 2, 0u));
        NetInit();
        Assert.Equal(0, Call(NetModule, "NetStackSetting19", 1u, 0u, 0u));
        Assert.Equal(0, Call(NetModule, "NetStackSetting19", 1u, 1u, 0u));
        Assert.Equal(Invalid, Call(NetModule, "NetStackSetting19", 1u, 2u, 0u));
        Assert.Equal(Invalid, Call(NetModule, "NetStackSetting19", 1u, 0u, 1u));
        Assert.Equal(0, Call(NetModule, "NetStackFlaggedList", entries, 2, 0u)); // the list is empty
        Assert.Equal(Invalid, Call(NetModule, "NetStackFlaggedList", entries, 2, 1u));
        Assert.Equal(Invalid, Call(NetModule, "NetStackFlaggedList", 0u, 2, 0u)); // the stack's EINVAL
        Assert.Equal(0, Call(NetModule, "sceNetSyscallControl", -1, 0x21, entries, 16));
    }

    [Fact]
    public void NetInterfaceHardwareAddressTakesExactlyOneInterface()
    {
        uint mac = _harness.Alloc(8);
        Assert.Equal(NotInit, Call(NetModule, "NetInterfaceHardwareAddress", mac, 2u));
        NetInit();
        Assert.Equal(Invalid, Call(NetModule, "NetInterfaceHardwareAddress", 0u, 2u));
        Assert.Equal(Invalid, Call(NetModule, "NetInterfaceHardwareAddress", mac, 0u));
        Assert.Equal(Invalid, Call(NetModule, "NetInterfaceHardwareAddress", mac, 3u));
        Assert.Equal(Invalid, Call(NetModule, "NetInterfaceHardwareAddress", mac, 4u));
        GuestMemory.Fill(mac, 0xAA, 8);
        Assert.Equal(0, Call(NetModule, "NetInterfaceHardwareAddress", mac, 1u));
        Assert.Equal(0u, GuestMemory.R32(mac)); // no eth0 here
        Assert.Equal(0xAAAAu, GuestMemory.R32(mac + 4) >> 16);
        Assert.Equal(0, Call(NetModule, "NetInterfaceHardwareAddress", mac, 2u));
    }

    /// <summary>"PSP" + '_' + the id type letter + the id's 9 bytes + "_L_" + up to 8 name characters.</summary>
    [Fact]
    public void AdhocSsidIsBuiltFromTheAdhocIdAndTheGroupName()
    {
        NetAdhoc adhoc = _harness.Module<NetAdhoc>();
        NetInit();
        uint id = _harness.Alloc(16);
        GuestMemory.W32(id, 1);
        GuestMemory.WriteCString(id + 4, "ULJM05001");
        Assert.Equal(0, Call(adhoc, "sceNetAdhocctlInit", id));
        uint ssid = _harness.Alloc(0x40);
        Assert.Equal(unchecked((int)0x80410B04), Call(adhoc, "AdhocGroupSsid", 0u, 0u, 0x40u));
        Assert.Equal(unchecked((int)0x80412111), Call(adhoc, "AdhocGroupSsid", 0u, ssid, 0x20u));
        Assert.Equal(0x11 + 8, Call(adhoc, "AdhocGroupSsid", _harness.Str("GROUPNAME9"), ssid, 0x21u));
        Assert.Equal("PSP_XULJM05001_L_GROUPNAM", GuestMemory.ReadCString(ssid));
        Assert.Equal(0x11, Call(adhoc, "AdhocGroupSsid", 0u, ssid, 0x40u));
        Assert.Equal("PSP_XULJM05001_L_", GuestMemory.ReadCString(ssid));
        Assert.Equal(0, Call(adhoc, "sceNetAdhocctlTerm"));
        GuestMemory.W32(id, 2);
        Assert.Equal(0, Call(adhoc, "sceNetAdhocctlInit", id));
        Assert.Equal(0x11 + 2, Call(adhoc, "AdhocGroupSsid", _harness.Str("AB"), ssid, 0x40u));
        Assert.Equal("PSP_SULJM05001_L_AB", GuestMemory.ReadCString(ssid));
    }

    [Fact]
    public void AdhocAuthServerStartsAfterCtlInitAndStopsOnce()
    {
        NetAdhoc adhoc = _harness.Module<NetAdhoc>();
        uint data = _harness.Alloc(0x80);
        uint parameters = _harness.Alloc(0x14);
        Assert.Equal(CtlNotInit, Call(adhoc, "AdhocAuthServerStart", 0x40, 0x2000u, parameters, 2, data));
        Assert.Equal(CtlNotInit, Call(adhoc, "AdhocAuthServerStop"));
        NetInit();
        uint id = _harness.Alloc(16);
        Assert.Equal(0, Call(adhoc, "sceNetAdhocctlInit", id));
        Assert.Equal(unchecked((int)0x80412108), Call(adhoc, "AdhocAuthServerStop"));
        Assert.Equal(0, Call(adhoc, "AdhocAuthServerStart", 0x40, 0x2000u, parameters, 2, data));
        Assert.Equal(0, Call(adhoc, "AdhocAuthServerStart", 0x40, 0x2000u, parameters, 2, data)); // a running one is replaced
        Assert.Equal(0, Call(adhoc, "AdhocAuthServerStop"));
        Assert.Equal(unchecked((int)0x80412108), Call(adhoc, "AdhocAuthServerStop"));

        uint product = _harness.Alloc(8);
        GuestMemory.Fill(product, 0x5A, 8);
        HleHarness.Invoke(adhoc, "AdhocSetProductId", product);
        Assert.Equal(Enumerable.Repeat((byte)0x5A, 8), adhoc.ProductId);
        HleHarness.Invoke(adhoc, "AdhocSetProductId", 0u);
        Assert.Equal(new byte[8], adhoc.ProductId);
    }

    /// <summary>SceUsbstor's syscall asks for a system program (the QA settings also pass) before it looks at the
    /// storage units: none is attached.</summary>
    [Fact]
    public void UsbstorAnswersSystemProgramsThatNoUnitIsActive()
    {
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        _harness.Emu.IsSystemProgram = true;
        Assert.Equal(0, Call(services, "UsbstorAnyUnitActive"));
        _harness.Emu.IsSystemProgram = false;
        Assert.Equal(unchecked((int)0x80010058), Call(services, "UsbstorAnyUnitActive"));
    }
}
