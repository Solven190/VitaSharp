using VitaSharp.Core.Input;
using VitaSharp.Mobile.Core.Input;

namespace VitaSharp.Tests;

/// <summary>
/// Remapping a controller in the Android app (Controles > Mapeamento, Mobile.Core/Input/PadCapture.cs): the press on
/// the controller becomes the binding the desktop launcher would store for it (InputCapture), and the remapped profile
/// then drives the game input through the same mapping as a game session (AndroidGamepads).
/// </summary>
public sealed class PadCaptureTests
{
    private const int Device = 7;
    private static readonly int[] s_standardAxes =
    [
        AndroidKeys.AxisX, AndroidKeys.AxisY, AndroidKeys.AxisZ, AndroidKeys.AxisRz, AndroidKeys.AxisHatX,
        AndroidKeys.AxisHatY, AndroidKeys.AxisBrake, AndroidKeys.AxisGas,
    ];

    private static PadCapture NewCapture(VitaControl control = VitaControl.Cross, Func<long>? clock = null) =>
        new(Device, "Wireless Controller", 0x054C, 0x09CC, s_standardAxes, control, clock);

    private static Func<int, float> Axes(Dictionary<int, float> values) => axis => values.GetValueOrDefault(axis);

    [Fact]
    public void AButtonPressIsTheButtonBinding()
    {
        PadCapture capture = NewCapture();
        Assert.Null(capture.OnKey(Device, AndroidKeys.ButtonY, false));
        InputBinding? bound = capture.OnKey(Device, AndroidKeys.ButtonY, true)?.Single;
        Assert.Equal(InputBinding.Of(AndroidKeys.Button(AndroidKeys.ButtonY)!.Value), bound);
    }

    [Fact]
    public void ATriggerOnTheBrakeAxisIsTheTriggerBinding()
    {
        PadCapture capture = NewCapture();
        Assert.Null(capture.OnMotion(Device, Axes(new() { [AndroidKeys.AxisBrake] = 0.2f })));
        Assert.Equal(InputBinding.Of(PadAxis.LeftTrigger), capture.OnMotion(Device, Axes(new() { [AndroidKeys.AxisBrake] = 0.9f }))?.Single);
    }

    [Fact]
    public void TheHatIsTheDpadAndAStickIsItsDirection()
    {
        Assert.Equal(InputBinding.Of(PadButton.DpadLeft), NewCapture().OnMotion(Device, Axes(new() { [AndroidKeys.AxisHatX] = -1f }))?.Single);
        Assert.Equal(InputBinding.Of(PadAxis.LeftX, -1), NewCapture().OnMotion(Device, Axes(new() { [AndroidKeys.AxisX] = -0.95f }))?.Single);
        Assert.Equal(InputBinding.Of(PadAxis.RightY, 1), NewCapture().OnMotion(Device, Axes(new() { [AndroidKeys.AxisRz] = 0.95f }))?.Single);
        Assert.Null(NewCapture().OnMotion(Device, Axes(new() { [AndroidKeys.AxisX] = 0.3f })));
    }

    [Fact]
    public void AnotherControllerOrANonControllerKeyIsIgnored()
    {
        PadCapture capture = NewCapture();
        Assert.Null(capture.OnKey(Device + 1, AndroidKeys.ButtonA, true));
        Assert.Null(capture.OnKey(Device, 29, true)); // KEYCODE_A of a keyboard
        Assert.Equal(InputBinding.Of(AndroidKeys.Button(AndroidKeys.ButtonA)!.Value), capture.OnKey(Device, AndroidKeys.ButtonA, true)?.Single);
    }

    [Fact]
    public void ARemappedButtonDrivesTheGameInput()
    {
        var store = new InputProfileStore();
        var pads = new AndroidGamepads(store);
        pads.Connect(Device, "Wireless Controller", 0x054C, 0x09CC, s_standardAxes);
        string guid = AndroidGamepads.Guid(0x054C, 0x09CC);
        ControllerProfile profile = store.ProfileFor(guid, "Wireless Controller", ControllerClassifier.Classify(guid, "Wireless Controller"), isGamepad: true);

        PadCapture capture = NewCapture();
        InputBinding bound = capture.OnKey(Device, AndroidKeys.ButtonY, true)!.Value.Single!.Value;
        profile.Set(VitaControl.Cross, bound);
        store.Put(profile);

        // a fresh session reads the stored profile (as the game process does from input.json)
        var session = new AndroidGamepads(store);
        session.Connect(Device, "Wireless Controller", 0x054C, 0x09CC, s_standardAxes);
        session.OnKey(Device, AndroidKeys.ButtonY, true);
        Assert.True(session.Map().State.Buttons.HasFlag(VitaSharp.Core.Host.VitaButtons.Cross));
        session.OnKey(Device, AndroidKeys.ButtonY, false);
        session.OnKey(Device, AndroidKeys.ButtonA, true);
        Assert.False(session.Map().State.Buttons.HasFlag(VitaSharp.Core.Host.VitaButtons.Cross));
    }

