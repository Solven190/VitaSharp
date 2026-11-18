using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Hle.Modules;

// Small kernel libraries of FW 3.74 over the unit's simple devices, each read in its module's code (study only):
// SceDebugLed (sysmem: the GPO debug LEDs, the GPI switches and two handler slots; the same NIDs serve the user
// syscalls and the kernel entries), ScePwmForDriver (lowio: the two PWM channels), SceCdramForDriver (lowio) and
// SceUartForKernel (sysmem: the serial ports of the debug console).
public sealed partial class SystemServicesVsh
{
    // ---- SceDebugLed
    [SnapshotMissing] // states of builds before it: the GPO word was not kept
    uint _debugLedGpo;

    /// <summary>The GPO bits the LEDs show (SetGPOMask); all eight on at boot.</summary>
    [SnapshotMissing] // states of builds before it: every bit was shown
    uint _debugLedGpoMask = 0xFF;

    [SnapshotMissing] // states of builds before it: the GPI word was always 0
    uint _debugLedGpi;

    [SnapshotMissing] // states of builds before it: no handler could be registered
    uint _debugLedHandle0, _debugLedHandle1;

    /// <summary>sceKernelSetGPO / ksceKernelSetGPO (0x78E702D3)(uint state): the GPO word (GetGPO reads it back);
    /// the LEDs show the bits of the mask. 0.</summary>
    [Hle]
    int sceKernelSetGPO(uint state)
    {
        _debugLedGpo = state;
        return 0;
    }

    /// <summary>ksceKernelGetGPO (0x3BB289F7)(): the GPO word SetGPO last wrote.</summary>
    [Hle(Nid = 0x3BB289F7, Library = "SceDebugLedForDriver")]
    uint ksceKernelGetGPO()
    {
        return _debugLedGpo;
    }

    /// <summary>ksceKernelSetGPOMask (0x098473B0)(uint clear, uint set): which GPO bits the LEDs show: the mask
    /// loses the bits of <paramref name="clear"/> and gains those of <paramref name="set"/>. 0.</summary>
    [Hle(Nid = 0x098473B0, Library = "SceDebugLedForDriver")]
    int ksceKernelSetGPOMask(uint clear, uint set)
    {
        _debugLedGpoMask = (_debugLedGpoMask & ~clear) | set;
        return 0;
    }

    /// <summary>sceKernelGetGPI / ksceKernelGetGPI (0x14F582CF)(): the GPI word (the switches; SetGPI sets it).</summary>
    [Hle]
    uint sceKernelGetGPI()
    {
        return _debugLedGpi;
    }

    /// <summary>ksceKernelSetGPI (0x51C5325A)(uint value): the GPI word GetGPI reports.</summary>
    [Hle(Nid = 0x51C5325A, Library = "SceDebugLedForDriver")]
    void ksceKernelSetGPI(uint value)
    {
        _debugLedGpi = value;
    }

    /// <summary>ksceDebugLedRegisterHandle0 (0x24173819)(handler): the function InvokeHandle0 calls (NULL: none).</summary>
    [Hle(Nid = 0x24173819, Library = "SceDebugLedForDriver")]
    void ksceDebugLedRegisterHandle0(uint handler)
    {
        _debugLedHandle0 = handler;
    }

    /// <summary>ksceDebugLedRegisterHandle1 (0xF62154E7)(handler): the function InvokeHandle1 calls.</summary>
    [Hle(Nid = 0xF62154E7, Library = "SceDebugLedForDriver")]
    void ksceDebugLedRegisterHandle1(uint handler)
    {
        _debugLedHandle1 = handler;
    }

    /// <summary>Calls a registered handler with the caller's arguments and answers what it answers; 0 without one.</summary>
    static int DebugLedInvoke(uint handler, uint a, uint b, uint c)
    {
        GuestThread? thread = GuestThread.Current;
        if (handler == 0 || thread == null)
        {
            return 0;
        }

        return (int)thread.CallGuest(handler, a, b, c);
    }

    /// <summary>sceDebugLedInvokeHandle0 / ksceDebugLedInvokeHandle0 (0x2B6EABAD)(a, b, c): runs handler 0.</summary>
    [Hle]
    int sceDebugLedInvokeHandle0(uint a, uint b, uint c)
    {
        return DebugLedInvoke(_debugLedHandle0, a, b, c);
    }

    /// <summary>sceDebugLedInvokeHandle1 / ksceDebugLedInvokeHandle1 (0x0E6B9890)(a, b, c): runs handler 1.</summary>
    [Hle]
    int sceDebugLedInvokeHandle1(uint a, uint b, uint c)
    {
        return DebugLedInvoke(_debugLedHandle1, a, b, c);
    }

