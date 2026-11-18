using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Core.Gpu;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>"Precompilar shaders": while the game process builds its recorded shaders and pipelines before the game
/// starts (<see cref="ShaderPrecompile"/> lines in its log), the status bar shows the progress and a "Cancelar" that
/// lets the game start right away (the game process reads it from its standard input).</summary>
public sealed partial class MainWindow
{
    readonly ProgressBar _precompileBar = new() { Width = 160, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    Button? _precompileCancel;

    /// <summary>The last progress shown (ui-render's check), null when no precompile is running.</summary>
    internal ShaderPrecompileProgress? PrecompileShownForTests { get; private set; }

    /// <summary>A line of the game's log (UI thread): shows the precompile progress it carries, if any.</summary>
    void OnPrecompileLine(string line)
    {
        if (!line.Contains("[precompile] ", StringComparison.Ordinal) || !ShaderPrecompile.TryParse(line, out ShaderPrecompileProgress progress))
        {
            return;
        }

        ShowPrecompile(progress.Finished ? null : progress);
    }

    void ShowPrecompile(ShaderPrecompileProgress? progress)
    {
        PrecompileShownForTests = progress;
        bool on = progress != null;
        _precompileBar.IsVisible = on;
        _precompileCancel?.IsVisible = on;

        if (progress is not { } p)
        {
            if (_runningTitle != null)
            {
                SetStatus(S.Running(_runningTitle));
            }

            return;
        }

        _precompileBar.Maximum = Math.Max(1, p.Total);
        _precompileBar.Value = Math.Min(p.Done, p.Total);
        SetStatus(S.PrecompileProgress(p.Done, p.Total));
    }

    /// <summary>The status bar's progress bar and "Cancelar" (added by <see cref="BuildStatusBar"/>).</summary>
    void AddPrecompileControls()
    {
        _precompileCancel = Kit.Click(_k.Text(S.PrecompileCancel, 12, FontWeight.Medium, static p => p.TextSoft), CancelPrecompile);
        _precompileCancel.Margin = new Thickness(0, 7, 16, 7);
        _precompileCancel.IsVisible = _precompileBar.IsVisible;
        _precompileBar.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(_precompileCancel, Dock.Right);
        DockPanel.SetDock(_precompileBar, Dock.Right);
        _statusBar.Children.Add(_precompileCancel);
        _statusBar.Children.Add(_precompileBar);
    }

    void CancelPrecompile()
    {
        try
        {
            _running?.StandardInput.WriteLine(ShaderPrecompile.CancelCommand);
            _running?.StandardInput.Flush();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            // the game process ended or its input is closed
        }

        ShowPrecompile(null);
    }
}
