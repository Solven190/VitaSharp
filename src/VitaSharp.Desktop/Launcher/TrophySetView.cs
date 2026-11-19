using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System.Globalization;
using VitaSharp.Core.Hle.Np;

namespace VitaSharp.Desktop.Launcher;

/// <summary>One trophy set of a game as the launcher shows it: the set from the game's TROPHY.TRP plus the unlocks the
/// emulator recorded (ux0:user/00/trophy/&lt;NPCOMMID&gt;.json).</summary>
public sealed record TrophySetView(TrophyPackage Package, Dictionary<int, ulong> Unlocked)
{
    public int UnlockedCount => Package.Trophies.Count(t => Unlocked.ContainsKey(t.Id));
    public int Percent => Package.Trophies.Count == 0 ? 0 : UnlockedCount * 100 / Package.Trophies.Count;

    /// <summary>The trophy sets of an installed game (texts in <paramref name="language"/> when the set has them). With
    /// a <paramref name="cache"/>, a TRP file that has not changed since it was read is not read again; the recorded
    /// unlocks are always read (they change while a game runs).</summary>
    public static List<TrophySetView> ForGame(string appDir, string fsRoot, int language, TrophyTextsCache? cache = null)
    {
        var list = new List<TrophySetView>();
        foreach (var (commId, trp) in TrophyPackage.FindSets(appDir))
        {
            TrophyPackage? package = cache != null ? cache.Get(trp, commId, language) : TrophyPackage.LoadTexts(trp, commId, language);
            if (package is { } pkg)
            {
                list.Add(new TrophySetView(pkg, TrophyUnlocks.Load(TrophyUnlocks.PathFor(fsRoot, commId))));
            }
        }

        return list;
    }

    /// <summary>SceSystemParamLang of the PC's UI language (TROP_&lt;nn&gt;.SFM).</summary>
    public static int VitaLanguage(CultureInfo c) => VitaSharp.Core.Firmware.SystemLanguage.FromCulture(c);
}

/// <summary>
/// The trophy sets' texts the launcher already read, by TRP file and language, kept while the file's size and write
/// time stay the same: a refresh (F5, a game closed) counts every game's trophies again without reading every TRP
/// again (66 games: their texts were read and thrown away at each refresh). A set whose file is gone or changed is
/// read again.
/// </summary>
public sealed class TrophyTextsCache
{
    private readonly Dictionary<(string Path, string CommId, int Language), (DateTime WriteTimeUtc, long Length, TrophyPackage? Package)> _sets = new();

    /// <summary>The set in <paramref name="trpPath"/> (<see cref="TrophyPackage.LoadTexts"/>), from the cache when the
    /// file is unchanged. Null when the file is missing or malformed.</summary>
    public TrophyPackage? Get(string trpPath, string commId, int language)
    {
        FileInfo file = new(trpPath);
        if (!file.Exists)
        {
            _sets.Remove((trpPath, commId, language));
            return null;
        }

        DateTime writeTimeUtc = file.LastWriteTimeUtc;
        long length = file.Length;
        if (_sets.TryGetValue((trpPath, commId, language), out var cached) && cached.WriteTimeUtc == writeTimeUtc && cached.Length == length)
        {
            return cached.Package;
        }

        TrophyPackage? package = TrophyPackage.LoadTexts(trpPath, commId, language);
        _sets[(trpPath, commId, language)] = (writeTimeUtc, length, package);
        return package;
    }

    /// <summary>Sets held (texts only: a game removed in the session keeps its few KB until the launcher closes).</summary>
    public int Count => _sets.Count;
}
