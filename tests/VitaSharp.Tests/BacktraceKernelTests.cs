using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceBacktraceForDriver (modulemgr 3.74): the unchecked twin of ksceKernelBacktrace runs the same HLE walk, and the
/// backtrace printer checks its frame array and starts at the frame of the given pc.
/// </summary>
[Collection("guest")]
public sealed class BacktraceKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "btrace");

    public void Dispose() => _harness.Dispose();

    [Theory]
    [InlineData(0x7C878F90u)]
    [InlineData(0x888E99B8u)]
    [InlineData(0x166B9C8Cu)] // ksceKernelBacktrace, the twin of the user call
    public void TheLibraryIsComplete(uint nid)
    {
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void ThePrinterChecksTheFramesAndPrintsFromTheStartPc()
    {
        SystemServicesVsh services = _harness.Module<SystemServicesVsh>();
        Assert.Equal(unchecked((int)0x80020006), Call(services, "KernelPrintBacktrace", 0x10005, 0u, 2, 0u));
        uint frames = _harness.Alloc(0x30);
        for (uint i = 0; i < 3; i++)
        {
            GuestMemory.W32(frames + i * 0x10, 0x81000000 + i * 0x40);
            GuestMemory.W32(frames + i * 0x10 + 4, 0x81100000 + i * 4);
        }

        Assert.Equal(0, Call(services, "KernelPrintBacktrace", 0x10005, frames, 3, 0x81100004u));
        Assert.Equal(0, Call(services, "KernelPrintBacktrace", 0x10005, frames, 3, 0u));
    }
}
