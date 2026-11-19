using VitaSharp.Core.Localization;
using Android.Views;
using MauiReactor;
using VitaSharp.Android.Services;
using VitaSharp.Android.Ui;
using VitaSharp.Core.Input;
using VitaSharp.Mobile.Core.Input;

namespace VitaSharp.Android.Pages;

/// <summary>A connected controller: Android's device id, the profile id (vendor:product), its name, family and motion axes.</summary>
public sealed record ControllerEntry(int DeviceId, string Id, string Name, ControllerKind Kind, int VendorId, int ProductId, int[] Axes);

public sealed class ControlsState
{
    public List<ControllerEntry> Controllers { get; set; } = [];
    public int Selected { get; set; }
    public int Revision { get; set; }
    /// <summary>The save line of the mapping ("Salvo" / "Alteracoes nao salvas").</summary>
    public MappingSaveState Save { get; } = new();
}

/// <summary>
/// Controles (controles.html): the connected controllers, the mapping of the selected one (the core's controller
/// profile, shared with the desktop: positional A = Cross, triggers also L/R, PS on the Home/Mode button), the O/X
/// confirm swap and the stick deadzone, plus the on-screen controls (opacity, vibration, hidden while a controller is
/// connected). Profiles are stored in &lt;data&gt;/config/input.json.
/// </summary>
public sealed class ControlsView : Screen<ControlsState>
{
    protected override void OnMounted()
    {
        base.OnMounted();
        Scan();
    }

    void Scan()
    {
        var list = new List<ControllerEntry>();
        foreach (int id in InputDevice.GetDeviceIds() ?? [])
        {
            var d = InputDevice.GetDevice(id);
            if (d == null || d.IsVirtual)
                continue;
            bool pad = (d.Sources & InputSourceType.Gamepad) == InputSourceType.Gamepad || (d.Sources & InputSourceType.Joystick) == InputSourceType.Joystick;
            if (!pad)
                continue;
            string guid = AndroidGamepads.Guid(d.VendorId, d.ProductId);
            int[] axes = d.MotionRanges?.Select(r => (int)r.Axis).Distinct().ToArray() ?? [];
            list.Add(new ControllerEntry(id, guid, d.Name ?? Loc.Get("MController"), ControllerClassifier.Classify(guid, d.Name), d.VendorId, d.ProductId, axes));
        }
        SetState(s => { s.Controllers = list; s.Selected = Math.Min(s.Selected, Math.Max(0, list.Count - 1)); });
    }

    ControllerProfile? Profile()
    {
        if (State.Controllers.Count == 0)
            return null;
        ControllerEntry c = State.Controllers[State.Selected];
        return AppServices.InputProfiles.ProfileFor(c.Id, c.Name, c.Kind, isGamepad: true);
    }

    public override VisualNode Render()
    {
        var prefs = AppServices.Prefs;
        var profile = Profile();
        var kind = State.Controllers.Count > 0 ? State.Controllers[State.Selected].Kind : ControllerKind.Generic;
        return PageBody(Loc.Get("MControls"),
            VStack(
                SectionTitle(Loc.Get("MControllerConnected"), State.Controllers.Count > 0 ? $"{State.Controllers.Count}" : null),
                Card(State.Controllers.Count == 0
                    ? [Row(Loc.Get("MNoController"), Loc.Get("MRefresh"), Scan, detail: Loc.Get("MConnectController"))]
                    : State.Controllers.SelectMany((c, i) => new VisualNode?[]
                    {
                        i > 0 ? Divider() : null,
                        Row(c.Name, i == State.Selected ? Loc.Get("MSelected") : null, () => SetState(s => s.Selected = i),
                            detail: KindName(c.Kind), valueColor: Palette.Accent),
                    }).ToArray()),
                profile != null ? SectionTitle(Loc.Get("MMapping")) : null,
                profile != null ? Card(SaveRow(profile)) : null,
                profile != null ? Card(Mapping(profile, kind)) : null,
                profile != null ? Card(
                    ToggleRow(Loc.Get("MSwapConfirm"), profile.SwapConfirm, v => SaveProfile(p => p.SwapConfirm = v),
                        detail: Loc.Get("MSwapConfirmDetail")),
                    Divider(),
                    Row(Loc.Get("MStickDeadzone"), $"{profile.LeftStick.Deadzone * 100:0}%", () => Deadzone(profile))
                ) : null,
                SectionTitle(Loc.Get("MOnScreenControls")),
                Card(
                    Row(Loc.Get("MOpacity"), $"{prefs.OnScreenOpacity * 100:0}%", Opacity),
                    Divider(),
                    ToggleRow(Loc.Get("MHaptics"), prefs.Haptics, v => { prefs.Haptics = v; AppServices.SavePrefs(); SetState(s => s.Revision++); },
                        detail: Loc.Get("MHapticsDetail")),
                    prefs.Haptics ? Divider() : null,
                    prefs.Haptics ? Row(Loc.Get("MHapticStrength"), Mobile.Core.Settings.SettingsChoices.LabelOf(Mobile.Core.Settings.SettingsChoices.HapticStrengths, prefs.HapticStrength.ToString()), HapticStrength) : null,
                    Divider(),
                    ToggleRow(Loc.Get("MHideWithGamepad"), prefs.HideOnScreenWithGamepad,
                        v => { prefs.HideOnScreenWithGamepad = v; AppServices.SavePrefs(); SetState(s => s.Revision++); })
                ),
                SectionTitle(Loc.Get("MMotion")),
                Card(Row(Loc.Get("MMotionSensors"), AndroidMotionModes.Label(AndroidMotionModes.Resolve(prefs.Motion, PhoneHasMotionSensors())), ChooseMotion,
                    detail: Loc.Get("MMotionSensorsDetail"))),
                Note(Loc.Get("MControlsNote"))
            ));
    }

