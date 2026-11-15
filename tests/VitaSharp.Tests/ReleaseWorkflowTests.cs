using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace VitaSharp.Tests;

/// <summary>
/// The Releases page gets every package the CI builds, the Android APK included: the "Collect the packages" step of
/// .github/workflows/release.yml runs here (bash) over the artifacts the CI uploads, with the names its upload steps
/// and the build scripts give them (build/build-android-apk.sh: VitaSharp-&lt;version&gt;-android-arm64.apk). Before
/// f2679736 the APK went up as VitaSharp-android-arm64-nativeaot, which the release's VitaSharp-*-packages pattern
/// never downloaded, and no check said so.
/// </summary>
public sealed class ReleaseWorkflowTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), "vitasharp-release-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, true);
        }
        catch (DirectoryNotFoundException) { }
    }

    private static string Root([CallerFilePath] string source = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(source)!); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root");
    }

    /// <summary>Git's bash on Windows (never the WSL bash.exe of System32), the system one elsewhere; null when absent.</summary>
    private static string? Bash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return File.Exists("/bin/bash") ? "/bin/bash" : null;
        }

        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string git = Path.Combine(dir, "git.exe");
            if (dir.Length == 0 || !File.Exists(git))
            {
                continue;
            }

            // <Git>\cmd\git.exe or <Git>\bin\git.exe or <Git>\mingw64\bin\git.exe -> <Git>\bin\bash.exe
            for (var d = new DirectoryInfo(dir); d != null; d = d.Parent)
            {
                string bash = Path.Combine(d.FullName, "bin", "bash.exe");
                if (File.Exists(bash))
                {
                    return bash;
                }
            }
        }
        string programFiles = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        return File.Exists(programFiles) ? programFiles : null;
    }

    /// <summary>The body of the "run: |" of the step named <paramref name="step"/>, unindented.</summary>
    private static string StepScript(string workflow, string step)
    {
        string[] lines = File.ReadAllLines(workflow);
        int at = Array.FindIndex(lines, l => l.Trim() == $"- name: {step}");
        Assert.True(at >= 0, $"no step '{step}' in {workflow}");
        int run = Array.FindIndex(lines, at, l => l.Trim() == "run: |");
        int indent = lines[run].IndexOf("run:", StringComparison.Ordinal) + 2;
        var body = new List<string>();
        for (int i = run + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length > 0 && lines[i].Length - lines[i].TrimStart().Length < indent)
            {
                break;
            }

            body.Add(lines[i].Length >= indent ? lines[i][indent..] : "");
        }
        return string.Join("\n", body) + "\n";
    }

    /// <summary>The literal artifact names the CI uploads, and the desktop rids of its package matrix.</summary>
    private static (List<string> Literal, List<string> DesktopRids) UploadedPackages(string ci)
    {
        string text = File.ReadAllText(ci);
        var literal = Regex.Matches(text, @"name: (VitaSharp-[A-Za-z0-9-]+-packages)\s*$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Contains("name: VitaSharp-aot-${{ matrix.rid }}-packages", text);
        var rids = Regex.Matches(text, @"rid: ([a-z]+-(?:x64|arm64))").Select(m => m.Groups[1].Value).Distinct().ToList();
        return (literal, rids);
    }

    private (int Code, string Output) Collect(string bash, string script, IEnumerable<(string Artifact, string File)> files)
    {
        foreach ((string artifact, string file) in files)
        {
            string dir = Path.Combine(_work, "packages", artifact);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, file), "package");
        }
        string scriptPath = Path.Combine(_work, "collect.sh");
        File.WriteAllText(scriptPath, "set -euo pipefail\n" + script);
        var psi = new ProcessStartInfo(bash, ["collect.sh"])
        {
            WorkingDirectory = _work,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["VERSION"] = "1.2.3";
        psi.Environment["SOURCE_VERSION"] = "ci-7";
        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static List<(string, string)> CiPackages(List<string> literal, List<string> rids)
    {
        var files = new List<(string, string)>();
        foreach (string rid in rids)
        {
            string artifact = $"VitaSharp-aot-{rid}-packages";
            string[] kinds = rid.StartsWith("win", StringComparison.Ordinal) ? [".zip", "-setup.exe"]
                : rid.StartsWith("linux", StringComparison.Ordinal) ? [".tar.gz", ".AppImage"]
                : [".app.zip", ".dmg"];
            foreach (string kind in kinds)
            {
                files.Add((artifact, $"VitaSharp-ci-7-{rid}{kind}"));
            }
        }
        // build/build-android-apk.sh names the APK VitaSharp-<VERSION>-android-arm64.apk
        string android = literal.Single(a => a.Contains("android", StringComparison.Ordinal));
        files.Add((android, "VitaSharp-ci-7-android-arm64.apk"));
        return files;
    }

    /// <summary>Skipped without bash (Git for Windows on Windows).</summary>
    public sealed class BashFactAttribute : FactAttribute
    {
        public BashFactAttribute()
        {
            if (Bash() == null)
            {
                Skip = "no bash: install Git for Windows (its bin/bash.exe), or bash on Linux/macOS";
            }
        }
    }

    [BashFact]
    public void TheReleaseCollectsEveryPackageTheCiUploadsTheApkIncluded()
    {
        string? bash = Bash();
        string root = Root();
        Assert.Contains("VitaSharp-${version}-${RID}.apk", File.ReadAllText(Path.Combine(root, "build", "build-android-apk.sh")));
        Assert.Contains("readonly RID=\"android-arm64\"", File.ReadAllText(Path.Combine(root, "build", "build-android-apk.sh")));
        string release = Path.Combine(root, ".github", "workflows", "release.yml");
        Assert.Contains("pattern: VitaSharp-*-packages", File.ReadAllText(release));
        (List<string> literal, List<string> rids) = UploadedPackages(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.Equal(["linux-x64", "osx-arm64", "win-x64"], rids.Order());
        string script = StepScript(release, "Collect the packages");

        (int code, string output) = Collect(bash!, script, CiPackages(literal, rids));
        Assert.True(code == 0, output);
        string[] collected = Directory.GetFiles(Path.Combine(_work, "release")).Select(Path.GetFileName).Order().ToArray()!;
        Assert.Contains("VitaSharp-1.2.3-android-arm64.apk", collected);
        Assert.Contains("VitaSharp-1.2.3-win-x64-setup.exe", collected);
        Assert.Equal(7, collected.Length);
    }

    [BashFact]
    public void AReleaseWithoutTheApkFails()
    {
        string? bash = Bash();
        string root = Root();
        (List<string> literal, List<string> rids) = UploadedPackages(Path.Combine(root, ".github", "workflows", "ci.yml"));
        string script = StepScript(Path.Combine(root, ".github", "workflows", "release.yml"), "Collect the packages");
        List<(string, string)> files = CiPackages(literal, rids);
        files.RemoveAll(f => f.Item2.EndsWith(".apk", StringComparison.Ordinal));
        (int code, string output) = Collect(bash!, script, files);
        Assert.NotEqual(0, code);
        Assert.Contains("missing package VitaSharp-1.2.3-android-arm64.apk", output);
    }
}
