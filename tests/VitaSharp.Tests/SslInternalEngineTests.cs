using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// SceSslInternal's TLS engine and certificate calls: the HLE has no libssl engine, so after the 3.74 argument checks
/// each answers like the firmware without the service (NULL objects, 0x271B, SceSsl's internal error), and a
/// certificate is never reported as equal, matching or valid. Found by name so the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class SslInternalEngineTests(GuestMemoryFixture fixture) : IDisposable
{
    private const uint InvalidArgument = 0x2721;
    private const uint NotSupported = 0x271B;

    private readonly HleHarness _harness = new(fixture, "ssl-internal-engine");

    public void Dispose() => _harness.Dispose();

    private Ssl SslModule => _harness.Module<Ssl>();

    private uint Word(string name, params object?[] args) => Convert.ToUInt32(Invoke(SslModule, name, args));

    [Theory]
    [InlineData(0xA374CD7Bu)]
    [InlineData(0xAC1DBE40u)]
    [InlineData(0xE746465Eu)]
    [InlineData(0xD28834E6u)]
    [InlineData(0x19B2BB58u)]
    [InlineData(0xEF9EBAC7u)]
    [InlineData(0xCEE46B33u)]
    [InlineData(0xB8E6E249u)]
    [InlineData(0xFFE2ED51u)]
    [InlineData(0x2185BB05u)]
    [InlineData(0x08179EBDu)]
    [InlineData(0x81FA5DBEu)]
    [InlineData(0x2074A6EBu)]
    [InlineData(0x2464F9D0u)]
    [InlineData(0x08ED1B25u)]
    [InlineData(0x568F0E80u)]
    [InlineData(0xD68D8421u)]
    [InlineData(0xD3C049B9u)]
    [InlineData(0x349B0E73u)]
    [InlineData(0x3746501Fu)]
    [InlineData(0x3DE4F3DBu)]
    [InlineData(0x4D80D3F7u)]
    [InlineData(0x93EAE08Au)]
    [InlineData(0x7D30F0A6u)]
    public void TheEngineCallsHaveADefaultEntry(uint nid)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.True(b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void NoEngineObjectIsEverMade()
    {
        uint context = _harness.Alloc(0x100);
        Assert.Equal(0u, Word("SslInternalTlsClientMethod"));
        Assert.Equal(0u, Word("SslInternalSslClientMethod"));
        Assert.Equal(0u, Word("SslInternalNewContext", 0x81000000u, 1u));
        Assert.Equal(0u, Word("SslInternalNewConnection", context));
        Assert.Equal(0u, Word("SslInternalNewSocketBio"));
        Assert.Equal(0u, Word("SslInternalNewMemoryBio", context, 4u));
        Invoke(SslModule, "SslInternalFreeConnection", context);
        Invoke(SslModule, "SslInternalFreeContext", context);
        GuestMemory.W32(context + 0x24, 1);
        Invoke(SslModule, "SslInternalSetConnectState", context);
        Assert.Equal(0u, GuestMemory.R32(context + 0x24)); // a client now
        Assert.Equal(0u, GuestMemory.R32(context + 0x20)); // without the engine's connect

        uint output = _harness.Word(0);
        Assert.Equal(unchecked((int)0x80435001), Call(SslModule, "SslInternalGlobalContext", output));
        Assert.Equal(0, Call(SslModule, "sceSslInit", 0x10000u));
        Assert.Equal(unchecked((int)0x80435026), Call(SslModule, "SslInternalGlobalContext", output));
        Assert.Equal(0u, GuestMemory.R32(output));
    }

    [Fact]
    public void CertificatesAreNeverReportedEqualMatchingOrRead()
    {
        uint certificate = _harness.Alloc(0x40);
        uint other = _harness.Alloc(0x40);
        uint output = _harness.Word(0);
        Assert.True(Convert.ToBoolean(Invoke(SslModule, "SslInternalCertificatesEqual", 0u, 0u)));
        Assert.False(Convert.ToBoolean(Invoke(SslModule, "SslInternalCertificatesEqual", certificate, 0u)));
        Assert.False(Convert.ToBoolean(Invoke(SslModule, "SslInternalCertificatesEqual", certificate, certificate)));
        Assert.False(Convert.ToBoolean(Invoke(SslModule, "SslInternalCertificatesEqual", certificate, other)));
        Assert.False(Convert.ToBoolean(Invoke(SslModule, "SslInternalCertificateMatchesName", certificate, _harness.Str("example.com"))));

        Assert.Equal(InvalidArgument, Word("SslInternalCertificateField7", 0u, 0u, output));
        Assert.Equal(NotSupported, Word("SslInternalCertificateField7", certificate, 0u, output));
        Assert.Equal(NotSupported, Word("SslInternalCertificateField4", certificate, 0u, output));
        Assert.Equal(InvalidArgument, Word("SslInternalCertificateName6", certificate, 0u));
        Assert.Equal(NotSupported, Word("SslInternalCertificateName6", certificate, output));
        Assert.Equal(NotSupported, Word("SslInternalCertificateName5", certificate, output));
        Assert.Equal(NotSupported, Word("SslInternalCertificateExtension", certificate, 0u, 0u));
        Assert.Equal(InvalidArgument, Word("SslInternalParseCertificate", certificate, 0u, output, 0u, 0u));
        Assert.Equal(NotSupported, Word("SslInternalParseCertificate", certificate, output, output, 0u, 0u));
    }

    [Fact]
    public void TheFingerprintSizeIsKnownButNotTheDigest()
    {
        uint certificate = _harness.Alloc(0x40);
        uint size = _harness.Word(0);
        uint buffer = _harness.Alloc(0x40);
        Assert.Equal(InvalidArgument, Word("SslInternalCertificateFingerprint", certificate, 0u, 0xA2, buffer, 0u));
        Assert.Equal(0x2722u, Word("SslInternalCertificateFingerprint", certificate, 0u, 0x99, buffer, size));
        Assert.Equal(0u, Word("SslInternalCertificateFingerprint", certificate, 0u, 0xA4, 0u, size)); // size only
        Assert.Equal(0x40u, GuestMemory.R32(size));
        Assert.Equal(NotSupported, Word("SslInternalCertificateFingerprint", certificate, 0u, 0x40, buffer, size));
        Assert.Equal(0x14u, GuestMemory.R32(size));
        Assert.Equal(0u, GuestMemory.R32(buffer));
    }

    [Fact]
    public void TheMesKeyAndDataCallsCheckThenAnswerNotSupported()
    {
        uint key = _harness.Alloc(0x20);
        uint output = _harness.Word(0);
        GuestMemory.W32(key + 4, 1);
        GuestMemory.W32(key + 0xC, 3);
        Assert.Equal(InvalidArgument, Word("SslInternalSetKeyData", 0u, 0u, 0, 0u));
        Assert.Equal(0u, Word("SslInternalSetKeyData", key, 0u, 0, 0u)); // length 0 clears the buffer
        Assert.Equal(0u, GuestMemory.R32(key + 4));
        Assert.Equal(1u, GuestMemory.R32(key + 0xC));
        Assert.Equal(NotSupported, Word("SslInternalSetKeyData", key, output, 4, 0u));
        Assert.Equal(InvalidArgument, Word("SslInternalDigestUpdate", 0u, 1, 0u, 0u));
        Assert.Equal(0x2722u, Word("SslInternalDigestUpdate", key, -1, 0u, 0u));
        Assert.Equal(NotSupported, Word("SslInternalDigestUpdate", key, 4, 0u, 0u));
        Assert.Equal(InvalidArgument, Word("SslInternalKeyAttribute", key, 5, 0u));
        Assert.Equal(0x2722u, Word("SslInternalKeyAttribute", key, 2, output));
        Assert.Equal(NotSupported, Word("SslInternalKeyAttribute", key, 0x10, output));

        uint data = _harness.Word(0xAAAAAAAA);
        uint length = _harness.Word(0xAAAAAAAA);
        Assert.Equal(unchecked((int)0x804351FE), Call(SslModule, "SslInternalCertificateTypeData", 5, 4u, 0u, length));
        Assert.Equal(0, Call(SslModule, "SslInternalCertificateTypeData", 5, 4u, data, length));
        Assert.Equal(0u, GuestMemory.R32(data));
        Assert.Equal(0u, GuestMemory.R32(length));
        GuestMemory.W32(data, 0xAAAAAAAA);
        Assert.Equal(unchecked((int)0x80435026), Call(SslModule, "SslInternalCertificateAlgorithm", key, data, 1, 0u, length, 0));
        Assert.Equal(0u, GuestMemory.R32(data));
    }
}
