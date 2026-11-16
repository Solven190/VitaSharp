using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The last functions of small firmware libraries (FW 3.74 modules read in their code): SceUsbSerial, SceUsbAudioIn,
/// SceDrmBridge/SceDrmBridgeUser and SceThreadmgr, each answered as its module answers on a unit without the device or
/// token. The NID test closes each library: every function the firmware exports there has an [Hle] entry.
/// </summary>
[Collection("guest")]
public sealed class HleSmallLibrariesTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NotStarted = unchecked((int)0x80244401);
    private const int NoPermission = unchecked((int)0x80010058);
    private const int NoDevice = unchecked((int)0x803E0004);
    private const int BadArgument = unchecked((int)0x803E0001);

    private readonly HleHarness _harness = new(fixture, "small-libs");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    /// <summary>Every NID the FW 3.74 modules export in these libraries and the HLE was missing has an entry now.</summary>
    [Theory]
    [InlineData(0x0ACC8A92u, true)] // SceUsbSerial
    [InlineData(0x2C08272Cu, true)]
    [InlineData(0x405DB744u, true)]
    [InlineData(0x60D93778u, true)]
    [InlineData(0x642F8F19u, true)]
    [InlineData(0x923535FAu, true)]
    [InlineData(0xA281DF87u, true)]
    [InlineData(0x198A7C33u, true)] // SceUsbAudioIn
    [InlineData(0xB81B597Au, true)] // SceDrmBridge
    [InlineData(0x6F5DC6CDu, true)] // SceDrmBridgeUser
    [InlineData(0xD06F2886u, false)] // SceThreadmgr (ksceKernelLockMutexCB)
    public void TheLibrarysLastFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void UsbSerialIsNeverStartedAndAsksForTheSerialPermission()
    {
        _harness.Emu.IsSystemProgram = true;
        Assert.Equal(NotStarted, Call(Services, "UsbSerialNotStarted"));
        _harness.Emu.IsSystemProgram = false;
        Assert.Equal(NoPermission, Call(Services, "UsbSerialNotStarted"));
    }

    /// <summary>SceUsbAudioIn looks the device up before checking the output pointer; the id list and the open check
    /// their arguments first (the list is (ids, count, max)).</summary>
    [Fact]
    public void UsbAudioInLooksTheDeviceUpBeforeTheOutputPointer()
    {
        uint value = _harness.Word(0xAAAAAAAA);
        Assert.Equal(NoDevice, Call(Services, "UsbAudioInGetCurrentValueOfVolume", 0, value));
        Assert.Equal(NoDevice, Call(Services, "UsbAudioInGetCurrentValueOfVolume", 0, 0u));
        Assert.Equal(NoDevice, Call(Services, "sceUsbAudioInGetMaxValueOfVolume", 0, 0u));
        Assert.Equal(NoDevice, Call(Services, "sceUsbAudioInGetMinValueOfVolume", 0, 0u));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(value));

        uint ids = _harness.Alloc(16);
        uint count = _harness.Word(0xAAAAAAAA);
        Assert.Equal(BadArgument, Call(Services, "sceUsbAudioInGetDeviceIdList", 0u, count, 4));
        Assert.Equal(BadArgument, Call(Services, "sceUsbAudioInGetDeviceIdList", ids, 0u, 4));
        Assert.Equal(BadArgument, Call(Services, "sceUsbAudioInGetDeviceIdList", ids, count, 0));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(count));
        Assert.Equal(0, Call(Services, "sceUsbAudioInGetDeviceIdList", ids, count, 4));
        Assert.Equal(0u, GuestMemory.R32(count));

        Assert.Equal(BadArgument, Call(Services, "sceUsbAudioInOpenDevice", 0, 8, 48000));
        Assert.Equal(BadArgument, Call(Services, "sceUsbAudioInOpenDevice", 0, 16, 44100));
        Assert.Equal(NoDevice, Call(Services, "sceUsbAudioInOpenDevice", 0, 16, 48000));
    }

    [Fact]
    public void DrmBridgeQafFlagIsOffForCapabilityBit0x80()
    {
        byte[] capability = new byte[ProgramAccess.CapabilitySize];
        capability[0x80 >> 3] = 0x80;
        _harness.Emu.Access = new ProgramAccess(0x2800000000000001UL, capability, false);
        Assert.Equal(0, Call(Services, "DrmBridgeQafFlag"));
        _harness.Emu.Access = new ProgramAccess(0x2800000000000001UL, new byte[ProgramAccess.CapabilitySize], false);
        Assert.Equal(NoPermission, Call(Services, "DrmBridgeQafFlag"));
    }
}
