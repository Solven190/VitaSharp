using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The last general-group functions of SceGxmInternalForTest (libgxm_es4.suprx FW 3.74) and SceKrm (bootimage SceKrm):
/// libgxm's test setters of an immediate context, its device-memory test handle and empty entries; the kernel key
/// service, never ready without its key material. The NID test closes both libraries.
/// </summary>
[Collection("guest")]
public sealed class HleGxmTestKrmTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int InvalidValue = unchecked((int)0x805B0003);
    private const int InvalidPointer = unchecked((int)0x805B0004);
    private const int InvalidAlignment = unchecked((int)0x805B0005);

    private readonly HleHarness _harness = new(fixture, "gxm-test-krm");

    public void Dispose() => _harness.Dispose();

    private Gxm Gxm => _harness.Module<Gxm>();

    private SystemServicesVsh Services => _harness.Module<SystemServicesVsh>();

    [Theory]
    [InlineData(0x1D68FB73u, false)] // SceGxmInternalForTest
    [InlineData(0x2B0A5A39u, false)]
    [InlineData(0x3D8863E2u, false)]
    [InlineData(0x46D6C306u, false)]
    [InlineData(0x588FD7FDu, false)]
    [InlineData(0x631E95AAu, false)]
    [InlineData(0x65FF05BCu, false)]
    [InlineData(0x6E9565D5u, false)]
    [InlineData(0x7C427319u, false)]
    [InlineData(0x9C7DC365u, false)]
    [InlineData(0x9CAFAE4Bu, false)]
    [InlineData(0x9F4A526Fu, false)]
    [InlineData(0xC65CED80u, false)]
    [InlineData(0xD352F90Au, false)]
    [InlineData(0xD412F27Bu, false)]
    [InlineData(0x10202EB4u, true)] // SceKrm
    [InlineData(0x18DE25C6u, true)]
    [InlineData(0x2528D613u, true)]
    [InlineData(0x3257BD35u, true)]
    [InlineData(0x350C8251u, true)]
    [InlineData(0x41547BE1u, true)]
    [InlineData(0x5296A954u, true)]
    [InlineData(0x7251237Du, true)]
    [InlineData(0x7401B932u, true)]
    [InlineData(0x820B45C6u, true)]
    [InlineData(0x949506DCu, true)]
    [InlineData(0x98EEB7F3u, true)]
    [InlineData(0xA100E9C3u, true)]
    [InlineData(0xA9201DA3u, true)]
    [InlineData(0xD7B35BE6u, true)]
    [InlineData(0xE0AEAE3Bu, true)]
    [InlineData(0xECE6B5F4u, true)]
    [InlineData(0xECF53237u, true)]
    [InlineData(0xF0B626E7u, true)]
    public void TheLibrarysLastFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    private uint Context()
    {
        uint p = _harness.Alloc(0x24);
        uint o = _harness.Word(0);
        GuestMemory.W32(p + 8, _harness.Alloc(0x1000));
        GuestMemory.W32(p + 12, 0x1000);
        GuestMemory.W32(p + 16, _harness.Alloc(0x4000));
        GuestMemory.W32(p + 20, 0x4000);
        GuestMemory.W32(p + 24, _harness.Alloc(0x4000));
        GuestMemory.W32(p + 28, 0x4000);
        Assert.Equal(0, Call(Gxm, "sceGxmCreateContext", p, new Ptr<uint>(o)));
        return GuestMemory.R32(o);
    }

    private VitaSharp.Core.Gxm.GxmContext ContextOf(uint handle) => (VitaSharp.Core.Gxm.GxmContext)Invoke(Gxm, "Ctx", handle)!;

    /// <summary>The test setters start from sceGxmCreateContext's values and keep the module's range checks.</summary>
    [Fact]
    public void TestSettersCheckTheirRanges()
    {
        uint ctx = Context();
        VitaSharp.Core.Gxm.GxmContext context = ContextOf(ctx);
        Assert.Equal(0x80u, context.TestControl);
        Assert.Equal((ushort)1000, context.TestRangeHigh);
        Assert.Equal(0xE9u, context.TestBytePair);
        Assert.Equal(1u, context.TestMode);

        Assert.Equal(InvalidPointer, Call(Gxm, "GxmTestSetControl", 0u, 0x80u));
        Assert.Equal(InvalidValue, Call(Gxm, "GxmTestSetControl", ctx, 0x81u));
        Assert.Equal(0, Call(Gxm, "GxmTestSetControl", ctx, 0u));
        Assert.Equal(0u, context.TestControl);

        Assert.Equal(InvalidValue, Call(Gxm, "GxmTestSetRange", ctx, 0x11u, 5));
        Assert.Equal(InvalidValue, Call(Gxm, "GxmTestSetRange", ctx, 1u, 0));
        Assert.Equal(InvalidValue, Call(Gxm, "GxmTestSetRange", ctx, 1u, 0x8000));
        Assert.Equal(0, Call(Gxm, "GxmTestSetRange", ctx, 0x10u, 0x7FFF));
        Assert.Equal((ushort)0x10, context.TestRangeLow);
        Assert.Equal((ushort)0x7FFF, context.TestRangeHigh);

        Assert.Equal(InvalidPointer, Call(Gxm, "GxmTestSetBytePair", ctx, 0x80, 0));
        Assert.Equal(InvalidPointer, Call(Gxm, "GxmTestSetBytePair", 0u, 0, 0));
        Assert.Equal(0, Call(Gxm, "GxmTestSetBytePair", ctx, -1, 0x7F));
        Assert.Equal(0xFF7Fu, context.TestBytePair);

        Assert.Equal(InvalidValue, Call(Gxm, "GxmTestSetMode", ctx, 0u, 5u));
        Assert.Equal(InvalidValue, Call(Gxm, "GxmTestSetMode", ctx, 1u, 0x400u));
        Assert.Equal(InvalidValue, Call(Gxm, "GxmTestSetMode", ctx, 2u, 0u));
        Assert.Equal(0, Call(Gxm, "GxmTestSetMode", ctx, 1u, 0x3FFu));
        Assert.Equal(0x3FFu, context.TestModeValue);

        Assert.Equal(InvalidAlignment, Call(Gxm, "GxmTestSetVisibilityBuffer", ctx, 0x81000010u, 8u));
        Assert.Equal(0, Call(Gxm, "GxmTestSetVisibilityBuffer", ctx, 0x81000010u, 0x20u));
        Assert.Equal(0x81000010u, context.VisBuffer);

        Assert.Equal(InvalidValue, Call(Gxm, "GxmTestSetYuvProfile", ctx, 2u, 0u));
        Assert.Equal(InvalidValue, Call(Gxm, "GxmTestSetYuvProfile", ctx, 0u, 4u));
        Assert.Equal(0, Call(Gxm, "GxmTestSetYuvProfile", ctx, 1u, 3u));
        Assert.Equal((byte)3, context.YuvProfile[1]);
    }

    /// <summary>The device-memory handle: a memblock mapped as its kind, the base and USSE offset readable, released
    /// with its mapping; the argument checks come first.</summary>
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void DeviceMemoryHandleMapsAndReleasesItsBlock(uint kind)
    {
        uint handleOut = _harness.Word(0);
        Assert.Equal(InvalidValue, Call(Gxm, "GxmTestAllocDeviceMemory", 4u, 1u, 0x1000u, 0x1000u, handleOut));
        Assert.Equal(InvalidValue, Call(Gxm, "GxmTestAllocDeviceMemory", kind, 4u, 0x1000u, 0x1000u, handleOut));
        Assert.Equal(InvalidAlignment, Call(Gxm, "GxmTestAllocDeviceMemory", kind, 1u, 0x1000u, 0x1800u, handleOut));
        Assert.Equal(InvalidPointer, Call(Gxm, "GxmTestAllocDeviceMemory", kind, 1u, 0x1000u, 0x1000u, 0u));

        Assert.Equal(0, Call(Gxm, "GxmTestAllocDeviceMemory", kind, 3u, 0x1234u, 0x1000u, handleOut));
        uint handle = GuestMemory.R32(handleOut);
        int block = (int)GuestMemory.R32(handle);
        MemBlock memBlock = _harness.Emu.Kernel.Objects.Get<MemBlock>(block)!;
        Assert.Equal("devicemem", memBlock.Name);
        Assert.Equal(kind == 1 ? 0x40000u : 0x2000u, memBlock.Size);
        Assert.Equal(memBlock.Address, (uint)Invoke(Gxm, "GxmTestDeviceMemoryBase", handle)!);
        uint expectedOffset = kind >= 2 ? memBlock.Address : 0;
        Assert.Equal(expectedOffset, (uint)Invoke(Gxm, "GxmTestDeviceMemoryOffset", handle)!);
        Assert.Equal(0x1234u, GuestMemory.R32(handle + 0xC));
        Assert.Equal(kind, GuestMemory.R32(handle + 0x10));

        Assert.Equal(0, Call(Gxm, "GxmTestFreeDeviceMemory", handle));
        Assert.Null(_harness.Emu.Kernel.Objects.Get<MemBlock>(block));
        if (kind < 2)
        {
            Assert.Equal(InvalidValue, Call(Gxm, "sceGxmUnmapMemory", memBlock.Address));
        }

        Assert.Equal(0, Call(Gxm, "GxmTestFreeDeviceMemory", 0u));
        Assert.Equal(0u, (uint)Invoke(Gxm, "GxmTestDeviceMemoryBase", 0u)!);
    }

    [Fact]
    public void KeyServiceIsNeverReady()
    {
        Assert.Equal(unchecked((int)0x80A0000A), Call(Services, "KrmNotReady"));
    }
}
