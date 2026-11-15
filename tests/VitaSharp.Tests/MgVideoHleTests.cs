using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>SceMgVideo / SceMgVideoForMiniApp as os0:kd/mgvideo.skprx 3.74 answers them on a unit without MagicGate.</summary>
[Collection("guest")]
public sealed class MgVideoHleTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "mgvideo");

    public void Dispose() => _harness.Dispose();

    /// <summary>A capability with only bit <paramref name="bit"/> set (counted from the first byte's top bit).</summary>
    private void RunWithCapabilityBit(int bit)
    {
        byte[] capability = new byte[ProgramAccess.CapabilitySize];
        capability[bit >> 3] = (byte)(0x80 >> (bit & 7));
        _harness.Emu.Access = new ProgramAccess(0x2800000000000001UL, capability, false);
    }

    [Fact]
    public void CapabilityBit0x81IsRequired()
    {
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        RunWithCapabilityBit(1); // a system program, but without the video capability
        Assert.Equal(SystemServicesVsh.MgVideoErrNotAllowed, Call(services, "MgVideoOpen", 0u, 0u, 0u));
        Assert.Equal(SystemServicesVsh.MgVideoErrNotAllowed, Call(services, "MgVideoWithSession"));
        Assert.Equal(SystemServicesVsh.MgVideoErrNotAllowed, Call(services, "MgVideoRelease"));
        RunWithCapabilityBit(0x80); // the neighbouring bit (the Marlin one) is not enough
        Assert.Equal(SystemServicesVsh.MgVideoErrNotAllowed, Call(services, "MgVideoOpen", 0u, 0u, 0u));
    }

    [Fact]
    public void OpenTakesOnlyZeroArgumentsAndFindsNoMagicGate()
    {
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        RunWithCapabilityBit(SystemServicesVsh.MgVideoCapabilityBit);
        Assert.Equal(SystemServicesVsh.MgVideoErrInvalid, Call(services, "MgVideoOpen", 1u, 0u, 0u));
        Assert.Equal(SystemServicesVsh.MgVideoErrInvalid, Call(services, "MgVideoOpen", 0u, 1u, 0u));
        Assert.Equal(SystemServicesVsh.MgVideoErrInvalid, Call(services, "MgVideoOpen", 0u, 0u, 1u));
        Assert.Equal(SystemServicesVsh.MgVideoErrNoMagicGate, Call(services, "MgVideoOpen", 0u, 0u, 0u));
    }

    [Fact]
    public void WithoutASessionCallsAnswerNotOpenAndReleasesZero()
    {
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        RunWithCapabilityBit(SystemServicesVsh.MgVideoCapabilityBit);
        Assert.Equal(SystemServicesVsh.MgVideoErrNotOpen, Call(services, "MgVideoWithSession"));
        Assert.Equal(0, Call(services, "MgVideoRelease"));
        Assert.Equal(SystemServicesVsh.MgVideoErrNotOpen, Call(services, "MgVideoFBF16BC5", 0x10000u, 0u));
        Assert.Equal(SystemServicesVsh.MgVideoErrNotOpen, Call(services, "MgVideoBCC6E991", 0x10000u, 0u, 0u, 0u));
    }

    [Fact]
    public void ParameterBlocksAreCopiedBeforeTheCapabilityCheck()
    {
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        RunWithCapabilityBit(1); // no video capability: the copy error still comes first
        Assert.Equal(KernelSpace.UserCopyFault, Call(services, "MgVideoECB4B61C", 0x10000u, 0u, 0u, 0u));
        Assert.Equal(KernelSpace.UserCopyFault, Call(services, "MgVideoE9367823", 0x10000u, 0u, 0u, 0u));
        uint block = _harness.Alloc(0x20);
        Assert.Equal(SystemServicesVsh.MgVideoErrNotAllowed, Call(services, "MgVideoECB4B61C", 0x10000u, 0u, 0u, block));

        // a pointer inside the block that cannot be read
        GuestMemory.W32(block, 0x10);
        Assert.Equal(KernelSpace.UserCopyFault, Call(services, "MgVideo74907E1E", 0x10000u, 0u, 0u, block));
    }

    [Fact]
    public void SizeLimitsComeBeforeTheSession()
    {
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        RunWithCapabilityBit(SystemServicesVsh.MgVideoCapabilityBit);
        uint size = _harness.Alloc(8);
        uint data = _harness.Alloc(0x404);
        GuestMemory.W32(size, 0x401);
        Assert.Equal(SystemServicesVsh.MgVideoErrTooLarge, Call(services, "MgVideo3F3E3A8E", 0x10000u, data, 0u, size));
        GuestMemory.W32(size, 0x400);
        Assert.Equal(SystemServicesVsh.MgVideoErrNotOpen, Call(services, "MgVideo3F3E3A8E", 0x10000u, data, 0u, size));
        // without data the size is not looked at
        GuestMemory.W32(size, 0x1000);
        Assert.Equal(SystemServicesVsh.MgVideoErrNotOpen, Call(services, "MgVideo3F3E3A8E", 0x10000u, 0u, 0u, size));

        uint parameters = _harness.Alloc(0x10);
        uint output = _harness.Alloc(0x40);
        GuestMemory.W32(parameters + 8, 0x41);
        Assert.Equal(SystemServicesVsh.MgVideoErrTooLarge, Call(services, "MgVideo0F6359B1", 0x10000u, output, 0u, parameters));
    }

    [Fact]
    public void OutputBuffersAreClearedWhateverTheResult()
    {
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        RunWithCapabilityBit(SystemServicesVsh.MgVideoCapabilityBit);
        uint parameters = _harness.Alloc(0x10);
        uint output = _harness.Alloc(400);
        GuestMemory.Fill(output, 0xAA, 400);
        GuestMemory.W32(parameters + 8, 0x20);
        Assert.Equal(SystemServicesVsh.MgVideoErrNotOpen, Call(services, "MgVideo0F6359B1", 0x10000u, output, 0u, parameters));
        Assert.Equal(0u, GuestMemory.R32(output + 0x1C));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(output + 0x20)); // only the given size

        GuestMemory.Fill(output, 0xAA, 400);
        Assert.Equal(SystemServicesVsh.MgVideoErrNotOpen, Call(services, "MgVideoACD50568", 0x10000u, 0u, output, parameters));
        Assert.Equal(0u, GuestMemory.R32(output + 396));
    }
}
