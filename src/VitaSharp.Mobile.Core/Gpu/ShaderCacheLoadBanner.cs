using VitaSharp.Core.Gpu;
using VitaSharp.Core.Localization;

namespace VitaSharp.Mobile.Core.Gpu;

/// <summary>
/// "Carregar o cache de shaders antes de jogar" on the Android game screen: while the game process builds the shaders
/// and pipelines recorded in earlier sessions (<see cref="ShaderCacheLoad.Reported"/>), a banner over the game shows the
/// progress and a "Cancelar" that lets the game start right away (<see cref="ShaderCacheLoad.Cancel"/>), the same text
/// and button as the desktop launcher's status bar. Platform-neutral, so it is unit-tested on the desktop.
/// </summary>
public sealed class ShaderCacheLoadBanner
{
    /// <summary>The banner's text, null when it is hidden (no load running, or the load ended or was cancelled).</summary>
    public string? Text { get; private set; }

    /// <summary>The bar's fraction, 0..1.</summary>
    public double Fraction { get; private set; }

    public bool Visible => Text != null;

    /// <summary>The "Cancelar" button's text.</summary>
    public static string CancelText => Loc.Get("ShaderCacheLoadCancel");

    /// <summary>A <see cref="ShaderCacheLoad.Reported"/> progress: shown, or hidden when it is the end.</summary>
    public void Show(ShaderCacheLoadProgress progress)
    {
        if (progress.Finished)
        {
            Hide();
            return;
        }

        Text = Loc.Format("ShaderCacheLoadStatus", progress.Done, progress.Total);
        Fraction = progress.Total > 0 ? Math.Clamp((double)progress.Done / progress.Total, 0, 1) : 0;
    }

    /// <summary>"Cancelar": the game starts at once (what is left keeps building in the background) and the banner goes.</summary>
    public void Cancel()
    {
        ShaderCacheLoad.Cancel();
        Hide();
    }

    public void Hide()
    {
        Text = null;
        Fraction = 0;
    }
}
