using VitaSharp.Core.Hle.Np;
using VitaSharp.Mobile.Core.Library;
using VitaSharp.Mobile.Core.Storage;

namespace VitaSharp.Tests;

/// <summary>
/// The Android library counts every game's trophies (its cards) and the trophy screen lists a game's trophies: both
/// keep the sets' texts only (TrophyPackage.LoadTexts, through the shared TrophyTextsCache), and a trophy's picture is
/// read from the TRP when its row is shown. Whole packages kept every game's icons in memory, as in the desktop
/// launcher before the same change.
/// </summary>
public sealed class AndroidTrophyMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-android-trophymem-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] s_icon = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];
    private static readonly byte[] s_trophyIcon = [0x89, (byte)'P', (byte)'N', (byte)'G', 9, 8];

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private (GameEntry Game, DataLayout Data, string Trp) InstallGame()
    {
        var data = new DataLayout(_root);
        data.EnsureCreated();
        string appDir = Path.Combine(data.Apps, "PCSA00001");
        string trophyDir = Path.Combine(appDir, "sce_sys", "trophy", "NPWR00001_00");
        Directory.CreateDirectory(trophyDir);
        string trp = Path.Combine(trophyDir, "TROPHY.TRP");
        File.WriteAllBytes(trp, NpLocalTests.BuildTrp(("TROPCONF.SFM", NpLocalTests.Sfm("Conf", "x")), ("TROP.SFM", NpLocalTests.Sfm("Game", "First")),
            ("ICON0.PNG", s_icon), ("TROP001.PNG", s_trophyIcon)));
        return (new GameEntry("PCSA00001", "Game", "01.00", "gd", appDir, null, 0), data, trp);
    }

    [Fact]
    public void TheTrophyListKeepsTextsAndReadsAPictureWhenShown()
    {
        (GameEntry game, DataLayout data, _) = InstallGame();
        var cache = new TrophyTextsCache();
        TrophyList list = TrophyList.Load(game, data, -1, cache);
        Assert.NotEmpty(list.Rows);
        TrophyPackage set = list.Rows[0].Set!;
        Assert.DoesNotContain(set.Files.Keys, name => name.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase));

        TrophyRow first = list.Rows.Single(r => r.Id == 1);
        Assert.True(first.HasIcon);
        Assert.Equal(s_trophyIcon, first.LoadIcon());
        TrophyRow other = list.Rows.First(r => r.Id != 1);
        Assert.False(other.HasIcon);
        Assert.Null(other.LoadIcon());
    }

    /// <summary>Coming back to the library counts again from the texts already read; a changed TRP is read again.</summary>
    [Fact]
    public void TheLibraryCountsFromTheCachedTextsUntilTheFileChanges()
    {
        (GameEntry game, DataLayout data, string trp) = InstallGame();
        var cache = new TrophyTextsCache();
        TrophyProgress before = GameLibrary.Trophies(game, data, cache);
        Assert.True(before.HasTrophies);
        TrophyPackage held = cache.Get(trp, "NPWR00001_00", -1)!;
        Assert.DoesNotContain(held.Files.Keys, name => name.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase));
        GameLibrary.Trophies(game, data, cache);
        Assert.Same(held, cache.Get(trp, "NPWR00001_00", -1));
        Assert.Equal(1, cache.Count);

        TrophyList list = TrophyList.Load(game, data, -1, cache);
        Assert.Same(held, list.Rows[0].Set); // the trophy screen shares the library's texts

        File.SetLastWriteTimeUtc(trp, File.GetLastWriteTimeUtc(trp).AddMinutes(1));
        GameLibrary.Trophies(game, data, cache);
        Assert.NotSame(held, cache.Get(trp, "NPWR00001_00", -1));
    }

    /// <summary>Without a cache the library still reads texts only.</summary>
    [Fact]
    public void WithoutACacheTheLibraryStillKeepsNoIcons()
    {
        (GameEntry game, DataLayout data, _) = InstallGame();
        TrophyList list = TrophyList.Load(game, data);
        Assert.DoesNotContain(list.Rows[0].Set!.Files.Keys, name => name.EndsWith(".PNG", StringComparison.OrdinalIgnoreCase));
        Assert.True(GameLibrary.Trophies(game, data).HasTrophies);
    }
}
