using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Exports without public names of the General group in SceNpUtility, each as FW 3.74's np_utility answers (read in
/// its code): the text and number helpers are exact, the request plumbing finds no requests.
/// </summary>
[Collection("guest")]
public sealed class GeneralImportsLotDTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "gen-lotd");

    public void Dispose()
    {
        _harness.Dispose();
    }

    public static TheoryData<uint, bool> Entries()
    {
        TheoryData<uint, bool> data = [];
        foreach (uint nid in new uint[]
        {
            0x4D52A224, 0xFF9F1571, 0x2E4D8BCF, 0xBDFC2D58, 0x475B35A8, 0x89484A9C, 0x8CA24D19, 0xE916C86A, 0x3A1BA624, 0x15D37FB1,
            0xB8DFA4D1, 0x72FC55A7, 0x2835BC9B, 0x450C80FB, 0x37AD71B9, 0x5D75C414, 0xAB499EA4, 0xA9D5542A, 0xF32ED701, 0xA3767D45,
            0x575BBBC4, 0xC01F7C6A,
        })
        {
            data.Add(nid, false);
        }

        foreach (uint nid in new uint[]
        {
            0x74B0D0AC, 0x191D314C, 0x04AA56CB, 0x2580CDAF, 0x67147C73, 0x839F805E, 0x15227842, 0xC02139E5, 0xE9544993, 0x6EB4C8FE,
            0x44FE1097, 0xF73C4411, 0x5631E596, 0x8904FE15, 0xF5086697, 0xD03605CD, 0xF32ED20E, 0xD5BB8464, 0xC63A0F6B, 0xB4423DAF,
            0x46543307, 0xA15150C9, 0x75FD803B, 0x200BBAC5, 0x23414285, 0x1392F18F, 0x4B30CAEF, 0x9EEDD7F8, 0xB418204E, 0x60D70B4A,
            0xDFFF438A, 0x8857763D, 0x8951DA6B, 0xC4FE3A57, 0xA48B44C9, 0x4FAC4566, 0x83445FED, 0x60CC5AFB,
        })
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
    public void NumbersAndOnlineIdsAsTheModuleWritesThem()
    {
        NpUtility u = _harness.Module<NpUtility>();
        uint buffer = _harness.Alloc(32);
        Assert.Equal(0, Call(u, "UtilFormatInt", buffer, -1234));
        Assert.Equal("-1234", GuestMemory.ReadCString(buffer));
        GuestMemory.Fill(buffer, 0, 32);
        Assert.Equal(0, Call(u, "UtilFormatUInt64", buffer, 0u, 18446744073709551615UL));
        Assert.Equal("18446744073709551615", GuestMemory.ReadCString(buffer));
        GuestMemory.Fill(buffer, 0, 32);
        Assert.Equal(0, Call(u, "UtilFormatInt64", buffer, 0u, 0L));
        Assert.Equal("0", GuestMemory.ReadCString(buffer));
        Assert.Equal(-42L, (long)Invoke(u, "UtilParseInt64", _harness.Str("-42x"))!);
        Assert.Equal(0L, (long)Invoke(u, "UtilParseInt64", 0u)!);
        Assert.Equal(1, Call(u, "UtilOnlineIdValid", _harness.Str("Alice_01")));
        Assert.Equal(0, Call(u, "UtilOnlineIdValid", _harness.Str("1lice")));
        Assert.Equal(0, Call(u, "UtilOnlineIdValid", _harness.Str("ab")));
        Assert.Equal(0, Call(u, "UtilOnlineIdValid", _harness.Str("Al ice")));
        Assert.Equal(-7, Call(u, "UtilMapHttpError", 0u, -7));
    }

    [Fact]
    public void TextsGoIntoNewHeapBlocks()
    {
        NpUtility u = _harness.Module<NpUtility>();
        uint output = _harness.Word(0), data = _harness.Str("abc");
        Assert.Equal(0, Call(u, "UtilBase64", output, data, 3u, 0));
        Assert.Equal("YWJj", GuestMemory.ReadCString(GuestMemory.R32(output)));
        Assert.Equal(0, Call(u, "UtilXmlUnescape", output, _harness.Str("a &amp; b"), 0));
        Assert.Equal("a & b", GuestMemory.ReadCString(GuestMemory.R32(output)));
        Assert.Equal(0, Call(u, "UtilXmlEscape", output, _harness.Str("a < b"), 0));
        Assert.Equal("a &lt; b", GuestMemory.ReadCString(GuestMemory.R32(output)));
        Assert.Equal(0, Call(u, "UtilXmlEscape", output, _harness.Str(""), 0));
        Assert.Equal("", GuestMemory.ReadCString(GuestMemory.R32(output)));
    }

    [Fact]
    public void RecordsFlagsAndWorkers()
    {
        NpUtility u = _harness.Module<NpUtility>();
        uint record = _harness.Alloc(0x40), worker = _harness.Alloc(0x28);
        Assert.Equal(unchecked((int)0x80550708), Call(u, "UtilFlag5D75C414", record));
        GuestMemory.W32(record + 4, 4);
        Assert.Equal(0, Call(u, "UtilFlag5D75C414", record));
        GuestMemory.W32(record + 0x10, 0x408);
        Assert.Equal(0, Call(u, "UtilFlagA9D5542A", record));
        Assert.Equal(0, Call(u, "UtilFlagF32ED701", record));
        Assert.Equal(unchecked((int)0x80550708), Call(u, "UtilFlagA3767D45", record));
        Assert.Equal(unchecked((int)0x80550601), Call(u, "UtilWorkerSetWords", 0u, 1u, 2u));
        Assert.Equal(0, Call(u, "UtilWorkerSetWords", record, 1u, 2u));
        Assert.Equal(2u, GuestMemory.R32(record + 0x20));

        GuestMemory.Fill(worker, 0xFF, 0x28); // as a failed set-up leaves it
        Assert.Equal(unchecked((int)0x80550601), Call(u, "UtilWorkerStart", worker));
        Assert.Equal(unchecked((int)0x8055060E), Call(u, "UtilWorkerCreate", worker, 0x10000100, 0u, 0, 4u, 0u, 0u));
        Assert.Equal(0, Call(u, "UtilWorkerCreate", worker, 0x10000100, 0u, 0, 4u, 0x81000001u, 0x81000003u));
        Assert.Equal(0, Call(u, "UtilWorkerStart", worker));
        Assert.Equal(0, Call(u, "UtilWorkerQueue", worker, 1u, 2u, 3u, 4u));
        Assert.Equal(0, Call(u, "UtilWorkerStop", worker));
        Assert.Equal(unchecked((int)0x80550601), Call(u, "UtilWorkerStop", worker));

        Assert.Equal(unchecked((int)0x80550782), Call(u, "UtilParser15227842", _harness.Alloc(0x548), 3, 0u, _harness.Alloc(0x20)));
        Assert.Equal(unchecked((int)0x8055070E), Call(u, "UtilRequestState", 0x200001u, record));
        Assert.Equal(unchecked((int)0x8055070E), Call(u, "UtilDeleteRequestOn", 1u, 3u));
    }
}
