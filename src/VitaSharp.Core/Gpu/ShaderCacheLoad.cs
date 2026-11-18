namespace VitaSharp.Core.Gpu;

/// <summary>Progress of <see cref="IGpuBackend.LoadShaderCacheBeforeStart"/>: recorded shaders and pipelines built so far,
/// out of those queued (the total grows while the records are read).</summary>
public readonly record struct ShaderCacheLoadProgress(int Done, int Total, bool Finished);

/// <summary>
/// The "Carregar o cache de shaders antes de jogar" wait before a game starts: the shaders and pipelines that the
/// title's earlier sessions recorded (its shader cache) are built before the first frame, like RPCS3's and Ryujinx's
/// shader cache loading at boot and Dolphin's "compile shaders before starting". It is not a precompilation: a first
/// session has nothing recorded and starts at once (the first use of each shader is what the ubershaders cover). The
/// progress goes to the log as <see cref="ProgressPrefix"/> lines that the desktop launcher reads to show it;
/// <see cref="Cancel"/> (the launcher's "Cancelar", through the game process's standard input) lets the game start
/// right away.
/// </summary>
public static class ShaderCacheLoad
{
    /// <summary>Log line: "[shadercache-load] progress &lt;done&gt; &lt;total&gt;"; the last one is
    /// "[shadercache-load] finished &lt;done&gt; &lt;total&gt;".</summary>
    public const string ProgressPrefix = "[shadercache-load] progress ";
    public const string FinishedPrefix = "[shadercache-load] finished ";
    /// <summary>What every progress line carries (the launcher's quick filter).</summary>
    public const string LineTag = "[shadercache-load] ";
    /// <summary>The line the launcher writes to the game's standard input to stop the wait.</summary>
    public const string CancelCommand = "cancel-shader-cache-load";
    /// <summary>The command-line option (on|off, default on).</summary>
    public const string Option = "--load-shader-cache";
    /// <summary>The option's earlier name, still accepted (with a warning) by the game and replay-gpu.</summary>
    public const string OldOption = "--precompile-shaders";

    private static CancellationTokenSource s_cancel = new();

    public static CancellationToken Token => s_cancel.Token;

    public static void Cancel() => s_cancel.Cancel();

    /// <summary>A new game session (tests start several in one process).</summary>
    public static void Reset()
    {
        s_cancel = new CancellationTokenSource();
    }

    /// <summary>Parses a <see cref="ProgressPrefix"/> or <see cref="FinishedPrefix"/> line (anywhere in a log line).</summary>
    public static bool TryParse(string line, out ShaderCacheLoadProgress progress)
    {
        progress = default;
        bool finished = false;
        int at = line.IndexOf(ProgressPrefix, StringComparison.Ordinal);
        int start = at + ProgressPrefix.Length;
        if (at < 0)
        {
            at = line.IndexOf(FinishedPrefix, StringComparison.Ordinal);
            start = at + FinishedPrefix.Length;
            finished = true;
        }

        if (at < 0)
        {
            return false;
        }

        string[] parts = line[start..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out int done) || !int.TryParse(parts[1], out int total))
        {
            return false;
        }

        progress = new ShaderCacheLoadProgress(done, total, finished);
        return true;
    }

    /// <summary>The log line of <paramref name="progress"/>.</summary>
    public static string Format(ShaderCacheLoadProgress progress) =>
        (progress.Finished ? FinishedPrefix : ProgressPrefix) + progress.Done.ToString(System.Globalization.CultureInfo.InvariantCulture) + " "
        + progress.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
