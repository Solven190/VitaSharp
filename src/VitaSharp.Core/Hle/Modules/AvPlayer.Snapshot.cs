using VitaSharp.Core.Media;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: players keep their callbacks, buffers and queue; the host media of a player is opened again
/// from its source and seeked to the saved playback time (a few pictures of drift around the key frame).</summary>
[GenerateSnapshot]
public sealed partial class AvPlayer
{
    [GenerateSnapshot, SnapshotIgnoreFields("Media", "Clock", "LastFrame")]
    sealed partial class Player
    {
        /// <summary>Load: playback time and whether the media was open, applied by <see cref="AfterSnapshotLoad"/>.</summary>
        public long RestoreUs = -1;
        public bool RestoreRunning;

        void SaveExtra(SnapshotWriter w)
        {
            w.Write(Media != null);
            w.Write(ClockOffsetUs + Clock.ElapsedMicroseconds);
            w.Write(Clock.IsRunning);
        }

        void LoadExtra(SnapshotReader r)
        {
            bool hadMedia = r.ReadBool();
            long us = r.ReadInt64();
            RestoreRunning = r.ReadBool();
            RestoreUs = hadMedia ? us : -1;
        }
    }

    /// <summary>
    /// A movie's host path saved in a state, on the device that loads it: a state saved on another device (a phone's
    /// "Gravar tudo" capture re-run on the desktop) names that device's data folder, so a path that does not exist here
    /// is looked up again by its part under the saved fs folder ("fs/ux0/app/..."), in this device's
    /// <paramref name="fsRoot"/>. A movie spooled from the game's callbacks (a temp file) stays as it is.
    /// </summary>
    internal static string HostPathOnThisDevice(string savedPath, string fsRoot)
    {
        if (savedPath.Length == 0 || File.Exists(savedPath))
        {
            return savedPath;
        }

        string normalized = savedPath.Replace('\\', '/');
        // the data folder's own layout (<data>/fs/<partition>): a fixed name of VitaSharp's, the first one is it
        int fs = normalized.IndexOf("/fs/", StringComparison.Ordinal);
        if (fs < 0)
        {
            return savedPath;
        }

        string relative = normalized[(fs + 4)..];
        string candidate = Path.Combine(fsRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(candidate) ? candidate : savedPath;
    }

    void BeforeSnapshotLoad()
    {
        lock (_players)
            foreach (var p in _players.Values) lock (p) { p.Media?.Dispose(); p.Media = null; }
    }

    void AfterSnapshotLoad()
    {
        lock (_players)
            foreach (var p in _players.Values)
            {
                p.Clock.Reset();
                p.ClockOffsetUs = Math.Max(0, p.RestoreUs);
                if (p.RestoreRunning) p.Clock.Start();
                if (p.RestoreUs < 0) continue;
                p.SourcePath = HostPathOnThisDevice(p.SourcePath, Emu.Vfs.FsRoot);
                p.Media = MediaPlayers.Open(p.SourcePath);
                if (p.Media == null) { Log.Warn($"Load state: AvPlayer cannot reopen {p.SourcePath}"); continue; }
                p.Media.Loop = p.Looping;
                p.Media.Seek(p.RestoreUs);
                Log.Info($"Load state: AvPlayer {Path.GetFileName(p.SourcePath)} reopened at {p.RestoreUs / 1e6:F2} s");
            }
    }
}
