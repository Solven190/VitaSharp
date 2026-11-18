using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The syscon's NVS (SceSysconForDriver: the transfer sizes, alignment and the 0xB60-byte area) and
/// SceSblSsMgrForKernel over it (8-byte chunked reads and writes, the retail unit's empty QA flags), FW 3.74.
/// </summary>
[Collection("guest")]
public sealed class NvsQafKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NvsRange = unchecked((int)0x80250001), SsInvalid = unchecked((int)0x800F0716);
    private const int NoQaf = unchecked((int)0x800F0726);

    private readonly HleHarness _harness = new(fixture, "nvsqaf");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0xACAFA2B8u, "SceSysconForDriver")]
    [InlineData(0x10C9657Au, "SceSysconForDriver")]
    [InlineData(0x81A6060Du, "SceSysconForDriver")]
    [InlineData(0xC2EC8F5Au, "SceSblSsMgrForKernel")]
    [InlineData(0xE29E161Cu, "SceSblSsMgrForKernel")]
    [InlineData(0x83D254FFu, "SceSblSsMgrForKernel")]
    [InlineData(0xE2DD0378u, "SceSblSsMgrForKernel")]
    [InlineData(0x516ECC08u, "SceSblSsMgrForKernel")]
    public void EveryExportHasARealEntry(uint nid, string library)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == library).ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Theory]
    [InlineData(0u, 3u)] // not a transfer size
    [InlineData(2u, 4u)] // misaligned
    [InlineData(0xB58u, 0x10u)] // past the area
    public void SysconTransfersOutsideTheRulesAreRefused(uint offset, uint size)
    {
        uint buffer = _harness.Alloc(0x10);
        Assert.Equal(NvsRange, Call(Services, "ksceSysconNvsReadData", offset, buffer, size));
        Assert.Equal(NvsRange, Call(Services, "ksceSysconNvsWriteData", offset, buffer, size));
    }

    [Fact]
    public void WhatIsWrittenIsReadBackInChunks()
    {
        uint source = _harness.Alloc(0x14), target = _harness.Alloc(0x14);
        for (uint i = 0; i < 0x14; i++)
        {
            GuestMemory.Write(source + i, (byte)(i + 1));
        }

        Assert.Equal(0, Call(Services, "ksceSysconNvsSetRunMode", 0));
        Assert.Equal(0, Call(Services, "ksceSblNvsWriteData", 0x500u, source, 0x10u));
        Assert.Equal(0, Call(Services, "ksceSblNvsReadData", 0x500u, target, 0x10u));
        Assert.True(GuestMemory.ReadSpan(target, 0x10).SequenceEqual(GuestMemory.ReadSpan(source, 0x10)));
        Assert.Equal(0, Call(Services, "ksceSysconNvsReadData", 0x508u, target, 8u));
        Assert.Equal(9, GuestMemory.ReadSpan(target, 1)[0]);

        // a 0x14-byte read is 8 + 8 + 4: the last transfer is a valid size
        Assert.Equal(0, Call(Services, "ksceSblNvsReadData", 0x500u, target, 0x14u));
        // a 0x13-byte read ends with a 3-byte transfer, which the syscon refuses after the first two
        Assert.Equal(NvsRange, Call(Services, "ksceSblNvsReadData", 0x500u, target, 0x13u));
    }

    [Fact]
    public void ARetailUnitHasNoQaFlags()
    {
        uint flags = _harness.Alloc(16), name = _harness.Alloc(0x18);
        GuestMemory.Fill(flags, 0xFF, 16);
        Assert.Equal(SsInvalid, Call(Services, "ksceSblQafManagerGetQAFlags", 0u));
        Assert.Equal(0, Call(Services, "ksceSblQafManagerGetQAFlags", flags));
        Assert.True(GuestMemory.ReadSpan(flags, 16).SequenceEqual(new byte[16]));
        Assert.Equal(SsInvalid, Call(Services, "ksceSblQafManagerGetQafName", 0u, 0x18u));
        Assert.Equal(NoQaf, Call(Services, "ksceSblQafManagerGetQafName", name, 0x18u));
    }

    /// <summary>NID_516ECC08 is ss_mgr's re-export of kscePmMgrGetProductMode (its export points at the import's
    /// stub): the retail unit's product mode is off.</summary>
    [Fact]
    public void Nid516ECC08IsTheProductModeQuery()
    {
        uint mode = _harness.Word(0xFF);
        Assert.Equal(unchecked((int)0x800F0A16), Call(Services, "SsMgrGetProductMode", 0u));
        Assert.Equal(0, Call(Services, "SsMgrGetProductMode", mode));
        Assert.Equal(0u, GuestMemory.R32(mode) & 0xFF);
    }
}
