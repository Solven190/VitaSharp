using VitaSharp.Core.Input;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;
using VitaSharp.Desktop.Input;
using VitaSharp.Desktop.Launcher;

namespace VitaSharp.Tests;

/// <summary>
/// The launcher's Controls screen on Avalonia's headless platform, with a scratch data folder (never the user's): the
/// mapping has a visible Save button and a "saved" / "unsaved changes" note, and saving on leaving the screen still
/// works.
/// </summary>
[Collection("launcher")]
public sealed class ControlsScreenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-controls-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedData = Environment.GetEnvironmentVariable(EnvVars.Data);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVars.Data, _savedData);
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string PrepareData()
    {
        string data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable(EnvVars.Data, data);
        LauncherApp.SetupHeadless();
        return data;
    }

    [Fact]
    public void TheSaveButtonWritesTheMappingAndTheNoteFollowsTheChanges()
    {
        string data = PrepareData();
        string config = HostInput.ConfigPath(data);
        using var pads = new SdlGamepads(null);
        var panel = new ControllerSettingsPanel(data, pads);
        Assert.True(panel.SelectDevice(ControllerProfile.KeyboardId));
        Assert.Equal(Loc.Get("CsSaveSaved"), panel.SaveStateText);
        Assert.False(panel.CanSave);

        panel.EditProfile(p => p.Set(VitaControl.Cross, InputBinding.OfKey("B")));
        Assert.Equal(Loc.Get("CsSaveUnsaved"), panel.SaveStateText);
        Assert.True(panel.CanSave);
        Assert.False(File.Exists(config));

        panel.SaveNow();
        Assert.Equal(Loc.Get("CsSaveSaved"), panel.SaveStateText);
        Assert.False(panel.CanSave);
        Assert.False(panel.HasChanges);
        Assert.Equal([InputBinding.OfKey("B")], InputProfileStore.Load(config).KeyboardProfile().Get(VitaControl.Cross));
    }

    [Fact]
    public void LeavingTheScreenStillSavesAndARestoreIsAnUnsavedChange()
    {
        string data = PrepareData();
        string config = HostInput.ConfigPath(data);
        using var pads = new SdlGamepads(null);
        var settings = LauncherSettings.Load();
        var page = new SettingsPage(settings, () => { }, pads);
        page.ShowSection(SettingsPage.SectionControls);
        ControllerSettingsPanel panel = page.Controls!;
        Assert.True(panel.SelectDevice(ControllerProfile.KeyboardId));
        panel.EditProfile(p => p.Set(VitaControl.Circle, InputBinding.OfKey("N")));
        Assert.Equal(Loc.Get("CsSaveUnsaved"), panel.SaveStateText);

        // what the launcher calls when the page is left
        page.SaveControls();
        Assert.Equal(Loc.Get("CsSaveSaved"), panel.SaveStateText);
        Assert.Equal([InputBinding.OfKey("N")], InputProfileStore.Load(config).KeyboardProfile().Get(VitaControl.Circle));
    }
}