    static string KindName(ControllerKind k) => k switch
    {
        ControllerKind.PlayStation => "PlayStation",
        ControllerKind.Xbox => "Xbox",
        ControllerKind.Nintendo => "Nintendo",
        _ => Loc.Get("MGeneric"),
    };

    VisualNode?[] Mapping(ControllerProfile p, ControllerKind kind)
    {
        // the buttons are remapped with a press on the controller (as Configuracoes > Controles on the desktop); the
        // sticks are shown only
        VitaControl[] shown =
        [
            VitaControl.Cross, VitaControl.Circle, VitaControl.Square, VitaControl.Triangle, VitaControl.L, VitaControl.R,
            VitaControl.Start, VitaControl.Select, VitaControl.PS, VitaControl.Up, VitaControl.Down, VitaControl.Left,
            VitaControl.Right, VitaControl.LStickUp, VitaControl.RStickUp, VitaControl.PauseEmulation,
        ];
        var rows = new List<VisualNode?>();
        foreach (VitaControl c in shown)
        {
            if (rows.Count > 0)
            {
                rows.Add(Divider());
            }

            // a hotkey's combinations too (buttons held together, recorded as one by the core's BindingRecorder)
            string bound = string.Join(" / ", p.Get(c).Select(b => b.Describe(kind)).Concat(p.GetCombos(c).Select(k => k.Describe(kind))))
                is { Length: > 0 } s ? s : "\u2014";
            bool stick = c is VitaControl.LStickUp or VitaControl.RStickUp;
            rows.Add(Row(ControlName(c), bound, stick ? null : () => Remap(c),
                detail: VitaControls.IsHotkey(c) ? Loc.Get("CsHoldCombo") : null));
        }

        rows.Add(Divider());
        rows.Add(Row(Loc.Get("CsResetDefault"), null, ResetMapping));
        return rows.ToArray();
    }

    /// <summary>"Salvo" (every change is written at once) or "Alteracoes nao salvas" with a row that saves again, and
    /// whether the controller uses a saved profile or the default mapping.</summary>
    VisualNode?[] SaveRow(ControllerProfile profile)
    {
        bool saved = AppServices.InputProfiles.FindSaved(profile.Id, profile.Name) != null;
        string detail = Loc.Get(saved ? "CsProfileSaved" : "CsProfileDefault");
        if (!State.Save.Unsaved)
        {
            return [Row(detail, State.Save.Text, valueColor: Palette.Ok)];
        }

        // a tap writes the profile again
        return [Row(detail, State.Save.Text, () => SaveProfile(_ => { }), valueColor: Palette.Danger)];
    }

    /// <summary>Records the selected controller's input for <paramref name="control"/> (a press, or for the pause hotkey
    /// the buttons held together) and stores it.</summary>
    void Remap(VitaControl control)
    {
        if (State.Controllers.Count == 0 || MainActivity.Current is not { } activity)
        {
            return;
        }

        ControllerEntry c = State.Controllers[State.Selected];
        var capture = new PadCapture(c.DeviceId, c.Name, c.VendorId, c.ProductId, c.Axes, control);
        activity.BeginPadCapture(capture, ControlName(control), recorded => SaveProfile(p => BindingRecorder.Apply(p, control, recorded)));
    }

