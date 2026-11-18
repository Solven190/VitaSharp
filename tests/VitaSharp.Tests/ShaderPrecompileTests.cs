using VitaSharp.Core.Gpu;
using VitaSharp.Desktop.Launcher;

namespace VitaSharp.Tests;

/// <summary>"Precompilar shaders": the progress lines the game process logs and the launcher reads, and the option
/// (global default on, a game's own choice wins).</summary>
public sealed class ShaderPrecompileTests
{
    [Fact]
    public void ProgressLinesRoundTripInsideALogLine()
    {
        var p = new ShaderPrecompileProgress(12, 552, false);
        Assert.True(ShaderPrecompile.TryParse("[18:00:01.000] [Info ] [main] " + ShaderPrecompile.Format(p), out ShaderPrecompileProgress back));
        Assert.Equal(p, back);
        var end = new ShaderPrecompileProgress(552, 552, true);
        Assert.True(ShaderPrecompile.TryParse(ShaderPrecompile.Format(end), out back));
        Assert.Equal(end, back);
    }

    [Theory]
    [InlineData("[shadercache] PCSA00001: driver cache hit")]
    [InlineData("[precompile] progress x 3")]
    [InlineData("[precompile] progress 3")]
    public void OtherLinesAreNotProgress(string line)
    {
        Assert.False(ShaderPrecompile.TryParse(line, out _));
    }

    [Fact]
    public void TheOptionIsOnByDefaultAndAGameChoiceWins()
    {
        var settings = new LauncherSettings();
        Assert.Contains("--precompile-shaders=on", settings.RunOptions("PCSA00001"));
        settings.PrecompileShaders = false;
        Assert.Contains("--precompile-shaders=off", settings.RunOptions("PCSA00001"));
        settings.Games["PCSA00001"] = new GameOptions { PrecompileShaders = true };
        Assert.Contains("--precompile-shaders=on", settings.RunOptions("PCSA00001"));
        Assert.Contains("--precompile-shaders=off", settings.RunOptions("PCSA00002"));
        Assert.False(settings.Games["PCSA00001"].IsDefault);
        Assert.True(new GameOptions().IsDefault);
    }

    [Fact]
    public void TheLauncherTextsExistInEveryLanguage()
    {
        foreach (string key in new[] { "PrecompileShaders", "PrecompileShadersDetail", "PrecompileShadersGlobalDetail", "PrecompileShadersTip", "PrecompileProgress", "PrecompileCancel" })
        {
            Assert.NotEqual(key, Core.Localization.Loc.Get(key));
        }
    }
}
