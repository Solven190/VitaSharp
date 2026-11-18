using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceUsbServForDriver, SceUsbAudioForDriver and SceUsbEtherRtlForDriver (3.74) on a retail handheld: the PHY selector
/// of the three channels (all selected 1 at start), the PS TV's Ethernet refused (0x80248002), the USB audio class
/// driver with no device (its argument checks first) and its callback list, and the PS TV's USB Ethernet adapter
/// absent (the control transfer's 0x80240003).
/// </summary>
[Collection("guest")]
public sealed class UsbKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int ServNotDolce = unchecked((int)0x80248002), ServInvalid = unchecked((int)0x80248003);
    private const int AudioInvalid = unchecked((int)0x803E0001), AudioRegistered = unchecked((int)0x803E0002);
    private const int AudioNoDevice = unchecked((int)0x803E0004), UsbdInvalid = unchecked((int)0x80240003);

    private readonly HleHarness _harness = new(fixture, "usbk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData("SceUsbServForDriver", 0x6D738018u, false)]
    [InlineData("SceUsbServForDriver", 0xD787B191u, false)]
    [InlineData("SceUsbServForDriver", 0x30AE5F66u, false)]
    [InlineData("SceUsbServForDriver", 0xF0553A69u, false)]
    [InlineData("SceUsbServForDriver", 0x7AD36284u, false)]
    [InlineData("SceUsbAudioForDriver", 0x96EC3E18u, true)]
    [InlineData("SceUsbAudioForDriver", 0x863B0617u, true)]
    [InlineData("SceUsbAudioForDriver", 0xEFB4E3E4u, true)]
    [InlineData("SceUsbAudioForDriver", 0x974DBE03u, false)]
    [InlineData("SceUsbAudioForDriver", 0x23A07D29u, false)]
    [InlineData("SceUsbEtherRtlForDriver", 0x1E0E3E59u, true)]
    [InlineData("SceUsbEtherRtlForDriver", 0x7473CB10u, true)]
    [InlineData("SceUsbEtherRtlForDriver", 0xBAF95A47u, true)]
    public void EveryExportHasAnEntry(string library, uint nid, bool noDevice)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == library).ToList();
        Assert.Single(bindings);
        Assert.Equal(noDevice, bindings[0].Default); // the answers without a device are documented defaults
    }

    [Fact]
    public void ThePhySelectorStartsAtOneAndSwitches()
    {
        Assert.Equal(1, Call(Services, "ksceUsbServMacGet", 0u));
        Assert.Equal(1, Call(Services, "ksceUsbServMacGet", 2u));
        Assert.Equal(ServInvalid, Call(Services, "ksceUsbServMacGet", 3u));
        Assert.Equal(0, Call(Services, "ksceUsbServMacSelect", 1, 0u));
        Assert.Equal(0, Call(Services, "ksceUsbServMacGet", 1u));
        Assert.Equal(1, Call(Services, "ksceUsbServMacGet", 0u));
        Assert.Equal(0, Call(Services, "ksceUsbServMacSelect", 1, 0u)); // the same selection again
        Assert.Equal(ServInvalid, Call(Services, "ksceUsbServMacSelect", 0, 2u));
        Assert.Equal(ServInvalid, Call(Services, "ksceUsbServMacSelect", 3, 0u));
    }

    [Fact]
    public void TheEthernetIsForThePsTvOnly()
    {
        Assert.Equal(ServNotDolce, Call(Services, "ksceUsbServEtherEnable"));
        Assert.Equal(ServNotDolce, Call(Services, "ksceUsbServEtherDisable"));
        Assert.Equal(ServNotDolce, Call(Services, "ksceUsbServDisableEtherSuspend"));
        Assert.Equal(1, Services.UsbServEtherSuspend);
    }

    [Fact]
    public void TheAudioDriverChecksItsArgumentsThenFindsNoDevice()
    {
        uint ids = _harness.Alloc(32);
        Assert.Equal(AudioInvalid, Call(Services, "ksceUsbAudioGetDeviceIdList", 0u, 8));
        Assert.Equal(0, Call(Services, "ksceUsbAudioGetDeviceIdList", ids, 8));
        uint buffer = _harness.Alloc(0x600);
        foreach (string entry in new[] { "ksceUsbAudioInput", "ksceUsbAudioInput2" })
        {
            Assert.Equal(AudioNoDevice, Call(Services, entry, 1, buffer, 0xC0u));
            Assert.Equal(AudioNoDevice, Call(Services, entry, 1, buffer, 0x600u));
            Assert.Equal(AudioInvalid, Call(Services, entry, 1, buffer, 0u));
            Assert.Equal(AudioInvalid, Call(Services, entry, 1, buffer, 0x6C0u));
            Assert.Equal(AudioInvalid, Call(Services, entry, 1, buffer, 0x100u));
            Assert.Equal(AudioInvalid, Call(Services, entry, 1, 0u, 0xC0u));
        }
    }

    [Fact]
    public void TheAudioCallbacksAreKeyedByTheirId()
    {
        Assert.Equal(0, Call(Services, "ksceUsbAudioRegisterCallback", 7, 0x81000000u, 0u, 0u));
        Assert.Equal(AudioRegistered, Call(Services, "ksceUsbAudioRegisterCallback", 7, 0x81000000u, 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceUsbAudioRegisterCallback", 8, 0x81000000u, 0u, 0u));
        Assert.Equal(0, Call(Services, "ksceUsbAudioUnregisterCallback", 7));
        Assert.Equal(0, Call(Services, "ksceUsbAudioUnregisterCallback", 7));
        Assert.Equal(0, Call(Services, "ksceUsbAudioRegisterCallback", 7, 0x81000000u, 0u, 0u));
    }

    [Fact]
    public void TheEthernetAdapterIsAbsent()
    {
        uint value = _harness.Word(0x1234);
        Assert.Equal(UsbdInvalid, Call(Services, "UsbEtherRtlWriteRegister", 0xE81C, 0x101, 0xC0u));
        Assert.Equal(-1, Call(Services, "UsbEtherRtlReadRegister", 0xDD00, 0x100, 0u));
        Assert.Equal(UsbdInvalid, Call(Services, "UsbEtherRtlReadRegister", 0xDD00, 0x100, value));
        Assert.Equal(0x1234u, GuestMemory.R32(value));
        Assert.Equal(UsbdInvalid, Call(Services, "UsbEtherRtlWriteMac", _harness.Alloc(6)));
    }
}
