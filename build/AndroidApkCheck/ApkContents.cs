using System.IO.Compression;

namespace VitaSharp.AndroidApkCheck;

/// <summary>
/// What the arm64 APK of the CI (build/build-android-apk.sh or .ps1, the android-arm64 job of .github/workflows/ci.yml)
/// must hold: the Release default runtime (Native AOT: the whole app in one lib/arm64-v8a/libVitaSharp.Android.so, no
/// Mono or CoreCLR runtime library and no assembly store next to it), the device shader compiler
/// (libshaderc_shared.so, build/build-android-shaderc) and libadrenotools with its four hook libraries
/// (build/build-android-adrenotools), and native libraries for arm64-v8a only. The app packages shaderc and
/// libadrenotools only when they were built, so an APK without them still builds: this check is what catches it.
/// Shared by build/AndroidApkCheck (run by the scripts) and tests/VitaSharp.Tests/AndroidApkContentsTests.
/// </summary>
public static class ApkContents
{
    /// <summary>The only ABI the CI APK carries.</summary>
    public const string Abi = "arm64-v8a";

    /// <summary>The app itself as Native AOT compiles it (ILC: every assembly in one shared library).</summary>
    public const string NativeAotLibrary = "libVitaSharp.Android.so";

    /// <summary>Libraries the APK must have in lib/arm64-v8a/.</summary>
    public static readonly IReadOnlyList<string> RequiredLibraries =
    [
        NativeAotLibrary,
        "libshaderc_shared.so",
        "libadrenotools.so",
        "libhook_impl.so",
        "libmain_hook.so",
        "libfile_redirect_hook.so",
        "libgsl_alloc_hook.so",
    ];

    /// <summary>
    /// Libraries of the other runtimes (Mono, CoreCLR) and their assembly stores: an APK that has one of them was not
    /// built as Native AOT.
    /// </summary>
    public static readonly IReadOnlyList<string> OtherRuntimeLibraries =
    [
        "libmonosgen-2.0.so",
        "libmonodroid.so",
        "libcoreclr.so",
        "libassemblies.arm64-v8a.blob.so",
        "libxamarin-app.so",
    ];

    /// <summary>The problems of an APK file (empty when it passes); an unreadable file is one problem.</summary>
    public static List<string> Problems(string apkPath)
    {
        List<string> entries;
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(apkPath);
            entries = archive.Entries.Select(entry => entry.FullName).ToList();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return [$"cannot read {apkPath} as an APK (zip): {error.Message}"];
        }

        return Problems(entries);
    }

    /// <summary>The problems of an APK given its entry names (empty when it passes).</summary>
    public static List<string> Problems(IEnumerable<string> entryNames)
    {
        var names = new HashSet<string>(entryNames.Select(name => name.Replace('\\', '/')), StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (string library in RequiredLibraries)
        {
            if (!names.Contains($"lib/{Abi}/{library}"))
            {
                problems.Add($"missing lib/{Abi}/{library}");
            }
        }

        foreach (string library in OtherRuntimeLibraries)
        {
            if (names.Contains($"lib/{Abi}/{library}"))
            {
                problems.Add($"lib/{Abi}/{library} is there: the APK was not built as Native AOT");
            }
        }

        IEnumerable<string> otherAbis = names
            .Where(name => name.StartsWith("lib/", StringComparison.Ordinal) && name.Count(c => c == '/') >= 2)
            .Select(name => name.Split('/')[1])
            .Where(abi => abi != Abi)
            .Distinct()
            .Order(StringComparer.Ordinal);
        foreach (string abi in otherAbis)
        {
            problems.Add($"native libraries for {abi}: the CI APK is {Abi} only");
        }

        return problems;
    }
}
