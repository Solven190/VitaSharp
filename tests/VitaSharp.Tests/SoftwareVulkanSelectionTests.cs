using System.Runtime.InteropServices;

namespace VitaSharp.Tests;

/// <summary>
/// Which software Vulkan driver the renderer tests pick on Windows (<see cref="SoftwareVulkan"/>): Mesa's lavapipe
/// from %LOCALAPPDATA%\VitaSharpTools\mesa\&lt;version&gt;\&lt;arch&gt; when it is there and the system loader can take
/// it, SwiftShader from Edge/Chrome otherwise, and a skip reason that says how to get either when both are missing.
/// The folder search and the choice are pure functions, so this runs on every OS.
/// </summary>
public sealed class SoftwareVulkanSelectionTests : IDisposable
{
    private readonly string _tools = Path.Combine(Path.GetTempPath(), "vitasharp-swvk-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tools))
        {
            Directory.Delete(_tools, true);
        }
    }

    private string Manifest(string version, string arch)
    {
        string dir = Path.Combine(_tools, "mesa", version, arch);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "lvp_icd.x86_64.json");
        File.WriteAllText(path, "{}");
        File.WriteAllText(Path.Combine(dir, "vulkan_lvp.dll"), "");
        return path;
    }

    [Fact]
    public void LavapipeIsChosenWhenItsManifestIsThere()
    {
        string manifest = Manifest("26.2.6", "x64");
        string? found = SoftwareVulkan.FindLavapipeManifest(_tools, Architecture.X64);
        Assert.Equal(manifest, found);
        (string? driver, _, string? missing) = SoftwareVulkan.ChooseWindows(found, @"C:\Edge\157\vk_swiftshader.dll");
        Assert.Equal(manifest, driver);
        Assert.Null(missing);
    }

    [Fact]
    public void SwiftShaderIsTheFallbackWithoutLavapipe()
    {
        Assert.Null(SoftwareVulkan.FindLavapipeManifest(_tools, Architecture.X64));
        (string? driver, _, string? missing) = SoftwareVulkan.ChooseWindows(null, @"C:\Edge\157\vk_swiftshader.dll");
        Assert.Equal(@"C:\Edge\157\vk_swiftshader.dll", driver);
        Assert.Null(missing);
    }

    [Fact]
    public void AFolderWithoutTheManifestOrForAnotherArchitectureIsNotLavapipe()
    {
        Directory.CreateDirectory(Path.Combine(_tools, "mesa", "26.2.6", "x64"));
        Manifest("26.2.6", "arm64");
        Assert.Null(SoftwareVulkan.FindLavapipeManifest(_tools, Architecture.X64));
        Assert.NotNull(SoftwareVulkan.FindLavapipeManifest(_tools, Architecture.Arm64));
    }

    [Fact]
    public void TheNewestVersionWinsByVersionNotByName()
    {
        Manifest("9.1.0", "x64");
        string newest = Manifest("26.2.6", "x64");
        Manifest("not-a-version", "x64");
        Assert.Equal(newest, SoftwareVulkan.FindLavapipeManifest(_tools, Architecture.X64));
    }

    [Fact]
    public void WithNeitherDriverTheSkipReasonNamesBoth()
    {
        (string? driver, _, string? missing) = SoftwareVulkan.ChooseWindows(null, null);
        Assert.Null(driver);
        Assert.NotNull(missing);
        Assert.Contains("lavapipe", missing);
        Assert.Contains("SwiftShader", missing);
        Assert.Contains("VK_DRIVER_FILES", missing);
    }
}
