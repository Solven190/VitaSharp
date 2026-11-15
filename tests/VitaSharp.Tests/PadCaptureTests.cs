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

    private static PadCapture NewCapture() => new(Device, "Wireless Controller", 0x054C, 0x09CC, s_standardAxes);

    private static Func<int, float> Axes(Dictionary<int, float> values) => axis => values.GetValueOrDefault(axis);

    [Fact]
    public void AButtonPressIsTheButtonBinding()
    {
        PadCapture capture = NewCapture();
        Assert.Null(capture.OnKey(Device, AndroidKeys.ButtonY, false));
        InputBinding? bound = capture.OnKey(Device, AndroidKeys.ButtonY, true);
        Assert.Equal(InputBinding.Of(AndroidKeys.Button(AndroidKeys.ButtonY)!.Value), bound);
    }

    [Fact]
    public void ATriggerOnTheBrakeAxisIsTheTriggerBinding()
    {
        PadCapture capture = NewCapture();
        Assert.Null(capture.OnMotion(Device, Axes(new() { [AndroidKeys.AxisBrake] = 0.2f })));
        Assert.Equal(InputBinding.Of(PadAxis.LeftTrigger), capture.OnMotion(Device, Axes(new() { [AndroidKeys.AxisBrake] = 0.9f })));
    }

    [Fact]
    public void TheHatIsTheDpadAndAStickIsItsDirection()
    {
        Assert.Equal(InputBinding.Of(PadButton.DpadLeft), NewCapture().OnMotion(Device, Axes(new() { [AndroidKeys.AxisHatX] = -1f })));
        Assert.Equal(InputBinding.Of(PadAxis.LeftX, -1), NewCapture().OnMotion(Device, Axes(new() { [AndroidKeys.AxisX] = -0.95f })));
        Assert.Equal(InputBinding.Of(PadAxis.RightY, 1), NewCapture().OnMotion(Device, Axes(new() { [AndroidKeys.AxisRz] = 0.95f })));
        Assert.Null(NewCapture().OnMotion(Device, Axes(new() { [AndroidKeys.AxisX] = 0.3f })));
    }

    [Fact]
    public void AnotherControllerOrANonControllerKeyIsIgnored()
    {
        PadCapture capture = NewCapture();
        Assert.Null(capture.OnKey(Device + 1, AndroidKeys.ButtonA, true));
        Assert.Null(capture.OnKey(Device, 29, true)); // KEYCODE_A of a keyboard
        Assert.Equal(InputBinding.Of(AndroidKeys.Button(AndroidKeys.ButtonA)!.Value), capture.OnKey(Device, AndroidKeys.ButtonA, true));
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
        InputBinding bound = capture.OnKey(Device, AndroidKeys.ButtonY, true)!.Value;
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
}
