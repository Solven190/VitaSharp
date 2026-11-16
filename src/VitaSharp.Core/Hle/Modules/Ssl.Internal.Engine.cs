using VitaSharp.Core.Memory;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// SceSslInternal's TLS engine and certificate calls of libssl.suprx 3.74 (read in its code, study only). The HLE's
// sceSslInit starts no libssl engine (its TLS runs through the host, see Http.Real.cs), so the engine's objects (method
// tables, contexts, connections, BIOs on its socket and memory methods) and its certificate and digest work do not
// exist here. Each call keeps the 3.74 argument checks and then answers the way the firmware does when the service is
// not there: a NULL object, the library's "not supported" code (0x271B) or SceSsl's internal error (0x80435026); a
// certificate is never reported as matching or valid. The calls are marked default and listed in the user's report of
// DRM, licence and security defaults.
public sealed partial class Ssl
{
    const int SSL_ERR_INTERNAL = unchecked((int)0x80435026);

    static void EngineUnavailable(string what)
    {
        Log.InfoOnce("ssl-engine-" + what, $"SceSslInternal: {what}: no libssl TLS engine in the HLE (service unavailable, default)");
    }

    /// <summary>NID_A374CD7B / NID_AC1DBE40 (): libssl's TLS client method tables (static, filled on first use with the
    /// engine's functions); NULL here, so a context cannot be made from them.</summary>
    [Hle("NID_A374CD7B", Nid = 0xA374CD7B, Library = "SceSslInternal", Default = true)]
    uint SslInternalTlsClientMethod()
    {
        EngineUnavailable("TLS client method");
        return 0;
    }

    [Hle("NID_AC1DBE40", Nid = 0xAC1DBE40, Library = "SceSslInternal", Default = true)]
    uint SslInternalSslClientMethod()
    {
        EngineUnavailable("SSL client method");
        return 0;
    }

    /// <summary>NID_E746465E SSL_CTX_new(method, -): NULL method or second argument fails (NULL) as in 3.74; otherwise
    /// the context needs the engine: NULL.</summary>
    [Hle("NID_E746465E", Nid = 0xE746465E, Library = "SceSslInternal", Default = true)]
    uint SslInternalNewContext(uint method, uint second)
    {
        EngineUnavailable("SSL_CTX_new");
        return 0;
    }

    /// <summary>NID_D28834E6 SSL_new(SSL_CTX *context): NULL for a NULL context or one without a method, as in 3.74;
    /// a connection needs the engine: NULL.</summary>
    [Hle("NID_D28834E6", Nid = 0xD28834E6, Library = "SceSslInternal", Default = true)]
    uint SslInternalNewConnection(uint context)
    {
        EngineUnavailable("SSL_new");
        return 0;
    }

    /// <summary>NID_19B2BB58 SSL_free / NID_EF9EBAC7 SSL_CTX_free: only engine objects can be freed, and the HLE makes
    /// none: nothing to do.</summary>
    [Hle("NID_19B2BB58", Nid = 0x19B2BB58, Library = "SceSslInternal", Default = true)]
    void SslInternalFreeConnection(uint ssl)
    {
        EngineUnavailable("SSL_free");
    }

    [Hle("NID_EF9EBAC7", Nid = 0xEF9EBAC7, Library = "SceSslInternal", Default = true)]
    void SslInternalFreeContext(uint context)
    {
        EngineUnavailable("SSL_CTX_free");
    }

    /// <summary>NID_CEE46B33 SSL_set_connect_state(SSL *ssl): +0x24 = 0 (client); the handshake function (+0x20) would
    /// be the engine's connect, which does not exist: it stays unset, so the handshake fails (-1).</summary>
    [Hle("NID_CEE46B33", Nid = 0xCEE46B33, Library = "SceSslInternal", Default = true)]
    void SslInternalSetConnectState(uint ssl)
    {
        EngineUnavailable("SSL_set_connect_state");
        GuestMemory.W32(ssl + 0x24, 0);
    }

    /// <summary>NID_B8E6E249 / NID_FFE2ED51 (...): a BIO on the engine's socket / memory method: NULL.</summary>
    [Hle("NID_B8E6E249", Nid = 0xB8E6E249, Library = "SceSslInternal", Default = true)]
    uint SslInternalNewSocketBio()
    {
        EngineUnavailable("BIO_new (socket)");
        return 0;
    }

