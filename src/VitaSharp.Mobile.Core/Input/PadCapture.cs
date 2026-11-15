using VitaSharp.Core.Input;

namespace VitaSharp.Mobile.Core.Input;

/// <summary>
/// "Aperte um botão" of Controles > Mapeamento: the next button, trigger, D-pad direction or stick direction pressed
/// on one controller, as the binding the controller profile stores. The Android key and motion events go through the
/// same translation as in a game (<see cref="AndroidGamepads"/>, the standard gamepad layout), and the press is found
/// by the desktop launcher's rule (<see cref="InputCapture.Detect"/> against the state when the capture started), so a
/// binding made on the phone is the one the desktop would make for the same press.
/// </summary>
public sealed class PadCapture
{
    private readonly AndroidGamepads _pads = new();
    private readonly int _deviceId;
    private readonly PadSnapshot _baseline;

    /// <summary>Starts listening to <paramref name="deviceId"/> (its name, ids and motion axes as Android reports them).</summary>
    public PadCapture(int deviceId, string name, int vendorId, int productId, IReadOnlyCollection<int> axes)
    {
        _deviceId = deviceId;
        _pads.Connect(deviceId, name, vendorId, productId, axes);
        _baseline = _pads.Snapshot(deviceId)!;
    }

    /// <summary>The controller this capture listens to.</summary>
    public int DeviceId => _deviceId;

    /// <summary>A key event of the controller: the binding when it is the press looked for, else null.</summary>
    public InputBinding? OnKey(int deviceId, int keyCode, bool down)
    {
        if (deviceId != _deviceId || !_pads.OnKey(deviceId, keyCode, down))
        {
            return null;
        }

        return Detect();
    }

    /// <summary>A motion event of the controller (sticks, triggers, hat): the binding when it is the press looked for.</summary>
    public InputBinding? OnMotion(int deviceId, Func<int, float> axisValue)
    {
        if (deviceId != _deviceId || !_pads.OnMotion(deviceId, axisValue))
        {
            return null;
        }

        return Detect();
    }

    private InputBinding? Detect()
    {
        return _pads.Snapshot(_deviceId) is { } now ? InputCapture.Detect(_baseline, now) : null;
    }
}
