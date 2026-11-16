using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceSslInternal's field accessors of libssl.suprx 3.74 (its "sslc" connection and context structures, read in its
// code, study only): each reads or writes the structure the caller passes, at the offsets the 3.74 module uses; the
// calls that go on to the structure's method table (+8) run the guest function there with the caller's arguments.
public sealed partial class Ssl
{
    /// <summary>libssl's MES codes: an invalid argument, an operation the object does not support.</summary>
    const uint MesInvalidArgument = 0x2721, MesNotSupported = 0x271B;

    /// <summary>NID_0DB6C206 (SSL *ssl, uint bits): adds the bits to the connection's options (+0x114) and to +0x118;
    /// the new options.</summary>
    [Hle("NID_0DB6C206", Nid = 0x0DB6C206, Library = "SceSslInternal")]
    uint SslInternalAddOptions(uint ssl, uint bits)
    {
        uint options = GuestMemory.R32(ssl + 0x114) | bits;
        GuestMemory.W32(ssl + 0x118, GuestMemory.R32(ssl + 0x118) | bits);
        GuestMemory.W32(ssl + 0x114, options);
        return options;
    }

    /// <summary>NID_FBEB1FD7 (SSL_CTX *context, uint bits): the same on a context's +4 and +8; the new +4.</summary>
    [Hle("NID_FBEB1FD7", Nid = 0xFBEB1FD7, Library = "SceSslInternal")]
    uint SslInternalAddContextOptions(uint context, uint bits)
    {
        uint options = GuestMemory.R32(context + 4) | bits;
        GuestMemory.W32(context + 8, GuestMemory.R32(context + 8) | bits);
        GuestMemory.W32(context + 4, options);
        return options;
    }

    /// <summary>NID_260F5A85 (SSL *ssl, int mode): the peer verification mode (+0x58).</summary>
    [Hle("NID_260F5A85", Nid = 0x260F5A85, Library = "SceSslInternal")]
    void SslInternalSetVerifyMode(uint ssl, uint mode)
    {
        GuestMemory.W32(ssl + 0x58, mode);
    }

    /// <summary>NID_9E38815D (SSL *ssl, value): the word at +0x68.</summary>
    [Hle("NID_9E38815D", Nid = 0x9E38815D, Library = "SceSslInternal")]
    void SslInternalSetWord68(uint ssl, uint value)
    {
        GuestMemory.W32(ssl + 0x68, value);
    }

    /// <summary>NID_AF50B945 (SSL *ssl, callback, argument): +0x48 = callback, +0x44 = argument.</summary>
    [Hle("NID_AF50B945", Nid = 0xAF50B945, Library = "SceSslInternal")]
    void SslInternalSetCallback44(uint ssl, uint callback, uint argument)
    {
        GuestMemory.W32(ssl + 0x44, argument);
        GuestMemory.W32(ssl + 0x48, callback);
    }

    /// <summary>NID_B0E4CB2F (SSL *ssl, value): +0xBC of the record at +0xD0, when there is one.</summary>
    [Hle("NID_B0E4CB2F", Nid = 0xB0E4CB2F, Library = "SceSslInternal")]
    void SslInternalSetSessionWord(uint ssl, uint value)
    {
        uint record = GuestMemory.R32(ssl + 0xD0);
        if (record != 0)
        {
            GuestMemory.W32(record + 0xBC, value);
        }
    }

    /// <summary>NID_F2C9B744 (SSL *ssl, value): the word at +0xE4.</summary>
    [Hle("NID_F2C9B744", Nid = 0xF2C9B744, Library = "SceSslInternal")]
    void SslInternalSetWordE4(uint ssl, uint value)
    {
        GuestMemory.W32(ssl + 0xE4, value);
    }

    /// <summary>NID_F71DB06F (SSL *ssl, a, b): +0xE8 = a, +0xEC = b.</summary>
    [Hle("NID_F71DB06F", Nid = 0xF71DB06F, Library = "SceSslInternal")]
    void SslInternalSetWordsE8(uint ssl, uint first, uint second)
    {
        GuestMemory.W32(ssl + 0xE8, first);
        GuestMemory.W32(ssl + 0xEC, second);
    }

