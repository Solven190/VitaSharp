using VitaSharp.Mobile.Core.Library;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Tests;

/// <summary>Deleting from a list of the Android app (uninstalling a game, deleting a recording): the item leaves the list
/// in place and the one that followed it on screen is the one highlighted (the desktop launcher's ListPosition rule).</summary>
public sealed class AndroidListRemovalTests
{
    private static GameEntry Game(string titleId, string title) => new(titleId, title, "01.00", "gd", "ux0:app/" + titleId, null, 0);

    private static readonly GameEntry[] s_library =
    [
        Game("PCSA00001", "Alpha"), Game("PCSA00002", "Bravo"), Game("PCSA00003", "Charlie"), Game("PCSA00004", "Delta"),
    ];

    [Fact]
    public void UninstallingAGameDropsItAndFocusesTheNextOneShown()
    {
        IReadOnlyList<GameEntry> shown = GameLibrary.Query(s_library, "", GameLibrary.Filter.All, _ => new GameSettings());
        (List<GameEntry> remaining, string? focus) = ListRemoval.Remove(s_library, g => g.TitleId, shown.Select(g => g.TitleId).ToList(), ["PCSA00002"]);
        Assert.Equal(["PCSA00001", "PCSA00003", "PCSA00004"], remaining.Select(g => g.TitleId));
        Assert.Equal("PCSA00003", focus);
    }

    [Fact]
    public void TheLastOneFocusesTheOneBeforeAndAnEmptyListNothing()
    {
        string[] keys = s_library.Select(g => g.TitleId).ToArray();
        Assert.Equal("PCSA00003", ListRemoval.Remove(s_library, g => g.TitleId, keys, ["PCSA00004"]).Focus);
        (List<GameEntry> none, string? focus) = ListRemoval.Remove([s_library[0]], g => g.TitleId, ["PCSA00001"], ["PCSA00001"]);
        Assert.Empty(none);
        Assert.Null(focus);
    }

    /// <summary>A search shows only some games: the next one is the next one on screen, not in the whole library.</summary>
    [Fact]
    public void TheNextOneIsTheNextOneOnScreen()
    {
        // "l": Alpha, Charlie and Delta (not Bravo, which follows Alpha in the whole library)
        IReadOnlyList<GameEntry> shown = GameLibrary.Query(s_library, "l", GameLibrary.Filter.All, _ => new GameSettings());
        List<string> shownKeys = shown.Select(g => g.TitleId).ToList();
        Assert.DoesNotContain("PCSA00002", shownKeys);
        (List<GameEntry> remaining, string? focus) = ListRemoval.Remove(s_library, g => g.TitleId, shownKeys, ["PCSA00001"]);
        Assert.Equal("PCSA00003", focus);
        Assert.Equal(3, remaining.Count);
    }

    [Fact]
    public void DeletingARecordingFocusesTheNextOne()
    {
        GameRecording[] recordings =
        [
            new("captures/PCSA00001_3.zip", 1, new DateTime(2026, 11, 18, 0, 0, 3, DateTimeKind.Utc)),
            new("captures/PCSA00001_2.zip", 1, new DateTime(2026, 11, 18, 0, 0, 2, DateTimeKind.Utc)),
            new("captures/PCSA00001_1.zip", 1, new DateTime(2026, 11, 18, 0, 0, 1, DateTimeKind.Utc)),
        ];
        string[] keys = recordings.Select(r => r.Path).ToArray();
        (List<GameRecording> remaining, string? focus) = ListRemoval.Remove(recordings, r => r.Path, keys, [recordings[0].Path]);
        Assert.Equal(recordings[1].Path, focus);
        Assert.Equal([recordings[1], recordings[2]], remaining);
    }
}
