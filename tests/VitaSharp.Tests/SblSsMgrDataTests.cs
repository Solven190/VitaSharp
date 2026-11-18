using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblSsMgrForDriver (ss_mgr.skprx 3.74), second part: the typed NVS values (the module's offset/size table over
/// the syscon NVS), the portability copy of key type 0 and its checks, and the secure processor's pass phrase.
/// </summary>
[Collection("guest")]
public sealed class SblSsMgrDataTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NvsArgument = unchecked((int)0x800F0016), PortabilityArgument = unchecked((int)0x800F1716);
    private const int Unavailable = unchecked((int)0x80020004), Invalid = unchecked((int)0x800F0C16);

    private readonly HleHarness _harness = new(fixture, "ssdata");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0xFDD6D5DEu, false)]
    [InlineData(0x249ADB07u, false)]
    [InlineData(0x21EC51F6u, false)]
    [InlineData(0x934DB6B5u, false)]
    [InlineData(0xB8B298FDu, true)]
    public void EveryEntryIsBound(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceSblSsMgrForDriver").ToList();
        Assert.Single(bindings);
        Assert.Equal(isDefault, bindings[0].Default);
    }

    [Fact]
    public void ATypedValueIsItsPlaceInTheNvs()
    {
        uint value = _harness.Alloc(0x20), raw = _harness.Alloc(0x10), back = _harness.Alloc(0x20);
        for (uint i = 0; i < 0x20; i++)
        {
            GuestMemory.Write(value + i, (byte)(0x40 + i));
        }

        Assert.Equal(0, Call(Services, "ksceSblSsSetNvsData", 3u, value, 0x20u)); // type 3: 0x20 bytes at 0x4E0
        Assert.Equal(0, Call(Services, "ksceSysconNvsReadData", 0x4F0u, raw, 0x10u));
        Assert.Equal(0x50, GuestMemory.ReadSpan(raw, 1)[0]);
        Assert.Equal(0, Call(Services, "ksceSblSsGetNvsData", 3u, back, 0x20u));
        Assert.True(GuestMemory.ReadSpan(back, 0x20).SequenceEqual(GuestMemory.ReadSpan(value, 0x20)));

        Assert.Equal(0, Call(Services, "ksceSblSsSetNvsData", 0u, value, 4u)); // type 0: 4 bytes at 0x4A4
        Assert.Equal(0, Call(Services, "ksceSysconNvsReadData", 0x4A4u, raw, 4u));
        Assert.Equal(0x40, GuestMemory.ReadSpan(raw, 1)[0]);
    }

    [Theory]
    [InlineData(6u, 0x20u)] // no such type
    [InlineData(3u, 0x10u)] // smaller than the value
    [InlineData(0u, 3u)] // not a buffer size the module takes
    public void TypedValueRequestsOutsideTheRulesAreRefused(uint type, uint size)
    {
        uint buffer = _harness.Alloc(0x20);
        Assert.Equal(NvsArgument, Call(Services, "ksceSblSsGetNvsData", type, buffer, size));
        Assert.Equal(NvsArgument, Call(Services, "ksceSblSsSetNvsData", type, buffer, size));
        Assert.Equal(NvsArgument, Call(Services, "ksceSblSsGetNvsData", 0u, 0u, 4u));
    }

    private uint Message(uint size, byte fill)
    {
        uint address = _harness.Alloc(0x24);
        GuestMemory.W32(address, size);
        GuestMemory.Fill(address + 4, fill, 0x20);
        return address;
    }

    [Fact]
    public void PortabilityKeyTypeZeroCopiesAndTheOthersNeedTheSecureProcessor()
    {
        uint iv = _harness.Alloc(0x10), source = Message(0x10, 0x11), target = Message(0x10, 0);
        Assert.Equal(0, Call(Services, "ksceSblSsEncryptWithPortability", 0u, iv, source, target));
        Assert.Equal(0x11, GuestMemory.ReadSpan(target + 4, 1)[0]);
        Assert.Equal(0, Call(Services, "ksceSblSsDecryptWithPortability", 0u, iv, target, Message(0x10, 0)));
        Assert.Equal(Unavailable, Call(Services, "ksceSblSsEncryptWithPortability", 1u, iv, source, target));
        Assert.Equal(PortabilityArgument, Call(Services, "ksceSblSsEncryptWithPortability", 0x15u, iv, source, target));
        Assert.Equal(PortabilityArgument, Call(Services, "ksceSblSsDecryptWithPortability", 0u, 0u, source, target));
        Assert.Equal(PortabilityArgument, Call(Services, "ksceSblSsDecryptWithPortability", 0u, iv, source, Message(0x20, 0)));
        Assert.Equal(PortabilityArgument, Call(Services, "ksceSblSsDecryptWithPortability", 0u, iv, Message(0x8, 0), Message(0x8, 0)));

        Assert.Equal(Invalid, Call(Services, "ksceSblSsCreatePassPhrase", 0u, iv));
        Assert.Equal(Unavailable, Call(Services, "ksceSblSsCreatePassPhrase", iv, iv));
    }
}