    /// <summary>
    /// NID_CC7F4211 (SSL *ssl, int command, long value): the connection's control call. 0x2C swaps +0x20 (the old
    /// value back), 0x2D reads it, 0x37 reads +0x28, 0x38 swaps +0x94, 0x41 adds the value's bits to +0xD8 (the new
    /// bits back); any other command goes to the connection method table's control (+0x34 of the table at +0).
    /// </summary>
    [Hle("NID_CC7F4211", Nid = 0xCC7F4211, Library = "SceSslInternal")]
    void SslInternalControl(HleContext context)
    {
        uint ssl = context.Gpr(0);
        uint value = context.Gpr(2);
        uint old;
        switch ((int)context.Gpr(1))
        {
            case 0x2C:
                old = GuestMemory.R32(ssl + 0x20);
                GuestMemory.W32(ssl + 0x20, value);
                break;
            case 0x2D:
                old = GuestMemory.R32(ssl + 0x20);
                break;
            case 0x37:
                old = GuestMemory.R32(ssl + 0x28);
                break;
            case 0x38:
                old = GuestMemory.R32(ssl + 0x94);
                GuestMemory.W32(ssl + 0x94, value);
                break;
            case 0x41:
                old = GuestMemory.R32(ssl + 0xD8) | value;
                GuestMemory.W32(ssl + 0xD8, old);
                break;
            default:
                old = CallMethod(context, GuestMemory.R32(GuestMemory.R32(ssl) + 0x34));
                break;
        }

        context.Return(old);
    }

    /// <summary>NID_A4EA9DE0 (SSL *ssl): how many certificates the peer chain has: the session's (+0x24 set: the
    /// chain at +0x10C, else the one of the session at +0x100's +0x6C) or, for a TLS 1.x client (+0 version 3.x)
    /// without one, the handshake's (+0x54's +0x230); 0 without a chain.</summary>
    [Hle("NID_A4EA9DE0", Nid = 0xA4EA9DE0, Library = "SceSslInternal")]
    uint SslInternalPeerChainCount(uint ssl)
    {
        uint chain = PeerChain(ssl);
        return chain == 0 ? 0 : GuestMemory.R32(chain);
    }

    /// <summary>NID_243DE290 (SSL *ssl, int index, X509 **certificate): the peer chain's certificate at index (the
    /// chain's +4 array); 0x2718 without a chain or for an index outside it.</summary>
    [Hle("NID_243DE290", Nid = 0x243DE290, Library = "SceSslInternal")]
    int SslInternalPeerChainCertificate(uint ssl, int index, uint certificate)
    {
        uint chain = PeerChain(ssl);
        if (chain == 0 || index < 0 || index >= (int)GuestMemory.R32(chain))
        {
            return 0x2718;
        }

        GuestMemory.W32(certificate, GuestMemory.R32(GuestMemory.R32(chain + 4) + (uint)index * 4));
        return 0;
    }

    static uint PeerChain(uint ssl)
    {
        if (GuestMemory.R32(ssl + 0x24) != 0)
        {
            uint chain = GuestMemory.R32(ssl + 0x10C);
            return chain != 0 ? chain : GuestMemory.R32(GuestMemory.R32(ssl + 0x100) + 0x6C);
        }

        uint handshake = GuestMemory.R32(ssl + 0x54);
        if ((int)GuestMemory.R32(ssl) >> 8 != 3 || handshake == 0)
        {
            return 0;
        }

        return GuestMemory.R32(handshake + 0x230);
    }

    /// <summary>NID_4F781BB6 (SSL *ssl, ...): the read through the connection's method (+8's +0x18) with the caller's
    /// arguments, unless the peer's close notify arrived (+0x2C bit 1): then rwstate (+0x18) is SSL_NOTHING and 0.</summary>
    [Hle("NID_4F781BB6", Nid = 0x4F781BB6, Library = "SceSslInternal")]
    void SslInternalRead(HleContext context)
    {
        uint ssl = context.Gpr(0);
        if ((GuestMemory.R32(ssl + 0x2C) & 2) != 0)
        {
            GuestMemory.W32(ssl + 0x18, 1);
            context.Return(0);
            return;
        }

        context.Return(CallMethod(context, GuestMemory.R32(GuestMemory.R32(ssl + 8) + 0x18)));
    }

