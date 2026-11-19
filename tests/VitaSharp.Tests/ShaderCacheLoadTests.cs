using VitaSharp.Core.Gpu;
using VitaSharp.Desktop.Launcher;

namespace VitaSharp.Tests;

/// <summary>"Carregar o cache de shaders antes de jogar": the progress lines the game process logs and the launcher reads, and the option
/// (global default on, a game's own choice wins).</summary>
public sealed class ShaderCacheLoadTests
{
    [Fact]
    public void ProgressLinesRoundTripInsideALogLine()
    {
        var p = new ShaderCacheLoadProgress(12, 552, false);
        Assert.True(ShaderCacheLoad.TryParse("[18:00:01.000] [Info ] [main] " + ShaderCacheLoad.Format(p), out ShaderCacheLoadProgress back));
        Assert.Equal(p, back);
        var end = new ShaderCacheLoadProgress(552, 552, true);
        Assert.True(ShaderCacheLoad.TryParse(ShaderCacheLoad.Format(end), out back));
        Assert.Equal(end, back);
    }

    [Theory]
    [InlineData("[shadercache] PCSA00001: driver cache hit")]
    [InlineData("[shadercache-load] progress x 3")]
    [InlineData("[shadercache-load] progress 3")]
    [InlineData("[precompile] progress 3 4")]
    public void OtherLinesAreNotProgress(string line)
    {
        Assert.False(ShaderCacheLoad.TryParse(line, out _));
    }

    [Fact]
    public void TheOptionIsOnByDefaultAndAGameChoiceWins()
    {
        var settings = new LauncherSettings();
        Assert.Contains("--load-shader-cache=on", settings.RunOptions("PCSA00001"));
        settings.LoadShaderCacheBeforePlaying = false;
        Assert.Contains("--load-shader-cache=off", settings.RunOptions("PCSA00001"));
        settings.Games["PCSA00001"] = new GameOptions { LoadShaderCacheBeforePlaying = true };
        Assert.Contains("--load-shader-cache=on", settings.RunOptions("PCSA00001"));
        Assert.Contains("--load-shader-cache=off", settings.RunOptions("PCSA00002"));
        Assert.DoesNotContain(settings.RunOptions("PCSA00001"), o => o.StartsWith("--precompile-shaders", StringComparison.Ordinal));
        Assert.False(settings.Games["PCSA00001"].IsDefault);
        Assert.True(new GameOptions().IsDefault);
    }

    /// <summary>A launcher.json of the build that called the option "Precompilar shaders": its global and per-game
    /// choices become the new option's, and the old key is not written back.</summary>
    [Fact]
    public void TheEarlierSettingKeyMigrates()
    {
        const string Json = """
            { "PrecompileShaders": false, "Games": { "PCSA00001": { "PrecompileShaders": true }, "PCSA00002": { "ResScale": 2 } } }
            """;
        LauncherSettings settings = System.Text.Json.JsonSerializer.Deserialize(Json, LauncherJson.Default.LauncherSettings)!;
        settings.MigrateShaderCacheLoad();
        Assert.False(settings.LoadShaderCacheBeforePlaying);
        Assert.True(settings.Games["PCSA00001"].LoadShaderCacheBeforePlaying);
        Assert.Null(settings.Games["PCSA00002"].LoadShaderCacheBeforePlaying);
        string written = System.Text.Json.JsonSerializer.Serialize(settings, LauncherJson.Default.LauncherSettings);
        Assert.DoesNotContain("PrecompileShaders", written, StringComparison.Ordinal);
        Assert.Contains("\"LoadShaderCacheBeforePlaying\": false", written, StringComparison.Ordinal);

        // a file without the old key keeps the default (on)
        LauncherSettings fresh = System.Text.Json.JsonSerializer.Deserialize("{}", LauncherJson.Default.LauncherSettings)!;
        fresh.MigrateShaderCacheLoad();
        Assert.True(fresh.LoadShaderCacheBeforePlaying);
    }

    /// <summary>The command-line option: the new name, and the earlier one still accepted.</summary>
    [Theory]
    [InlineData("--load-shader-cache=off", false)]
    [InlineData("--load-shader-cache=on", true)]
    [InlineData("--precompile-shaders=off", false)]
    [InlineData("--precompile-shaders=on", true)]
    [InlineData("--res-scale=2", null)]
    public void TheCommandLineTakesTheNewNameAndTheEarlierOne(string option, bool? expected)
    {
        Assert.Equal(expected, VitaSharp.Desktop.Runner.LoadShaderCacheChoice([option]));
    }

    [Fact]
    public void TheNewNameWinsOverTheEarlierOne()
    {
        Assert.False(VitaSharp.Desktop.Runner.LoadShaderCacheChoice(["--precompile-shaders=on", "--load-shader-cache=off"]));
    }

    [Fact]
    public void TheLauncherTextsExistInEveryLanguage()
    {
        foreach (string key in new[] { "LoadShaderCache", "LoadShaderCacheDetail", "LoadShaderCacheGlobalDetail", "LoadShaderCacheTip", "ShaderCacheLoadStatus", "ShaderCacheLoadCancel" })
        {
            Assert.NotEqual(key, Core.Localization.Loc.Get(key));
        }
    }
}