    [Hle("NID_FFE2ED51", Nid = 0xFFE2ED51, Library = "SceSslInternal", Default = true)]
    uint SslInternalNewMemoryBio(uint data, uint length)
    {
        EngineUnavailable("BIO_new (memory)");
        return 0;
    }

    /// <summary>NID_2185BB05 (SSL_CTX **context): the library's global context sceSslInit made; before sceSslInit
    /// 0x80435001, after it the engine's context does not exist: SceSsl's internal error.</summary>
    [Hle("NID_2185BB05", Nid = 0x2185BB05, Library = "SceSslInternal", Default = true)]
    int SslInternalGlobalContext(uint context)
    {
        if (!_inited)
        {
            return ERR_BEFORE_INIT;
        }

        EngineUnavailable("global context");
        return SSL_ERR_INTERNAL;
    }

    /// <summary>NID_08179EBD / NID_81FA5DBE (certificate, value, uint *out): the certificate's field 7 / 4 (subject /
    /// issuer data through its key and digest objects): NULL certificate or out 0x2721; the certificate service is
    /// not there: 0x271B.</summary>
    [Hle("NID_08179EBD", Nid = 0x08179EBD, Library = "SceSslInternal", Default = true)]
    uint SslInternalCertificateField7(uint certificate, uint value, uint output)
    {
        return CertificateCall(certificate != 0 && output != 0, "certificate field 7");
    }

    [Hle("NID_81FA5DBE", Nid = 0x81FA5DBE, Library = "SceSslInternal", Default = true)]
    uint SslInternalCertificateField4(uint certificate, uint value, uint output)
    {
        return CertificateCall(certificate != 0 && output != 0, "certificate field 4");
    }

    /// <summary>NID_2074A6EB / NID_2464F9D0 (certificate, out): the certificate's name 6 / 5 copied out; NULL 0x2721;
    /// 0x271B.</summary>
    [Hle("NID_2074A6EB", Nid = 0x2074A6EB, Library = "SceSslInternal", Default = true)]
    uint SslInternalCertificateName6(uint certificate, uint output)
    {
        return CertificateCall(certificate != 0 && output != 0, "certificate name 6");
    }

    [Hle("NID_2464F9D0", Nid = 0x2464F9D0, Library = "SceSslInternal", Default = true)]
    uint SslInternalCertificateName5(uint certificate, uint output)
    {
        return CertificateCall(certificate != 0 && output != 0, "certificate name 5");
    }

    /// <summary>
    /// NID_08ED1B25 (certificate, -, int algorithm, -, uint *size): the certificate's fingerprint. NULL certificate or
    /// size 0x2721; the algorithm's digest size (0xA4 64, 0xA3 48, 0xA2 32, 0x40 20, 4 and 3 16 bytes; another 0x2722)
    /// is stored first; with no digest buffer that is all (0). Computing the digest is the certificate service: 0x271B.
    /// </summary>
    [Hle("NID_08ED1B25", Nid = 0x08ED1B25, Library = "SceSslInternal", Default = true)]
    uint SslInternalCertificateFingerprint(uint certificate, uint unused, int algorithm, uint buffer, uint size)
    {
        if (certificate == 0 || size == 0)
        {
            return MesInvalidArgument;
        }

        uint length = algorithm switch
        {
            0xA4 => 0x40,
            0xA3 => 0x30,
            0xA2 => 0x20,
            0x40 => 0x14,
            4 or 3 => 0x10,
            _ => 0,
        };
        if (length == 0)
        {
            return 0x2722;
        }

        GuestMemory.W32(size, length);
        if (buffer == 0)
        {
            return 0;
        }

        return CertificateCall(true, "certificate fingerprint");
    }

    /// <summary>NID_568F0E80 (certificate a, certificate b): whether both are the same certificate. Two NULLs are; one
    /// NULL is not; comparing two certificates' encodings is the certificate service, so they are never reported
    /// equal (false).</summary>
    [Hle("NID_568F0E80", Nid = 0x568F0E80, Library = "SceSslInternal", Default = true)]
    bool SslInternalCertificatesEqual(uint first, uint second)
    {
        if (first == 0 && second == 0)
        {
            return true;
        }

        if (first != 0 && second != 0)
        {
            EngineUnavailable("certificate comparison");
        }

        return false;
    }

