using System.Reflection;
using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// System-imported functions without public names of SceShell, SceNpUtility and the XML reader of SceNpCommon, each as
/// its FW 3.74 module answers (shell.self, np_utility, np_common read in their code).
/// </summary>
[Collection("guest")]
public sealed class SystemImportsLot5Tests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "sys-lot5");
    private readonly PsnAccount _savedAccount = PsnAccount.Current;

    public void Dispose()
    {
        PsnAccount.Current = _savedAccount;
        _harness.Dispose();
    }

    public static TheoryData<uint, bool> Entries()
    {
        TheoryData<uint, bool> data = [];
        foreach (uint nid in new uint[]
        {
            0x0025417F, 0x05D6FCB0, 0x29A14252, 0x2E0B6FA6, 0x3059EEBC, 0x36788D70, 0x3E737FBE, 0x3F41E440, 0x42053414,
            0x449348B6, 0x548A3111, 0x5DC4A753, 0x60129092, 0x6A6123E3, 0x77642C92, 0x7FE4E732, 0x83D16CA8, 0x8858B876,
            0x8C1D932A, 0xB4D888D5, 0xC1886828, 0xD4C7D2B7, 0xE3D3A162, 0xEC75C2F3, 0xF197032C, 0xFDAA5D2E,
            0x25D26B6F, 0xB9FFB253, 0xED2B86ED, 0x4013244C, 0x485F368A, 0x31D3542E, 0x7A86C37E, 0x34B42669, 0xA3E828ED,
            0x01C44B07, 0xA08E60BA, 0x418E1324, 0x6C1161AD, 0x08893FA1, 0xE64B4101, 0x23B387A5, 0xB2FD5E9D, 0x9D8E0730,
            0xC2598A46, 0xC9283605, 0x7BC7E7F7, 0xA0991531, 0x44A2F444, 0xF4B21646, 0x548625FC, 0xB7A52202, 0xD46E45DE,
            0x4A479D1D, 0x944FE83C, 0x89C8F74E,
        })
        {
            data.Add(nid, true);
        }

        foreach (uint nid in new uint[] { 0x99893E59, 0xCD3A3712, 0x31423835, 0x866D737A, 0x54C48F92, 0x14437509, 0xE2135903, 0x88B18A5B })
        {
            data.Add(nid, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void TheLibrarysSystemFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    private HleModule ModuleNamed(string name) => _harness.Emu.Hle.Modules.Single(m => m.GetType().Name == name);

    [Fact]
    public void ShellExportsAnswerWithoutTheShellsServices()
    {
        HleModule shell = ModuleNamed("Shell");
        const int NotReady = unchecked((int)0x80412181), NoInstaller = unchecked((int)0x80AF0001);
        Assert.Equal(NotReady, Call(shell, "ShellNet0025417F", 0u, 0u));
        Assert.Equal(NotReady, Call(shell, "ShellNet60129092", 1u, 31u));
        Assert.Equal(NotReady, Call(shell, "ShellNetFDAA5D2E"));
        Assert.Equal(NoInstaller, Call(shell, "ShellInstaller548A3111", 0u, 0u, 0u));
        Assert.Equal(0, Call(shell, "ShellInstallerState"));
        Assert.Equal(0, Call(shell, "ShellHook7FE4E732"));
    }

    [Fact]
    public void NpUtilityKnowsNoRequestsAndChecksFirst()
    {
        NpUtility u = _harness.Module<NpUtility>();
        PsnAccount.Current = PsnAccount.Create(true, "Alice", localServices: false);
        const int NotFound = unchecked((int)0x8055070E), Null = unchecked((int)0x8055070C), Value = unchecked((int)0x80550704),
            NotInit = unchecked((int)0x80550702), Aborted = unchecked((int)0x80550707);
        uint word = _harness.Word(0xAAAA), request = _harness.Alloc(0x60);
        Assert.Equal(NotFound, Call(u, "UtilFindRequest", 0x200001u, word));
        Assert.Equal(0u, GuestMemory.R32(word));
        Assert.Equal(Null, Call(u, "UtilRequest08893FA1", 1u, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(Value, Call(u, "UtilRequest08893FA1", 1u, word, 0u, 0u, 0u, 1u));
        Assert.Equal(NotFound, Call(u, "UtilRequest08893FA1", 1u, word, 0u, 0u, 0u, 0u));
        Assert.Equal(Value, Call(u, "UtilSetTimeouts", 1u, 0, 0u, 0u, 0u, 0u));
        Assert.Equal(Value, Call(u, "UtilSetTimeouts", 1u, 0, 999_999u, 0u, 0u, 0u));
        Assert.Equal(NotFound, Call(u, "UtilSetTimeouts", 1u, 0, 1_000_000u, 10_000_000u, 0u, 0u));
        Assert.Equal(NotFound, Call(u, "UtilAbortRequest", 1, 3u));
        Assert.Equal(NotInit, Call(u, "UtilAbortRequest", 1, 0u));
        Assert.Equal(NotInit, Call(u, "UtilCreateRequest44A2F444", 0u, word, 0u, 0u));
        Assert.Equal(0, Call(u, "UtilStartA", 0));
        Assert.Equal(Null, Call(u, "UtilCreateRequest44A2F444", 0u, 0u, 0u, 0u));
        Assert.Equal(PsnAccount.Current.ServerError, Call(u, "UtilCreateRequest44A2F444", 0u, word, 0u, 0u));
        Assert.Equal(0, Call(u, "UtilStopA", 0));

        GuestMemory.W32(request + 0x44, 1);
        Assert.Equal(0, Call(u, "UtilMarkAborted", request));
        Assert.Equal(1u, GuestMemory.R32(request + 8));
        GuestMemory.W32(request + 0x18, 1);
        Assert.Equal(Aborted, Call(u, "UtilSend", request));
        Assert.Equal(Aborted, Call(u, "UtilRead", request, word, 4, word));
        GuestMemory.W32(request + 0xC, 5);
        GuestMemory.W32(request + 0x10, 6);
        Assert.Equal(0, Call(u, "UtilDropHttp", request));
        Assert.Equal(0u, GuestMemory.R32(request + 0xC));
        Assert.Equal(0u, GuestMemory.R32(request + 0x10));

        uint record = _harness.Alloc(0x548);
        GuestMemory.Fill(record, 0xAA, 0x548);
        Assert.Equal(0, Call(u, "UtilInitRecord", record, 1u, 2u, 3u));
        Assert.Equal(1u, GuestMemory.R32(record));
        Assert.Equal(0u, GuestMemory.R32(record + 4));
        Assert.Equal(2u, GuestMemory.R32(record + 0x540));
        Assert.Equal(3u, GuestMemory.R32(record + 0x544));
    }

    [Fact]
    public void NpUtilityTurnsJidsIntoNpIdsAndBack()
    {
        NpUtility u = _harness.Module<NpUtility>();
        uint npId = _harness.Alloc(0x24), jid = _harness.Word(0);
        Assert.Equal(0, Call(u, "UtilNpIdFromJid", npId, _harness.Str("Alice@ab.cd.rc.playstation.net/abc"), 0));
        Assert.Equal("Alice", GuestMemory.ReadCString(npId));
        Assert.Equal(0, Call(u, "UtilJidFromNpId", jid, npId, 0));
        Assert.Equal("Alice@ab.cd.rc.playstation.net/abc", GuestMemory.ReadCString(GuestMemory.R32(jid)));
    }

    private uint Schema(params (string Name, uint Kind)[][] levels)
    {
        uint tables = _harness.Alloc((uint)levels.Length * 4);
        for (int level = 0; level < levels.Length; level++)
        {
            uint table = _harness.Alloc((uint)(levels[level].Length + 1) * 8);
            for (int i = 0; i < levels[level].Length; i++)
            {
                GuestMemory.W32(table + (uint)i * 8, _harness.Str(levels[level][i].Name));
                GuestMemory.W32(table + (uint)i * 8 + 4, levels[level][i].Kind);
            }

            GuestMemory.W32(table + (uint)levels[level].Length * 8, _harness.Str(""));
            GuestMemory.W32(tables + (uint)level * 4, table);
        }

        uint schema = _harness.Alloc(8);
        GuestMemory.W32(schema, tables);
        GuestMemory.W32(schema + 4, (uint)levels.Length);
        return schema;
    }

    [Fact]
    public void TheXmlReaderCallsBackForTheSchemasElementsAcrossChunks()
    {
        NpManager np = _harness.Module<NpManager>();
        List<string> events = [];
        Func<int, int, int, string, string, int> sink = (ev, level, index, a, b) =>
        {
            events.Add($"{ev}:{level}:{index}:{a}:{b}");
            return 0;
        };
        typeof(NpManager).GetField("XmlSink", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(np, sink);
        try
        {
            uint schema = Schema([("result", 0)], [("name", 1), ("item", 0)]);
            uint record = _harness.Alloc(0x248);
            Assert.Equal(0, Call(np, "NID_650D111B", record, schema, 0x81000001u, 0x1234u));
            string xml = "<?xml version=\"1.0\"?><result code=\"7\"><name>A &amp; B</name><other><name>x</name></other><item id='2'/></result>";
            uint data = _harness.Str(xml);
            int half = xml.Length / 2;
            Assert.Equal(0, Call(np, "NpCommonXmlParse", record, data, half));
            Assert.Equal(0, Call(np, "NpCommonXmlParse", record, data + (uint)half, xml.Length - half));
            Assert.Equal(
            [
                "1:0:0:result:", "4:0:0:code:7", "1:1:0:name:", "3:1:0:A & B:A & B", "1:1:1:item:", "4:1:1:id:2", "2:1:1::",
                "2:0:0::",
            ], events);

            // a negative answer stops the parse with it
            events.Clear();
            typeof(NpManager).GetField("XmlSink", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(np,
                (Func<int, int, int, string, string, int>)((ev, level, index, a, b) => -5));
            Assert.Equal(0, Call(np, "NID_650D111B", record, schema, 0x81000001u, 0x1234u));
            Assert.Equal(-5, Call(np, "NpCommonXmlParse", record, data, xml.Length));
        }
        finally
        {
            typeof(NpManager).GetField("XmlSink", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(np, null);
        }
    }
}
