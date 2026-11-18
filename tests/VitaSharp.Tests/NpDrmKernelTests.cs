using System.Buffers.Binary;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceNpDrmForDriver's license queries (GetRifInfo, GetRifVitaKey, GetRifPspKey) as SceNpDrm 3.74 answers them on a
/// retail unit: the fields of the license, the kind check, the unit and game card bindings and the activation check
/// (an account license without tm0:npdrm/act.dat is 0x80870003, the answer the plugins of the scene hook), the
/// signature check of the secure module failing, and the key the emulator's loader uses for a license that passes.
/// </summary>
[Collection("guest")]
public sealed class NpDrmKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80870001), NotActivated = unchecked((int)0x80870003);
    private const int BadLicense = unchecked((int)0x80870004), BadData = unchecked((int)0x80870005);
    private const int NoActivationData = unchecked((int)0x80870006), OtherAccount = unchecked((int)0x80870008);
    private const int OtherUnit = unchecked((int)0x80870009), GameCard = unchecked((int)0x8087000A);
    private const ulong Epoch1970 = 0xDCBFFEFF2BC000, Year2001 = 0xE039BB591E1C00, Never = 0x7FFFFFFFFFFFFFFF;

    private readonly HleHarness _harness = new(fixture, "npdrmk");

    public void Dispose()
    {
        string? act = _harness.Emu.Vfs.Resolve("tm0:npdrm/act.dat");
        if (act != null && File.Exists(act))
        {
            File.Delete(act);
        }

        _harness.Dispose();
    }

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    /// <summary>A PS Vita license (0x200 bytes) of the given kind and flags, with a recognisable key and content id.</summary>
    private uint License(ushort flags, ushort type = 1, ulong accountId = 0, uint skuFlag = 0, ulong expirationMs = 0)
    {
        byte[] rif = new byte[0x200];
        BinaryPrimitives.WriteUInt16BigEndian(rif, 1);
        BinaryPrimitives.WriteUInt16BigEndian(rif.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(rif.AsSpan(4), type);
        BinaryPrimitives.WriteUInt16BigEndian(rif.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt64LittleEndian(rif.AsSpan(8), accountId);
        "UP0000-PCSE00000_00-0000000000000000"u8.CopyTo(rif.AsSpan(0x10));
        for (int i = 0; i < 0x10; i++)
        {
            rif[0x50 + i] = (byte)(0xA0 + i);
        }

        BinaryPrimitives.WriteUInt64BigEndian(rif.AsSpan(0x68), expirationMs);
        BinaryPrimitives.WriteUInt32BigEndian(rif.AsSpan(0xFC), skuFlag);
        uint address = _harness.Alloc(0x200);
        rif.CopyTo(GuestMemory.Span(address, 0x200));
        return address;
    }

    private void Activate(ulong accountId, ushort version = 1)
    {
        string act = _harness.Emu.Vfs.Resolve("tm0:npdrm/act.dat", forWrite: true)!;
        Directory.CreateDirectory(Path.GetDirectoryName(act)!);
        byte[] data = new byte[0x1038];
        BinaryPrimitives.WriteUInt16BigEndian(data, version);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), accountId);
        File.WriteAllBytes(act, data);
    }

    private int RifInfo(uint license, uint size, int checkSign, uint contentId = 0, uint kind = 0, uint flags = 0,
        uint sku = 0, uint start = 0, uint expiration = 0) =>
        Call(Services, "ksceNpDrmGetRifInfo", license, size, checkSign, contentId, 0u, 0u, kind, flags, sku, start,
            expiration, 0u);

    [Theory]
    [InlineData(0xDB406EAEu)]
    [InlineData(0x723322B5u)]
    [InlineData(0xDACB71F4u)]
    public void TheLicenseQueriesHaveARealEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceNpDrmForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void AnAccountLicenseWithoutActivationIsNotActivatedAndKeepsTheKey()
    {
        uint license = License(0), key = _harness.Alloc(0x10), kind = _harness.Word(0xFF), contentId = _harness.Alloc(0x30);
        Assert.Equal(NotActivated, RifInfo(license, 0x200, 0, contentId, kind));
        Assert.Equal(0u, GuestMemory.R32(kind));
        Assert.Equal("UP0000-PCSE00000_00-0000000000000000", GuestMemory.ReadCString(contentId, 0x30));
        Assert.Equal(NotActivated, Call(Services, "ksceNpDrmGetRifVitaKey", license, key, 0u, 0u, 0u, 0u));
        Assert.True(GuestMemory.ReadSpan(key, 0x10).SequenceEqual(new byte[0x10])); // no key without the check
        Assert.Equal(NotActivated, Call(Services, "ksceNpDrmGetRifPspKey", license, key, 0u, 0u, 0u));
    }

    [Fact]
    public void AnActivatedUnitGetsTheLicensesKey()
    {
        Activate(0x1122334455667788);
        uint license = License(0), key = _harness.Alloc(0x10);
        Assert.Equal(0, Call(Services, "ksceNpDrmGetRifVitaKey", license, key, 0u, 0u, 0u, 0u));
        Assert.Equal(0xA0, GuestMemory.ReadSpan(key, 0x10)[0]);
        Assert.Equal(0xAF, GuestMemory.ReadSpan(key, 0x10)[15]);
        Assert.Equal(0, RifInfo(license, 0x200, 0));
    }

    [Fact]
    public void AFreeLicenseNeedsNoActivation()
    {
        uint license = License(RifKindFree), key = _harness.Alloc(0x10);
        Assert.Equal(0, Call(Services, "ksceNpDrmGetRifVitaKey", license, key, 0u, 0u, 0u, 0u));
        Assert.Equal(0xA0, GuestMemory.ReadSpan(key, 1)[0]);
    }

    private const ushort RifKindFree = 0xD;

    [Fact]
    public void AnActivatedAccountsLicenseMustMatchTheActivation()
    {
        uint license = License(2, accountId: 0x42);
        Assert.Equal(OtherAccount, RifInfo(license, 0x200, 0)); // no activation: account 0
        uint unitAccount = License(2, accountId: 0);
        Assert.Equal(NoActivationData, RifInfo(unitAccount, 0x200, 0));
        Activate(0x42);
        Assert.Equal(0, RifInfo(license, 0x200, 0));
        Assert.Equal(OtherAccount, RifInfo(unitAccount, 0x200, 0));
    }

    [Theory]
    [InlineData((ushort)0x0001)]
    [InlineData((ushort)0x0004)]
    [InlineData((ushort)0x00FF)]
    public void AnUnknownLicenseKindDoesNotVerify(ushort flags)
    {
        Assert.Equal(BadData, RifInfo(License(flags), 0x200, 0));
    }

    [Fact]
    public void BindingsToAUnitAndAGameCard()
    {
        Assert.Equal(OtherUnit, RifInfo(License(0x100 | RifKindFree), 0x200, 0)); // OpenPSID field all zero
        uint bound = License(0x100 | RifKindFree);
        ConsoleIdentity.OpenPsId.CopyTo(GuestMemory.Span(bound + 0xC0, 0x10));
        Assert.Equal(0, RifInfo(bound, 0x200, 0));
        Assert.Equal(BadData, RifInfo(License(0x100 | RifKindFree, type: 0), 0x200, 0)); // a PSP license cannot be
        Assert.Equal(GameCard, RifInfo(License(0x400 | RifKindFree), 0x200, 0));
    }

    [Fact]
    public void PointersSizesAndTheSignatureCheck()
    {
        uint kind = _harness.Word(0xFF);
        Assert.Equal(Invalid, RifInfo(0, 0x200, 0));
        Assert.Equal(BadLicense, RifInfo(License(RifKindFree), 0x97, 0, kind: kind));
        Assert.Equal(0u, GuestMemory.R32(kind));
        Assert.Equal(BadLicense, RifInfo(License(2), 0x200, 1)); // the secure module's check does not pass
        Assert.Equal(BadData, RifInfo(License(0, type: 0), 0x200, 1)); // a PSP license on the RSA path
        Assert.Equal(Invalid, Call(Services, "ksceNpDrmGetRifVitaKey", 0u, _harness.Alloc(0x10), 0u, 0u, 0u, 0u));
        Assert.Equal(Invalid, Call(Services, "ksceNpDrmGetRifVitaKey", License(RifKindFree), 0u, 0u, 0u, 0u, 0u));
    }

    [Fact]
    public void TimesFlagsAndSku()
    {
        uint start = _harness.Alloc(8), expiration = _harness.Alloc(8), flags = _harness.Word(0), sku = _harness.Word(0);
        Assert.Equal(0, RifInfo(License(RifKindFree, skuFlag: 1), 0x200, 0, flags: flags, sku: sku, start: start,
            expiration: expiration));
        Assert.Equal(Year2001, GuestMemory.Read<ulong>(start));
        Assert.Equal(Never, GuestMemory.Read<ulong>(expiration));
        Assert.Equal(1u, GuestMemory.R32(sku));
        Assert.Equal(1u, GuestMemory.R32(flags)); // SKU flag 1: a full license
        Assert.Equal(0, RifInfo(License(RifKindFree, expirationMs: 1000), 0x200, 0, expiration: expiration));
        Assert.Equal(1000UL * 1000 + Epoch1970, GuestMemory.Read<ulong>(expiration));
    }
}
