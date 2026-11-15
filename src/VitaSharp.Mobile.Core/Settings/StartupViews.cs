using VitaSharp.Core.Localization;

namespace VitaSharp.Mobile.Core.Settings;

/// <summary>
/// "Ao abrir o emulador" (<see cref="AppPreferences.StartupView"/>), the desktop launcher's option of the same name and
/// values: "library" (the default) opens the app's screens, "system" boots the firmware's own shell (Sistema do Vita)
/// right away, like turning a console on; the screens stay behind it.
/// </summary>
public static class StartupViews
{
    public const string Library = "library", System = "system";

    public static IReadOnlyList<Choice> Choices =>
    [
        new(Library, Loc.Get("Library")),
        new(System, Loc.Get("MVitaSystem")),
    ];

    /// <summary>
    /// Whether this start of the app boots the shell: the setting says so, the shell is installed, and the start is a
    /// real one (not the activity restored by Android, nor a start that already opens a game: a shortcut, adb).
    /// </summary>
    public static bool OpensSystem(string? setting, bool shellInstalled, bool restored, bool gameRequested)
    {
        return setting == System && shellInstalled && !restored && !gameRequested;
    }
}
