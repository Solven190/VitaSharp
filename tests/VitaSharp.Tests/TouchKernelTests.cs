using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The SceTouchForDriver entries that keep a value or that SceTouch 3.74 leaves empty.
/// </summary>
[Collection("guest")]
public sealed class TouchKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80350001);

    private readonly HleHarness _harness = new(fixture, "touchk");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x03C3AC7Cu)]
    [InlineData(0xA32529B8u)]
    [InlineData(0x628D9324u)]
    [InlineData(0x384F6C68u)]
    public void EveryEntryIsBound(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceTouchForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void TheEntriesAnswerAsTheModule()
    {
        Assert.Equal(0, Call(Services, "ksceTouchRegisterVirtualTouchDriver", 0u));
        Assert.Equal(0, Call(Services, "ksceTouchSetTouchEmulationData", 0u));
        Assert.Equal(Invalid, Call(Services, "ksceTouchSetCtrlpCallback", 0u));
        Assert.Equal(0, Call(Services, "ksceTouchSetCtrlpCallback", 0x81001000u));
        Assert.Equal(Invalid, Call(Services, "TouchSetPortWord", 2u, 1u));
        Assert.Equal(0, Call(Services, "TouchSetPortWord", 1u, 7u));
        Assert.Equal((0x81001000u, 0u, 7u), Services.TouchKernelState);
    }
}