    /// <summary>NID_EC9FAF42 (SSL *ssl): the shutdown through the connection's method (+8's +0x24) when no handshake is
    /// in progress (+0x30 bits 12..13) or +0x114 bit 18 allows it, else -1; NULL: 1.</summary>
    [Hle("NID_EC9FAF42", Nid = 0xEC9FAF42, Library = "SceSslInternal")]
    void SslInternalShutdown(HleContext context)
    {
        uint ssl = context.Gpr(0);
        if (ssl == 0)
        {
            context.Return(1);
            return;
        }

        if ((GuestMemory.R32(ssl + 0x30) & 0x3000) != 0 && (GuestMemory.R32(ssl + 0x114) & 0x40000) == 0)
        {
            context.Return(0xFFFFFFFF);
            return;
        }

        context.Return(CallMethod(context, GuestMemory.R32(GuestMemory.R32(ssl + 8) + 0x24)));
    }

    /// <summary>NID_D776E2F8 (SSL *ssl, ...): the connection method's +0x40 with the caller's arguments.</summary>
    [Hle("NID_D776E2F8", Nid = 0xD776E2F8, Library = "SceSslInternal")]
    void SslInternalMethod40(HleContext context)
    {
        context.Return(CallMethod(context, GuestMemory.R32(GuestMemory.R32(context.Gpr(0) + 8) + 0x40)));
    }

    /// <summary>NID_E5EDD91B (SSL *ssl): the handshake. Without a handshake function (+0x20) -1 (libssl records
    /// SSL_R_CONNECTION_TYPE_NOT_SET in its error queue); else the method's +0x2C runs first, then, while a handshake
    /// is wanted (+0x30 bits 12..14), the handshake function; 1 when none is.</summary>
    [Hle("NID_E5EDD91B", Nid = 0xE5EDD91B, Library = "SceSslInternal")]
    int SslInternalDoHandshake(uint ssl)
    {
        uint handshake = GuestMemory.R32(ssl + 0x20);
        if (handshake == 0)
        {
            return -1;
        }

        GuestThread thread = GuestThread.Current!;
        thread.CallGuest(GuestMemory.R32(GuestMemory.R32(ssl + 8) + 0x2C), ssl);
        if ((GuestMemory.R32(ssl + 0x30) & 0x7000) == 0)
        {
            return 1;
        }

        return (int)thread.CallGuest(handshake, ssl);
    }

    /// <summary>NID_24C6257F (SSL *ssl, int what, uint *value): one of the connection's values: 1 +0x138, 2 and 4 0,
    /// 3 / 5 / 6 the session's (+0x100) +0xA8 / +0xAC / +0xB0, 7 +0x158; NULL ssl or value 0x2721, another what 0x271B.</summary>
    [Hle("NID_24C6257F", Nid = 0x24C6257F, Library = "SceSslInternal")]
    uint SslInternalConnectionValue(uint ssl, int what, uint value)
    {
        if (ssl == 0 || value == 0)
        {
            return 0x2721;
        }

        uint session = what is 3 or 5 or 6 ? GuestMemory.R32(ssl + 0x100) : 0;
        uint? answer = what switch
        {
            1 => GuestMemory.R32(ssl + 0x138),
            2 or 4 => 0,
            3 => GuestMemory.R32(session + 0xA8),
            5 => GuestMemory.R32(session + 0xAC),
            6 => GuestMemory.R32(session + 0xB0),
            7 => GuestMemory.R32(ssl + 0x158),
            _ => null,
        };
        if (answer is not { } word)
        {
            return 0x271B;
        }

        GuestMemory.W32(value, word);
        return 0;
    }

    /// <summary>A guest method with the call's arguments (r0..r3 and four stack words).</summary>
    static uint CallMethod(HleContext context, uint method)
    {
        return context.Thread.CallGuest(method, context.Gpr(0), context.Gpr(1), context.Gpr(2), context.Gpr(3), context.Gpr(4),
            context.Gpr(5), context.Gpr(6), context.Gpr(7));
    }

    /// <summary>NID_0C693D36 (SSL *ssl): +0xA4 of the record at +0xD0, 0 without a record.</summary>
    [Hle("NID_0C693D36", Nid = 0x0C693D36, Library = "SceSslInternal")]
    uint SslInternalSessionWordA4(uint ssl)
    {
        uint record = GuestMemory.R32(ssl + 0xD0);
        return record != 0 ? GuestMemory.R32(record + 0xA4) : 0;
    }

    /// <summary>NID_3EF42454 (object): its first word.</summary>
    [Hle("NID_3EF42454", Nid = 0x3EF42454, Library = "SceSslInternal")]
    uint SslInternalFirstWord(uint objectAddress)
    {
        return GuestMemory.R32(objectAddress);
    }

