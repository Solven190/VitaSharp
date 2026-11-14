using FFmpeg.AutoGen;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Media;

/// <summary>
/// Locates the FFmpeg shared libraries (avcodec/avformat/avutil). They ship next to the executable in "ffmpeg/"
/// (Windows and Linux, downloaded at build time by build/FFmpeg.targets) or come from the system: Homebrew on macOS,
/// the distribution's packages on Linux. Without them, video playback and the MP3/AAC audio decoders report
/// "no media" instead of failing.
/// </summary>
public static class FFmpegLoader
{
    private static readonly object s_lock = new();
    private static bool? s_available;

    public static bool Available
    {
        get
        {
            lock (s_lock)
            {
                return s_available ??= Init();
            }
        }
    }

    private static bool Init()
    {
        string[] candidates =
        [
            Environment.GetEnvironmentVariable(EnvVars.Ffmpeg) ?? "",
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            AppContext.BaseDirectory,
            .. OperatingSystem.IsMacOS() ? new[] { "/opt/homebrew/lib", "/usr/local/lib" } : [],
        ];
        // "avcodec-62.dll" on Windows, "libavcodec.so.62" on Linux, "libavcodec.62.dylib" on macOS
        foreach (string? dir in candidates.Where(d => d.Length > 0 && HasAvcodec(d)))
        {
            if (TryLoad(dir))
            {
                return true;
            }
        }
        // Linux: the distribution's FFmpeg, found by the dynamic loader's default search path
        if (OperatingSystem.IsLinux() && TryLoad(""))
        {
            return true;
        }

        Log.Warn(MissingMessage(MediaPlayers.Backend != null, VideoDecoders.Backend is not FFmpegVideoBackend, Mp4Demuxers.Backend != null,
            AudioFrameDecoders.AacBackend != null, AudioFrameDecoders.Mp3Backend != null));
        return false;
    }

    /// <summary>
    /// The warning when FFmpeg is missing: what is really off, given the front-end's own decoders (Android installs
    /// MediaExtractor/MediaCodec ones for every FFmpeg user but G.729 and the H.264 encoder, and carries no FFmpeg on
    /// purpose: it said "videos (SceAvPlayer) ... are disabled" while the movies played through MediaCodec).
    /// </summary>
    public static string MissingMessage(bool moviePlayer, bool h264Decoder, bool mp4Demuxer, bool aacDecoder, bool mp3Decoder)
    {
        var off = new List<string>();
        var replaced = new List<string>();
        void Feature(bool hasReplacement, string name) => (hasReplacement ? replaced : off).Add(name);
        Feature(moviePlayer, "videos (SceAvPlayer)");
        Feature(h264Decoder, "H.264 decoding (SceAvcdec)");
        Feature(mp4Demuxer, "MP4 reading (SceMp4)");
        Feature(aacDecoder, "AAC decoding (SceAudiodec)");
        Feature(mp3Decoder, "MP3 decoding (SceAudiodec)");
        off.Add("G.729 decoding (SceG729)");
        off.Add("H.264 encoding (camera videos)");
        string message = $"FFmpeg libraries not found: {string.Join(", ", off)} disabled";
        return replaced.Count == 0 ? message : $"{message}; {string.Join(", ", replaced)} through this platform's own decoders";
    }

    /// <summary>Whether <paramref name="dir"/> holds an avcodec library. A folder the process may not list counts as none:
    /// under Native AOT on Android the base directory is the app_process folder (/system/bin), which an app cannot read.</summary>
    private static bool HasAvcodec(string dir)
    {
        try
        {
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*avcodec*").Any();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool TryLoad(string dir)
    {
        try
        {
            ffmpeg.RootPath = dir;
            uint v = ffmpeg.avcodec_version();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            Log.Info($"FFmpeg avcodec {v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF} loaded from {(dir.Length > 0 ? dir : "the system library path")}");
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or NotSupportedException)
        {
            if (dir.Length > 0)
            {
                Log.Warn($"FFmpeg in {dir} could not be loaded: {e.Message}");
            }

            return false;
        }
    }
}
