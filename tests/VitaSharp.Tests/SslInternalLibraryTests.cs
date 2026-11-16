using VitaSharp.Core.Cpu;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The last SceSslInternal exports of libssl.suprx 3.74 (the General group): the connection accessors at their 3.74
/// offsets, the dispatchers (+0x10 with its fixed argument, +0x30 with the seventh-argument check, BIO_printf through
/// BIO_write, SSL_connect through the method table), the memory functions a caller installs (libssl's later
/// allocations and frees run them), and the engine calls, which answer the firmware's "unavailable" results and never
/// install a certificate or key or choose a cipher. Found by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class SslInternalLibraryTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "ssl-internal-library");

    public void Dispose() => _harness.Dispose();

    private Ssl SslModule => _harness.Module<Ssl>();

    /// <summary>Thumb code from halfwords, followed by an optional literal word at +8.</summary>
    private uint Code(ushort[] halfwords, uint literal = 0)
    {
        uint code = _harness.Alloc(0x10);
        for (int i = 0; i < halfwords.Length; i++)
        {
            GuestMemory.Write(code + (uint)(i * 2), halfwords[i]);
        }

        GuestMemory.W32(code + 8, literal);
        return code | 1;
    }

    /// <summary>adds r0, r1, r2 (r0 = r1 + r2) ; bx lr.</summary>
    private uint AddCode() => Code([0x1888, 0x4770]);

    /// <summary>ldr r0, [pc, #4] ; bx lr ; (literal) — returns <paramref name="value"/>.</summary>
    private uint ReturnCode(uint value) => Code([0x4801, 0x4770, 0xBF00, 0xBF00], value);

    /// <summary>ldr r1, [pc, #4] ; str r0, [r1] ; bx lr — stores its first argument at <paramref name="cell"/>.</summary>
    private uint StoreCode(uint cell) => Code([0x4901, 0x6008, 0x4770, 0xBF00], cell);

    private uint U(string name, params object?[] args) => Convert.ToUInt32(Invoke(SslModule, name, args));

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

    private unsafe int Printf(uint bio, string format, uint argument)
    {
        CpuState* cpu = CpuState.Allocate();
        try
        {
            cpu->R[2] = argument;
            var context = new HleContext { Cpu = cpu, Emu = _harness.Emu, Thread = VitaSharp.Core.Kernel.GuestThread.Current! };
            return Call(SslModule, "SslInternalBioPrintf", bio, format, new VarArgs(context, 2));
        }
        finally
        {
            CpuState.Free(cpu);
        }
    }

    [Theory]
    [InlineData(0x0C693D36u, false)]
    [InlineData(0x2B90012Eu, false)]
    [InlineData(0x3EF42454u, false)]
    [InlineData(0x422DFE70u, false)]
    [InlineData(0x4725F9E7u, false)]
    [InlineData(0x6F3B9809u, false)]
    [InlineData(0xB1745DADu, false)]
    [InlineData(0xC1299FB6u, false)]
    [InlineData(0xCB83EA07u, false)]
    [InlineData(0xD6D32A2Fu, false)]
    [InlineData(0xDABB61EEu, false)]
    [InlineData(0xF4007DBEu, false)]
    [InlineData(0xFEFE761Cu, false)]
    [InlineData(0x00ABEBC3u, true)]
    [InlineData(0x05F6A292u, true)]
    [InlineData(0x09657E15u, true)]
    [InlineData(0x133ED36Du, true)]
    [InlineData(0x26CBEEB6u, true)]
    [InlineData(0x6046C1F3u, true)]
    [InlineData(0x6D0D3B11u, true)]
    [InlineData(0x92560B8Du, true)]
    [InlineData(0xCBEC7D13u, true)]
    [InlineData(0xD419451Bu, true)]
    [InlineData(0xDEC6DD27u, true)]
    [InlineData(0xFB51AE89u, true)]
    public void TheExportsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void TheAccessorsReadTheirOffsets()
    {
        uint ssl = _harness.Alloc(0x120);
        uint record = _harness.Alloc(0x240);
        GuestMemory.W32(ssl + 0x18, 0x18);
        GuestMemory.W32(ssl + 0x2C, 0x2C);
        GuestMemory.W32(ssl + 0x30, 0x30);
        GuestMemory.W32(record + 0xA4, 0xA4);
        Assert.Equal(0u, U("SslInternalSessionWordA4", ssl)); // no record
        GuestMemory.W32(ssl + 0xD0, record);
        Assert.Equal(0xA4u, U("SslInternalSessionWordA4", ssl));
        Assert.Equal(0x18u, U("SslInternalWord18", ssl));
        Assert.Equal(0x2Cu, U("SslInternalWord2C", ssl));
        Assert.Equal(0x30u, U("SslInternalStateWord", ssl));
        Assert.Equal(0x55u, U("SslInternalFirstWord", _harness.Word(0x55)));
        Assert.Equal(0, Call(SslModule, "SslInternalRemoveErrorState"));

        // F4007DBE: a client's word comes from +0x230 of the record at +0x54 only with an SSL 3 / TLS version
        GuestMemory.W32(record + 0x230, 0x230);
        GuestMemory.W32(ssl + 0x54, record);
        GuestMemory.W32(ssl, 0x0301);
        Assert.Equal(0x230u, U("SslInternalPeerWord", ssl));
        GuestMemory.W32(ssl, 0x0002);
        Assert.Equal(0u, U("SslInternalPeerWord", ssl));
        GuestMemory.W32(ssl, 0x0301);
        GuestMemory.W32(ssl + 0x54, 0);
        Assert.Equal(0u, U("SslInternalPeerWord", ssl));
        // a server: +0x10C, else +0x6C of the context at +0x100
        uint context = _harness.Alloc(0x70);
        GuestMemory.W32(context + 0x6C, 0x6C);
        GuestMemory.W32(ssl + 0x24, 1);
        GuestMemory.W32(ssl + 0x100, context);
        Assert.Equal(0x6Cu, U("SslInternalPeerWord", ssl));
        GuestMemory.W32(ssl + 0x10C, 0x10C);
        Assert.Equal(0x10Cu, U("SslInternalPeerWord", ssl));

        uint cipher = _harness.Alloc(8);
        GuestMemory.W32(cipher + 4, 0x1234);
        Assert.Equal(0x1234u, U("SslInternalCipherName", cipher));
        uint none = U("SslInternalCipherName", 0u);
        Assert.Equal("(NONE)", GuestMemory.ReadCString(none));
        Assert.Equal(none, U("SslInternalCipherName", 0u)); // one string
    }

    [Fact]
    public void TheDispatchersRunTheirMethods()
    {
        uint table = _harness.Alloc(0x40);
        uint objectAddress = _harness.Word(table);
        uint noTable = _harness.Word(0);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0x2721, Call(SslModule, "SslInternalDispatch10", 0u, 2u, 1u));
            Assert.Equal(0x2721, Call(SslModule, "SslInternalDispatch10", objectAddress, 2u, 0u));
            Assert.Equal(0x271B, Call(SslModule, "SslInternalDispatch10", objectAddress, 2u, 1u));
            GuestMemory.W32(table + 0x10, AddCode());
            Assert.Equal(6, Call(SslModule, "SslInternalDispatch10", objectAddress, 5u, 9u)); // (object, 1, value): 1 + 5

            Assert.Equal(0x2721u, Run("SslInternalDispatch30Checked", objectAddress, 1, 2, 0, 0, 0)); // seventh argument 0
            Assert.Equal(0x2721u, Run("SslInternalDispatch30Checked", 0, 1, 2, 0, 0, 1));
            Assert.Equal(0x2721u, Run("SslInternalDispatch30Checked", noTable, 1, 2, 0, 0, 1));
            Assert.Equal(0x271Bu, Run("SslInternalDispatch30Checked", objectAddress, 1, 2, 0, 0, 1));
            GuestMemory.W32(table + 0x30, AddCode());
            Assert.Equal(3u, Run("SslInternalDispatch30Checked", objectAddress, 1, 2, 0, 0, 1));

            uint ssl = _harness.Alloc(0x40);
            Assert.Equal(-1, Call(SslModule, "SslInternalConnect", ssl)); // no method table: the handshake fails
            uint methods = _harness.Alloc(0x20);
            GuestMemory.W32(ssl + 8, methods);
            Assert.Equal(-1, Call(SslModule, "SslInternalConnect", ssl));
            GuestMemory.W32(methods + 0x14, ReturnCode(7));
            Assert.Equal(7, Call(SslModule, "SslInternalConnect", ssl));
        });
    }

    [Fact]
    public void BioPrintfWritesTheFormattedText()
    {
        uint table = _harness.Alloc(0x40);
        uint bio = _harness.Alloc(0x44);
        GuestMemory.W32(bio, table);
        GuestMemory.W32(table + 8, Code([0x0010, 0x4770])); // write: movs r0, r2 (the length)
        GuestMemory.W32(bio + 0xC, 1);
        GuestMemory.W32(bio + 0x2C, 1);
        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Printf(0, "a%db", 42));
            Assert.Equal(4, Printf(bio, "a%db", 42));
            Assert.Equal(4u, GuestMemory.R32(bio + 0x34));
            Assert.Equal(0x3FF, Printf(bio, new string('x', 0x500), 0)); // cut to libssl's buffer
        });
    }

    [Fact]
    public void InstalledMemoryFunctionsServeLibssl()
    {
        uint block = _harness.Alloc(0x40);
        GuestMemory.Fill(block, 0xCC, 0x40);
        uint freed = _harness.Word(0);
        uint output = _harness.Word(0);
        _harness.OnGuest(_ =>
        {
            uint malloc = ReturnCode(block);
            uint free = StoreCode(freed);
            Call(SslModule, "SslInternalSetMemoryFunctions", malloc, 0u, free); // one NULL: nothing changes
            Assert.Equal(0, Call(SslModule, "SslInternalNewRecord", 5u, 0u, output));
            Assert.NotEqual(block, GuestMemory.R32(output));

            Call(SslModule, "SslInternalSetMemoryFunctions", malloc, ReturnCode(0), free);
            Assert.Equal(0, Call(SslModule, "SslInternalNewRecord", 5u, 0u, output));
            Assert.Equal(block, GuestMemory.R32(output));
            Assert.Equal(5u, GuestMemory.R32(block));
            Assert.Equal(1u, GuestMemory.R32(block + 4));
            Assert.Equal(0u, GuestMemory.R32(block + 8)); // zeroed
            Assert.Equal(0xCCCCCCCCu, GuestMemory.R32(block + 0x28)); // past the record: untouched
            Assert.Equal(0, Call(SslModule, "SslInternalFreeRecord", block));
            Assert.Equal(block, GuestMemory.R32(freed));

            // the library context's last reference goes back through the caller's free
            uint libraryContext = _harness.Alloc(0x19C);
            GuestMemory.W32(libraryContext + 0x194, 2);
            GuestMemory.W32(freed, 0);
            Assert.Equal(0, Call(SslModule, "SslInternalFreeLibraryContext", libraryContext));
            Assert.Equal(1u, GuestMemory.R32(libraryContext + 0x194));
            Assert.Equal(0u, GuestMemory.R32(freed));
            Assert.Equal(0, Call(SslModule, "SslInternalFreeLibraryContext", libraryContext));
            Assert.Equal(libraryContext, GuestMemory.R32(freed));
            Assert.Equal(0, Call(SslModule, "SslInternalFreeLibraryContext", 0u));

            Call(SslModule, "SslInternalSetMemoryFunctions", ReturnCode(0), malloc, free); // a malloc that fails
            Assert.Equal(0x2715, Call(SslModule, "SslInternalNewRecord", 5u, 0u, output));
        });
    }

    [Fact]
    public void TheEngineCallsNeverInstallOrAccept()
    {
        uint output = _harness.Word(0xAAAAAAAA);
        uint ssl = _harness.Alloc(0x120);
        uint certificate = _harness.Alloc(0x40);
        Assert.Equal(0u, U("SslInternalMethodTable"));
        Assert.Equal(0u, U("SslInternalResourceList"));
        Invoke(SslModule, "SslInternalCleanup");

        Assert.Equal(0x2721, Call(SslModule, "SslInternalNewLibraryContext", 0u, 0u, output));
        Assert.Equal(0x271B, Call(SslModule, "SslInternalNewLibraryContext", certificate, 0u, output));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(output));

        Assert.Equal(0, Call(SslModule, "SslInternalAddStoreCertificate", 0u, certificate));
        Assert.Equal(0, Call(SslModule, "SslInternalAddStoreCertificate", ssl, certificate)); // never added
        Assert.Equal(0, Call(SslModule, "SslInternalUseKey", 0u, certificate, 0));
        Assert.Equal(0x2722, Call(SslModule, "SslInternalUseKey", ssl, certificate, 3));
        Assert.Equal(0x2722, Call(SslModule, "SslInternalUseKey", ssl, certificate, -1));
        Assert.Equal(0, Call(SslModule, "SslInternalUseKey", ssl, certificate, 2));

        _harness.OnGuest(_ =>
        {
            Assert.Equal(0, Call(SslModule, "SslInternalUseCertificate", ssl, 0u));
            Assert.Equal(0u, GuestMemory.R32(ssl + 0x4C)); // a NULL certificate makes no record
            Assert.Equal(0, Call(SslModule, "SslInternalUseCertificate", ssl, certificate)); // never installed
            uint record = GuestMemory.R32(ssl + 0x4C);
            Assert.NotEqual(0u, record);
            Assert.Equal(record + 0x24, GuestMemory.R32(record + 4));
            Assert.Equal(1u, GuestMemory.R32(record + 0x64));
            Assert.Equal(0, Call(SslModule, "SslInternalUsePrivateKey", ssl, certificate));
            Assert.Equal(record, GuestMemory.R32(ssl + 0x4C)); // the same record
        });

        Assert.False((bool)Invoke(SslModule, "SslInternalSetCipherList", ssl, certificate)!);
        Assert.False((bool)Invoke(SslModule, "SslInternalSetCipherList", ssl, 0u)!);

        Assert.Equal(0x2721, Call(SslModule, "SslInternalReadCertificate", 0u, certificate, 1, 0, output));
        Assert.Equal(0x2721, Call(SslModule, "SslInternalReadCertificate", certificate, 0u, 1, 0, output));
        Assert.Equal(0x2721, Call(SslModule, "SslInternalReadCertificate", certificate, certificate, 1, 0, 0u));
        Assert.Equal(0x271A, Call(SslModule, "SslInternalReadCertificate", certificate, certificate, 1, 0x200, output));
        Assert.Equal(0x271B, Call(SslModule, "SslInternalReadCertificate", certificate, certificate, 1, 0x1100, output));

        Assert.Equal(0x2721, Call(SslModule, "SslInternalConnectionControl", 0u, 2, 1u));
        Assert.Equal(0x2721, Call(SslModule, "SslInternalConnectionControl", ssl, 2, 0u));
        Assert.Equal(0x271B, Call(SslModule, "SslInternalConnectionControl", ssl, 7, 0u)); // 7 takes a NULL argument
        Assert.Equal(0, Call(SslModule, "SslInternalConnectionControl", ssl, 2, 1u));
        GuestMemory.W32(ssl + 0xA4, 0x99);
        Assert.Equal(0, Call(SslModule, "SslInternalConnectionControl", ssl, 1, 0x99u)); // already that library context
        Assert.Equal(0x271B, Call(SslModule, "SslInternalConnectionControl", ssl, 1, 0x98u));
        Assert.Equal(0x271B, Call(SslModule, "SslInternalConnectionControl", ssl, 0xC, 1u));
        Assert.Equal(0x271B, Call(SslModule, "SslInternalConnectionControl", ssl, 3, 1u));
    }
}
