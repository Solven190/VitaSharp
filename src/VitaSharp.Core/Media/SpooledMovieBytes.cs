using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Media;

/// <summary>
/// The save state part of a module that spools movies from the game's file callbacks (<see cref="TempMovies"/>): the
/// bytes of each open spooled movie, by its path. Loaded before the module reopens its movies, it writes back the temp
/// files that are gone, so a state never depends on a file of the session that saved it. A field of the module
/// (declared <c>[SnapshotMissing]</c>: a state of a build before it has none, and loads as before).
/// </summary>
public sealed class SpooledMovieBytes(Func<IEnumerable<string?>> openMovies) : ISnapshotState
{
    /// <summary>Movies written back by the last load (tests, logs).</summary>
    public int Restored { get; private set; }

    public void SaveState(SnapshotWriter w)
    {
        var kept = new List<(string Path, byte[] Bytes)>();
        foreach (string? path in openMovies())
        {
            if (TempMovies.ReadForState(path) is { } bytes)
            {
                kept.Add((path!, bytes));
            }
        }

        w.Write(kept.Count);
        foreach ((string path, byte[] bytes) in kept)
        {
            w.Write(path);
            w.Write(bytes);
        }
    }

    public void LoadState(SnapshotReader r)
    {
        Restored = 0;
        int count = r.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string path = r.ReadStringNotNull();
            byte[] bytes = r.ReadBytes() ?? [];
            bool existed = File.Exists(path);
            if (TempMovies.RestoreAt(path, bytes) && !existed)
            {
                Restored++;
            }
        }
    }
}
