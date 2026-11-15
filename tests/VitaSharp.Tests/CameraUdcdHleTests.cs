using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Loader.Self;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>SceCamera NID_99B5FB62 and SceUdcd NID_FCD876FB as the bootimage.skprx 3.74 drivers answer them.</summary>
[Collection("guest")]
public sealed class CameraUdcdHleTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int CameraParam = unchecked((int)0x802E0000);
    private const int NotAllowed = unchecked((int)0x80010058);

    private readonly HleHarness _harness = new(fixture, "camera-udcd");

    public void Dispose() => _harness.Dispose();

    private static T Property<T>(object module, string name) =>
        (T)(module.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMemberException(module.GetType().Name, name)).GetValue(module)!;

    [Fact]
    public void CameraBufferModeTakesOnlyZeroOrOne()
    {
        Camera camera = _harness.Module<Camera>();
        Assert.Equal(0, Property<int>(camera, "BufferMode"));
        Assert.Equal(0, Call(camera, "CameraSetBufferMode", 1));
        Assert.Equal(1, Property<int>(camera, "BufferMode"));
        Assert.Equal(CameraParam, Call(camera, "CameraSetBufferMode", 2));
        Assert.Equal(CameraParam, Call(camera, "CameraSetBufferMode", -1)); // compared unsigned
        Assert.Equal(1, Property<int>(camera, "BufferMode"));                 // a refused mode changes nothing
        Assert.Equal(0, Call(camera, "CameraSetBufferMode", 0));
        Assert.Equal(0, Property<int>(camera, "BufferMode"));
    }

    [Fact]
    public void UdcdPowerModeIsForSystemProgramsAndOnlyOneIsBusPowered()
    {
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        _harness.Emu.Access = new ProgramAccess(0x210000101CC404D8UL, new byte[ProgramAccess.CapabilitySize], false);
        Assert.Equal(NotAllowed, Call(services, "UdcdSetPowerMode", 1));
        Assert.False(Property<bool>(services, "UdcdBusPowered"));

        byte[] capability = new byte[ProgramAccess.CapabilitySize];
        capability[0] = 0x40;
        _harness.Emu.Access = new ProgramAccess(0x2800000000000025UL, capability, false);
        Assert.Equal(0, Call(services, "UdcdSetPowerMode", 1));
        Assert.True(Property<bool>(services, "UdcdBusPowered"));
        Assert.Equal(0, Call(services, "UdcdSetPowerMode", 2)); // any other mode: self-powered, still 0
        Assert.False(Property<bool>(services, "UdcdBusPowered"));
        Assert.Equal(0, Call(services, "UdcdSetPowerMode", 1));
        Assert.Equal(0, Call(services, "UdcdSetPowerMode", 0));
        Assert.False(Property<bool>(services, "UdcdBusPowered"));
    }
}
