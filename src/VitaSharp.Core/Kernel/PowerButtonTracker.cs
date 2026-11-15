using VitaSharp.Core.Host;

namespace VitaSharp.Core.Kernel;

/// <summary>
/// The POWER button decisions of ScePower's button thread (FW 3.74 os0:kd/bootimage.skprx), on the buttons word and a
/// clock in microseconds:
/// <list type="bullet">
/// <item>released before the standby push time (2 s by default, vshPowerSetStandbyButtonPushTime): POWER_PRESS (the
/// shell puts the system in standby);</item>
/// <item>still held at the push time: POWER_HOLD (the shell's power menu); still held at 4 s after the press (when the
/// push time is shorter than 4 s; else 4 s after the hold): the kernel turns the system off;</item>
/// <item>PS held as well while POWER is down: PS+POWER (0x08000000), and nothing else until both are released.</item>
/// </list>
/// The firmware waits on the controller driver's button events; here every sample of the buttons is one call to
/// <see cref="Update"/>, so a test drives it with its own clock.
/// </summary>
public sealed class PowerButtonTracker
{
    public const long DefaultStandbyPushTimeUs = 2_000_000;
    private const long PowerOffAfterPressUs = 4_000_000;

    private enum Phase
    {
        Idle,
        Pressed,
        Held,
        WaitRelease,
    }

    private Phase _phase;
    private long _pressedAtUs;
    private long _powerOffAtUs;

    /// <summary>How long POWER must stay down to be a hold, in microseconds.</summary>
    public long StandbyPushTimeUs { get; set; } = DefaultStandbyPushTimeUs;

    /// <summary>The press that woke the system from standby is not a press of its own: nothing until it is released.</summary>
    public void IgnoreUntilReleased() => _phase = Phase.WaitRelease;

    /// <summary>One sample of the buttons at <paramref name="nowUs"/>: the power callback events it produces (0 when
    /// none), and whether the kernel turns the system off now.</summary>
    public uint Update(VitaButtons buttons, long nowUs, out bool powerOff)
    {
        powerOff = false;
        bool power = (buttons & VitaButtons.PowerButton) != 0;
        bool ps = (buttons & VitaButtons.PsButton) != 0;
        switch (_phase)
        {
            case Phase.Idle:
                if (!power)
                {
                    return 0;
                }

                if (ps)
                {
                    _phase = Phase.WaitRelease;
                    return PowerEvents.ButtonPsPowerPress;
                }

                _phase = Phase.Pressed;
                _pressedAtUs = nowUs;
                return 0;
            case Phase.Pressed:
                if (power && ps)
                {
                    _phase = Phase.WaitRelease;
                    return PowerEvents.ButtonPsPowerPress;
                }

                if (!power)
                {
                    _phase = Phase.Idle;
                    return PowerEvents.ButtonPowerPress;
                }

                if (nowUs - _pressedAtUs < StandbyPushTimeUs)
                {
                    return 0;
                }

                _phase = Phase.Held;
                long more = StandbyPushTimeUs < PowerOffAfterPressUs ? PowerOffAfterPressUs - StandbyPushTimeUs : PowerOffAfterPressUs;
                _powerOffAtUs = _pressedAtUs + StandbyPushTimeUs + more;
                return PowerEvents.ButtonPowerHold;
            case Phase.Held:
                if (!power)
                {
                    _phase = Phase.Idle;
                    return 0;
                }

                if (nowUs >= _powerOffAtUs)
                {
                    _phase = Phase.WaitRelease;
                    powerOff = true;
                }

                return 0;
            default:
                if (!power && !ps)
                {
                    _phase = Phase.Idle;
                }

                return 0;
        }
    }
}
