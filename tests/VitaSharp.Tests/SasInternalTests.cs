using System.Reflection;
using System.Text;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;

namespace VitaSharp.Tests;

/// <summary>
/// The context variants of SceSas as the FW 3.74 libsas has them (decompiled, study only): a context made in a buffer of
/// exactly the needed size, its handle the buffer aligned to 16, independent contexts, the public calls being the
/// variants on sceSasInit's context, sceSasCoreInternal's unsigned levels (0/0 writes, anything else mixes), the end of a
/// context and the grain check.
/// </summary>
[Collection("guest")]
public sealed class SasInternalTests(GuestMemoryFixture fixture)
{
    private const int NotInit = unchecked((int)0x80420100);
    private const int BadAddress = unchecked((int)0x80420005);
    private const int InvalidGrain = unchecked((int)0x80420001);
    private const int InvalidVolume = unchecked((int)0x80420018);
    private const string Config = "numGrains=256 numVoices=4 numReverbs=1";

    private static int Call(Sas module, string name, params object[] args) =>
        (int)typeof(Sas).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(module, args)!;

    private static Ptr<T> P<T>(uint address) where T : unmanaged => new(address);

    private uint Str(string text)
    {
        uint address = fixture.Memory.Alloc(0x100, "sascfg");
        byte[] bytes = Encoding.ASCII.GetBytes(text + "\0");
        bytes.CopyTo(GuestMemory.Span(address, bytes.Length));
        return address;
    }

    private uint Word()
    {
        uint address = fixture.Memory.Alloc(4, "sasword");
        GuestMemory.W32(address, 0xDEADBEEF);
        return address;
    }

    private uint NeededSize(Sas sas, uint config)
    {
        uint size = Word();
        Assert.Equal(0, Call(sas, "sceSasGetNeededMemorySizeInternal", config, P<uint>(size)));
        return GuestMemory.R32(size);
    }

    /// <summary>A context in a buffer that starts 4 bytes past a 16-byte boundary.</summary>
    private uint NewContext(Sas sas, out uint buffer, out uint size)
    {
        uint config = Str(Config);
        size = NeededSize(sas, config);
        buffer = fixture.Memory.Alloc(size + 0x20, "saswork") + 4;
        uint handle = Word();
        Assert.Equal(0, Call(sas, "sceSasInitInternal", config, buffer, size, P<uint>(handle)));
        return GuestMemory.R32(handle);
    }

    [Fact]
    public void AContextIsTheBufferAlignedTo16AndNeedsTheExactSize()
    {
        var sas = new Sas();
        uint config = Str(Config);
        uint size = NeededSize(sas, config);
        uint buffer = fixture.Memory.Alloc(size + 0x20, "saswork") + 4;
        uint handle = Word();
        Assert.Equal(BadAddress, Call(sas, "sceSasInitInternal", config, buffer, size + 0x10, P<uint>(handle)));
        Assert.Equal(BadAddress, Call(sas, "sceSasInitInternal", config, 0u, size, P<uint>(handle)));
        Assert.Equal(0xDEADBEEFu, GuestMemory.R32(handle));
        Assert.Equal(0, Call(sas, "sceSasInitInternal", config, buffer, size, P<uint>(handle)));
        Assert.Equal((buffer + 0xF) & ~0xFu, GuestMemory.R32(handle));
        Assert.Equal(256, Call(sas, "sceSasGetGrainInternal", GuestMemory.R32(handle)));
    }

    [Fact]
    public void TheNeededSizeWithoutAPointerOnlyChecksTheConfig()
    {
        var sas = new Sas();
        Assert.Equal(0, Call(sas, "sceSasGetNeededMemorySizeInternal", Str(Config), P<uint>(0)));
        Assert.Equal(BadAddress, Call(sas, "sceSasGetNeededMemorySizeInternal", 0u, P<uint>(0)));
        uint size = Word();
        Assert.Equal(unchecked((int)0x80420002), Call(sas, "sceSasGetNeededMemorySizeInternal", Str("numVoices=0"), P<uint>(size)));
        Assert.Equal(0u, GuestMemory.R32(size)); // cleared first
    }

