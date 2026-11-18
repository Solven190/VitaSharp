using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceAVConfigForDriver (SceAVConfig 3.74): the two worker switches and the output request table SceAppMgr drives
/// (16 slots, the lowest active slot's request is applied), on the bridge object that serves the module's user entries.
/// </summary>
[Collection("guest")]
public sealed class AvConfigKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x803A0101);

    private readonly HleHarness _harness = new(fixture, "avck");

    public void Dispose() => _harness.Dispose();

    private VshBridge Bridge => _harness.Module<VshBridge>();

    [Theory]
    [InlineData(0x0E60701Au)]
    [InlineData(0x2A2E9042u)]
    [InlineData(0x3817CDA1u)]
    [InlineData(0x5662E799u)]
    [InlineData(0x91792D6Fu)]
    [InlineData(0xD8B7AE3Du)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceAVConfigForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void TheSwitchesTakeTheirRanges()
    {
        Assert.Equal(0, Call(Bridge, "AvcKernelSetSwitch", 1u));
        Assert.Equal(Invalid, Call(Bridge, "AvcKernelSetSwitch", 2u));
        Assert.Equal(1, Bridge.AvcKernelSwitch);
        Assert.Equal(0, Call(Bridge, "AvcKernelSetMode", 2u));
        Assert.Equal(Invalid, Call(Bridge, "AvcKernelSetMode", 3u));
        Assert.Equal(2, Bridge.AvcKernelMode);
    }

    [Fact]
    public void ARequestOutOfRangeIsRefused()
    {
        Assert.Equal(Invalid, Call(Bridge, "AvcKernelRequestOutput", 16u, 0u, 0u, 0));
        Assert.Equal(Invalid, Call(Bridge, "AvcKernelRequestOutput", 0u, 2u, 0u, 0));
        Assert.Equal(Invalid, Call(Bridge, "AvcKernelRequestOutput", 0u, 0u, 5u, 0));
        Assert.Equal(-1, Bridge.AvcAppliedOutputSlot);
    }

    [Fact]
    public void TheHighestRankedRequestIsApplied()
    {
        Assert.Equal(0, Call(Bridge, "AvcKernelRequestOutput", 5u, 1u, 2u, 1));
        Assert.Equal(5, Bridge.AvcAppliedOutputSlot);
        Assert.Equal((true, 1, 2, 1), Bridge.AvcAppliedOutput());

        // a lower-ranked request is only kept
        Assert.Equal(0, Call(Bridge, "AvcKernelRequestOutput", 9u, 0u, 4u, 0));
        Assert.Equal(5, Bridge.AvcAppliedOutputSlot);

        // a higher-ranked one takes over
        Assert.Equal(0, Call(Bridge, "AvcKernelRequestOutputAlias", 2u, 0u, 3u, 7));
        Assert.Equal((true, 0, 3, 7), Bridge.AvcAppliedOutput());

        // the leading slot keeps its first request
        Assert.Equal(0, Call(Bridge, "AvcKernelRequestOutput", 2u, 1u, 1u, 1));
        Assert.Equal((true, 0, 3, 7), Bridge.AvcAppliedOutput());
    }

    [Fact]
    public void ReleasingFallsBackToTheNextSlotThenTheDefault()
    {
        Call(Bridge, "AvcKernelRequestOutput", 1u, 1u, 1u, 0);
        Call(Bridge, "AvcKernelRequestOutput", 4u, 0u, 2u, 0);
        Call(Bridge, "AvcKernelRequestOutput", 8u, 1u, 3u, 0);

        // a lower slot released under the leader: nothing changes
        Assert.Equal(0, Call(Bridge, "AvcKernelReleaseOutput", 4u));
        Assert.Equal(1, Bridge.AvcAppliedOutputSlot);
        Assert.Equal(Invalid, Call(Bridge, "AvcKernelReleaseOutput", 4u));

        // the leader released: the next active slot
        Assert.Equal(0, Call(Bridge, "AvcKernelReleaseOutput", 1u));
        Assert.Equal((true, 1, 3, 0), Bridge.AvcAppliedOutput());

        // the last one: the default output
        Assert.Equal(0, Call(Bridge, "AvcKernelReleaseOutput", 8u));
        Assert.Equal((false, 0, 0, 0), Bridge.AvcAppliedOutput());
        Assert.Equal(Invalid, Call(Bridge, "AvcKernelReleaseOutput", 16u));
    }

    [Fact]
    public void TheParameterChangesOnlyAnActiveSlot()
    {
        Assert.Equal(Invalid, Call(Bridge, "AvcKernelChangeOutputParam", 3u, 1));
        Call(Bridge, "AvcKernelRequestOutput", 3u, 1u, 0u, 0);
        Call(Bridge, "AvcKernelRequestOutput", 6u, 1u, 0u, 0);
        Assert.Equal(0, Call(Bridge, "AvcKernelChangeOutputParam", 6u, 5));
        Assert.Equal((true, 1, 0, 0), Bridge.AvcAppliedOutput());
        Assert.Equal(0, Call(Bridge, "AvcKernelChangeOutputParam", 3u, 1));
        Assert.Equal((true, 1, 0, 1), Bridge.AvcAppliedOutput());
    }
}
