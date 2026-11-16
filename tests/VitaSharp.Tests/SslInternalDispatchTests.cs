using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSslInternal's method-table dispatchers and BIO calls as libssl.suprx 3.74 has them, on synthetic objects whose
/// tables point at small guest functions: NULL object 0x2721, missing method 0x271B, the method run with the caller's
/// arguments; BIO_ctrl / BIO_write with their callback protocol; the engine hooks (none installed: 0). Found by name so
/// the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class SslInternalDispatchTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "ssl-internal-dispatch");

    public void Dispose() => _harness.Dispose();

    private Ssl SslModule => _harness.Module<Ssl>();

    /// <summary>Thumb code: adds r0, r1, r2 ; bx lr.</summary>
    private uint AddCode()
    {
        uint code = _harness.Alloc(8);
        GuestMemory.Write(code, (ushort)0x1888);
        GuestMemory.Write(code + 2, (ushort)0x4770);
        return code | 1;
    }

    private unsafe uint Run(string name, uint r0, uint r1, uint r2, uint r3, uint stack0 = 0, uint stack2 = 0)
    {
        CpuState* cpu = CpuState.Allocate();
        try
        {
            uint stack = _harness.Alloc(0x40);
            GuestMemory.W32(stack, stack0);
            GuestMemory.W32(stack + 8, stack2);
            cpu->R[0] = r0;
            cpu->R[1] = r1;
            cpu->R[2] = r2;
            cpu->R[3] = r3;
            cpu->R[13] = stack;
            var context = new HleContext { Cpu = cpu, Emu = _harness.Emu, Thread = VitaSharp.Core.Kernel.GuestThread.Current! };
            Invoke(SslModule, name, context);
            return cpu->R[0];
        }
        finally
        {
            CpuState.Free(cpu);
        }
    }

    [Theory]
    [InlineData(0xCE37B69Eu, false)]
    [InlineData(0x144FA354u, false)]
    [InlineData(0x29AAC768u, false)]
    [InlineData(0x91C2531Cu, false)]
    [InlineData(0x64BDF1BBu, false)]
    [InlineData(0x17BF2367u, false)]
    [InlineData(0xB5A099D2u, false)]
    [InlineData(0x306E2A22u, false)]
    [InlineData(0x170D595Du, false)]
    [InlineData(0x46358909u, false)]
    [InlineData(0xD4ADF069u, false)]
    [InlineData(0x5E50CB55u, false)]
    [InlineData(0x6CDB4EE8u, false)]
    [InlineData(0x04BAED82u, false)]
    [InlineData(0xD1A82B1Au, false)]
    [InlineData(0x88391E03u, false)]
    [InlineData(0x6D2A86F0u, true)]
    [InlineData(0x4AAF0D45u, true)]
    [InlineData(0x16D7C7CAu, false)]
    [InlineData(0x33AC7A16u, false)]
    public void TheExportsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Theory]
    [InlineData("SslInternalDispatch04", 0x04, false)]
    [InlineData("SslInternalDispatch14", 0x14, false)]
    [InlineData("SslInternalDispatch20", 0x20, true)]
    [InlineData("SslInternalDispatch24", 0x24, true)]
    [InlineData("SslInternalDispatch2C", 0x2C, false)]
    [InlineData("SslInternalDispatch30", 0x30, true)]
    public void TableDispatchersRunTheirSlot(string name, int offset, bool needsThird)
    {
        uint table = _harness.Alloc(0x60);
        uint objectAddress = _harness.Word(table);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0x2721u, Run(name, 0, 1, 2, 0));
            if (needsThird)
            {
                Assert.Equal(0x2721u, Run(name, objectAddress, 1, 0, 0));
            }

            Assert.Equal(0x271Bu, Run(name, objectAddress, 1, 2, 0));
            GuestMemory.W32(table + (uint)offset, AddCode());
            Assert.Equal(5u, Run(name, objectAddress, 2, 3, 0));
        });
    }

    [Fact]
    public void TheOtherDispatchersFollowTheirTables()
    {
        uint table = _harness.Alloc(0x60);
        uint sub = _harness.Alloc(0x10);
        uint objectAddress = _harness.Word(table);
        uint holder = _harness.Word(objectAddress);
        uint inner = _harness.Alloc(0x40);
        uint innerInner = _harness.Alloc(0x40);
        uint wrapper = _harness.Alloc(8);
        GuestMemory.W32(wrapper + 4, inner);
        GuestMemory.W32(inner + 4, innerInner);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0x2721u, Run("SslInternalDispatch1C", objectAddress, 1, 2, 0, 0, 1)); // the fifth argument 0
            Assert.Equal(0x271Bu, Run("SslInternalDispatch1C", objectAddress, 1, 2, 0, 1, 1));
            GuestMemory.W32(table + 0x1C, AddCode());
            Assert.Equal(3u, Run("SslInternalDispatch1C", objectAddress, 1, 2, 0, 1, 1));

            Assert.Equal(0x2721u, Run("SslInternalDispatchSub0", objectAddress, 0, 2, 0));
            Assert.Equal(0x271Bu, Run("SslInternalDispatchSub0", objectAddress, 1, 2, 0)); // no sub-table
            GuestMemory.W32(table + 0x4C, sub);
            GuestMemory.W32(sub, AddCode());
            GuestMemory.W32(sub + 8, AddCode());
            Assert.Equal(3u, Run("SslInternalDispatchSub0", objectAddress, 1, 2, 0));
            Assert.Equal(0x2721u, Run("SslInternalDispatchSub8", objectAddress, 1, 0, 0));
            Assert.Equal(4u, Run("SslInternalDispatchSub8", objectAddress, 1, 3, 0));

            Assert.Equal(0u, Run("SslInternalRelease", 0, 0, 0, 0));
            Assert.Equal(0x271Bu, Run("SslInternalRelease", holder, 0, 0, 0));
            GuestMemory.W32(table + 0x20, AddCode());
            Assert.Equal(9u, Run("SslInternalRelease", holder, 4, 5, 0));

            Assert.Equal(0x2721u, Run("SslInternalInner04", 0, 0, 0, 0));
            GuestMemory.W32(inner + 4, AddCode());
            Assert.Equal(6u, Run("SslInternalInner04", wrapper, 2, 4, 0));
            GuestMemory.W32(inner + 4, innerInner);
            GuestMemory.W32(innerInner + 0x14, AddCode());
            Assert.Equal(1u, Run("SslInternalInnerInner14", wrapper, 0, 1, 0));
        });
        Assert.Equal(0, Call(SslModule, "SslInternalHookA"));
        Assert.Equal(0, Call(SslModule, "SslInternalHookB"));
    }

    [Fact]
    public void BioControlAndWriteKeepTheCallbackProtocol()
    {
        uint table = _harness.Alloc(0x40);
        uint bio = _harness.Alloc(0x44);
        GuestMemory.W32(bio, table);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(SslModule, "SslInternalBioControl", 0u, 1u, 2u, 3u));
            Assert.Equal(-2, Call(SslModule, "SslInternalBioControl", bio, 1u, 2u, 3u));
            GuestMemory.W32(table + 0x18, AddCode());
            Assert.Equal(3, Call(SslModule, "SslInternalBioControl", bio, 1u, 2u, 3u)); // command + value

            Assert.Equal(0, Call(SslModule, "SslInternalBioWrite", 0u, 0u, 0u));
            Assert.Equal(-2, Call(SslModule, "SslInternalBioWrite", bio, 0u, 4u)); // no write method
            GuestMemory.W32(table + 8, AddCode());
            Assert.Equal(-2, Call(SslModule, "SslInternalBioWrite", bio, 0u, 4u)); // not initialised
            GuestMemory.W32(bio + 0xC, 1);
            Assert.Equal(0, Call(SslModule, "SslInternalBioWrite", bio, 6u, 4u)); // no references: 0
            GuestMemory.W32(bio + 0x2C, 1);
            Assert.Equal(10, Call(SslModule, "SslInternalBioWrite", bio, 6u, 4u));
            Assert.Equal(20u, GuestMemory.R32(bio + 0x34)); // both writes counted
        });
    }
}
