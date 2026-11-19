namespace VitaSharp.Core.Cpu;

/// <summary>
/// The progress protocol of "Precompilar código ARM" (<see cref="CodeCache.PrecompilePending"/>, the boot step that
/// translates the game's ARM code before it starts): <see cref="ProgressPrefix"/> lines in the log, which the desktop
/// launcher reads to show its own progress bar, and <see cref="CancelCommand"/> on the game process's standard input
/// (the bar's "Cancelar"), which lets the game start at once. Separate from the shader cache load
/// (<see cref="Gpu.ShaderCacheLoad"/>): that one rebuilds recorded shaders, this one translates CPU code.
/// </summary>
public static class CodePrecompileLog
{
    /// <summary>Log line: "[code-precompile] progress &lt;done&gt; &lt;total&gt;"; the last one is
    /// "[code-precompile] finished &lt;done&gt; &lt;total&gt;".</summary>
    public const string ProgressPrefix = "[code-precompile] progress ";
    public const string FinishedPrefix = "[code-precompile] finished ";
    /// <summary>What every progress line carries (the launcher's quick filter).</summary>
    public const string LineTag = "[code-precompile] ";
    /// <summary>The line the launcher writes to the game's standard input to stop the wait.</summary>
    public const string CancelCommand = "cancel-code-precompile";
    /// <summary>The command-line option (on|off, default on).</summary>
    public const string Option = "--precompile-code";

    private static CancellationTokenSource s_cancel = new();

    public static CancellationToken Token => s_cancel.Token;

    public static void Cancel() => s_cancel.Cancel();

    /// <summary>A new game session (tests start several in one process).</summary>
    public static void Reset()
    {
        s_cancel = new CancellationTokenSource();
    }

    /// <summary>Parses a <see cref="ProgressPrefix"/> or <see cref="FinishedPrefix"/> line (anywhere in a log line).</summary>
    public static bool TryParse(string line, out CodePrecompileProgress progress)
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

        progress = new CodePrecompileProgress(done, total, finished);
        return true;
    }

    /// <summary>The log line of <paramref name="progress"/>.</summary>
    public static string Format(CodePrecompileProgress progress) =>
        (progress.Finished ? FinishedPrefix : ProgressPrefix) + progress.Done.ToString(System.Globalization.CultureInfo.InvariantCulture) + " "
        + progress.Total.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
