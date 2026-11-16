using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// ksceKernelCreateClass (ksceUIDClassInitClass) and the class checks of the GUID references as the FW 3.74 sysmem has
/// them (decompiled, study only): no argument checks, the allocation size rounded to 4/8/16/32, the parent kept as
/// given, the class linked at the tail of the circular list that starts at the root class with the next index (a
/// name already in the list is not linked again); ksceGUIDReferObjectWithClass matches the class exactly,
/// ksceGUIDReferObjectWithSubclass walks the parent chain, and both clear *obj on a mismatch.
/// </summary>
[Collection("guest")]
public sealed class KernelClassTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "kclass");

    public void Dispose() => _harness.Dispose();

    private SysmemKernel Sysmem => _harness.Module<SysmemKernel>();

    private int S(string function, params object?[] args) => Call(Sysmem, function, args);

    private uint Root => Convert.ToUInt32(Invoke(Sysmem, "ksceKernelGetUidClass"));

    private uint NewClass(string name, uint parent = 0, uint itemSize = 0x10)
    {
        uint cls = _harness.Alloc(0x30);
        Assert.Equal(0, S("ksceKernelCreateClass", cls, _harness.Str(name), parent, itemSize, 0u, 0u));
        return cls;
    }

    [Theory]
    [InlineData(0u, 4)]
    [InlineData(1u, 4)]
    [InlineData(4u, 4)]
    [InlineData(5u, 8)]
    [InlineData(8u, 8)]
    [InlineData(9u, 0x10)]
    [InlineData(0x10u, 0x10)]
    [InlineData(0x11u, 0x20)]
    [InlineData(0x20u, 0x20)]
    [InlineData(0x21u, 0x40)]
    [InlineData(0x12345u, 0x2360)]
    public void TheItemSizeIsKeptAndItsAllocationSizeRounded(uint itemSize, int allocationSize)
    {
        uint cls = NewClass($"SceSize{itemSize:X}", 0, itemSize);
        Assert.Equal((ushort)itemSize, GuestMemory.Read<ushort>(cls + 0x18));
        Assert.Equal(allocationSize, GuestMemory.Read<ushort>(cls + 0x1A));
        Assert.Equal(0xABCE9DA5u, GuestMemory.R32(cls + 0x28));
        Assert.Equal(Root, GuestMemory.R32(cls + 0x04));
        Assert.Equal(0u, GuestMemory.R32(cls + 0x1C));
    }

    [Fact]
    public void TheParentIsKeptAsGiven()
    {
        uint orphan = NewClass("SceOrphan");
        Assert.Equal(0u, GuestMemory.R32(orphan + 0x10));
        uint child = NewClass("SceChild", orphan);
        Assert.Equal(orphan, GuestMemory.R32(child + 0x10));
    }

    [Fact]
    public void ClassesAreLinkedAtTheTailOfTheRootListWithTheNextIndex()
    {
        uint root = Root;
        Assert.Equal(1, GuestMemory.Read<byte>(root + 0x14));
        uint first = NewClass("SceFirst");
        uint second = NewClass("SceSecond");
        Assert.Equal(2, GuestMemory.Read<byte>(first + 0x14));
        Assert.Equal(3, GuestMemory.Read<byte>(second + 0x14));
        Assert.Equal(first, GuestMemory.R32(root));
        Assert.Equal(second, GuestMemory.R32(first));
        Assert.Equal(root, GuestMemory.R32(second));
        Assert.Equal(second, GuestMemory.R32(root + 0x08));
        Assert.Equal(first, GuestMemory.R32(second + 0x08));
        Assert.Equal(root, GuestMemory.R32(first + 0x08));
    }

    [Fact]
    public void ANameAlreadyInTheListIsNotLinkedAgain()
    {
        uint root = Root;
        uint first = NewClass("SceSameName");
        uint again = _harness.Alloc(0x30);
        GuestMemory.W32(again, 0x1234);
        Assert.Equal(0, S("ksceKernelCreateClass", again, _harness.Str("SceSameName"), 0u, 0x10u, 0u, 0u));
        Assert.Equal(0x1234u, GuestMemory.R32(again)); // the list fields are not touched
        Assert.Equal(0, GuestMemory.Read<byte>(again + 0x14));
        Assert.Equal(0xABCE9DA5u, GuestMemory.R32(again + 0x28)); // the record is still filled
        Assert.Equal(root, GuestMemory.R32(first));

        // the same record created again is not linked twice
        Assert.Equal(0, S("ksceKernelCreateClass", first, _harness.Str("SceRenamedFirst"), 0u, 0x10u, 0u, 0u));
        Assert.Equal(root, GuestMemory.R32(first));
        Assert.Equal(first, GuestMemory.R32(root));
    }

    [Fact]
    public void ReferWithClassIsExactAndWithSubclassWalksTheParents()
    {
        _harness.OnGuest(_ =>
        {
            uint parent = NewClass("SceParent");
            uint child = NewClass("SceDerived", parent);
            uint other = NewClass("SceUnrelated");
            uint objOut = _harness.Word(0);
            int guid = S("ksceGUIDKernelCreate", child, _harness.Str("SceDerivedObject"), objOut);
            Assert.True(guid > 0);
            uint body = GuestMemory.R32(objOut);
            uint got = _harness.Word(0);

            Assert.Equal(0, S("ksceGUIDReferObjectWithClass", guid, child, got));
            Assert.Equal(body, GuestMemory.R32(got));
            GuestMemory.W32(got, 0xDEAD);
            Assert.Equal(SceError.INVALID_UID_CLASS, S("ksceGUIDReferObjectWithClass", guid, parent, got));
            Assert.Equal(0u, GuestMemory.R32(got));
            Assert.Equal(SceError.INVALID_UID_CLASS, S("ksceGUIDReferObjectWithClass", guid, 0u, got));

            Assert.Equal(0, S("ksceGUIDReferObjectWithSubclass", guid, parent, got));
            Assert.Equal(body, GuestMemory.R32(got));
            Assert.Equal(0, S("ksceGUIDReferObjectWithSubclass", guid, child, got));
            GuestMemory.W32(got, 0xDEAD);
            Assert.Equal(SceError.UID_NOT_OF_SUBCLASS, S("ksceGUIDReferObjectWithSubclass", guid, other, got));
            Assert.Equal(0u, GuestMemory.R32(got));
            Assert.Equal(SceError.UID_NOT_OF_SUBCLASS, S("ksceGUIDReferObjectWithSubclass", guid, Root, got));
            Assert.Equal(SceError.INVALID_UID, S("ksceGUIDReferObjectWithClass", 2, child, got));
        });
    }
}