    /// <summary>NID_D68D8421 (certificate, name): whether the certificate's subject matches the name; never reported
    /// as matching (false).</summary>
    [Hle("NID_D68D8421", Nid = 0xD68D8421, Library = "SceSslInternal", Default = true)]
    bool SslInternalCertificateMatchesName(uint certificate, uint name)
    {
        if (certificate != 0 && name != 0)
        {
            EngineUnavailable("certificate name check");
        }

        return false;
    }

    /// <summary>NID_D3C049B9 (certificate, a, b): a certificate extension handler looked up through the engine's
    /// tables; 0x271B.</summary>
    [Hle("NID_D3C049B9", Nid = 0xD3C049B9, Library = "SceSslInternal", Default = true)]
    uint SslInternalCertificateExtension(uint certificate, uint first, uint second)
    {
        return CertificateCall(true, "certificate extension");
    }

    /// <summary>NID_349B0E73 (int type, uint flags, const void **data, uint *length): the certificate data libssl keeps
    /// for a type (only type 5 with flag bit 2 has some); NULL outputs 0x804351FE; here none: NULL, 0 and 0.</summary>
    [Hle("NID_349B0E73", Nid = 0x349B0E73, Library = "SceSslInternal", Default = true)]
    int SslInternalCertificateTypeData(int type, uint flags, uint data, uint length)
    {
        if (data == 0 || length == 0)
        {
            return ERR_INVALID_VALUE;
        }

        EngineUnavailable("certificate type data");
        GuestMemory.W32(data, 0);
        GuestMemory.W32(length, 0);
        return 0;
    }

    /// <summary>NID_3746501F (certificate, void **out, int wanted, -, uint *outLength, int kind): the certificate's
    /// signature algorithm entry from libssl's table; the outputs cleared, SceSsl's internal error.</summary>
    [Hle("NID_3746501F", Nid = 0x3746501F, Library = "SceSslInternal", Default = true)]
    int SslInternalCertificateAlgorithm(uint certificate, uint output, int wanted, uint unused, uint outputLength, int kind)
    {
        if (output != 0 && wanted != 0)
        {
            GuestMemory.W32(output, 0);
        }

        if (outputLength != 0)
        {
            GuestMemory.W32(outputLength, 0);
        }

        EngineUnavailable("certificate algorithm");
        return SSL_ERR_INTERNAL;
    }

    /// <summary>NID_3DE4F3DB (object, int length, uint flags, -): NULL 0x2721, a negative length 0x2722; the MES buffer
    /// and digest work: 0x271B.</summary>
    [Hle("NID_3DE4F3DB", Nid = 0x3DE4F3DB, Library = "SceSslInternal", Default = true)]
    uint SslInternalDigestUpdate(uint objectAddress, int length, uint flags, uint unused)
    {
        if (objectAddress == 0)
        {
            return MesInvalidArgument;
        }

        return length < 0 ? 0x2722 : CertificateCall(true, "MES digest");
    }

    /// <summary>NID_4D80D3F7 (object, data, int length, -): NULL 0x2721; a length 0 clears the object's buffer (+4, +8,
    /// +0xC bit 1); copying data in is the MES key/digest service: 0x271B.</summary>
    [Hle("NID_4D80D3F7", Nid = 0x4D80D3F7, Library = "SceSslInternal", Default = true)]
    uint SslInternalSetKeyData(uint objectAddress, uint data, int length, uint unused)
    {
        if (objectAddress == 0)
        {
            return MesInvalidArgument;
        }

        if (length == 0)
        {
            GuestMemory.W32(objectAddress + 4, 0);
            GuestMemory.W32(objectAddress + 8, 0);
            GuestMemory.W32(objectAddress + 0xC, GuestMemory.R32(objectAddress + 0xC) & ~2u);
            return 0;
        }

        return CertificateCall(true, "MES key data");
    }

