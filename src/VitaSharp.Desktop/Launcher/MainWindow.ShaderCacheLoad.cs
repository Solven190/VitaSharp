using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Core.Gpu;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>"Carregar o cache de shaders antes de jogar": while the game process builds its recorded shaders and pipelines before the game
/// starts (<see cref="ShaderCacheLoad"/> lines in its log), the status bar shows the progress and a "Cancelar" that
/// lets the game start right away (the game process reads it from its standard input).</summary>
public sealed partial class MainWindow
{
    readonly ProgressBar _shaderCacheLoadBar = new() { Width = 160, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    Button? _shaderCacheLoadCancel;

    /// <summary>The last progress shown (ui-render's check), null when no shader cache load is running.</summary>
    internal ShaderCacheLoadProgress? ShaderCacheLoadShownForTests { get; private set; }

    /// <summary>A line of the game's log (UI thread): shows the shader cache load progress it carries, if any.</summary>
    void OnShaderCacheLoadLine(string line)
    {
        if (!line.Contains(ShaderCacheLoad.LineTag, StringComparison.Ordinal) || !ShaderCacheLoad.TryParse(line, out ShaderCacheLoadProgress progress))
        {
            return;
        }

        ShowShaderCacheLoad(progress.Finished ? null : progress);
    }

    void ShowShaderCacheLoad(ShaderCacheLoadProgress? progress)
    {
        ShaderCacheLoadShownForTests = progress;
        bool on = progress != null;
        _shaderCacheLoadBar.IsVisible = on;
        _shaderCacheLoadCancel?.IsVisible = on;

        if (progress is not { } p)
        {
            if (_runningTitle != null)
            {
                SetStatus(S.Running(_runningTitle));
            }

            return;
        }

        _shaderCacheLoadBar.Maximum = Math.Max(1, p.Total);
        _shaderCacheLoadBar.Value = Math.Min(p.Done, p.Total);
        SetStatus(S.ShaderCacheLoadStatus(p.Done, p.Total));
    }

    /// <summary>The status bar's progress bar and "Cancelar" (added by <see cref="BuildStatusBar"/>).</summary>
    void AddShaderCacheLoadControls()
    {
        _shaderCacheLoadCancel = Kit.Click(_k.Text(S.ShaderCacheLoadCancel, 12, FontWeight.Medium, static p => p.TextSoft), CancelShaderCacheLoad);
        _shaderCacheLoadCancel.Margin = new Thickness(0, 7, 16, 7);
        _shaderCacheLoadCancel.IsVisible = _shaderCacheLoadBar.IsVisible;
        _shaderCacheLoadBar.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(_shaderCacheLoadCancel, Dock.Right);
        DockPanel.SetDock(_shaderCacheLoadBar, Dock.Right);
        _statusBar.Children.Add(_shaderCacheLoadCancel);
        _statusBar.Children.Add(_shaderCacheLoadBar);
    }

    void CancelShaderCacheLoad()
    {
        try
        {
            _running?.StandardInput.WriteLine(ShaderCacheLoad.CancelCommand);
            _running?.StandardInput.Flush();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            // the game process ended or its input is closed
        }

        ShowShaderCacheLoad(null);
    }
}
