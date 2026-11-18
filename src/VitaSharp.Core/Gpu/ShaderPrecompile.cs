namespace VitaSharp.Core.Gpu;

/// <summary>Progress of <see cref="IGpuBackend.PrecompileBeforeStart"/>: recorded shaders and pipelines built so far,
/// out of those queued (the total grows while the records are read).</summary>
public readonly record struct ShaderPrecompileProgress(int Done, int Total, bool Finished);

/// <summary>
/// The "Precompilar shaders" wait before a game starts (RPCS3, Ryujinx and Dolphin's "compile shaders before starting"
/// build their recorded shader/pipeline caches behind a progress screen the same way). The progress goes to the log as
/// <see cref="ProgressPrefix"/> lines that the desktop launcher reads to show it; <see cref="Cancel"/> (the launcher's
/// "Cancelar", through the game process's standard input) lets the game start right away.
/// </summary>
public static class ShaderPrecompile
{
    /// <summary>Log line: "[precompile] progress &lt;done&gt; &lt;total&gt;"; the last one is "[precompile] finished &lt;done&gt; &lt;total&gt;".</summary>
    public const string ProgressPrefix = "[precompile] progress ";
    public const string FinishedPrefix = "[precompile] finished ";
    /// <summary>The line the launcher writes to the game's standard input to stop the wait.</summary>
    public const string CancelCommand = "cancel-precompile";

    private static CancellationTokenSource s_cancel = new();

    public static CancellationToken Token => s_cancel.Token;

    public static void Cancel() => s_cancel.Cancel();

    /// <summary>A new game session (tests start several in one process).</summary>
    public static void Reset()
    {
        s_cancel = new CancellationTokenSource();
    }

    /// <summary>Parses a <see cref="ProgressPrefix"/> or <see cref="FinishedPrefix"/> line (anywhere in a log line).</summary>
    public static bool TryParse(string line, out ShaderPrecompileProgress progress)
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

        progress = new ShaderPrecompileProgress(done, total, finished);
        return true;
    }

    /// <summary>The log line of <paramref name="progress"/>.</summary>
    public static string Format(ShaderPrecompileProgress progress) =>
        (progress.Finished ? FinishedPrefix : ProgressPrefix) + progress.Done.ToString(System.Globalization.CultureInfo.InvariantCulture) + " "
        + progress.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