    /// <summary>NID_93EAE08A (object, int attribute, uint *out): NULL object or out 0x2721; an attribute 1..3 0x2722;
    /// the key/certificate attribute query: 0x271B.</summary>
    [Hle("NID_93EAE08A", Nid = 0x93EAE08A, Library = "SceSslInternal", Default = true)]
    uint SslInternalKeyAttribute(uint objectAddress, int attribute, uint output)
    {
        if (output == 0 || objectAddress == 0)
        {
            return MesInvalidArgument;
        }

        return attribute is > 0 and < 4 ? 0x2722 : CertificateCall(true, "MES key attribute");
    }

    /// <summary>NID_7D30F0A6 (context, int *out, void **certificate, -, uint flags): parses a certificate into a MES
    /// object; NULL context, out or certificate 0x2721; 0x271B.</summary>
    [Hle("NID_7D30F0A6", Nid = 0x7D30F0A6, Library = "SceSslInternal", Default = true)]
    uint SslInternalParseCertificate(uint context, uint output, uint certificate, uint unused, uint flags)
    {
        return CertificateCall(output != 0 && certificate != 0 && context != 0, "certificate parse");
    }

    static uint CertificateCall(bool argumentsValid, string what)
    {
        if (!argumentsValid)
        {
            return MesInvalidArgument;
        }

        EngineUnavailable(what);
        return MesNotSupported;
    }

    /// <summary>NID_133ED36D / NID_FB51AE89 (): another libssl method table and the MES library's resource list, both
    /// static data its engine fills; NULL here (no engine).</summary>
    [Hle("NID_133ED36D", Nid = 0x133ED36D, Library = "SceSslInternal", Default = true)]
    uint SslInternalMethodTable()
    {
        EngineUnavailable("method table");
        return 0;
    }

    [Hle("NID_FB51AE89", Nid = 0xFB51AE89, Library = "SceSslInternal", Default = true)]
    uint SslInternalResourceList()
    {
        EngineUnavailable("resource list");
        return 0;
    }

    /// <summary>NID_DEC6DD27 (): tears down the engine's subsystems (cipher, digest, certificate and error tables);
    /// the HLE started none: nothing to do.</summary>
    [Hle("NID_DEC6DD27", Nid = 0xDEC6DD27, Library = "SceSslInternal", Default = true)]
    void SslInternalCleanup()
    {
        EngineUnavailable("cleanup");
    }

    /// <summary>NID_09657E15 (resource list, -, context **out): a MES library context (0x19C bytes) built by each
    /// resource's init callback; NULL list or out 0x2721. The resources are the engine's: 0x271B, out untouched.</summary>
    [Hle("NID_09657E15", Nid = 0x09657E15, Library = "SceSslInternal", Default = true)]
    uint SslInternalNewLibraryContext(uint resources, uint unused, uint output)
    {
        return CertificateCall(resources != 0 && output != 0, "library context");
    }

    /// <summary>NID_4725F9E7 (library context): drops one reference (+0x194); at the context's base count (+0x198)
    /// its resources (none without the engine) and the block go back. NULL is nothing; always 0.</summary>
    [Hle("NID_4725F9E7", Nid = 0x4725F9E7, Library = "SceSslInternal")]
    uint SslInternalFreeLibraryContext(uint libraryContext)
    {
        if (libraryContext == 0)
        {
            return 0;
        }

        uint references = GuestMemory.R32(libraryContext + 0x194) - 1;
        GuestMemory.W32(libraryContext + 0x194, references);
        if (references == GuestMemory.R32(libraryContext + 0x198))
        {
            LibsslFree(libraryContext);
        }

        return 0;
    }

    /// <summary>NID_00ABEBC3 (SSL_CTX *context, certificate): adds a certificate to the context's store (+0x6C) after
    /// reading its subject; NULL context or certificate 0; the store and the certificate service are the engine's:
    /// 0 (not added).</summary>
    [Hle("NID_00ABEBC3", Nid = 0x00ABEBC3, Library = "SceSslInternal", Default = true)]
    uint SslInternalAddStoreCertificate(uint context, uint certificate)
    {
        if (context != 0 && certificate != 0)
        {
            EngineUnavailable("add certificate to store");
        }

        return 0;
    }

