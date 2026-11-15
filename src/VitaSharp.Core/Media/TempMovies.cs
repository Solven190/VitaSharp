using VitaSharp.Core.Util;

namespace VitaSharp.Core.Media;

/// <summary>
/// Temp copies of movies a game serves through its own file callbacks (SceMp4, SceAvPlayer). They are deleted when the
/// game closes the movie, but a session that ends mid-movie (the window closed, a crash, a timed headless run) left
/// them behind: 161 files, 4.7 GB in %TEMP% on 07/11/2026. The first copy a process makes deletes the ones of
/// processes that are no longer running (the file name carries the process id).
/// </summary>
public static class TempMovies
{
    private static int s_swept;

    /// <summary>A new temp path "vitasharp-{kind}-{pid}-{suffix}.mp4".</summary>
    public static string NewPath(string kind, string suffix)
    {
        SweepOnce();
        return Path.Combine(Path.GetTempPath(), $"vitasharp-{kind}-{Environment.ProcessId}-{suffix}.mp4");
    }

    /// <summary>
    /// A movie the emulator spooled from the game's own file callbacks (a temp file named by <see cref="NewPath"/>):
    /// it exists only for this session, so a save state carries its bytes (<see cref="ReadForState"/>). The name is
    /// VitaSharp's own fixed format (the one <see cref="SweepStale"/> also matches), not something a game chooses.
    /// </summary>
    public static bool IsSpool(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return dir != null
            && string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), temp, StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(path).StartsWith("vitasharp-", StringComparison.Ordinal)
            && path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The bytes a save state keeps for <paramref name="path"/>: the spooled movie's content, or null for a
    /// movie that is a file of the game (reopened from its path on load).</summary>
    public static byte[]? ReadForState(string? path)
    {
        if (!IsSpool(path) || !File.Exists(path))
        {
            return null;
        }

        return File.ReadAllBytes(path!);
    }

    /// <summary>
    /// On load: a spooled movie whose temp file is gone (the session that made it ended, the temp folder was cleaned)
    /// is written again at the path the state names, so the module reopens it as it was. The sweep of earlier
    /// sessions' files runs first (once per process), so it does not delete the file again. False when the path is not
    /// a spool or its folder does not exist on this device.
    /// </summary>
    public static bool RestoreAt(string path, byte[] bytes)
    {
        if (!IsSpool(path))
        {
            return false;
        }

        SweepOnce();
        if (File.Exists(path))
        {
            return true;
        }

        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            return false;
        }

        File.WriteAllBytes(path, bytes);
        Log.Info($"Load state: movie {Path.GetFileName(path)} written again from the state");
        return true;
    }

    private static void SweepOnce()
    {
        if (Interlocked.Exchange(ref s_swept, 1) == 0)
        {
            SweepStale();
        }
    }

    /// <summary>Deletes the temp movies of VitaSharp processes that ended.</summary>
    public static int SweepStale()
    {
        int deleted = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(Path.GetTempPath(), "vitasharp-*-*-*.mp4"))
            {
                string[] parts = Path.GetFileNameWithoutExtension(file).Split('-');
                if (parts.Length < 4 || !int.TryParse(parts[2], out int pid) || Running(pid))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (deleted > 0)
        {
            Log.Info($"Media: deleted {deleted} temp movie(s) left by earlier sessions");
        }

        return deleted;
    }

    private static bool Running(int pid)
    {
        if (pid == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception) // cannot tell: keep it
        {
            return true;
        }
    }
}
