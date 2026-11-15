using System.Reflection;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Media;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// A movie a game serves through its own file callbacks is spooled to a temp file of the session (SceMp4 and
/// SceAvPlayer). A save state taken with it open must not depend on that file: it keeps the movie's bytes and the load
/// writes them again when the temp file is gone (another session, a cleaned temp folder). Before, the load failed with
/// "the movie ... of the saved session cannot be opened" and the player threads stopped (Wipeout 2048, 13/11/2026).
/// A movie that is a file of the game is reopened from its path, with nothing extra in the state.
/// Shares the "guest" collection with the other users of the static <see cref="Mp4Demuxers.Backend"/> hook.
/// </summary>
[Collection("guest")]
public sealed class SpooledMovieStateTests(GuestMemoryFixture fixture)
{
    private sealed class FakeDemuxer : IMp4Demuxer
    {
        public IReadOnlyList<DemuxedStream> Streams { get; } = [];
        public long Duration90k => 0;
        public DemuxedUnit? Next(Func<int, bool> enabled) => null;
        public void Seek(long pts90k)
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Puts an open movie into SceMp4's table, as sceMp4OpenFile does after spooling it.</summary>
    private static void AddOpenMovie(Mp4 mp4, int handle, string hostPath, string tempPath)
    {
        Type fileType = typeof(Mp4).GetNestedType("File", BindingFlags.NonPublic)!;
        object file = Activator.CreateInstance(fileType, nonPublic: true)!;
        fileType.GetField("Demux")!.SetValue(file, new FakeDemuxer());
        fileType.GetField("HostPath")!.SetValue(file, hostPath);
        fileType.GetField("TempPath")!.SetValue(file, tempPath);
        object files = typeof(Mp4).GetField("_files", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mp4)!;
        files.GetType().GetMethod("Add")!.Invoke(files, [handle, file]);
    }

    private static string? OpenHostPath(Mp4 mp4, int handle)
    {
        object files = typeof(Mp4).GetField("_files", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(mp4)!;
        object?[] args = [handle, null];
        if (!(bool)files.GetType().GetMethod("TryGetValue")!.Invoke(files, args)!)
        {
            return null;
        }

        return (string?)args[1]!.GetType().GetField("HostPath")!.GetValue(args[1]);
    }

    [Fact]
    public void SpooledMovieIsWrittenAgainFromTheStateWhenItsTempFileIsGone()
    {
        using var saving = new HleHarness(fixture, "spool-save");
        using var loading = new HleHarness(fixture, "spool-load");
        byte[] movie = new byte[64 * 1024];
        new Random(15).NextBytes(movie);
        string spool = TempMovies.NewPath("mp4", "statetest" + Guid.NewGuid().ToString("N"));
        string gameMovie = Path.Combine(saving.Root, "movie.mp4"); // a file of the game: reopened from its path
        File.WriteAllBytes(spool, movie);
        File.WriteAllBytes(gameMovie, [1, 2, 3]);
        var opened = new List<string>();
        Func<string, IMp4Demuxer?>? previous = Mp4Demuxers.Backend;
        Mp4Demuxers.Backend = path =>
        {
            if (File.Exists(path))
            {
                lock (opened)
                {
                    opened.Add(path);
                }

                return new FakeDemuxer();
            }

            return null;
        };
        try
        {
            Mp4 source = saving.Module<Mp4>();
            AddOpenMovie(source, 1, spool, spool);
            AddOpenMovie(source, 2, gameMovie, "");
            var writer = new SnapshotWriter();
            ((ISnapshotState)source).SaveState(writer);
            byte[] state = writer.Data.ToArray();
            Assert.True(state.Length > movie.Length, "the state does not carry the spooled movie");
            Assert.True(state.Length < 2 * movie.Length, "the game's own movie file was copied into the state too");
            File.Delete(spool); // the session that spooled it is gone

            var reader = new SnapshotReader(state) { Objects = loading.Emu.Kernel.Objects };
            ((ISnapshotState)loading.Module<Mp4>()).LoadState(reader); // threw "cannot be opened" before
            Assert.True(reader.AtEnd);
            Assert.Equal(spool, OpenHostPath(loading.Module<Mp4>(), 1));
            Assert.Equal(movie, File.ReadAllBytes(spool));
            Assert.Equal(gameMovie, OpenHostPath(loading.Module<Mp4>(), 2));
            Assert.Contains(spool, opened);
        }
        finally
        {
            Mp4Demuxers.Backend = previous;
            File.Delete(spool);
        }
    }

    /// <summary>The shared part (SceMp4 and SceAvPlayer): only the emulator's own spools are carried, an existing file
    /// is left as it is, and a state without the part (an older build) changes nothing.</summary>
    [Fact]
    public void OnlyTheEmulatorsOwnSpoolsAreCarriedAndWrittenBack()
    {
        string spool = TempMovies.NewPath("avplayer", "statetest" + Guid.NewGuid().ToString("N"));
        string notOurs = Path.Combine(Path.GetTempPath(), "movie-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(spool, [7, 7, 7]);
        File.WriteAllBytes(notOurs, [9]);
        try
        {
            Assert.True(TempMovies.IsSpool(spool));
            Assert.False(TempMovies.IsSpool(notOurs));
            Assert.Null(TempMovies.ReadForState(notOurs));
            Assert.Null(TempMovies.ReadForState(null));
            var part = new SpooledMovieBytes(() => [spool, notOurs, null]);
            var writer = new SnapshotWriter();
            part.SaveState(writer);
            byte[] state = writer.Data.ToArray();

            // the file still there: left as it is
            var loaded = new SpooledMovieBytes(() => []);
            loaded.LoadState(new SnapshotReader(state));
            Assert.Equal(0, loaded.Restored);
            Assert.Equal([7, 7, 7], File.ReadAllBytes(spool));
            // gone: written again at its path; the file that is not a spool was never carried
            File.Delete(spool);
            File.Delete(notOurs);
            loaded.LoadState(new SnapshotReader(state));
            Assert.Equal(1, loaded.Restored);
            Assert.Equal([7, 7, 7], File.ReadAllBytes(spool));
            Assert.False(File.Exists(notOurs));
            // a path outside the temp folder is never written, whatever a state says
            Assert.False(TempMovies.RestoreAt(notOurs, [1]));
            Assert.False(File.Exists(notOurs));
        }
        finally
        {
            File.Delete(spool);
            File.Delete(notOurs);
        }
    }
}