    /// <summary>NID_05F6A292 (SSL_CTX *context, key, int type): installs a key of type 0..2; NULL context or key 0, a
    /// type outside 0..2 0x2722; the key service is the engine's: 0 (not installed).</summary>
    [Hle("NID_05F6A292", Nid = 0x05F6A292, Library = "SceSslInternal", Default = true)]
    uint SslInternalUseKey(uint context, uint key, int type)
    {
        if (context == 0 || key == 0)
        {
            return 0;
        }

        if (type is < 0 or > 2)
        {
            return 0x2722;
        }

        EngineUnavailable("use key");
        return 0;
    }

    /// <summary>
    /// NID_26CBEEB6 / NID_D419451B (SSL *ssl, certificate / key): the connection's certificate record (+0x4C, a 0x68-byte
    /// block of libssl's heap { +4 = its +0x24, +0x64 = 1 }) is made when missing, then the certificate / key is
    /// installed in it through the certificate service. NID_26CBEEB6 refuses a NULL certificate first (0). The
    /// service is the engine's: 0 (not installed).
    /// </summary>
    [Hle("NID_26CBEEB6", Nid = 0x26CBEEB6, Library = "SceSslInternal", Default = true)]
    uint SslInternalUseCertificate(uint ssl, uint certificate)
    {
        if (certificate == 0)
        {
            return 0;
        }

        return CertificateRecordCall(ssl, "use certificate");
    }

    [Hle("NID_D419451B", Nid = 0xD419451B, Library = "SceSslInternal", Default = true)]
    uint SslInternalUsePrivateKey(uint ssl, uint key)
    {
        return CertificateRecordCall(ssl, "use private key");
    }

    uint CertificateRecordCall(uint ssl, string what)
    {
        if (GuestMemory.R32(ssl + 0x4C) == 0)
        {
            uint record = LibsslAlloc(0x68);
            if (record == 0)
            {
                return 0;
            }

            GuestMemory.W32(record + 4, record + 0x24);
            GuestMemory.W32(record + 0x64, 1);
            GuestMemory.W32(ssl + 0x4C, record);
        }

        EngineUnavailable(what);
        return 0;
    }

    /// <summary>NID_6046C1F3 (SSL_CTX *context, const char *list): sets the cipher list from the engine's cipher
    /// table; NULL list false; no engine, so no cipher is ever chosen: false.</summary>
    [Hle("NID_6046C1F3", Nid = 0x6046C1F3, Library = "SceSslInternal", Default = true)]
    bool SslInternalSetCipherList(uint context, uint list)
    {
        if (list != 0)
        {
            EngineUnavailable("cipher list");
        }

        return false;
    }

    /// <summary>
    /// NID_6D0D3B11 (object, BIO *bio, int type, int format, out): reads a certificate from a BIO (format 0 DER, 0x100
    /// base64, 0x1100 PEM "CERTIFICATE" / "WTLS CERTIFICATE") into the object; NULL object, BIO or out 0x2721,
    /// another format 0x271A; the decoding is the certificate service: 0x271B.
    /// </summary>
    [Hle("NID_6D0D3B11", Nid = 0x6D0D3B11, Library = "SceSslInternal", Default = true)]
    uint SslInternalReadCertificate(uint objectAddress, uint bio, int type, int format, uint output)
    {
        if (objectAddress == 0 || output == 0 || bio == 0)
        {
            return MesInvalidArgument;
        }

        return format is 0 or 0x100 or 0x1100 ? CertificateCall(true, "certificate read") : 0x271A;
    }

    /// <summary>
    /// NID_CBEC7D13 (connection, int command, argument): NULL connection 0x2721, a NULL argument 0x2721 for any command
    /// but 7. Command 2 is 0; command 1 with that library context already set (+0xA4) is 0; setting a new library
    /// context (1), its query (7) and the session import (0xC) are the engine's: 0x271B; another command 0x271B.
    /// </summary>
    [Hle("NID_CBEC7D13", Nid = 0xCBEC7D13, Library = "SceSslInternal", Default = true)]
    uint SslInternalConnectionControl(uint connection, int command, uint argument)
    {
        if (connection == 0 || (argument == 0 && command != 7))
        {
            return MesInvalidArgument;
        }

        if (command == 2 || (command == 1 && argument == GuestMemory.R32(connection + 0xA4)))
        {
            return 0;
        }

        if (command is 1 or 7 or 0xC)
        {
            EngineUnavailable("control " + command);
        }

        return MesNotSupported;
    }
}
