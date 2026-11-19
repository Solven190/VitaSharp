using VitaSharp.Core.Input;

namespace VitaSharp.Mobile.Core.Input;

/// <summary>
/// The recording of Controles > Mapeamento on one controller, through the core's <see cref="BindingRecorder"/> (the
/// desktop launcher's recorder): a game control takes the next button, trigger, D-pad direction or stick direction
/// pressed; an emulator hotkey (<see cref="VitaControl.PauseEmulation"/>) takes the whole combination of buttons held
/// together, recorded once all of them are released (never its first button on its own). The Android key and motion
/// events go through the same translation as in a game (<see cref="AndroidGamepads"/>, the standard gamepad layout),
/// so a binding made on the phone is the one the desktop would make for the same press.
/// </summary>
public sealed class PadCapture
{
    private readonly AndroidGamepads _pads = new();
    private readonly int _deviceId;
    private readonly BindingRecorder _recorder;
    private readonly Func<long> _clockMs;

    /// <summary>Starts recording <paramref name="control"/> on <paramref name="deviceId"/> (its name, ids and motion axes
    /// as Android reports them). <paramref name="clockMs"/>: a monotonic millisecond clock (the combination's settle time).</summary>
    public PadCapture(int deviceId, string name, int vendorId, int productId, IReadOnlyCollection<int> axes, VitaControl control,
        Func<long>? clockMs = null)
    {
        _deviceId = deviceId;
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        _pads.Connect(deviceId, name, vendorId, productId, axes);
        Kind = ControllerClassifier.Classify(AndroidGamepads.Guid(vendorId, productId), name);
        PadSnapshot baseline = _pads.Snapshot(deviceId)!;
        // a new recording replaces the control's bindings (the desktop's default); a hotkey still records a combination
        _recorder = new BindingRecorder(control, RecordMode.Replace, controller: true, baseline);
    }

    /// <summary>The controller this capture listens to.</summary>
    public int DeviceId => _deviceId;

    public VitaControl Control => _recorder.Control;

    /// <summary>The controller's family (the names of its buttons on the screen).</summary>
    public ControllerKind Kind { get; }

    /// <summary>The recording waits for buttons held together and their release (a hotkey), so the screen keeps asking
    /// until <see cref="Tick"/> or an event ends it.</summary>
    public bool RecordsCombination => _recorder.RecordsCombination;

    /// <summary>The buttons of the combination held so far (for the screen).</summary>
    public IReadOnlyList<InputBinding> Parts => _recorder.Parts;

    /// <summary>A key event of the controller: the recorded input once the recording is complete, else null.</summary>
    public RecordedInput? OnKey(int deviceId, int keyCode, bool down)
    {
        if (deviceId != _deviceId || !_pads.OnKey(deviceId, keyCode, down))
        {
            return null;
        }

        return Update();
    }

    /// <summary>A motion event of the controller (sticks, triggers, hat): the recorded input once complete.</summary>
    public RecordedInput? OnMotion(int deviceId, Func<int, float> axisValue)
    {
        if (deviceId != _deviceId || !_pads.OnMotion(deviceId, axisValue))
        {
            return null;
        }

        return Update();
    }

    /// <summary>Called on a timer while the recording runs: a combination ends once every button has been released for
    /// <see cref="BindingRecorder.SettleMs"/>, which no controller event announces.</summary>
    public RecordedInput? Tick() => Update();

    private RecordedInput? Update()
    {
        return _pads.Snapshot(_deviceId) is { } now ? _recorder.Update(now, _clockMs()) : null;
    }
}
