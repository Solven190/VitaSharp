using VitaSharp.Core.Gpu;
using VitaSharp.Core.Localization;
using VitaSharp.Mobile.Core.Gpu;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Tests;

/// <summary>"Carregar o cache de shaders antes de jogar" on Android: the option (global, per game, the desktop's
/// default), the progress the game screen's banner gets from the core (<see cref="ShaderCacheLoad.Reported"/>) and its
/// "Cancelar".</summary>
[Collection("ShaderCacheLoad")]
public sealed class AndroidShaderCacheLoadTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vitasharp-android-scl-" + Guid.NewGuid().ToString("N"));

    public AndroidShaderCacheLoadTests()
    {
        Directory.CreateDirectory(_folder);
        ShaderCacheLoad.Reset();
    }

    public void Dispose()
    {
        ShaderCacheLoad.Reset();
        Directory.Delete(_folder, true);
    }

    /// <summary>A backend whose recorded cache reports <see cref="_steps"/> before it ends; it checks the token between
    /// steps, as the Vulkan renderer does.</summary>
    private sealed class RecordedCacheBackend(params ShaderCacheLoadProgress[] steps) : IGpuBackend
    {
        private readonly ShaderCacheLoadProgress[] _steps = steps;
        public Action? BeforeStep { get; init; }

        public void Execute(GpuCommand command)
        {
        }

        public void Dispose()
        {
        }

        public bool LoadShaderCacheBeforeStart(Action<ShaderCacheLoadProgress>? progress, CancellationToken cancel)
        {
            if (_steps.Length == 0)
            {
                return false;
            }

            foreach (ShaderCacheLoadProgress step in _steps)
            {
                BeforeStep?.Invoke();
                if (cancel.IsCancellationRequested)
                {
                    return true;
                }

                progress?.Invoke(step);
            }

            return true;
        }
    }

    [Fact]
    public void TheOptionIsOnByDefaultLikeTheDesktopAndAGameChoiceWins()
    {
        var app = new AppPreferences();
        Assert.True(app.LoadShaderCacheBeforePlaying);
        Assert.True(EffectiveSettings.Resolve(app, null).LoadShaderCacheBeforePlaying);
        Assert.False(EffectiveSettings.Resolve(app, new GameSettings { LoadShaderCacheBeforePlaying = false }).LoadShaderCacheBeforePlaying);
        app.LoadShaderCacheBeforePlaying = false;
        Assert.False(EffectiveSettings.Resolve(app, new GameSettings()).LoadShaderCacheBeforePlaying);
        Assert.True(EffectiveSettings.Resolve(app, new GameSettings { LoadShaderCacheBeforePlaying = true }).LoadShaderCacheBeforePlaying);
        Assert.True(new GameSettings { LoadShaderCacheBeforePlaying = true }.HasOverrides);
        Assert.False(new GameSettings().HasOverrides);
    }

    [Fact]
    public void TheOptionSurvivesTheSettingsFilesAndOldFilesGetTheDefault()
    {
        string appPath = Path.Combine(_folder, "app.json");
        File.WriteAllText(appPath, "{\"VSync\":true}");
        Assert.True(AppPreferences.Load(appPath).LoadShaderCacheBeforePlaying);
        new AppPreferences { LoadShaderCacheBeforePlaying = false }.Save(appPath);
        Assert.False(AppPreferences.Load(appPath).LoadShaderCacheBeforePlaying);

        string gamePath = Path.Combine(_folder, "PCSA00001.json");
        new GameSettings { LoadShaderCacheBeforePlaying = false }.Save(gamePath);
        Assert.False(GameSettings.Load(gamePath).LoadShaderCacheBeforePlaying);
        new GameSettings().Save(gamePath);
        Assert.Null(GameSettings.Load(gamePath).LoadShaderCacheBeforePlaying);
    }

    [Fact]
    public void TheBannerFollowsTheCoreProgressAndHidesAtTheEnd()
    {
        var banner = new ShaderCacheLoadBanner();
        var seen = new List<ShaderCacheLoadProgress>();
        void OnReported(ShaderCacheLoadProgress progress)
        {
            seen.Add(progress);
            banner.Show(progress);
        }

        string? shownWhileLoading = null;
        var backend = new RecordedCacheBackend(new(0, 4, false), new(4, 4, true))
        {
            BeforeStep = () => shownWhileLoading ??= banner.Text,
        };
        ShaderCacheLoad.Reported += OnReported;
        try
        {
            ShaderCacheLoad.Wait(backend);
        }
        finally
        {
            ShaderCacheLoad.Reported -= OnReported;
        }

        Assert.Equal([new(0, 4, false), new(4, 4, true)], seen);
        Assert.False(banner.Visible);
        banner.Show(new ShaderCacheLoadProgress(1, 4, false));
        Assert.True(banner.Visible);
        Assert.Equal(Loc.Format("ShaderCacheLoadStatus", 1, 4), banner.Text);
        Assert.Equal(0.25, banner.Fraction);
    }

    /// <summary>A first session (nothing recorded) still sends the end, so a banner shown by an earlier report never stays.</summary>
    [Fact]
    public void NothingRecordedStillReportsTheEnd()
    {
        var seen = new List<ShaderCacheLoadProgress>();
        void OnReported(ShaderCacheLoadProgress progress) => seen.Add(progress);
        ShaderCacheLoad.Reported += OnReported;
        try
        {
            ShaderCacheLoad.Wait(new RecordedCacheBackend());
        }
        finally
        {
            ShaderCacheLoad.Reported -= OnReported;
        }

        Assert.Equal([new ShaderCacheLoadProgress(0, 0, true)], seen);
    }

    /// <summary>"Cancelar" on the banner: the core's wait stops at its next step, the banner hides, and the end is
    /// reported.</summary>
    [Fact]
    public void CancelStopsTheWaitAndHidesTheBanner()
    {
        var banner = new ShaderCacheLoadBanner();
        var seen = new List<ShaderCacheLoadProgress>();
        void OnReported(ShaderCacheLoadProgress progress)
        {
            seen.Add(progress);
            banner.Show(progress);
            if (!progress.Finished)
            {
                banner.Cancel(); // the user taps "Cancelar" as soon as the banner appears
                Assert.False(banner.Visible);
            }
        }

        var backend = new RecordedCacheBackend(new(0, 10, false), new(5, 10, false), new(10, 10, true));
        ShaderCacheLoad.Reported += OnReported;
        try
        {
            ShaderCacheLoad.Wait(backend);
        }
        finally
        {
            ShaderCacheLoad.Reported -= OnReported;
        }

        Assert.True(ShaderCacheLoad.Token.IsCancellationRequested);
        Assert.Equal([new(0, 10, false), new(0, 0, true)], seen);
        Assert.False(banner.Visible);
    }
}
