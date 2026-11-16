using System.Reflection;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// np_commerce2.suprx 3.74 exports whose code is another export's with one constant changed (decompiled and compared
/// instruction by instruction, study only): six request creators with their request type, and the stop of the
/// module's other background store check. The parsers among them are checked against the firmware module by
/// <see cref="NpCommerce2FirmwareTests"/>. Found by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class NpCommerce2VariantsTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "c2var");

    public void Dispose() => _harness.Dispose();

    private HleModule Commerce => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == "NpCommerce2");

    [Theory]
    [InlineData("Create4A6B0527", 0x16)]
    [InlineData("Create72ABB589", 0x0E)]
    [InlineData("Create901E6455", 0x43)]
    [InlineData("Create94AB4446", 0x15)]
    [InlineData("CreateB5D6F4AF", 0x0F)]
    [InlineData("CreateDB8BA72E", 0x3E)]
    public void TheRequestCreatorsMakeARequestOfTheirType(string creator, int type)
    {
        HleModule c2 = Commerce;
        uint ctxOut = _harness.Word(0);
        uint req = _harness.Word(0);
        Assert.Equal(unchecked((int)0x80550F01), Call(c2, creator, 1u, req));
        Assert.Equal(0, Call(c2, "sceNpCommerce2Init"));
        Assert.Equal(0, Call(c2, "sceNpCommerce2CreateCtx", 2u, new Ptr<uint>(ctxOut)));
        uint ctx = GuestMemory.R32(ctxOut);
        Assert.Equal(unchecked((int)0x80550F03), Call(c2, creator, ctx, 0u));
        Assert.Equal(unchecked((int)0x80550F0C), Call(c2, creator, ctx + 100, req));
        Assert.Equal(0, Call(c2, creator, ctx, req));
        uint id = GuestMemory.R32(req);
        Assert.Equal(ctx, id >> 16);
        var types = (Dictionary<uint, ushort>)c2.GetType().GetField("_reqTypes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(c2)!;
        Assert.Equal((ushort)type, types[id]);
    }

    [Fact]
    public void StoppingTheOtherStoreCheckIsAlwaysZero()
    {
        Assert.Equal(0, Call(Commerce, "NID_785095D9"));
        Assert.Equal(0, Call(Commerce, "sceNpCommerce2Init"));
        Assert.Equal(0, Call(Commerce, "NID_785095D9"));
    }
}
