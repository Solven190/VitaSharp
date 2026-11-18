using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceIdStorageForDriver (SceIdStorage 3.74) on a retail unit: the storage is formatted, writable and clean, its
/// leaves read as zeros, and every change is refused outside product mode (0x80230000), through the kernel entries
/// and through the bridge alike.
/// </summary>
[Collection("guest")]
public sealed class IdStorageKernelTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int Invalid = unchecked((int)0x80230000), LeafId = unchecked((int)0x80230001);

    private readonly HleHarness _harness = new(fixture, "idstor");

    public void Dispose() => _harness.Dispose();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x99ACCB71u)]
    [InlineData(0x08A471A6u)]
    [InlineData(0x2C97AB36u)]
    [InlineData(0x31E08AFBu)]
    [InlineData(0x3AD32523u)]
    [InlineData(0x958089DBu)]
    [InlineData(0x37833CB8u)]
    [InlineData(0xEB830733u)]
    [InlineData(0xB9069BADu)]
    [InlineData(0xFEFA40C2u)]
    [InlineData(0x2D633688u)]
    [InlineData(0x6FE062D1u)]
    [InlineData(0xEB00C509u)]
    [InlineData(0x60B647A5u)]
    [InlineData(0xF4BCB3EEu)]
    [InlineData(0x683AAC10u)]
    [InlineData(0x1FA4D135u)]
    public void EveryExportHasARealEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings())
            .Where(b => b.Nid == nid && b.Library == "SceIdStorageForDriver").ToList();
        Assert.Single(bindings);
        Assert.False(bindings[0].Default);
    }

    [Fact]
    public void TheStorageIsReadableAndItsLeavesAreZero()
    {
        Assert.Equal(0x200, Call(Services, "ksceIdStorageGetLeafSize"));
        Assert.Equal(1, Call(Services, "ksceIdStorageIsFormatted"));
        Assert.Equal(0, Call(Services, "ksceIdStorageIsReadOnly"));
        Assert.Equal(0, Call(Services, "ksceIdStorageIsDirty"));
        uint leaf = _harness.Alloc(0x200);
        GuestMemory.Fill(leaf, 0xAA, 0x200);
        Assert.Equal(0, Call(Services, "ksceIdStorageReadLeaf", 0x116u, leaf));
        Assert.True(GuestMemory.ReadSpan(leaf, 0x200).SequenceEqual(new byte[0x200]));
        Assert.Equal(LeafId, Call(Services, "ksceIdStorageReadLeaf", 0xFFF0u, leaf));
        Assert.Equal(Invalid, Call(Services, "ksceIdStorageReadLeaf", 0x116u, 0u));
        Assert.Equal(0, Call(Services, "ksceIdStorageLookup", 0x116u, 0u, leaf, 4u));
        Assert.Equal(Invalid, Call(Services, "ksceIdStorageLookup", 0x116u, 0x1FFu, leaf, 2u));
        Assert.Equal(LeafId, Call(Services, "ksceIdStorageLookup", 0xFFFFu, 0u, leaf, 4u));
    }

    /// <summary>The bridge's changes used to succeed; SceIdStorage refuses them on a retail unit and the bridge passes
    /// the kernel's answer on.</summary>
    [Fact]
    public void ChangesAreRefusedOutsideProductMode()
    {
        Assert.Equal(Invalid, Call(Services, "IdStorageChange"));
        VshBridge bridge = _harness.Module<VshBridge>();
        _harness.Emu.IsSystemProgram = true;
        uint leaf = _harness.Alloc(0x200);
        Assert.Equal(Invalid, Call(bridge, "vshIdStorageFormat"));
        Assert.Equal(Invalid, Call(bridge, "vshIdStorageWriteLeaf", 0x116, leaf));
        Assert.Equal(Invalid, Call(bridge, "vshIdStorageUpdate", 0x116, 0u, leaf, 4u));
        Assert.Equal(Invalid, Call(bridge, "vshIdStorageCreateLeaf", 0x116));
        Assert.Equal(0, Call(bridge, "vshIdStorageFlush"));
    }
}