    [Fact]
    public void ContextsAreIndependentOfEachOtherAndOfThePublicOne()
    {
        var sas = new Sas();
        uint first = NewContext(sas, out _, out _);
        uint second = NewContext(sas, out _, out _);
        Assert.NotEqual(first, second);
        Assert.Equal(NotInit, Call(sas, "sceSasGetGrain"));

        Assert.Equal(0, Call(sas, "sceSasSetGrainInternal", first, 512u));
        Assert.Equal(512, Call(sas, "sceSasGetGrainInternal", first));
        Assert.Equal(256, Call(sas, "sceSasGetGrainInternal", second));
        Assert.Equal(0, Call(sas, "sceSasSetOutputmodeInternal", second, 1u));
        Assert.Equal(1, Call(sas, "sceSasGetOutputmodeInternal", second));
        Assert.Equal(0, Call(sas, "sceSasGetOutputmodeInternal", first));
        Assert.Equal(0, Call(sas, "sceSasSetPauseInternal", first, 2, 1u));
        Assert.Equal(1, Call(sas, "sceSasGetPauseStateInternal", first, 2));
        Assert.Equal(0, Call(sas, "sceSasGetPauseStateInternal", second, 2));
        Assert.Equal(unchecked((int)0x80420010), Call(sas, "sceSasSetKeyOnInternal", first, 4));

        Assert.Equal(NotInit, Call(sas, "sceSasGetGrainInternal", 0u));
        Assert.Equal(NotInit, Call(sas, "sceSasSetKeyOnInternal", first + 0x10, 0));
    }

    [Fact]
    public void ThePublicCallsAreTheVariantsOnTheContextSceSasInitMade()
    {
        var sas = new Sas();
        uint buffer = fixture.Memory.Alloc(0x10000, "saswork") + 8;
        Assert.Equal(0, Call(sas, "sceSasInit", Str(Config), buffer, 0x10000u));
        uint handle = (buffer + 0xF) & ~0xFu;
        Assert.Equal(0, Call(sas, "sceSasSetGrainInternal", handle, 128u));
        Assert.Equal(128, Call(sas, "sceSasGetGrain"));
        Assert.Equal(0, Call(sas, "sceSasSetPause", 1, 1u));
        Assert.Equal(1, Call(sas, "sceSasGetPauseStateInternal", handle, 1));
    }

    [Fact]
    public void CoreLevelsAreUnsignedAndZeroWritesTheGrain()
    {
        var sas = new Sas();
        uint context = NewContext(sas, out _, out _);
        Assert.Equal(0, Call(sas, "sceSasSetGrainInternal", context, 64u));
        uint output = fixture.Memory.Alloc(64 * 4, "sasout");
        GuestMemory.Span<short>(output, 128).Fill(0x100);
        Assert.Equal(0, Call(sas, "sceSasCoreInternal", context, output, 0u, 0u));
        Assert.All(GuestMemory.Span<short>(output, 128).ToArray(), sample => Assert.Equal(0, sample)); // silence written

        GuestMemory.Span<short>(output, 128).Fill(0x100);
        Assert.Equal(0, Call(sas, "sceSasCoreInternal", context, output, 0x800u, 0x1000u));
        short[] mixed = GuestMemory.Span<short>(output, 128).ToArray();
        Assert.Equal(0x80, mixed[0]); // the existing left sample at half level
        Assert.Equal(0x100, mixed[1]);

        Assert.Equal(InvalidVolume, Call(sas, "sceSasCoreInternal", context, output, 0x1001u, 0u));
        Assert.Equal(InvalidVolume, Call(sas, "sceSasCoreInternal", context, output, 0u, 0xFFFFFFFFu));
        Assert.Equal(BadAddress, Call(sas, "sceSasCoreInternal", context, 0u, 0u, 0u));
        Assert.Equal(NotInit, Call(sas, "sceSasCoreInternal", 0u, output, 0u, 0u));
    }