    /// <summary>The pause hotkey recorded as L3 + R3 pressed one at a time: only the whole combination is stored, once
    /// both are released and the settle time passed; L3 alone is never recorded on the way.</summary>
    [Fact]
    public void AHotkeyRecordsOnlyTheFinalCombination()
    {
        long now = 1000;
        PadCapture capture = NewCapture(VitaControl.PauseEmulation, () => now);
        Assert.True(capture.RecordsCombination);
        Assert.Null(capture.OnKey(Device, AndroidKeys.ButtonThumbL, true));
        now += 50;
        Assert.Null(capture.OnKey(Device, AndroidKeys.ButtonThumbR, true));
        Assert.Equal(2, capture.Parts.Count);
        now += 50;
        Assert.Null(capture.OnKey(Device, AndroidKeys.ButtonThumbL, false));
        Assert.Null(capture.OnKey(Device, AndroidKeys.ButtonThumbR, false));
        Assert.Null(capture.Tick()); // released, but not settled yet
        now += BindingRecorder.SettleMs - 1;
        Assert.Null(capture.Tick());
        now += 1;
        RecordedInput recorded = capture.Tick()!.Value;
        Assert.Null(recorded.Single);
        Assert.Equal([InputBinding.Of(PadButton.LeftStick), InputBinding.Of(PadButton.RightStick)], recorded.Combo!.Value.Parts);

        var profile = new ControllerProfile { Id = "test" };
        BindingRecorder.Apply(profile, VitaControl.PauseEmulation, recorded);
        Assert.Single(profile.GetCombos(VitaControl.PauseEmulation));
        Assert.Empty(profile.Get(VitaControl.PauseEmulation));
    }

    /// <summary>The recorded combination fires the pause in a game only with every button of it held.</summary>
    [Fact]
    public void ARecordedCombinationPausesOnlyWithAllItsButtonsHeld()
    {
        var store = new InputProfileStore();
        string guid = AndroidGamepads.Guid(0x054C, 0x09CC);
        ControllerProfile profile = store.ProfileFor(guid, "Wireless Controller", ControllerClassifier.Classify(guid, "Wireless Controller"), isGamepad: true);
        profile.AddCombo(VitaControl.PauseEmulation, new InputCombo([InputBinding.Of(PadButton.LeftStick), InputBinding.Of(PadButton.RightStick)]), replace: true);
        store.Put(profile);

        var session = new AndroidGamepads(store);
        session.Connect(Device, "Wireless Controller", 0x054C, 0x09CC, s_standardAxes);
        Assert.False(session.PauseHotkeyPressed());
        session.OnKey(Device, AndroidKeys.ButtonThumbL, true);
        Assert.False(session.PauseHotkeyPressed());
        session.OnKey(Device, AndroidKeys.ButtonThumbR, true);
        Assert.True(session.PauseHotkeyPressed());
        Assert.False(session.PauseHotkeyPressed()); // held: once per press
        session.OnKey(Device, AndroidKeys.ButtonThumbR, false);
        Assert.False(session.PauseHotkeyPressed());
        session.OnKey(Device, AndroidKeys.ButtonThumbR, true);
        Assert.True(session.PauseHotkeyPressed());
    }

    /// <summary>The mapping screen's save line: "Salvo" after a write, "Alterações não salvas" when the write failed,
    /// "Salvo" again once a retry reaches the file.</summary>
    [Fact]
    public void TheSaveLineSaysWhetherTheMappingReachedTheFile()
    {
        var state = new MappingSaveState();
        Assert.True(state.Save(() => { }));
        Assert.False(state.Unsaved);
        Assert.Equal(VitaSharp.Core.Localization.Loc.Get("CsSaveSaved"), state.Text);
        Assert.False(state.Save(() => throw new IOException("disk full")));
        Assert.True(state.Unsaved);
        Assert.Equal(VitaSharp.Core.Localization.Loc.Get("CsSaveUnsaved"), state.Text);
        Assert.True(state.Save(() => { }));
        Assert.Equal(VitaSharp.Core.Localization.Loc.Get("CsSaveSaved"), state.Text);
    }

    /// <summary>A game control on a controller still takes the first press, at once (no settle wait).</summary>
    [Fact]
    public void AGameControlTakesTheFirstPressWithoutWaiting()
    {
        PadCapture capture = NewCapture(VitaControl.Triangle, () => 0);
        Assert.False(capture.RecordsCombination);
        RecordedInput recorded = capture.OnKey(Device, AndroidKeys.ButtonThumbL, true)!.Value;
        Assert.Equal(InputBinding.Of(PadButton.LeftStick), recorded.Single);
        Assert.True(recorded.Replace);
    }
}
