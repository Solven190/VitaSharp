using VitaSharp.Core.Hle.Np;
using VitaSharp.Desktop.Launcher;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// The launcher shows every game's trophy counts at once (library cards and list rows), so the sets it keeps hold only
/// the structure and texts; an icon is read from the TRP when a trophy page shows it. Keeping whole packages held every
/// game's icons in memory (66 games: most of the launcher's ~165 MB of live byte arrays).
/// </summary>
public sealed class TrophyPackageTextsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vitasharp-trp-" + Guid.NewGuid().ToString("N"));

    public TrophyPackageTextsTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly byte[] s_icon = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
    private static readonly byte[] s_trophyIcon = [0x89, (byte)'P', (byte)'N', (byte)'G', 9, 8];

    private string WriteTrp(string folder)
    {
        string dir = Path.Combine(folder, "sce_sys", "trophy", "NPWR00001_00");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "TROPHY.TRP");
        File.WriteAllBytes(path, NpLocalTests.BuildTrp(("TROPCONF.SFM", NpLocalTests.Sfm("Conf", "x")), ("TROP.SFM", NpLocalTests.Sfm("Game", "First")),
            ("TROP_17.SFM", NpLocalTests.Sfm("Jogo", "Primeiro")), ("ICON0.PNG", s_icon), ("TROP001.PNG", s_trophyIcon)));
        return path;
    }

    [Fact]
    public void TextsOnlyReadsTheSameSetAndKeepsNoIcons()
    {
        string trp = WriteTrp(_dir);
        TrophyPackage full = TrophyPackage.Parse(File.ReadAllBytes(trp), "NPWR00001_00");
        TrophyPackage texts = TrophyPackage.LoadTexts(trp, "NPWR00001_00")!;
        Assert.Equal(full.Title, texts.Title);
        Assert.Equal(full.Detail, texts.Detail);
        Assert.Equal(full.Trophies, texts.Trophies);
        Assert.Equal(full.Groups, texts.Groups);
        Assert.DoesNotContain(texts.Files.Keys, name => name.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase));

        // the icons come from the file when shown
        Assert.Equal(s_icon, texts.GameIcon);
        Assert.Equal(s_trophyIcon, texts.TrophyIcon(1));
        Assert.Null(texts.TrophyIcon(2));
        Assert.Null(texts.GroupIcon(1));
        Assert.Equal("Primeiro", TrophyPackage.LoadTexts(trp, "", 17)!.Trophies[1].Name); // pt-BR translation
    }

    [Fact]
    public void TextsOnlyRejectsWhatParseRejects()
    {
        string bad = Path.Combine(_dir, "bad.TRP");
        File.WriteAllBytes(bad, new byte[0x20]);
        Assert.Null(TrophyPackage.LoadTexts(bad));
        File.WriteAllBytes(bad, NpLocalTests.BuildTrp(("ICON0.PNG", s_icon)));
        Assert.Null(TrophyPackage.LoadTexts(bad)); // no configuration
        Assert.Null(TrophyPackage.LoadTexts(Path.Combine(_dir, "missing.TRP")));
    }

    /// <summary>What the launcher keeps per game: counts and texts, no icon bytes; the trophy page still gets them.</summary>
    [Fact]
    public void TheLauncherKeepsNoTrophyIconsInMemory()
    {
        WriteTrp(_dir);
        TrophySetView set = Assert.Single(TrophySetView.ForGame(_dir, _dir, -1));
        Assert.Equal(3, set.Package.Trophies.Count);
        Assert.DoesNotContain(set.Package.Files.Keys, name => name.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(s_trophyIcon, set.Package.TrophyIcon(1));
    }
}
