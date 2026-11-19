namespace VitaSharp.Core.Hle.Np;

/// <summary>
/// The trophy sets' texts already read, by TRP file and language, kept while the file's size and write time stay the
/// same: a refresh of a library (the desktop launcher's F5 or a game closed, the Android app's library coming back)
/// counts every game's trophies again without reading every TRP again (66 games: their texts were read and thrown away
/// at each refresh). A set whose file is gone or changed is read again. Only texts are kept
/// (<see cref="TrophyPackage.LoadTexts"/>): the icons are read from the TRP when shown. Thread-safe (the Android
/// screens load on worker threads).
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
            lock (_sets)
            {
                _sets.Remove((trpPath, commId, language));
            }

            return null;
        }

        DateTime writeTimeUtc = file.LastWriteTimeUtc;
        long length = file.Length;
        lock (_sets)
        {
            if (_sets.TryGetValue((trpPath, commId, language), out var cached) && cached.WriteTimeUtc == writeTimeUtc && cached.Length == length)
            {
                return cached.Package;
            }
        }

        TrophyPackage? package = TrophyPackage.LoadTexts(trpPath, commId, language);
        lock (_sets)
        {
            _sets[(trpPath, commId, language)] = (writeTimeUtc, length, package);
        }

        return package;
    }

    /// <summary>Sets held (texts only: a game removed in the session keeps its few KB until the app closes).</summary>
    public int Count
    {
        get
        {
            lock (_sets)
            {
                return _sets.Count;
            }
        }
    }
}