    [Fact]
    public void ExitHandsTheBufferBackAndEndsTheContext()
    {
        var sas = new Sas();
        uint context = NewContext(sas, out uint buffer, out uint size);
        uint outBuffer = Word();
        uint outSize = Word();
        Assert.Equal(0, Call(sas, "sceSasExitInternal", context, P<uint>(outBuffer), P<uint>(outSize)));
        Assert.Equal(buffer, GuestMemory.R32(outBuffer));
        Assert.Equal(size, GuestMemory.R32(outSize));
        Assert.Equal(NotInit, Call(sas, "sceSasGetGrainInternal", context));

        GuestMemory.W32(outBuffer, 0xDEADBEEF);
        GuestMemory.W32(outSize, 0xDEADBEEF);
        Assert.Equal(NotInit, Call(sas, "sceSasExitInternal", context, P<uint>(outBuffer), P<uint>(outSize)));
        Assert.Equal(0u, GuestMemory.R32(outBuffer)); // both cleared first
        Assert.Equal(0u, GuestMemory.R32(outSize));
    }

    [Theory]
    [InlineData(64u, 0)]
    [InlineData(2048u, 0)]
    [InlineData(96u, 0)]
    [InlineData(32u, InvalidGrain)]
    [InlineData(100u, InvalidGrain)]
    [InlineData(2080u, InvalidGrain)]
    public void TheGrainCheckAllows64To2048InStepsOf32(uint grain, int expected)
    {
        var sas = new Sas();
        Assert.Equal(expected, Call(sas, "sceSasCheckGrainInternal", grain));
        uint context = NewContext(sas, out _, out _);
        Assert.Equal(expected, Call(sas, "sceSasSetGrainInternal", context, grain));
    }

    [Fact]
    public void VoiceAndEffectCallsReachTheirContext()
    {
        var sas = new Sas();
        uint context = NewContext(sas, out _, out _);
        uint other = NewContext(sas, out _, out _);
        uint pcm = fixture.Memory.Alloc(0x400, "saspcm");
        Assert.Equal(0, Call(sas, "sceSasSetVoicePCMInternal", context, 0, pcm, 0x100, 0));
        Assert.Equal(0, Call(sas, "sceSasSetVolumeInternal", context, 0, 0x1000, 0x1000, 0, 0));
        Assert.Equal(0, Call(sas, "sceSasSetPitchInternal", context, 0, 0x1000));
        Assert.Equal(0, Call(sas, "sceSasSetSimpleADSRInternal", context, 0, 0x000Fu, 0x1FC0u));
        Assert.Equal(0, Call(sas, "sceSasSetKeyOnInternal", context, 0));
        Assert.Equal(0, Call(sas, "sceSasGetEndStateInternal", context, 0));
        Assert.Equal(1, Call(sas, "sceSasGetEndStateInternal", other, 0));
        Assert.Equal(0, Call(sas, "sceSasSetKeyOffInternal", context, 0));
        Assert.Equal(unchecked((int)0x80420012), Call(sas, "sceSasSetPitchInternal", context, 0, 0));
        Assert.Equal(unchecked((int)0x80420011), Call(sas, "sceSasSetNoiseInternal", context, 0, 64u));
        Assert.Equal(unchecked((int)0x80420014), Call(sas, "sceSasSetSLInternal", context, 0, 0x80000000u));
        Assert.Equal(0, Call(sas, "sceSasSetEffectTypeInternal", context, 6));
        Assert.Equal(unchecked((int)0x80420022), Call(sas, "sceSasSetEffectParamInternal", context, 128u, 0u));
        Assert.Equal(unchecked((int)0x80420023), Call(sas, "sceSasSetEffectVolumeInternal", context, 0x1001, 0));
        Assert.Equal(0, Call(sas, "sceSasSetEffectInternal", context, 1, 1));
        uint left = Word();
        Assert.Equal(0, Call(sas, "sceSasGetDryPeakInternal", context, P<int>(left), P<int>(0)));
        Assert.Equal(BadAddress, Call(sas, "sceSasGetWetPeakInternal", context, P<int>(0), P<int>(0)));
        Assert.Equal(NotInit, Call(sas, "sceSasGetPreMasterPeakInternal", 0u, P<int>(left), P<int>(0)));
    }
}
