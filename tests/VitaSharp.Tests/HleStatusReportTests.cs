using System.Globalization;
using System.Runtime.CompilerServices;
using VitaSharp.Core.Firmware;

namespace VitaSharp.Tests;

/// <summary>
/// The README status (badges, progress map, docs/hle/progress.md) is generated from docs/hle/data/hle-status.tsv by
/// build/hle-status.ps1. Rendering the committed summary again must give the committed files, so a hand edit or a
/// summary refreshed without its pictures fails here (run build/hle-status.ps1 again).
/// </summary>
public class HleStatusReportTests
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

    [Fact]
    public void CommittedFilesMatchTheSummary()
    {
        HleStatusReport.Status status = HleStatusReport.Parse(Read(HleStatusReport.TsvPath));
        Assert.NotEmpty(status.Libraries);
        IReadOnlyList<HleStatusReport.KernelLibrary> kernel = HleStatusReport.ParseKernel(Read(HleStatusReport.KernelTsvPath));
        var stale = HleStatusReport.Render(status, kernel).Where(f => !File.Exists(Path.Combine(Root(), f.Key)) || Read(f.Key) != f.Value)
            .Select(f => f.Key).ToList();
        Assert.Empty(stale);
    }

    [Fact]
    public void BadgesShowTheSummaryCounts()
    {
        HleStatusReport.Status status = HleStatusReport.Parse(Read(HleStatusReport.TsvPath));
        Assert.Equal(status.Libraries.Sum(l => l.Total), status.Total);
        foreach ((string? id, string _, int impl, int total) in HleStatusReport.Badges(status))
        {
            string svg = Read($"{HleStatusReport.AssetDir}/badge-{id}.svg");
            Assert.Contains($"({impl.ToString("N0", CultureInfo.InvariantCulture)}/{total.ToString("N0", CultureInfo.InvariantCulture)}", svg);
        }
    }

    [Fact]
    public void RoundTripsThroughTheTsv()
    {
        HleStatusReport.Status s = HleStatusReport.Build("3.74", 2, 10, 4,
        [
            ("SceA", "vs0:a.suprx", "games", true, false, false), ("SceA", "vs0:a.suprx", "system", false, false, false), ("SceB", "os0:b.skprx", "general", true, true, false),
            ("SceB", "os0:b.skprx", "system", true, true, false), ("SceC", "ux0:c.suprx", "general", true, false, true),
        ], 3, 5);
        HleStatusReport.Status back = HleStatusReport.Parse(HleStatusReport.ToTsv(s));
        Assert.Equal(s.Firmware, back.Firmware);
        Assert.Equal((s.VitaSdk, s.VitaSdkImpl, s.VitaSdkDefault, s.VitaSdkLle, s.GameTitles), (back.VitaSdk, back.VitaSdkImpl, back.VitaSdkDefault, back.VitaSdkLle, back.GameTitles));
        Assert.Equal(s.Libraries, back.Libraries);
        Assert.Equal((5, 4, 1, 1, 2, 1), (back.Total, back.Impl, back.Games, back.GamesImpl, back.System, back.SystemImpl));
        Assert.Equal((2, 0, 1, 1), (back.Default, back.GamesDefault, back.SystemDefault, back.Lle));
    }

    /// <summary>The kernel side of the status (the README badge and the progress page's section) shows the kernel
    /// census's counts, so it cannot drift from docs/hle/data/kernel-exports.tsv.</summary>
    [Fact]
    public void TheKernelBadgeAndSectionShowTheCensus()
    {
        IReadOnlyList<HleStatusReport.KernelLibrary> kernel = HleStatusReport.ParseKernel(Read(HleStatusReport.KernelTsvPath));
        string[] rows = Read(HleStatusReport.KernelTsvPath).Split('\n').Where(l => l.Length > 0 && l[0] != '#' && !l.StartsWith("library\t", StringComparison.Ordinal)).ToArray();
        Assert.Equal(rows.Length, kernel.Sum(l => l.Total));
        Assert.Equal(rows.Count(l => l.Split('\t')[3] is "impl" or "default"), kernel.Sum(l => l.Impl));
        Assert.Equal(rows.Count(l => l.Split('\t')[3] == "default"), kernel.Sum(l => l.Default));
        HleStatusReport.Status status = HleStatusReport.Parse(Read(HleStatusReport.TsvPath));
        (string label, int impl, int total, int def) = HleStatusReport.KernelBadge(status.Firmware, kernel);
        string svg = Read($"{HleStatusReport.AssetDir}/badge-kernel.svg");
        Assert.Contains(label, svg);
        Assert.Contains($"({impl.ToString("N0", CultureInfo.InvariantCulture)}/{total.ToString("N0", CultureInfo.InvariantCulture)}, {def.ToString("N0", CultureInfo.InvariantCulture)} default)", svg);
        string page = Read(HleStatusReport.ProgressPath);
        Assert.Contains($"## Kernel ({kernel.Count} libraries)", page);
        Assert.All(kernel, l => Assert.Contains($"| {l.Name} | {l.Impl} / {l.Total} |", page));
        // both READMEs show the kernel badge next to the others
        foreach (string readme in new[] { "README.md", "README.pt-BR.md" })
        {
            Assert.Contains("(docs/assets/status/badge-kernel.svg)", Read(readme));
        }
    }

    [Fact]
    public void KernelCountsPerLibrary()
    {
        string tsv = "# census\nlibrary\tnid\tname\tstatus\tvitasharp\timporters\n"
            + "SceA\t0x1\ta\timpl\tA.a\tSceB\nSceA\t0x2\tb\tdefault\tA.b\t\nSceA\t0x3\tc\tmissing\t\tSceC\nSceB\t0x4\td\tmissing\t\t\n";
        IReadOnlyList<HleStatusReport.KernelLibrary> kernel = HleStatusReport.ParseKernel(tsv);
        Assert.Equal([new HleStatusReport.KernelLibrary("SceA", 3, 2, 1, 2, 1), new HleStatusReport.KernelLibrary("SceB", 1, 0, 0, 0, 0)], kernel);
        HleStatusReport.Status status = HleStatusReport.Build("3.74", 0, 0, 0, [("SceX", "os0:x.skprx", "general", true, false, false)]);
        IReadOnlyDictionary<string, string> files = HleStatusReport.Render(status, kernel);
        Assert.Contains("firmware 3.74 kernel exports", files[$"{HleStatusReport.AssetDir}/badge-kernel.svg"]);
        Assert.Contains("(2/4, 1 default)", files[$"{HleStatusReport.AssetDir}/badge-kernel.svg"]);
        Assert.Contains("| Imported | 1 | - | - | 2 | 50% |", files[HleStatusReport.ProgressPath]);
        // without a census: no kernel badge or section (a checkout without the table)
        Assert.DoesNotContain($"{HleStatusReport.AssetDir}/badge-kernel.svg", HleStatusReport.Render(status).Keys);
    }
}