    // ---- ScePwmForDriver: two channels, each a control word (bit 0 enabled, bit 1 update pending, bits 8/9 the
    // output levels), a period and a duty word, and the driver's state (0 idle, 1 running, 2 stopping). The device
    // takes an update at once, so the pending bit always reads clear.

    /// <summary>ScePwm: a channel other than 0 or 1; a mode or duty out of range; an update while one is pending.</summary>
    const int PWM_ERR_CHANNEL = unchecked((int)0x803F0200), PWM_ERR_INVALID = unchecked((int)0x803F0201);
    const int PWM_ERR_BUSY = unchecked((int)0x803F0280);

    /// <summary>The control word's bits: enabled, update pending, and the two output level bits.</summary>
    const uint PwmEnabled = 1, PwmPending = 2, PwmLevelBits = 0x300;

    [SnapshotMissing] // states of builds before it: the channels were never started
    readonly uint[] _pwmControl = new uint[2], _pwmPeriod = new uint[2], _pwmDuty = new uint[2];

    [SnapshotMissing] // states of builds before it: the channels were never started
    readonly byte[] _pwmState = new byte[2];

    /// <summary>A write of the control word: the device takes the update at once (the pending bit clears).</summary>
    void PwmWriteControl(uint channel, uint value)
    {
        _pwmControl[channel] = value & ~PwmPending;
    }

    /// <summary>NID_13161CCC (ScePwmForDriver)(int channel, uint mode, uint period, uint duty): starts a channel with
    /// the output levels of <paramref name="mode"/> (bits 8/9 only, else 0x803F0201; the duty at most the period);
    /// returns the previous state.</summary>
    [Hle("NID_13161CCC", Nid = 0x13161CCC, Library = "ScePwmForDriver")]
    int PwmStart(uint channel, uint mode, uint period, uint duty)
    {
        if (channel > 1)
        {
            return PWM_ERR_CHANNEL;
        }

        if ((mode & ~PwmLevelBits) != 0 || duty > period)
        {
            return PWM_ERR_INVALID;
        }

        int previous = _pwmState[channel];
        uint control = _pwmControl[channel];
        if (previous == 2)
        {
            previous = 0;
            if ((control & PwmPending) != 0)
            {
                return PWM_ERR_BUSY;
            }
        }

        _pwmState[channel] = 1;
        uint update = (control & PwmEnabled) != 0 ? PwmEnabled | PwmPending : PwmEnabled;
        _pwmPeriod[channel] = period;
        _pwmDuty[channel] = duty;
        PwmWriteControl(channel, (mode & PwmLevelBits) | update);
        return previous;
    }

    /// <summary>NID_514773B1 (ScePwmForDriver)(int channel, uint period, uint duty): changes a running channel's
    /// period and duty (an idle channel: nothing, 0).</summary>
    [Hle("NID_514773B1", Nid = 0x514773B1, Library = "ScePwmForDriver")]
    int PwmUpdate(uint channel, uint period, uint duty)
    {
        if (channel > 1)
        {
            return PWM_ERR_CHANNEL;
        }

        byte state = _pwmState[channel];
        if (state == 0)
        {
            return 0;
        }

        if (state == 2)
        {
            if ((_pwmControl[channel] & PwmPending) == 0)
            {
                _pwmState[channel] = 0;
            }

            return 0;
        }

        if ((_pwmControl[channel] & PwmPending) != 0)
        {
            return PWM_ERR_BUSY;
        }

        _pwmPeriod[channel] = period;
        _pwmDuty[channel] = duty;
        PwmWriteControl(channel, _pwmControl[channel] | PwmPending);
        return 0;
    }

    /// <summary>NID_22C17D24 (ScePwmForDriver)(int channel): stops a channel (period 0, the enable bit kept until the
    /// update is taken, then the channel is idle); returns the previous state.</summary>
    [Hle("NID_22C17D24", Nid = 0x22C17D24, Library = "ScePwmForDriver")]
    int PwmStop(uint channel)
    {
        if (channel > 1)
        {
            return PWM_ERR_CHANNEL;
        }

        int previous = _pwmState[channel];
        uint control = _pwmControl[channel];
        if (previous == 2 && (control & PwmPending) == 0)
        {
            _pwmState[channel] = 0;
            return 0;
        }

        if (previous == 0)
        {
            return 0;
        }

        _pwmPeriod[channel] = 0;
        PwmWriteControl(channel, control | PwmPending);
        _pwmState[channel] = 2;
        if ((_pwmControl[channel] & PwmPending) != 0)
        {
            return previous;
        }

        PwmWriteControl(channel, _pwmControl[channel] & PwmEnabled);
        _pwmState[channel] = 0;
        return previous;
    }

