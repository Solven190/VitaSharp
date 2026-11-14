using System.IO.Compression;
using VitaSharp.AndroidApkCheck;

namespace VitaSharp.Tests;

/// <summary>
/// The CI's arm64 APK check (build/AndroidApkCheck, run by build/build-android-apk.sh / .ps1): synthetic zips with the
/// entry layout of a .NET for Android APK. The app packages libshaderc and libadrenotools only when they were built, so
/// the build alone never fails without them; the check must.
/// </summary>
public sealed class AndroidApkContentsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vitasharp-apkcheck-" + Guid.NewGuid().ToString("N"));

    public AndroidApkContentsTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, true);
    }

    /// <summary>The entries of a good Native AOT arm64 APK (the layout of a real one, 13/11/2026).</summary>
    private static List<string> GoodApk() =>
    [
        "AndroidManifest.xml",
        "classes.dex",
        "resources.arsc",
        "res/drawable/icon.png",
        "META-INF/MANIFEST.MF",
        "lib/arm64-v8a/libVitaSharp.Android.so",
        "lib/arm64-v8a/libe_sqlite3.so",
        "lib/arm64-v8a/libshaderc_shared.so",
        "lib/arm64-v8a/libadrenotools.so",
        "lib/arm64-v8a/libfile_redirect_hook.so",
        "lib/arm64-v8a/libgsl_alloc_hook.so",
        "lib/arm64-v8a/libhook_impl.so",
        "lib/arm64-v8a/libmain_hook.so",
    ];

    private string WriteZip(IEnumerable<string> entries)
    {
        string path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".apk");
        using (ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (string entry in entries)
            {
                using Stream stream = archive.CreateEntry(entry).Open();
                stream.WriteByte(0x7F);
            }
        }

        return path;
    }

    [Fact]
    public void GoodApkPasses()
    {
        Assert.Empty(ApkContents.Problems(WriteZip(GoodApk())));
    }

    [Fact]
    public void ApkWithoutShadercFails()
    {
        List<string> entries = GoodApk();
        entries.Remove("lib/arm64-v8a/libshaderc_shared.so");

        List<string> problems = ApkContents.Problems(WriteZip(entries));

        Assert.Equal(["missing lib/arm64-v8a/libshaderc_shared.so"], problems);
    }

    [Theory]
    [InlineData("libadrenotools.so")]
    [InlineData("libhook_impl.so")]
    [InlineData("libmain_hook.so")]
    [InlineData("libfile_redirect_hook.so")]
    [InlineData("libgsl_alloc_hook.so")]
    [InlineData("libVitaSharp.Android.so")]
    public void ApkWithoutARequiredLibraryFails(string library)
    {
        List<string> entries = GoodApk();
        entries.Remove($"lib/arm64-v8a/{library}");

        Assert.Equal([$"missing lib/arm64-v8a/{library}"], ApkContents.Problems(WriteZip(entries)));
    }

    /// <summary>shaderc built for another ABI only (build-android-shaderc x86_64) is still a missing arm64 one.</summary>
    [Fact]
    public void ShadercOfAnotherAbiDoesNotCount()
    {
        List<string> entries = GoodApk();
        entries.Remove("lib/arm64-v8a/libshaderc_shared.so");
        entries.Add("lib/x86_64/libshaderc_shared.so");

        List<string> problems = ApkContents.Problems(WriteZip(entries));

        Assert.Contains("missing lib/arm64-v8a/libshaderc_shared.so", problems);
        Assert.Contains("native libraries for x86_64: the CI APK is arm64-v8a only", problems);
    }

    /// <summary>A Mono (or CoreCLR) APK: the Release default is Native AOT, so the CI artifact must be one.</summary>
    [Theory]
    [InlineData("libmonosgen-2.0.so")]
    [InlineData("libmonodroid.so")]
    [InlineData("libcoreclr.so")]
    [InlineData("libassemblies.arm64-v8a.blob.so")]
    [InlineData("libxamarin-app.so")]
    public void ApkWithAnotherRuntimeFails(string library)
    {
        List<string> entries = GoodApk();
        entries.Add($"lib/arm64-v8a/{library}");

        Assert.Equal([$"lib/arm64-v8a/{library} is there: the APK was not built as Native AOT"],
            ApkContents.Problems(WriteZip(entries)));
    }

    [Fact]
    public void ApkWithBothAbisFails()
    {
        List<string> entries = GoodApk();
        entries.Add("lib/x86_64/libVitaSharp.Android.so");

        Assert.Equal(["native libraries for x86_64: the CI APK is arm64-v8a only"], ApkContents.Problems(WriteZip(entries)));
    }

    [Fact]
    public void NotAZipFails()
    {
        string path = Path.Combine(_folder, "broken.apk");
        File.WriteAllText(path, "not a zip");

        string problem = Assert.Single(ApkContents.Problems(path));
        Assert.StartsWith($"cannot read {path} as an APK (zip):", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFileFails()
    {
        string path = Path.Combine(_folder, "absent.apk");

        string problem = Assert.Single(ApkContents.Problems(path));
        Assert.StartsWith($"cannot read {path}", problem, StringComparison.Ordinal);
    }
}
