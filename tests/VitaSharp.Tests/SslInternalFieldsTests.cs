using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSslInternal's field accessors and engine-free object helpers as libssl.suprx 3.74 has them, on synthetic
/// connection, context, BIO and record structures at the 3.74 offsets: options, control, peer chain, connection
/// values, read/shutdown/handshake gates, SSL_get_error, the MES-to-SceSsl error translation, MES records, BIO_free,
/// SSL_set_bio and the key method call. Found by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class SslInternalFieldsTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "ssl-internal-fields");

    public void Dispose() => _harness.Dispose();

    private Ssl SslModule => _harness.Module<Ssl>();

    /// <summary>Thumb code: adds r0, r1, r2 ; bx lr (returns its second and third arguments added).</summary>
    private uint AddCode()
    {
        uint code = _harness.Alloc(8);
        GuestMemory.Write(code, (ushort)0x1888);
        GuestMemory.Write(code + 2, (ushort)0x4770);
        return code | 1;
    }

    [Theory]
    [InlineData(0x0DB6C206u)]
    [InlineData(0xFBEB1FD7u)]
    [InlineData(0x260F5A85u)]
    [InlineData(0x9E38815Du)]
    [InlineData(0xAF50B945u)]
    [InlineData(0xB0E4CB2Fu)]
    [InlineData(0xF2C9B744u)]
    [InlineData(0xF71DB06Fu)]
    [InlineData(0xCC7F4211u)]
    [InlineData(0xA4EA9DE0u)]
    [InlineData(0x243DE290u)]
    [InlineData(0x24C6257Fu)]
    [InlineData(0x4F781BB6u)]
    [InlineData(0xEC9FAF42u)]
    [InlineData(0xD776E2F8u)]
    [InlineData(0xE5EDD91Bu)]
    [InlineData(0x2F0FC3CDu)]
    [InlineData(0x461C5990u)]
    [InlineData(0xBF99D664u)]
    [InlineData(0xF030A0DEu)]
    [InlineData(0xC4D984C2u)]
    [InlineData(0x4A1557A7u)]
    [InlineData(0x67FB1CD3u)]
    public void TheExportsHaveAnEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.False(b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void OptionsAndSettersWriteThe374Offsets()
    {
        uint ssl = _harness.Alloc(0x200);
        GuestMemory.W32(ssl + 0x114, 1);
        GuestMemory.W32(ssl + 0x118, 2);
        Assert.Equal(5u, Convert.ToUInt32(Invoke(SslModule, "SslInternalAddOptions", ssl, 4u)));
        Assert.Equal(6u, GuestMemory.R32(ssl + 0x118));
        uint context = _harness.Alloc(16);
        GuestMemory.W32(context + 4, 8);
        Assert.Equal(9u, Convert.ToUInt32(Invoke(SslModule, "SslInternalAddContextOptions", context, 1u)));
        Assert.Equal(1u, GuestMemory.R32(context + 8));

        Invoke(SslModule, "SslInternalSetVerifyMode", ssl, 1u);
        Assert.Equal(1u, GuestMemory.R32(ssl + 0x58));
        Invoke(SslModule, "SslInternalSetWord68", ssl, 7u);
        Assert.Equal(7u, GuestMemory.R32(ssl + 0x68));
        Invoke(SslModule, "SslInternalSetCallback44", ssl, 0x81000001u, 0x1234u);
        Assert.Equal(0x1234u, GuestMemory.R32(ssl + 0x44));
        Assert.Equal(0x81000001u, GuestMemory.R32(ssl + 0x48));
        Invoke(SslModule, "SslInternalSetSessionWord", ssl, 3u); // no record at +0xD0: nothing
        uint record = _harness.Alloc(0x100);
        GuestMemory.W32(ssl + 0xD0, record);
        Invoke(SslModule, "SslInternalSetSessionWord", ssl, 3u);
        Assert.Equal(3u, GuestMemory.R32(record + 0xBC));
        Invoke(SslModule, "SslInternalSetWordE4", ssl, 5u);
        Invoke(SslModule, "SslInternalSetWordsE8", ssl, 6u, 7u);
        Assert.Equal(5u, GuestMemory.R32(ssl + 0xE4));
        Assert.Equal(6u, GuestMemory.R32(ssl + 0xE8));
        Assert.Equal(7u, GuestMemory.R32(ssl + 0xEC));
    }

    [Fact]
    public void TheControlKeepsItsOwnCommandsAndForwardsTheRest()
    {
        uint ssl = _harness.Alloc(0x200);
        uint table = _harness.Alloc(0x40);
        GuestMemory.W32(ssl, table);
        GuestMemory.W32(table + 0x34, AddCode());
        GuestMemory.W32(ssl + 0x20, 11);
        GuestMemory.W32(ssl + 0x28, 12);
        GuestMemory.W32(ssl + 0x94, 13);
        GuestMemory.W32(ssl + 0xD8, 1);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(11u, Control(ssl, 0x2C, 21));
            Assert.Equal(21u, Control(ssl, 0x2D, 0));
            Assert.Equal(12u, Control(ssl, 0x37, 0));
            Assert.Equal(13u, Control(ssl, 0x38, 23));
            Assert.Equal(23u, GuestMemory.R32(ssl + 0x94));
            Assert.Equal(3u, Control(ssl, 0x41, 2));
            Assert.Equal(0x99u + 5u, Control(ssl, 0x99, 5)); // the method table's control: r1 + r2
        });
    }

    private uint Control(uint ssl, uint command, uint value) => Run("SslInternalControl", ssl, command, value, 0);

    /// <summary>Runs an HLE function that takes the raw context: r0..r3 as given, sp at a zeroed scratch block (the
    /// fifth and later arguments read 0); its r0 back.</summary>
    private unsafe uint Run(string name, uint r0, uint r1, uint r2, uint r3)
    {
        CpuState* cpu = CpuState.Allocate();
        try
        {
            cpu->R[0] = r0;
            cpu->R[1] = r1;
            cpu->R[2] = r2;
            cpu->R[3] = r3;
            cpu->R[13] = _harness.Alloc(0x40);
            var context = new HleContext { Cpu = cpu, Emu = _harness.Emu, Thread = VitaSharp.Core.Kernel.GuestThread.Current! };
            Invoke(SslModule, name, context);
            return cpu->R[0];
        }
        finally
        {
            CpuState.Free(cpu);
        }
    }

    [Fact]
    public void ThePeerChainAndTheConnectionValuesFollowTheSession()
    {
        uint ssl = _harness.Alloc(0x200);
        uint session = _harness.Alloc(0x100);
        uint chain = _harness.Alloc(8);
        uint certificates = _harness.Alloc(8);
        uint output = _harness.Word(0xAAAAAAAA);
        Assert.Equal(0u, Convert.ToUInt32(Invoke(SslModule, "SslInternalPeerChainCount", ssl)));
        Assert.Equal(0x2718, Call(SslModule, "SslInternalPeerChainCertificate", ssl, 0, output));
        GuestMemory.W32(ssl + 0x24, 1);
        GuestMemory.W32(ssl + 0x100, session);
        GuestMemory.W32(session + 0x6C, chain);
        GuestMemory.W32(chain, 2);
        GuestMemory.W32(chain + 4, certificates);
        GuestMemory.W32(certificates + 4, 0x5555);
        Assert.Equal(2u, Convert.ToUInt32(Invoke(SslModule, "SslInternalPeerChainCount", ssl)));
        Assert.Equal(0, Call(SslModule, "SslInternalPeerChainCertificate", ssl, 1, output));
        Assert.Equal(0x5555u, GuestMemory.R32(output));
        Assert.Equal(0x2718, Call(SslModule, "SslInternalPeerChainCertificate", ssl, 2, output));
        Assert.Equal(0x2718, Call(SslModule, "SslInternalPeerChainCertificate", ssl, -1, output));

        GuestMemory.W32(ssl + 0x138, 0x11);
        GuestMemory.W32(session + 0xAC, 0x22);
        Assert.Equal(0x2721u, Convert.ToUInt32(Invoke(SslModule, "SslInternalConnectionValue", ssl, 1, 0u)));
        Assert.Equal(0u, Convert.ToUInt32(Invoke(SslModule, "SslInternalConnectionValue", ssl, 1, output)));
        Assert.Equal(0x11u, GuestMemory.R32(output));
        Assert.Equal(0u, Convert.ToUInt32(Invoke(SslModule, "SslInternalConnectionValue", ssl, 5, output)));
        Assert.Equal(0x22u, GuestMemory.R32(output));
        Assert.Equal(0u, Convert.ToUInt32(Invoke(SslModule, "SslInternalConnectionValue", ssl, 4, output)));
        Assert.Equal(0u, GuestMemory.R32(output));
        Assert.Equal(0x271Bu, Convert.ToUInt32(Invoke(SslModule, "SslInternalConnectionValue", ssl, 8, output)));
    }

    [Fact]
    public void ReadShutdownAndHandshakeGates()
    {
        uint ssl = _harness.Alloc(0x200);
        GuestMemory.W32(ssl + 0x2C, 2); // the peer's close notify arrived
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0u, Run("SslInternalRead", ssl, 0, 0, 0));
            Assert.Equal(1u, GuestMemory.R32(ssl + 0x18));
            Assert.Equal(1u, Run("SslInternalShutdown", 0, 0, 0, 0));
            GuestMemory.W32(ssl + 0x30, 0x1000); // handshake in progress
            Assert.Equal(0xFFFFFFFFu, Run("SslInternalShutdown", ssl, 0, 0, 0));
            Assert.Equal(-1, Call(SslModule, "SslInternalDoHandshake", ssl)); // no handshake function
        });
    }

    [Fact]
    public void GetErrorClassifiesTheFailure()
    {
        uint ssl = _harness.Alloc(0x200);
        uint bio = _harness.Alloc(0x40);
        Assert.Equal(0, Call(SslModule, "SslInternalGetError", ssl, 1));
        GuestMemory.W32(ssl + 0x18, 3); // reading
        Assert.Equal(2, Call(SslModule, "SslInternalGetError", ssl, -1)); // NULL read BIO: all flags
        GuestMemory.W32(ssl + 0xC, bio);
        GuestMemory.W32(bio + 0x14, 2);
        Assert.Equal(3, Call(SslModule, "SslInternalGetError", ssl, -1));
        GuestMemory.W32(bio + 0x14, 4);
        GuestMemory.W32(bio + 0x18, 3);
        Assert.Equal(9, Call(SslModule, "SslInternalGetError", ssl, -1));
        GuestMemory.W32(bio + 0x14, 0);
        Assert.Equal(5, Call(SslModule, "SslInternalGetError", ssl, -1));
        GuestMemory.W32(ssl + 0x18, 4);
        Assert.Equal(4, Call(SslModule, "SslInternalGetError", ssl, -1));
        uint handshake = _harness.Alloc(0x200);
        GuestMemory.W32(ssl + 0x54, handshake);
        GuestMemory.W32(ssl + 0x2C, 2);
        Assert.Equal(6, Call(SslModule, "SslInternalGetError", ssl, 0));
        GuestMemory.W32(handshake + 0x16C, 1);
        Assert.Equal(5, Call(SslModule, "SslInternalGetError", ssl, 0));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0x2715, unchecked((int)0x80435022))]
    [InlineData(0, 0x2711, unchecked((int)0x80435201))]
    [InlineData(0, 0x2721, unchecked((int)0x80435211))]
    [InlineData(0, 0x2728, unchecked((int)0x804352FF))]
    [InlineData(0, 0x272B, unchecked((int)0x804352FF))]
    [InlineData(0, 0x2735, unchecked((int)0x80435225))]
    [InlineData(0, 0x2736, unchecked((int)0x804352FF))]
    [InlineData(0, 5, unchecked((int)0x804352FF))]
    [InlineData(1, 0x1234, 0x1234)]
    [InlineData(2, 0, unchecked((int)0x804351FE))]
    public void MesErrorsBecomeSceSslErrors(int kind, int code, int expected)
    {
        Assert.Equal(expected, Call(SslModule, "SslInternalTranslateError", kind, code));
    }

    [Fact]
    public void RecordsAndBiosLiveOnLibsslsHeap()
    {
        uint output = _harness.Word(0);
        Assert.Equal(0x2721, Call(SslModule, "SslInternalNewRecord", 7u, 0u, 0u));
        Assert.Equal(0, Call(SslModule, "SslInternalNewRecord", 7u, 0u, output));
        uint record = GuestMemory.R32(output);
        Assert.Equal(7u, GuestMemory.R32(record));
        Assert.Equal(1u, GuestMemory.R32(record + 4));
        Assert.Equal(0xFFFFFFFFu, GuestMemory.R32(record + 16));
        Assert.Equal(0, Call(SslModule, "SslInternalFreeRecord", record));
        Assert.Equal(0, Call(SslModule, "SslInternalFreeRecord", 0u));

        uint bio = _harness.Alloc(0x44);
        GuestMemory.W32(bio + 0x2C, 2);
        Assert.Equal(0, Call(SslModule, "SslInternalBioFree", 0u));
        Assert.Equal(1, Call(SslModule, "SslInternalBioFree", bio));
        Assert.Equal(1u, GuestMemory.R32(bio + 0x2C));
        _harness.OnGuest(_ => Assert.Equal(1, Call(SslModule, "SslInternalBioFree", bio))); // no method: kept
        Assert.Equal(0u, GuestMemory.R32(bio + 0x2C));

        uint ssl = _harness.Alloc(0x200);
        uint read = _harness.Alloc(0x44);
        uint write = _harness.Alloc(0x44);
        Invoke(SslModule, "SslInternalSetBio", ssl, read, write);
        Assert.Equal(read, GuestMemory.R32(ssl + 0xC));
        Assert.Equal(write, GuestMemory.R32(ssl + 0x10));
    }

    [Fact]
    public void TheKeyMethodNeedsItsRecord()
    {
        uint ssl = _harness.Alloc(0x200);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0x20u, Run("SslInternalKeyMethodBC", ssl, 0, 1, 0));
            Assert.Equal(0x2721u, Run("SslInternalKeyMethodBC", ssl, 1, 1, 0));
            uint keyObject = _harness.Alloc(0x20);
            GuestMemory.W32(ssl + 0xC4, keyObject);
            Assert.Equal(0x271Fu, Run("SslInternalKeyMethodBC", ssl, 1, 1, 0));
            uint record = _harness.Alloc(0x100);
            GuestMemory.W32(keyObject + 0x18, record);
            Assert.Equal(0x271Bu, Run("SslInternalKeyMethodBC", ssl, 1, 1, 0));
            GuestMemory.W32(record + 0xBC, AddCode());
            Assert.Equal(7u, Run("SslInternalKeyMethodBC", ssl, 3, 4, 0));
        });
    }
}
