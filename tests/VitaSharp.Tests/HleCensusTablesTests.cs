using System.Runtime.CompilerServices;
using VitaSharp.Core.Firmware;

namespace VitaSharp.Tests;

/// <summary>
/// The per-function tables hle-coverage --status writes next to docs/hle/data/hle-status.tsv
/// (<see cref="HleCensusTables"/>): the missing functions of each status group, the user-side holes and the implemented
/// NIDs. The committed tables must agree with the committed summary and hold only data (a refresh once redirected the
/// emulator's log into vitasharp-implemented.tsv).
/// </summary>
public class HleCensusTablesTests
{
    private static string Root([CallerFilePath] string source = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(source)!); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root(), relative)).Replace("\r\n", "\n");

    private static HleCensusTables.Function F(uint nid, string library, string module, string group, bool covered) =>
        new(nid, $"f{nid:X}", library, module, group, covered, group == "games" ? 1 : 0, group == "system" ? 2 : 0, "-");

    [Fact]
    public void MissingByGroupListsOnlyTheUncoveredFunctionsInStatusOrder()
    {
        string tsv = HleCensusTables.MissingByGroup("3.74",
        [
            F(0x30, "SceB", "vs0:b.suprx", "general", false), F(0x20, "SceA", "os0:kd/a.skprx", "vitasdk", false),
            F(0x10, "SceB", "vs0:b.suprx", "games", true), F(0x12, "SceB", "vs0:b.suprx", "games", false),
            F(0x11, "SceA", "vs0:a.suprx", "games", false), F(0x40, "SceC", "vs0:c.suprx", "system", false),
        ]);
        string[] rows = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).ToArray();
        Assert.Equal("group\tlibrary\tnid\tname\tmodule\tgames\tsystem\timporter", rows[0]);
        Assert.Equal(["games\tSceA\t0x00000011", "games\tSceB\t0x00000012", "system\tSceC\t0x00000040", "general\tSceB\t0x00000030",
            "vitasdk\tSceA\t0x00000020"], rows[1..].Select(r => string.Join('\t', r.Split('\t')[..3])).ToArray());
        Assert.Contains("# firmware=3.74 games=2 system=1 general=1 vitasdk=1", tsv);
        Assert.Equal(new Dictionary<string, int> { ["games"] = 2, ["system"] = 1, ["general"] = 1, ["vitasdk"] = 1 },
            HleCensusTables.ParseMissingCounts(tsv));
    }

    /// <summary>The committed missing table has, per group, exactly the functions the summary counts as not
    /// implemented.</summary>
    [Fact]
    public void CommittedMissingTableMatchesTheSummary()
    {
        HleStatusReport.Status s = HleStatusReport.Parse(Read(HleStatusReport.TsvPath));
        IReadOnlyDictionary<string, int> missing = HleCensusTables.ParseMissingCounts(Read(HleCensusTables.MissingPath));
        Assert.Equal(s.Games - s.GamesImpl, missing["games"]);
        Assert.Equal(s.System - s.SystemImpl, missing["system"]);
        Assert.Equal(s.Total - s.Games - s.System - (s.Impl - s.GamesImpl - s.SystemImpl), missing["general"]);
        Assert.Equal(s.VitaSdk - s.VitaSdkImpl, missing["vitasdk"]);
    }

    /// <summary>Only data in the committed tables: comment lines, the header, then rows (vitasharp-implemented.tsv has
    /// no header: every line is a NID row).</summary>
    [Fact]
    public void CommittedTablesHoldOnlyData()
    {
        Assert.All(Read(HleCensusTables.ImplementedPath).Split('\n', StringSplitOptions.RemoveEmptyEntries),
            l => Assert.Matches(@"^0x[0-9A-F]{8}\t[^\t]+\t[^\t]+\t[^\t]+\t(vh|-)(\tdefault)?$", l));
        string[] holes = Read(HleCensusTables.UserExportsPath).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).ToArray();
        Assert.Equal("library\tnid\tname\tkind\tgames\tsystem\tvitasdk", holes[0]);
        Assert.All(holes[1..], l => Assert.Matches(@"^[^\t]+\t0x[0-9A-F]{8}\t[^\t]+\t(kernel-syscall|os0-user|vs0-user)\t\d+\t\d+\t(yes|-)$", l));
    }

    [Theory]
    [InlineData("os0:kd/bootimage.skprx/SceSysmem", false, "kernel-syscall")]
    [InlineData("os0:kd/ngs.skprx", true, "kernel-syscall")]
    [InlineData("os0:us/libkernel.suprx", true, "os0-user")]
    [InlineData("vs0:sys/external/libcdlg.suprx", false, "vs0-user")]
    [InlineData("vs0:sys/external/libcdlg.suprx", true, null)]
    [InlineData("vs0:vsh/shell/shell.self", true, null)]
    public void HoleKindFollowsWhoServesTheLibrary(string module, bool firmwareServes, string? kind) =>
        Assert.Equal(kind, HleCensusTables.HoleKind(module, "SceLib", _ => firmwareServes));

    [Fact]
    public void UserExportsSkipsImplementedAndRealModuleFunctions()
    {
        string tsv = HleCensusTables.UserExports(
        [
            F(0x2, "SceK", "os0:kd/k.skprx", "games", false), F(0x1, "SceK", "os0:kd/k.skprx", "general", false),
            F(0x3, "SceV", "vs0:sys/external/v.suprx", "system", false), F(0x4, "SceL", "vs0:sys/external/l.suprx", "general", true),
        ], nid => nid == 0x1, lib => lib == "SceL", nid => nid == 0x3);
        string[] rows = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).Skip(1).ToArray();
        Assert.Equal(["SceK\t0x00000002\tf2\tkernel-syscall\t1\t0\t-", "SceV\t0x00000003\tf3\tvs0-user\t0\t2\tyes"], rows);
    }

    /// <summary>The library column of vitasharp-implemented.tsv is the one the [Hle] entry declares (an unnamed NID
    /// such as SceGpuEs4User's NID_F4735A47 is in no NID database, so the table printed "-" for it).</summary>
    [Fact]
    public void CommittedImplementedTableNamesTheLibraryTheHleEntryDeclares()
    {
        var libraryOf = new Dictionary<uint, string>();
        foreach (string line in Read(HleCensusTables.ImplementedPath).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] columns = line.Split('\t');
            libraryOf[Convert.ToUInt32(columns[0], 16)] = columns[3];
        }

        var wrong = new List<string>();
        foreach (VitaSharp.Core.Hle.HleModuleEntry module in VitaSharp.Core.Hle.HleBindings.Modules)
        {
            foreach (VitaSharp.Core.Hle.HleBinding binding in module.Bindings())
            {
                if (binding.Nid == 0 || binding.Library == null)
                {
                    continue;
                }

                if (!libraryOf.TryGetValue(binding.Nid, out string? library) || library == "-")
                {
                    wrong.Add($"{module.Type.Name}.{binding.Method} 0x{binding.Nid:X8} {binding.Library}: {library ?? "absent"}");
                }
            }
        }

        Assert.Empty(wrong);
    }
}
