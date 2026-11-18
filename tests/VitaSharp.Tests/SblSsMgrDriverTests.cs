using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSblSsMgrForDriver (ss_mgr.skprx 3.74), first part: the identity words (the same ones the bridge reports), the
/// random generators, SsMemset, and the DMAC5 AES operations with a key in memory, checked against the NIST SP 800-38A
/// vectors; the key slot forms need the secure processor's keys (0x80020004).
/// </summary>
[Collection("guest")]
public sealed class SblSsMgrDriverTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Dmac5Invalid = unchecked((int)0x800F1516), AimgrInvalid = unchecked((int)0x800F0C16);
    private const int Unavailable = unchecked((int)0x80020004);

    private static readonly byte[] s_key128 = Convert.FromHexString("2B7E151628AED2A6ABF7158809CF4F3C");
    private static readonly byte[] s_plain = Convert.FromHexString("6BC1BEE22E409F96E93D7E117393172A");

    private readonly HleHarness _harness = new(fixture, "ssmgrd");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    private uint Bytes(byte[] value)
    {
        uint address = _harness.Alloc((uint)value.Length);
        value.CopyTo(GuestMemory.Span(address, value.Length));
        return address;
    }

    [Theory]
    [InlineData(0xFC6CDD68u, false)]
    [InlineData(0xA5B5D269u, false)]
    [InlineData(0xE0DC2587u, false)]
    [InlineData(0x9A9676D0u, false)]
    [InlineData(0x04843835u, true)]
    [InlineData(0x4DD1B2E5u, false)]
    [InlineData(0xAC57F4F0u, false)]
    [InlineData(0x4F9BFBE5u, false)]
    [InlineData(0xCD98CC92u, false)]
    [InlineData(0xC517770Du, false)]
    [InlineData(0x7C978BE7u, false)]
    [InlineData(0x0F7D28AFu, false)]
    [InlineData(0x197ACF6Fu, false)]
    [InlineData(0xE6E1AD15u, false)]
    [InlineData(0x121FA69Fu, false)]
    [InlineData(0x711C057Au, false)]
    [InlineData(0x1901CB5Eu, false)]
    [InlineData(0x82B5DCEFu, false)]
    [InlineData(0x7D46768Cu, false)]
    [InlineData(0xEB3AF9B5u, false)]
    [InlineData(0x6704D985u, false)]
    [InlineData(0x92E37656u, false)]
    [InlineData(0x79F38554u, false)]
    [InlineData(0x01BE0374u, true)]
    [InlineData(0x8B4700CBu, true)]
    [InlineData(0x37DD5CBFu, true)]
    [InlineData(0x8EAFB18Au, true)]
    [InlineData(0x05B38698u, true)]
    [InlineData(0x926BCCF0u, true)]
    [InlineData(0xEA6ACB6Du, true)]
    public void EveryEntryIsBound(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceSblSsMgrForDriver").ToList();
        Assert.Single(bindings);
        Assert.Equal(isDefault, bindings[0].Default);
    }

    [Fact]
    public void TheIdentityIsTheOneTheBridgeReports()
    {
        uint kernel = _harness.Alloc(16), bridge = _harness.Alloc(16), pscode = _harness.Alloc(8);
        Assert.Equal(AimgrInvalid, Call(Services, "ksceSblAimgrGetConsoleId", 0u));
        Assert.Equal(0, Call(Services, "ksceSblAimgrGetConsoleId", kernel));
        _harness.Emu.IsSystemProgram = true;
        Assert.Equal(0, Call(_harness.Module<VshBridge>(), "_vshSblAimgrGetConsoleId", bridge));
        Assert.True(GuestMemory.ReadSpan(kernel, 16).SequenceEqual(GuestMemory.ReadSpan(bridge, 16)));

        Assert.Equal(0, Call(Services, "ksceSblAimgrGetOpenPsId", kernel));
        Assert.True(GuestMemory.ReadSpan(kernel, 16).SequenceEqual(ConsoleIdentity.OpenPsId));

        Assert.Equal(0, Call(Services, "ksceSblAimgrGetPscode", pscode));
        Assert.Equal(ConsoleIdentity.CompanyCode, GuestMemory.Read<ushort>(pscode));
        Assert.Equal(ConsoleIdentity.ProductCode, GuestMemory.Read<ushort>(pscode + 2));
        Assert.Equal(ConsoleIdentity.ProductSubCode, GuestMemory.Read<ushort>(pscode + 4));
        Assert.Equal(unchecked((int)0x800F0C29), Call(Services, "ksceSblAimgrGetPscode2", pscode)); // retail: no product mode
        Assert.Equal(AimgrInvalid, Call(Services, "ksceSblAimgrGetPscode2", 0u));
        Assert.Equal(0, Call(Services, "ksceSblAimgrGetVisibleId", _harness.Alloc(0x20)));
    }

    /// <summary>The bridge passes the pointer to ksceSblAimgrGetPscode2 and answers what it answers: a retail unit has
    /// no product mode, so both refuse alike (it used to copy the pscode out).</summary>
    [Fact]
    public void TheBridgesPscode2AnswersAsTheKernelEntry()
    {
        uint pscode = _harness.Alloc(8);
        VshBridge bridge = _harness.Module<VshBridge>();
        _harness.Emu.IsSystemProgram = true;
        Assert.Equal(Call(Services, "ksceSblAimgrGetPscode2", pscode), Call(bridge, "_vshSblAimgrGetPscode2", pscode));
        Assert.Equal(unchecked((int)0x800F0C29), Call(bridge, "_vshSblAimgrGetPscode2", pscode));
        Assert.Equal(Call(Services, "ksceSblAimgrGetPscode2", 0u), Call(bridge, "_vshSblAimgrGetPscode2", 0u));
        _harness.Emu.IsSystemProgram = false;
        Assert.Equal(unchecked((int)0x80010058), Call(bridge, "_vshSblAimgrGetPscode2", pscode));
    }

    [Fact]
    public void RandomNumbersAndMemset()
    {
        uint buffer = _harness.Alloc(0x80);
        Assert.Equal(Dmac5Invalid, Call(Services, "ksceSblDmac5Rnd", buffer, 0x41u, 1));
        Assert.Equal(0, Call(Services, "ksceSblDmac5Rnd", buffer, 0x40u, 1));
        Assert.Equal(0, Call(Services, "ksceSblRngGenuineRandomNumber", buffer));
        Assert.Equal(0, Call(Services, "ksceSblRngPseudoRandomNumber", buffer, 0x80u));
        Assert.False(GuestMemory.ReadSpan(buffer, 0x40).SequenceEqual(new byte[0x40]));
        Assert.Equal(buffer, (uint)Invoke(Services, "ksceSblSsMemset", buffer, 0x5A, 0x80u)!);
        Assert.All(GuestMemory.ReadSpan(buffer, 0x80).ToArray(), b => Assert.Equal(0x5A, b));
    }

    [Fact]
    public void AesEcbCbcAndCtrMatchTheNistVectors()
    {
        uint key = Bytes(s_key128), src = Bytes(s_plain), dst = _harness.Alloc(16), back = _harness.Alloc(16);
        Assert.Equal(0, Call(Services, "ksceSblDmac5AesEcbEnc", src, dst, 16u, key, 0x80u, 1));
        Assert.Equal("3AD77BB40D7A3660A89ECAF32466EF97", Convert.ToHexString(GuestMemory.ReadSpan(dst, 16)));
        Assert.Equal(0, Call(Services, "ksceSblDmac5AesEcbDecNP", dst, back, 16u, key, 0x80u, 0u, 1));
        Assert.True(GuestMemory.ReadSpan(back, 16).SequenceEqual(s_plain));

        byte[] cbcIv = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
        uint iv = Bytes(cbcIv);
        Assert.Equal(0, Call(Services, "ksceSblDmac5AesCbcEnc", src, dst, 16u, key, 0x80u, iv, 1));
        Assert.Equal("7649ABAC8119B246CEE98E9B12E9197D", Convert.ToHexString(GuestMemory.ReadSpan(dst, 16)));
        Assert.Equal("7649ABAC8119B246CEE98E9B12E9197D", Convert.ToHexString(GuestMemory.ReadSpan(iv, 16))); // chained
        Assert.Equal(0, Call(Services, "ksceSblDmac5AesCbcDecNP", dst, back, 16u, key, 0x80u, Bytes(cbcIv), 0u, 1));
        Assert.True(GuestMemory.ReadSpan(back, 16).SequenceEqual(s_plain));

        byte[] ctrStart = Convert.FromHexString("F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF");
        uint counter = Bytes(ctrStart);
        Assert.Equal(0, Call(Services, "ksceSblDmac5AesCtrEnc", src, dst, 16u, key, 0x80u, counter, 1));
        Assert.Equal("874D6191B620E3261BEF6864990DB6CE", Convert.ToHexString(GuestMemory.ReadSpan(dst, 16)));
        Assert.Equal("F0F1F2F3F4F5F6F7F8F9FAFBFCFDFF00", Convert.ToHexString(GuestMemory.ReadSpan(counter, 16))); // next block
        Assert.Equal(0, Call(Services, "ksceSblDmac5AesCtrDec", dst, back, 16u, key, 0x80u, Bytes(ctrStart), 1));
        Assert.True(GuestMemory.ReadSpan(back, 16).SequenceEqual(s_plain));
    }

    /// <summary>SHA-1 (FIPS 180 "abc"), HMAC-SHA1 (RFC 2202 case 1) and HMAC-SHA256 (RFC 4231 case 1): the 20-byte
    /// keys of the RFC cases are the engine's 0x20-byte key padded with zeros, which HMAC treats alike.</summary>
    [Fact]
    public void HashesOfOneCallMatchTheKnownVectors()
    {
        uint abc = Bytes("abc"u8.ToArray()), hiThere = Bytes("Hi There"u8.ToArray()), digest = _harness.Alloc(0x20);
        byte[] rfcKey = new byte[0x20];
        Array.Fill(rfcKey, (byte)0x0B, 0, 20);
        uint key = Bytes(rfcKey);
        Assert.Equal(0, Call(Services, "ksceSblDmac5Sha1", abc, digest, 3u, 0u, 1, 0u));
        Assert.Equal("A9993E364706816ABA3E25717850C26C9CD0D89D", Convert.ToHexString(GuestMemory.ReadSpan(digest, 20)));
        Assert.Equal(0, Call(Services, "ksceSblDmac5Sha1HmacTransform", hiThere, digest, 8u, key, 0u, 1, 0u));
        Assert.Equal("B617318655057264E28BC0B6FB378C8EF146BE00", Convert.ToHexString(GuestMemory.ReadSpan(digest, 20)));
        Assert.Equal(0, Call(Services, "ksceSblDmac5Sha1HmacNP", hiThere, digest, 8u, key, 0u, 0u, 1, 0u));
        Assert.Equal("B617318655057264E28BC0B6FB378C8EF146BE00", Convert.ToHexString(GuestMemory.ReadSpan(digest, 20)));
        Assert.Equal(0, Call(Services, "ksceSblDmac5Sha256Hmac", hiThere, digest, 8u, key, 0u, 1, 0u));
        Assert.Equal("B0344C61D8DB38535CA8AFCEAF0BF12B881DC200C9833DA726E9376C2E32CFF7",
            Convert.ToHexString(GuestMemory.ReadSpan(digest, 0x20)));
    }

    [Fact]
    public void ChainingContextsAndDerivedKeysNeedTheSecureSide()
    {
        uint data = Bytes(s_plain), digest = _harness.Alloc(0x20), key = Bytes(new byte[0x20]);
        Assert.Equal(Unavailable, Call(Services, "ksceSblDmac5Sha1", data, digest, 16u, _harness.Alloc(0x28), 1, 0x400u));
        Assert.Equal(Unavailable, Call(Services, "ksceSblDmac5Sha256Hmac", data, digest, 16u, key, _harness.Alloc(0x28), 1, 0x800u));
        Assert.Equal(Dmac5Invalid, Call(Services, "ksceSblDmac5Sha256Hmac", data, digest, 16u, 0u, 0u, 1, 0u));
        Assert.Equal(Unavailable, Call(Services, "ksceSblDmac5Sha1HmacNP", data, digest, 16u, key, 0u, 0x10000u, 1, 0u));
        Assert.Equal(Unavailable, Call(Services, "ksceSblDmac5AesEcbEncNP", data, digest, 16u, key, 0x80u, 0x20001u, 1));
    }

    [Fact]
    public void TheEngineChecksTheKey()
    {
        uint key = Bytes(s_key128), src = Bytes(s_plain), dst = _harness.Alloc(16);
        Assert.Equal(Dmac5Invalid, Call(Services, "ksceSblDmac5AesEcbEnc", src, dst, 16u, 0u, 0x80u, 1));
        Assert.Equal(Dmac5Invalid, Call(Services, "ksceSblDmac5AesEcbEnc", src, dst, 16u, key, 0x90u, 1));
        Assert.Equal(Unavailable, Call(Services, "Dmac5WithKeyslot"));
    }
}
