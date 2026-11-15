using VitaSharp.Desktop.Launcher;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Tests;

/// <summary>
/// Options that exist in the desktop launcher and in the Android app (README, "Options on both apps"): the same
/// default and the same emulator setting on both sides. The Android side is <see cref="AppPreferences"/>; the desktop
/// side is what <see cref="LauncherSettings.RunOptions"/> passes to the game.
/// </summary>
public sealed class AndroidOptionParityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vitasharp-option-parity-" + Guid.NewGuid().ToString("N"));

    public AndroidOptionParityTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, true);
    }

    [Fact]
    public void ShaderOptionsHaveTheDesktopDefaults()
    {
        var android = new AppPreferences();
        var desktop = new LauncherSettings().RunOptions().ToList();
        Assert.True(android.FastShaderCompile);
        Assert.True(android.BackgroundShaderCompile);
        Assert.Contains($"--shaderc-opt={android.ShaderOptimization}", desktop);
        Assert.Contains("--async-shaders=on", desktop);
    }

    [Fact]
    public void ShaderOptionsTurnedOffMeanTheSameOnBothSides()
    {
        var android = new AppPreferences { FastShaderCompile = false, BackgroundShaderCompile = false };
        var desktop = new LauncherSettings { FastShaderCompile = false, BackgroundShaderCompile = false }.RunOptions().ToList();
        Assert.Equal("perf", android.ShaderOptimization);
        Assert.Contains($"--shaderc-opt={android.ShaderOptimization}", desktop);
        Assert.Contains("--async-shaders=off", desktop);
    }

    [Fact]
    public void ShaderOptionsSurviveTheSettingsFileAndOldFilesGetTheDefaults()
    {
        string path = Path.Combine(_folder, "app.json");
        File.WriteAllText(path, "{\"VSync\":true}"); // a settings file from before these options
        var old = AppPreferences.Load(path);
        Assert.True(old.FastShaderCompile);
        Assert.True(old.BackgroundShaderCompile);

        new AppPreferences { FastShaderCompile = false, BackgroundShaderCompile = false }.Save(path);
        var saved = AppPreferences.Load(path);
        Assert.False(saved.FastShaderCompile);
        Assert.False(saved.BackgroundShaderCompile);
        Assert.DoesNotContain("ShaderOptimization", File.ReadAllText(path));
    }

    [Fact]
    public void EveryDesktopInternalResolutionIsOfferedOnAndroidWithTheSameScale()
    {
        foreach ((int factor, string _) in GameOptions.ResScales)
        {
            if (factor == 1)
            {
                continue; // native: the empty value on both sides
            }

            string value = factor.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Contains(SettingsChoices.ResScales, c => c.Value == value);
            var effective = EffectiveSettings.Resolve(new AppPreferences(), new GameSettings { ResScale = value });
            Assert.Equal(factor, effective.ResScaleValue);
            Assert.Contains($"--res-scale={factor}", new GameOptions { ResScale = factor }.RunOptions());
        }
    }

    [Fact]
    public void KernelPluginsOptionExistsOnBothSidesAndIsOffByDefault()
    {
        Assert.False(new AppPreferences().KernelPlugins);
        Assert.False(new LauncherSettings().KernelPlugins);
        Assert.Contains("--kernel-plugins=off", new LauncherSettings().RunOptions());
        Assert.Contains("--kernel-plugins=on", new LauncherSettings { KernelPlugins = true }.RunOptions());
    }
}
