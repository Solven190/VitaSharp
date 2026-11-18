using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The SceSblSsMgrForDriver entries left by the first parts (ss_mgr 3.74): AES-CMAC with a key in memory (the RFC 4493
/// vectors), the hash command (SHA-1 3, SHA-256 0x13; the keyed ones need a key the engine holds), the key slot cipher
/// of ScePfsMgr, and the physical vector helpers over ksceKernelAllocHeapMemoryWithOption.
/// </summary>
[Collection("guest")]
public sealed class SsMgrDmac5CmacKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Unsupported = unchecked((int)0x80020004), Invalid = unchecked((int)0x800F1516);

    private static readonly byte[] s_cmacKey = Convert.FromHexString("2B7E151628AED2A6ABF7158809CF4F3C");

    private static readonly byte[] s_cmacMessage = Convert.FromHexString(
        "6BC1BEE22E409F96E93D7E117393172AAE2D8A571E03AC9C9EB76FAC45AF8E5130C81C46A35CE411");

    private readonly HleHarness _harness = new(fixture, "cmac");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    [Theory]
    [InlineData(0x83B058F5u)]
    [InlineData(0x9641374Eu)]
    [InlineData(0xE0B13BA7u)]
    [InlineData(0xC38D0CEAu)]
    public void EveryExportHasAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceSblSsMgrForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void TheKeySlotCipherIsADefault()
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == 0x1B14658Du && b.Library == "SceSblSsMgrForDriver").ToList();
        Assert.Single(bindings);
        Assert.True(bindings[0].Default);
        Assert.Equal(Invalid, Call(Services, "Dmac5CipherWithKeyslot", 0u, 0u, 0u, 0u, 0x20u));
        Assert.Equal(Unsupported, Call(Services, "Dmac5CipherWithKeyslot", 0u, 0u, 0u, 0u, 0x80u));
    }

    [Theory]
    [InlineData(0, "BB1D6929E95937287FA37D129B756746")]
    [InlineData(16, "070A16B46B4D4144F79BDD9DD04A287C")]
    [InlineData(40, "DFA66747DE9AE63030CA32611497C827")]
    public void CmacMatchesTheRfcVectors(int length, string expected)
    {
        uint key = _harness.Alloc(16);
        s_cmacKey.CopyTo(GuestMemory.Span(key, 16));
        uint src = _harness.Alloc(64);
        s_cmacMessage.AsSpan(0, length).CopyTo(GuestMemory.Span(src, length));
        uint dst = _harness.Alloc(16);
        Assert.Equal(0, Call(Services, "ksceSblDmac5AesCmacNP", src, dst, (uint)length, key, 0x80u, 0u, 0u, 0, 0u));
        Assert.Equal(expected, Convert.ToHexString(GuestMemory.ReadSpan(dst, 16)));
    }

    [Fact]
    public void CmacRefusesWhatTheEngineCannotDoHere()
    {
        uint key = _harness.Alloc(32);
        uint src = _harness.Alloc(16);
        Assert.Equal(Invalid, Call(Services, "ksceSblDmac5AesCmacNP", src, 0u, 16u, key, 0x40u, 0u, 0u, 0, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceSblDmac5AesCmacNP", src, 0u, 16u, 0u, 0x80u, 0u, 0u, 0, 0u));
        Assert.Equal(Unsupported, Call(Services, "ksceSblDmac5AesCmacNP", src, 0u, 16u, key, 0x80u, 0u, 0x10000u, 0, 0u));
        Assert.Equal(Unsupported, Call(Services, "ksceSblDmac5AesCmacNP", src, 0u, 16u, key, 0x80u, 0u, 0u, 0, 0x400u));
        Assert.Equal(0, Call(Services, "ksceSblDmac5AesCmacNP", src, 0u, 16u, key, 0x100u, 0u, 0u, 0, 0u));
    }

    [Fact]
    public void TheHashCommandRunsTheUnkeyedHashes()
    {
        uint src = _harness.Str("abc");
        uint dst = _harness.Alloc(32);
        Assert.Equal(0, Call(Services, "ksceSblSsMgrExecuteDmac5HashCommand", src, dst, 3u, 0u, 0, 3u, 0u));
        Assert.Equal("A9993E364706816ABA3E25717850C26C9CD0D89D", Convert.ToHexString(GuestMemory.ReadSpan(dst, 20)));
        Assert.Equal(0, Call(Services, "ksceSblSsMgrExecuteDmac5HashCommand", src, dst, 3u, 0u, 0, 0x13u, 0u));
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD",
            Convert.ToHexString(GuestMemory.ReadSpan(dst, 32)));
        foreach (uint command in new uint[] { 0x0B, 0x23, 0x33, 0x3B })
        {
            Assert.Equal(Unsupported, Call(Services, "ksceSblSsMgrExecuteDmac5HashCommand", src, dst, 3u, 0u, 0, command, 0u));
        }

        Assert.Equal(Unsupported, Call(Services, "ksceSblSsMgrExecuteDmac5HashCommand", src, dst, 3u, 0u, 0, 3u, 0x800u));
    }

    [Fact]
    public void ThePhysicalVectorIsBuiltFromTheHeapAndFreed()
    {
        int heap = Call(Sysmem, "ksceKernelCreateHeap", _harness.Str("cmac"), 0x10000u, 0u);
        Assert.True(heap > 0);
        uint option = _harness.Alloc(0x14);
        GuestMemory.W32(option, 0x10);
        Assert.Equal(0u, (uint)Invoke(Sysmem, "ksceKernelAllocHeapMemoryWithOption", heap, 0x40u, option)!);
        GuestMemory.W32(option, 0x14);
        GuestMemory.W32(option + 8, 0x20);
        Assert.NotEqual(0u, (uint)Invoke(Sysmem, "ksceKernelAllocHeapMemoryWithOption", heap, 0x40u, option)!);

        uint buffer = (uint)Invoke(Sysmem, "ksceKernelAllocHeapMemory", heap, 0x100u)!;
        uint vector = _harness.Alloc(0x14);
        Assert.Equal(0, Call(Sysmem, "SsMgrBuildPaVector", buffer, 0x100u, heap, vector));
        Assert.Equal(0x14u, GuestMemory.R32(vector));
        Assert.Equal(1u, GuestMemory.R32(vector + 8));
        Assert.Equal(1u, GuestMemory.R32(vector + 0xC));
        uint pairs = GuestMemory.R32(vector + 0x10);
        Assert.NotEqual(0u, pairs);
        Assert.Equal(0x100u, GuestMemory.R32(pairs + 4));
        Assert.Equal(0, Call(Sysmem, "SsMgrFreePaVector", heap, vector));
        Assert.Equal(unchecked((int)0x800F0016), Call(Sysmem, "SsMgrFreePaVector", heap, vector)); // freed already
    }
}