    /// <summary>NID_0374213C (ScePwmForDriver)(int channel, ushort *period, ushort *duty, uint *level): the channel's
    /// period and duty and its output level (0/1 when it is enabled with a constant output, else 2); returns the
    /// state.</summary>
    [Hle("NID_0374213C", Nid = 0x0374213C, Library = "ScePwmForDriver")]
    int PwmGetState(uint channel, uint period, uint duty, uint level)
    {
        if (channel > 1)
        {
            return PWM_ERR_CHANNEL;
        }

        int state = _pwmState[channel];
        uint control = _pwmControl[channel];
        uint channelPeriod = _pwmPeriod[channel], channelDuty = _pwmDuty[channel];
        if (state == 2)
        {
            if ((control & PwmPending) == 0)
            {
                _pwmState[channel] = 0;
            }

            state = 0;
        }

        if (period != 0)
        {
            Memory.GuestMemory.Write(period, (ushort)channelPeriod);
        }

        if (duty != 0)
        {
            Memory.GuestMemory.Write(duty, (ushort)channelDuty);
        }

        if (level != 0)
        {
            Memory.GuestMemory.W32(level, PwmLevel(control, channelPeriod, channelDuty));
        }

        return state;
    }

    /// <summary>The output level of an enabled channel whose output never changes: bit 9 with no period, the inverse
    /// of bit 8 at full duty, bit 8 at zero duty; 2 for a running (or disabled) channel.</summary>
    static uint PwmLevel(uint control, uint period, uint duty)
    {
        if ((control & 3) != PwmEnabled)
        {
            return 2;
        }

        if (period == 0)
        {
            return (control >> 9) & 1;
        }

        if (period == duty)
        {
            return ((control ^ 0x100) >> 8) & 1;
        }

        return duty == 0 ? (control >> 8) & 1 : 2;
    }

    /// <summary>NID_99911920 (ScePwmForDriver)(int channel): whether an update is pending (never: the device takes
    /// it at once).</summary>
    [Hle("NID_99911920", Nid = 0x99911920, Library = "ScePwmForDriver")]
    int PwmIsUpdatePending(uint channel)
    {
        if (channel > 1)
        {
            return PWM_ERR_CHANNEL;
        }

        return (int)((_pwmControl[channel] & PwmPending) >> 1);
    }

    // ---- SceCdramForDriver

    /// <summary>NID_0657FC1B / NID_2D728EBF (SceCdramForDriver)(): requests to the secure monitor (smc 0) about the
    /// CDRAM, which the emulated memory does not need; nothing to do.</summary>
    [Hle("NID_0657FC1B", Nid = 0x0657FC1B, Library = "SceCdramForDriver", Default = true)]
    [Hle("NID_2D728EBF", Nid = 0x2D728EBF, Library = "SceCdramForDriver", Default = true)]
    void CdramSecureMonitorCall()
    {
    }

    /// <summary>NID_3EFA7540 (SceCdramForDriver)(): waits until lowio has set up the CDRAM (done at boot), then 0.</summary>
    [Hle("NID_3EFA7540", Nid = 0x3EFA7540, Library = "SceCdramForDriver")]
    int CdramWaitReady()
    {
        return 0;
    }

    // ---- SceUartForKernel: the serial ports of the debug console (no cable is attached to the emulated unit:
    // what is written goes to the log, nothing is ever received)

    [SnapshotIgnore]
    readonly System.Text.StringBuilder[] _uartLines = [new(), new(), new(), new(), new(), new(), new()];

    /// <summary>ksceUartInit (0x4C02AA05)(int port): sets the port up (ports 1 and 6 at their own rates). 0.</summary>
    [Hle(Nid = 0x4C02AA05, Library = "SceUartForKernel", Default = true)]
    int ksceUartInit(int port)
    {
        return 0;
    }

    /// <summary>ksceUartWrite (0x430C48F1)(int port, char c): sends a character; each line goes to the log.</summary>
    [Hle(Nid = 0x430C48F1, Library = "SceUartForKernel")]
    int ksceUartWrite(int port, int character)
    {
        System.Text.StringBuilder line = _uartLines[(uint)port % (uint)_uartLines.Length];
        char c = (char)(byte)character;
        if (c == '\n')
        {
            Log.Info($"[uart {port}] {line}");
            line.Clear();
        }
        else if (c != '\r')
        {
            line.Append(c);
        }

        return 0;
    }

    /// <summary>ksceUartReadAvailable (0x16780BC3)(int port): the bytes waiting in the receive FIFO: none.</summary>
    [Hle(Nid = 0x16780BC3, Library = "SceUartForKernel", Default = true)]
    int ksceUartReadAvailable(int port)
    {
        return 0;
    }

    /// <summary>ksceUartRead (0x4E97D3AD)(int port): the module waits for a received byte, which never comes on the
    /// emulated unit; the HLE answers 0 instead of spinning.</summary>
    [Hle(Nid = 0x4E97D3AD, Library = "SceUartForKernel", Default = true)]
    int ksceUartRead(int port)
    {
        return 0;
    }
}