    /// <summary>NID_6F3B9809 (SSL *ssl): the connection's state word (+0x30).</summary>
    [Hle("NID_6F3B9809", Nid = 0x6F3B9809, Library = "SceSslInternal")]
    uint SslInternalStateWord(uint ssl)
    {
        return GuestMemory.R32(ssl + 0x30);
    }

    /// <summary>NID_B1745DAD (SSL *ssl): the word at +0x2C.</summary>
    [Hle("NID_B1745DAD", Nid = 0xB1745DAD, Library = "SceSslInternal")]
    uint SslInternalWord2C(uint ssl)
    {
        return GuestMemory.R32(ssl + 0x2C);
    }

    /// <summary>NID_D6D32A2F (SSL *ssl): the word at +0x18 (the read/write state SSL_get_error looks at).</summary>
    [Hle("NID_D6D32A2F", Nid = 0xD6D32A2F, Library = "SceSslInternal")]
    uint SslInternalWord18(uint ssl)
    {
        return GuestMemory.R32(ssl + 0x18);
    }

    /// <summary>
    /// NID_F4007DBE (SSL *ssl): a client (+0x24 0) with an SSL 3 / TLS version (+0 high byte 3) gives +0x230 of the
    /// record at +0x54 (0 without one); a server gives +0x10C, or when that is 0 +0x6C of the context at +0x100.
    /// </summary>
    [Hle("NID_F4007DBE", Nid = 0xF4007DBE, Library = "SceSslInternal")]
    uint SslInternalPeerWord(uint ssl)
    {
        if (GuestMemory.R32(ssl + 0x24) == 0)
        {
            uint record = GuestMemory.R32(ssl + 0x54);
            bool version3 = (int)GuestMemory.R32(ssl) >> 8 == 3;
            return version3 && record != 0 ? GuestMemory.R32(record + 0x230) : 0;
        }

        uint word = GuestMemory.R32(ssl + 0x10C);
        return word != 0 ? word : GuestMemory.R32(GuestMemory.R32(ssl + 0x100) + 0x6C);
    }

    /// <summary>libssl's "(NONE)" string in guest memory (made on first use).</summary>
    [SnapshotMissing]
    uint _noCipherName;

    /// <summary>NID_2B90012E (const SSL_CIPHER *cipher): its name (+4); "(NONE)" for a NULL cipher.</summary>
    [Hle("NID_2B90012E", Nid = 0x2B90012E, Library = "SceSslInternal")]
    uint SslInternalCipherName(uint cipher)
    {
        if (cipher != 0)
        {
            return GuestMemory.R32(cipher + 4);
        }

        lock (_libsslBlocks)
        {
            if (_noCipherName == 0)
            {
                uint name = Emu.Memory.Alloc(8, "libssl (NONE)", MemoryRegion.User, 4);
                GuestMemory.WriteCString(name, "(NONE)", 8);
                _noCipherName = name;
            }

            return _noCipherName;
        }
    }

    /// <summary>NID_422DFE70 (): drops the calling thread's entry of libssl's error-state table and returns its
    /// value; the HLE keeps no error queue, so no thread has an entry: 0.</summary>
    [Hle("NID_422DFE70", Nid = 0x422DFE70, Library = "SceSslInternal")]
    uint SslInternalRemoveErrorState()
    {
        return 0;
    }

    /// <summary>libssl's memory functions (malloc, realloc, free) a caller installed with NID_C1299FB6; 0 = its own
    /// heap.</summary>
    [SnapshotMissing]
    uint _libsslMalloc;

    [SnapshotMissing]
    uint _libsslRealloc;

    [SnapshotMissing]
    uint _libsslFree;

    /// <summary>NID_C1299FB6 (malloc, realloc, free): with all three non-NULL they become libssl's memory functions
    /// (its later allocations and frees run them); otherwise nothing changes.</summary>
    [Hle("NID_C1299FB6", Nid = 0xC1299FB6, Library = "SceSslInternal")]
    void SslInternalSetMemoryFunctions(uint malloc, uint realloc, uint free)
    {
        if (malloc == 0 || realloc == 0 || free == 0)
        {
            return;
        }

        lock (_libsslBlocks)
        {
            _libsslMalloc = malloc;
            _libsslRealloc = realloc;
            _libsslFree = free;
        }
    }
}
