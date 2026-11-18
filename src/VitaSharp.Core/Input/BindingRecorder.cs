namespace VitaSharp.Core.Input;

/// <summary>How a recorded input is stored: replacing the control's bindings, or added to them.</summary>
public enum RecordMode
{
    /// <summary>The recorded input becomes the control's only binding (single bindings and combinations).</summary>
    Replace,
    /// <summary>The recorded input is added to the control's bindings.</summary>
    Add,
    /// <summary>Several buttons held together, added to the control's bindings (<see cref="InputCombo"/>).</summary>
    Combo,
}

/// <summary>What a recording produced: one input, or a combination of buttons held together.</summary>
public readonly record struct RecordedInput(InputBinding? Single, InputCombo? Combo, bool Replace);

/// <summary>
/// Records the controller input for one control of the configuration screen. A game control takes the first button or
/// axis moved (<see cref="InputCapture.Detect"/>). An emulator hotkey (<see cref="VitaControls.IsHotkey"/>) on a
/// controller may be a combination (<see cref="InputCombo"/>), so its recording waits: it keeps the largest set of
/// buttons held at the same time and ends only when every button has been released for <see cref="SettleMs"/>. A
/// combination pressed one button at a time (L3, then L3 + R3) is recorded once, as the whole combination, never as
/// its first button followed by the combination.
/// </summary>
public sealed class BindingRecorder
{
    /// <summary>Time with every button released before a combination is taken as complete: a button that slips for a
    /// moment while the next one is pressed (a stick click) does not end the recording.</summary>
    public const long SettleMs = 400;

    private readonly PadSnapshot _baseline;
    private readonly List<InputBinding> _pressOrder = [];
    private readonly List<InputBinding> _largest = [];
    private long _releasedSinceMs = -1;

    public BindingRecorder(VitaControl control, RecordMode mode, bool controller, PadSnapshot baseline)
    {
        Control = control;
        Mode = mode;
        _baseline = baseline.Clone();
        RecordsCombination = controller && (mode == RecordMode.Combo || VitaControls.IsHotkey(control));
    }

    public VitaControl Control { get; }

    public RecordMode Mode { get; }

    /// <summary>The recording waits for buttons held together (a hotkey on a controller) instead of taking the first
    /// input moved.</summary>
    public bool RecordsCombination { get; }

    /// <summary>The largest set of buttons held together so far, in the order they were pressed (for the screen).</summary>
    public IReadOnlyList<InputBinding> Parts => _largest;

    /// <summary>Feeds the controller state at <paramref name="nowMs"/> (any monotonic millisecond clock). Returns the
    /// recorded input once the recording is complete, null while it goes on.</summary>
    public RecordedInput? Update(PadSnapshot now, long nowMs)
    {
        if (!RecordsCombination)
        {
            return InputCapture.Detect(_baseline, now) is { } detected
                ? new RecordedInput(detected, null, Mode == RecordMode.Replace)
                : null;
        }

        List<InputBinding> held = InputCapture.Held(_baseline, now);
        if (held.Count > 0)
        {
            _releasedSinceMs = -1;
            foreach (InputBinding binding in held)
            {
                if (!_pressOrder.Contains(binding))
                {
                    _pressOrder.Add(binding);
                }
            }

            if (held.Count > _largest.Count)
            {
                _largest.Clear();
                foreach (InputBinding binding in _pressOrder)
                {
                    if (held.Contains(binding))
                    {
                        _largest.Add(binding);
                    }
                }
            }

            return null;
        }

        if (_largest.Count == 0)
        {
            return null;
        }

        if (_releasedSinceMs < 0)
        {
            _releasedSinceMs = nowMs;
            return null;
        }

        if (nowMs - _releasedSinceMs < SettleMs)
        {
            return null;
        }

        bool replace = Mode == RecordMode.Replace;
        return _largest.Count == 1
            ? new RecordedInput(_largest[0], null, replace)
            : new RecordedInput(null, new InputCombo(_largest.ToArray()), replace);
    }

    /// <summary>Stores a recorded input in <paramref name="profile"/> as <see cref="Mode"/> says.</summary>
    public static void Apply(ControllerProfile profile, VitaControl control, RecordedInput recorded)
    {
        if (recorded.Combo is { } combo)
        {
            profile.AddCombo(control, combo, recorded.Replace);
        }
        else if (recorded.Single is { } single)
        {
            if (recorded.Replace)
            {
                profile.Set(control, single);
            }
            else
            {
                profile.Add(control, single);
            }
        }
    }
}
