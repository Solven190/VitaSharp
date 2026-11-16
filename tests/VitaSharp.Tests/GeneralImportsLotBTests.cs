using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Exports without public names of the General group in SceHttp and SceNetInternal, each as its FW 3.74 module
/// answers (libhttp, libnet read in their code).
/// </summary>
[Collection("guest")]
public sealed class GeneralImportsLotBTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "gen-lotb");

    public void Dispose()
    {
        _harness.Dispose();
    }

    public static TheoryData<uint, bool> Entries()
    {
        TheoryData<uint, bool> data = [];
        foreach (uint nid in new uint[]
        {
            0x30F5D11F, 0xC498765A, 0x175685B8, 0x0B0D8E7A, 0x88116DB8, 0xB0E41827, 0xFE60380A, 0xE0825488, 0x11C9BCE8, 0xF8885CFD,
            0x0CC59EF6, 0x031EBD2A, 0x802E3C30, 0x42108FBE, 0x65AFF9BE, 0xE2F6CAEA, 0x873DE778, 0x0F83B7A9, 0xFE8C89AE, 0x7FD92AE7,
            0xF9E3A8B7, 0x853EEABE, 0xA64CFBF5, 0x4054D2D8, 0x97527BDB,
        })
        {
            data.Add(nid, false);
        }

        foreach (uint nid in new uint[] { 0x54847CFE, 0x80E6013D, 0xAAA34850 })
        {
            data.Add(nid, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void TheLibrarysFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void HttpOptionsAndTheCertificateStore()
    {
        Http http = _harness.Module<Http>();
        uint value = _harness.Word(0xAAAA), info = _harness.Alloc(16), data = _harness.Alloc(16);
        Assert.Equal(unchecked((int)0x80431001), Call(http, "HttpGetOptionE", 1, value));
        Assert.Equal(0, Call(http, "sceHttpInit", 0x10000u));
        int template = Call(http, "sceHttpCreateTemplate", _harness.Str("ua"), 1, 0);
        Assert.Equal(unchecked((int)0x804311FE), Call(http, "HttpGetOptionE", template, 0u));
        Assert.Equal(0, Call(http, "sceHttpSetOptionE", template, 1));
        Assert.Equal(0, Call(http, "HttpGetOptionE", template, value));
        Assert.Equal(1u, GuestMemory.R32(value));
        Assert.Equal(unchecked((int)0x80431100), Call(http, "HttpSetOption15", template + 100, 1));
        Assert.Equal(0, Call(http, "HttpSetOption15", template, 1));
        Assert.Equal(unchecked((int)0x804311FE), Call(http, "HttpSetTemplateWordD4", template, 1));
        Assert.Equal(0, Call(http, "HttpSetTemplateWordD4", template, 3));
        Assert.Equal(unchecked((int)0x804311FE), Call(http, "HttpRequestInfo", 1, 0u));
        Assert.Equal(unchecked((int)0x80431100), Call(http, "HttpConnectionInfo", template, info));
        Assert.Equal(0, Call(http, "HttpDebugDump"));

        Assert.Equal(0, Call(http, "HttpCertStoreStart", 0x81000001u, 0x81000003u));
        Assert.Equal(0, Call(http, "HttpCertStoreAddCa", data, 16u));
        Assert.Equal(0, Call(http, "HttpCertStoreAddCa", data, 8u));
        Assert.Equal(0, Call(http, "HttpCertStoreSetClientCert", data, 16u));
        Assert.Equal(0, Call(http, "HttpCertStoreSetClientKey", data, 16u));
        Assert.Equal((2, true, true, true), ((int, bool, bool, bool))Invoke(http, "get_CertStore")!);
        Assert.Equal(0, Call(http, "HttpCertStoreClearCa"));
        Assert.Equal(0, Call(http, "HttpCertStoreClearClient"));
        Assert.Equal((0, false, false, true), ((int, bool, bool, bool))Invoke(http, "get_CertStore")!);
        Assert.Equal(0, Call(http, "HttpCertStoreEnd"));
        Assert.Equal(0, Call(http, "sceHttpTerm"));
    }

    [Fact]
    public void LibnetInternalsNeedTheLibraryAndReachTheKernel()
    {
        Net net = _harness.Module<Net>();
        const int NotInit = unchecked((int)0x804101C8), Invalid = unchecked((int)0x80410116), NoDev = unchecked((int)0x80410113);
        uint value = _harness.Alloc(8), sync = _harness.Alloc(8);
        Assert.Equal(NotInit, Call(net, "NetInternalDescriptorCreate", _harness.Str("d"), 0));
        Assert.Equal(NotInit, Call(net, "NetInternalSetWord", 1u));
        uint param = _harness.Alloc(12);
        GuestMemory.W32(param, _harness.Alloc(0x4000));
        GuestMemory.W32(param + 4, 0x4000);
        Assert.Equal(0, Call(net, "NetInternalInit", param, 0u, 0u, 0u));
        try
        {
            int descriptor = Call(net, "NetInternalDescriptorCreate", _harness.Str("d"), 0);
            Assert.True(descriptor > 0);
            Assert.Equal(0, Call(net, "NetInternalDescriptorCtl", descriptor, 1, 0u, 0));
            Assert.Equal(0, Call(net, "NetInternalDescriptorClose", descriptor));
            Assert.Equal(unchecked((int)0x80410109), Call(net, "NetInternalDescriptorClose", descriptor)); // EBADF
            Assert.Equal(Invalid, Call(net, "NetInternalSysctl", 0u, 0u, 0u, 0u, 0u, 0u));
            Assert.Equal(Invalid, Call(net, "NetInternalKernelIndex", -1));
            Assert.Equal(1, Call(net, "NetInternalKernelIndex", 0)); // lo0
            Assert.Equal(Invalid, Call(net, "NetInternalGetInterfaceValue", 0, 0u, 0));
            Assert.Equal(NoDev, Call(net, "NetInternalGetInterfaceValue", 1, value, 0));
            Assert.Equal(Invalid, Call(net, "NetInternalNeighbourEntry", value, 8u));
            Assert.Equal(unchecked((int)0x80410102), Call(net, "NetInternalNeighbourEntry", value, 1u));
            Assert.Equal(0, Call(net, "NetInternalSetWord", 7u));
            Assert.Equal(0, Call(net, "NetInternalSyncCreate", sync, 1u, _harness.Str("SceNetLibnetSync")));
            Assert.True((int)GuestMemory.R32(sync) > 0);
            Assert.Equal(0, Call(net, "NetInternalSyncDelete", sync));
        }
        finally
        {
            Call(net, "sceNetTerm");
        }
    }
}
