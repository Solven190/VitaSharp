using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using VitaSharp.Core.Cpu;
using VitaSharp.Desktop.Launcher.Ui;
using S = VitaSharp.Desktop.Launcher.Ui.Strings;

namespace VitaSharp.Desktop.Launcher;

/// <summary>"Precompilar código ARM": while the game process translates its ARM code before the game starts
/// (<see cref="CodePrecompileLog"/> lines in its log), the status bar shows that progress in its own bar with its own
/// "Cancelar" (the game process reads it from its standard input and starts at once). Separate from the shader cache
/// load's bar (<see cref="MainWindow.ShowShaderCacheLoad"/>): the two waits are different work.</summary>
public sealed partial class MainWindow
{
    readonly ProgressBar _codePrecompileBar = new() { Width = 160, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    Button? _codePrecompileCancel;

    /// <summary>The last progress shown (ui-render's check), null when no code precompilation is running.</summary>
    internal CodePrecompileProgress? CodePrecompileShownForTests { get; private set; }

    /// <summary>A line of the game's log (UI thread): shows the code precompilation progress it carries, if any.</summary>
    void OnCodePrecompileLine(string line)
    {
        if (!line.Contains(CodePrecompileLog.LineTag, StringComparison.Ordinal) || !CodePrecompileLog.TryParse(line, out CodePrecompileProgress progress))
        {
            return;
        }

        ShowCodePrecompile(progress.Finished ? null : progress);
    }

    void ShowCodePrecompile(CodePrecompileProgress? progress)
    {
        CodePrecompileShownForTests = progress;
        bool on = progress != null;
        _codePrecompileBar.IsVisible = on;
        _codePrecompileCancel?.IsVisible = on;

        if (progress is not { } p)
        {
            if (_runningTitle != null)
            {
                SetStatus(S.Running(_runningTitle));
            }

            return;
        }

        _codePrecompileBar.Maximum = Math.Max(1, p.Total);
        _codePrecompileBar.Value = Math.Min(p.Done, p.Total);
        SetStatus(S.PrecompileCodeStatus(p.Done, p.Total));
    }

    /// <summary>The status bar's code precompilation bar and "Cancelar" (added by <see cref="BuildStatusBar"/>).</summary>
    void AddCodePrecompileControls()
    {
        _codePrecompileCancel = Kit.Click(_k.Text(S.PrecompileCodeCancel, 12, FontWeight.Medium, static p => p.TextSoft), CancelCodePrecompile);
        _codePrecompileCancel.Margin = new Thickness(0, 7, 16, 7);
        _codePrecompileCancel.IsVisible = _codePrecompileBar.IsVisible;
        _codePrecompileBar.Margin = new Thickness(0, 0, 12, 0);
        DockPanel.SetDock(_codePrecompileCancel, Dock.Right);
        DockPanel.SetDock(_codePrecompileBar, Dock.Right);
        _statusBar.Children.Add(_codePrecompileCancel);
        _statusBar.Children.Add(_codePrecompileBar);
    }

    void CancelCodePrecompile()
    {
        try
        {
            _running?.StandardInput.WriteLine(CodePrecompileLog.CancelCommand);
            _running?.StandardInput.Flush();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException)
        {
            // the game process ended or its input is closed
        }

        ShowCodePrecompile(null);
    }
}
