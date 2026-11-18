using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceGpioForDriver (lowio 3.74): two banks of 32 ports; the port mode (1 output), the output latch through set,
/// clear and the flipping reset, reads of outputs (the latch) and inputs (the pin, undriven: 0), the interrupt modes,
/// and no interrupt pending; the bus, port and mode checks.
/// </summary>
[Collection("guest")]
public sealed class GpioKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Bus = unchecked((int)0x803F0100), Port = unchecked((int)0x803F0101), Mode = unchecked((int)0x803F0102);

    private readonly HleHarness _harness = new(fixture, "gpio");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x35AAD77Au)]
    [InlineData(0xE97A3B31u)]
    [InlineData(0xC4CB78E2u)]
    [InlineData(0xF6310435u)]
    [InlineData(0x129DF5ACu)]
    [InlineData(0xA1B5A462u)]
    [InlineData(0xD454A584u)]
    [InlineData(0x010DC295u)]
    [InlineData(0xBBEA1DDCu)]
    [InlineData(0x372022A4u)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceGpioForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void TheChecksComeInTheModulesOrder()
    {
        Assert.Equal(Bus, Call(Services, "ksceGpioSetPortMode", 2u, 40u, 5u));
        Assert.Equal(Port, Call(Services, "ksceGpioSetPortMode", 0u, 32u, 5u));
        Assert.Equal(Mode, Call(Services, "ksceGpioSetPortMode", 0u, 3u, 2u));
        Assert.Equal(Mode, Call(Services, "ksceGpioSetIntrMode", 1u, 3u, 4u));
        Assert.Equal(Port, Call(Services, "ksceGpioPortSet", 1u, 32u));
        Assert.Equal(Bus, Call(Services, "ksceGpioPortRead", 2u, 0u));
        Assert.Equal(Port, Call(Services, "ksceGpioQueryIntr", 0u, 33u));
    }

    [Fact]
    public void AnOutputReadsItsLatch()
    {
        Assert.Equal(1, Call(Services, "ksceGpioSetPortMode", 0u, 5u, 1u)); // it was an output already
        Assert.Equal(0, Call(Services, "ksceGpioPortSet", 0u, 5u));
        Assert.Equal(1, Call(Services, "ksceGpioPortRead", 0u, 5u));
        Assert.Equal(0, Call(Services, "ksceGpioPortRead", 1u, 5u)); // the other bank
        Assert.Equal(0, Call(Services, "ksceGpioPortClear", 0u, 5u));
        Assert.Equal(0, Call(Services, "ksceGpioPortRead", 0u, 5u));
        Assert.Equal(0, Call(Services, "ksceGpioPortReset", 0u, 5u)); // flips: was 0
        Assert.Equal(1, Call(Services, "ksceGpioPortRead", 0u, 5u));
        Assert.Equal(1, Call(Services, "ksceGpioPortReset", 0u, 5u));
        Assert.Equal(0, Call(Services, "ksceGpioPortRead", 0u, 5u));
    }

    [Fact]
    public void AnInputReadsItsUndrivenPin()
    {
        Assert.Equal(1, Call(Services, "ksceGpioSetPortMode", 1u, 7u, 0u));
        Assert.Equal(0, Call(Services, "ksceGpioGetPortMode", 1u, 7u));
        Call(Services, "ksceGpioPortSet", 1u, 7u);
        Assert.Equal(0, Call(Services, "ksceGpioPortRead", 1u, 7u));
        Assert.Equal(0, Call(Services, "ksceGpioSetPortMode", 1u, 7u, 1u)); // back to output: the latch shows
        Assert.Equal(1, Call(Services, "ksceGpioPortRead", 1u, 7u));
    }

    [Fact]
    public void TheInterruptModesAreKeptAndNothingIsPending()
    {
        Assert.Equal(0, Call(Services, "ksceGpioSetIntrMode", 0u, 3u, 2u));
        Assert.Equal(0, Call(Services, "ksceGpioSetIntrMode", 0u, 20u, 3u));
        Assert.Equal(2, Call(Services, "ksceGpioGetIntrMode", 0u, 3u));
        Assert.Equal(3, Call(Services, "ksceGpioGetIntrMode", 0u, 20u));
        Assert.Equal(0, Call(Services, "ksceGpioGetIntrMode", 1u, 3u));
        Assert.Equal(0, Call(Services, "ksceGpioQueryIntr", 0u, 3u));
        Assert.Equal(0, Call(Services, "ksceGpioAcquireIntr", 0u, 3u));
    }
}
