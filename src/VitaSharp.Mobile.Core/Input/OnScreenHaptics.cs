namespace VitaSharp.Mobile.Core.Input;

/// <summary>
/// The vibration of the on-screen controls (Ajustes > Controles > "Vibracao"): one short pulse for every control a
/// finger newly presses (<see cref="OnScreenPad.ControlPressed"/>), also while another finger holds a stick or a
/// button. The old rule vibrated only when no control was held before the touch, so a button pressed with the thumb
/// on the analog stick gave no feedback.
/// </summary>
public sealed class OnScreenHaptics
{
    /// <summary>The strengths of the option: 1 = weak, 2 = medium (the default), 3 = strong.</summary>
    public const int Weak = 1;
    public const int Medium = 2;
    public const int Strong = 3;

    /// <summary>Length of one pulse in milliseconds (a key tap).</summary>
    public const int PulseMilliseconds = 18;

    private readonly Action<int> _vibrate;

    /// <param name="pad">The on-screen pad whose presses vibrate.</param>
    /// <param name="vibrate">Plays one pulse with the given amplitude (1..255, Android's VibrationEffect scale).</param>
    public OnScreenHaptics(OnScreenPad pad, Action<int> vibrate)
    {
        _vibrate = vibrate;
        pad.ControlPressed += OnPressed;
    }

    /// <summary>Off = no pulses (the "Vibracao" toggle).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary><see cref="Weak"/>, <see cref="Medium"/> or <see cref="Strong"/>; other values count as medium.</summary>
    public int Strength { get; set; } = Medium;

    /// <summary>The amplitude of a pulse for <paramref name="strength"/> (1..255).</summary>
    public static int Amplitude(int strength)
    {
        return strength switch
        {
            Weak => 60,
            Strong => 255,
            _ => 150,
        };
    }

    private void OnPressed(OnScreenControl control)
    {
        if (!Enabled)
        {
            return;
        }

        _vibrate(Amplitude(Strength));
    }
}