    void ResetMapping()
    {
        if (State.Controllers.Count == 0)
        {
            return;
        }

        ControllerEntry c = State.Controllers[State.Selected];
        AppServices.InputProfiles.Put(DefaultMappings.For(c.Id, c.Name, c.Kind, isGamepad: true));
        State.Save.Save(AppServices.InputProfiles.Save);
        SetState(s => s.Revision++);
    }

    static string ControlName(VitaControl c) => c switch
    {
        VitaControl.Cross => "\u2715",
        VitaControl.Circle => "\u25CB",
        VitaControl.Square => "\u25A1",
        VitaControl.Triangle => "\u25B3",
        VitaControl.PS => Loc.Get("MPsButton"),
        VitaControl.Up => Loc.Get("MDpad") + " \u2191",
        VitaControl.Down => Loc.Get("MDpad") + " \u2193",
        VitaControl.Left => Loc.Get("MDpad") + " \u2190",
        VitaControl.Right => Loc.Get("MDpad") + " \u2192",
        VitaControl.LStickUp => Loc.Get("MLeftStick"),
        VitaControl.RStickUp => Loc.Get("MRightStick"),
        VitaControl.Start => "START",
        VitaControl.Select => "SELECT",
        VitaControl.PauseEmulation => Loc.Get("CtlPauseEmulation"),
        _ => c.ToString(),
    };

    void SaveProfile(Action<ControllerProfile> change)
    {
        if (Profile() is not { } p)
            return;
        change(p);
        AppServices.InputProfiles.Put(p);
        State.Save.Save(AppServices.InputProfiles.Save);
        SetState(s => s.Revision++);
    }

    async void Deadzone(ControllerProfile p)
    {
        var choices = new[] { 0, 5, 10, 15, 20, 25 }.Select(v => new Mobile.Core.Settings.Choice(v.ToString(), $"{v}%")).ToList();
        var v = await Choose(Loc.Get("MDeadzone"), choices);
        if (v == null)
            return;
        float dz = int.Parse(v) / 100f;
        SaveProfile(x => { x.LeftStick.Deadzone = dz; x.RightStick.Deadzone = dz; });
    }

    static bool PhoneHasMotionSensors()
    {
        var sm = (global::Android.Hardware.SensorManager?)AppServices.AppContext.GetSystemService(global::Android.Content.Context.SensorService);
        return sm?.GetDefaultSensor(global::Android.Hardware.SensorType.Accelerometer) != null
            && sm.GetDefaultSensor(global::Android.Hardware.SensorType.Gyroscope) != null;
    }

    /// <summary>"Movimento": the phone's sensors (the default when it has them), a controller's, or off (the desktop's
    /// motion source setting; the hardware keyboard's tilt keys take over while no sensor device feeds).</summary>
    async void ChooseMotion()
    {
        bool phone = PhoneHasMotionSensors();
        var choices = new List<Mobile.Core.Settings.Choice>
        {
            new(AndroidMotionModes.Phone, AndroidMotionModes.Label(AndroidMotionMode.Phone) + (phone ? "" : Loc.Get("MNotOnThisDevice"))),
            new(AndroidMotionModes.Controller, AndroidMotionModes.Label(AndroidMotionMode.Controller) + " (Android 12+)"),
            new(AndroidMotionModes.Off, AndroidMotionModes.Label(AndroidMotionMode.Off)),
        };
        var v = await Choose(Loc.Get("MMotion"), choices);
        if (v == null)
            return;
        AppServices.Prefs.Motion = v;
        AppServices.SavePrefs();
        SetState(s => s.Revision++);
    }

    async void HapticStrength()
    {
        var v = await Choose(Loc.Get("MHapticStrength"), Mobile.Core.Settings.SettingsChoices.HapticStrengths);
        if (v == null)
        {
            return;
        }

        AppServices.Prefs.HapticStrength = int.Parse(v);
        AppServices.SavePrefs();
        SetState(s => s.Revision++);
    }

    async void Opacity()
    {
        var choices = new[] { 20, 35, 55, 75, 100 }.Select(v => new Mobile.Core.Settings.Choice(v.ToString(), $"{v}%")).ToList();
        var v = await Choose(Loc.Get("MControlsOpacity"), choices);
        if (v == null)
            return;
        AppServices.Prefs.OnScreenOpacity = int.Parse(v) / 100f;
        AppServices.SavePrefs();
        SetState(s => s.Revision++);
    }
}
