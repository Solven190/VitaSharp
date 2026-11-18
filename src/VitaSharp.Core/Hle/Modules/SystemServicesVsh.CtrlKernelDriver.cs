using VitaSharp.Core.Kernel;
using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceCtrlForDriver (os0:kd/bootimage.skprx SceCtrl 3.74, read in the module, study only), the external-controller and
// system part:
// - the virtual controller driver: one table of entry points (SceDs3 registers it, NULL is 0x80340001). The entries
//   about external controllers (active port +0x14, port assignment +0x18, turn-off interval +0x10, +0x1C, the
//   foreground change +0x2C) jump to it, and answer 0x80340021 while no driver registered. SceDs3 is loaded by
//   psp2config_dolce only (the PS TV): on the handheld nothing registers and they always answer 0x80340021.
// - the notify callbacks: 16 slots; registering takes the first free slot (a full table keeps nothing and still
//   answers 0), unregistering frees every slot of that callback, and notifying calls each registered callback with the
//   argument (a callback the kernel can no longer notify loses its slot).
// - the four system sample hooks (NID_FF9D4DD4: a callback needed, else 0x80340001; the first free of four slots, its
//   index as the result, 0x803400FF when full; the port 0 buttons of the last sample written back) and their release
//   (slot 0..3, else 0x80340001).
// - kept values: the vibration setting, the CtrlP callback (NULL 0x80340001) and its process, the start mode byte
//   ScePower sets (1 at start), the foreground process (-1 at start), the low-level sampling configuration.
// - port 0's analog and button emulation (port 0 only, slot 0..3, else 0x80340001; vita-headers: the user's values,
//   then the kernel's, then the number of samples). The sampling adds each active slot's analog offsets from 0x80 and
//   ORs its buttons for that many samples, one sample per vertical blank as the driver samples.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceCtrl: no virtual controller driver registered (0x80340021).</summary>
    internal const int CTRL_ERR_NO_DRIVER = unchecked((int)0x80340021);

    /// <summary>SceCtrl: the four system sample hooks are taken (0x803400FF).</summary>
    const int CTRL_ERR_NO_SLOT = unchecked((int)0x803400FF);

    const int CtrlNotifySlots = 16, CtrlSampleHooks = 4, CtrlEmulationSlots = 4;

    [SnapshotMissing] // states of builds before it: no driver, no callbacks, nothing emulated
    uint _ctrlVirtualDriver;

    [SnapshotMissing]
    readonly int[] _ctrlNotifyCallback = new int[CtrlNotifySlots]; // 0: a free slot (the module's -1)

    [SnapshotMissing]
    readonly uint[] _ctrlSampleHook = new uint[CtrlSampleHooks * 3];

    [SnapshotMissing]
    int _ctrlVibrationSetting;

    [SnapshotMissing]
    uint _ctrlCtrlpCallback;

    [SnapshotMissing(-1)]
    int _ctrlCtrlpProcess = -1;

    [SnapshotMissing]
    int _ctrlCtrlpEnabled;

    [SnapshotMissing((byte)1)]
    byte _ctrlStartMode = 1;

    [SnapshotMissing(-1)]
    int _ctrlForegroundProcess = -1;

    [SnapshotMissing]
    uint _ctrlLowLevelConfig;

    // port 0's emulation slots: the analog bytes (0x80 is no offset; the user's four sticks, then the kernel's), the
    // user's and the kernel's buttons, and the samples of each, counted from the vertical blank of the set
    [SnapshotMissing]
    readonly ulong[] _ctrlEmulatedAnalog = new ulong[CtrlEmulationSlots];

    [SnapshotMissing]
    readonly int[] _ctrlEmulatedAnalogSamples = new int[CtrlEmulationSlots];

    [SnapshotMissing]
    readonly long[] _ctrlEmulatedAnalogFrom = new long[CtrlEmulationSlots];

    [SnapshotMissing]
    readonly uint[] _ctrlEmulatedUserButtons = new uint[CtrlEmulationSlots];

    [SnapshotMissing]
    readonly uint[] _ctrlEmulatedKernelButtons = new uint[CtrlEmulationSlots];

    [SnapshotMissing]
    readonly int[] _ctrlEmulatedButtonSamples = new int[CtrlEmulationSlots];

    [SnapshotMissing]
    readonly long[] _ctrlEmulatedButtonFrom = new long[CtrlEmulationSlots];

    /// <summary>The values the module keeps, for the tests.</summary>
    internal (int Vibration, uint CtrlpCallback, int CtrlpProcess, byte StartMode, int Foreground, uint LowLevel) CtrlKernelValues =>
        (_ctrlVibrationSetting, _ctrlCtrlpCallback, _ctrlCtrlpProcess, _ctrlStartMode, _ctrlForegroundProcess, _ctrlLowLevelConfig);

    /// <summary>Calls the virtual controller driver's entry at <paramref name="offset"/> with the caller's arguments,
    /// or answers 0x80340021 when no driver registered.</summary>
    int CtrlCallDriver(uint offset, uint first, uint second)
    {
        if (_ctrlVirtualDriver == 0)
        {
            return CTRL_ERR_NO_DRIVER;
        }

        uint entry = GuestMemory.R32(_ctrlVirtualDriver + offset);
        if (GuestThread.Current is not { } thread || entry == 0)
        {
            return CTRL_ERR_NO_DRIVER;
        }

        return (int)thread.CallGuest(entry, first, second);
    }

    /// <summary>ksceCtrlRegisterVirtualControllerDriver(const SceCtrlVirtualControllerDriver *driver).</summary>
    [Hle(Nid = 0x18823EEA, Library = "SceCtrlForDriver")]
    int ksceCtrlRegisterVirtualControllerDriver(uint driver)
    {
        if (driver == 0)
        {
            return CTRL_ERR_INVALID_ARG;
        }

        _ctrlVirtualDriver = driver;
        return 0;
    }

    /// <summary>ksceCtrlGetActiveControllerPort(): the driver's answer, 0x80340021 without one.</summary>
    [Hle(Nid = 0xFA88D837, Library = "SceCtrlForDriver")]
    internal int ksceCtrlGetActiveControllerPort()
    {
        return CtrlCallDriver(0x14, 0, 0);
    }

    /// <summary>ksceCtrlChangePortAssign(int port1, int port2): the driver's answer, 0x80340021 without one.</summary>
    [Hle(Nid = 0x53381364, Library = "SceCtrlForDriver")]
    internal int ksceCtrlChangePortAssign(uint first, uint second)
    {
        return CtrlCallDriver(0x18, first, second);
    }

    /// <summary>ksceCtrlSetTurnOffInterval(int interval): the driver's answer, 0x80340021 without one.</summary>
    [Hle(Nid = 0xED8F0129, Library = "SceCtrlForDriver")]
    internal int ksceCtrlSetTurnOffInterval(uint interval)
    {
        return CtrlCallDriver(0x10, interval, 0);
    }

    /// <summary>NID_69E45836 (ScePower): the driver's entry +0x1C, 0x80340021 without one.</summary>
    [Hle("NID_69E45836", Nid = 0x69E45836, Library = "SceCtrlForDriver")]
    int CtrlDriverEntry1C(uint first, uint second)
    {
        return CtrlCallDriver(0x1C, first, second);
    }

    /// <summary>ksceCtrlSetVibrationSetting(int enable): kept for the driver's vibration; 0.</summary>
    [Hle(Nid = 0x4D8FE670, Library = "SceCtrlForDriver")]
    internal int ksceCtrlSetVibrationSetting(int enable)
    {
        _ctrlVibrationSetting = enable;
        return 0;
    }

    /// <summary>ksceCtrlSetCtrlpCallback(callback): kept; NULL is 0x80340001.</summary>
    [Hle(Nid = 0x7AD9467E, Library = "SceCtrlForDriver")]
    int ksceCtrlSetCtrlpCallback(uint callback)
    {
        if (callback == 0)
        {
            return CTRL_ERR_INVALID_ARG;
        }

        _ctrlCtrlpCallback = callback;
        return 0;
    }

    /// <summary>NID_5B708A15 (SceAppMgr)(SceUID pid, int enable): the process the CtrlP data goes to (-1 when
    /// disabled); 0.</summary>
    [Hle("NID_5B708A15", Nid = 0x5B708A15, Library = "SceCtrlForDriver")]
    int CtrlSetCtrlpProcess(int pid, int enable)
    {
        _ctrlCtrlpEnabled = enable;
        _ctrlCtrlpProcess = enable == 0 ? -1 : pid;
        return 0;
    }

    /// <summary>NID_C8CE3E43 (ScePower)(uint8 mode): the start mode the driver reads when sampling starts; 0.</summary>
    [Hle("NID_C8CE3E43", Nid = 0xC8CE3E43, Library = "SceCtrlForDriver")]
    int CtrlSetStartMode(uint mode)
    {
        _ctrlStartMode = (byte)mode;
        return 0;
    }

    /// <summary>NID_B26C2231 (SceAppMgr)(SceUID pid): the new foreground process the driver samples for; the virtual
    /// driver hears of it too. 0.</summary>
    [Hle("NID_B26C2231", Nid = 0xB26C2231, Library = "SceCtrlForDriver")]
    int CtrlSetForegroundProcess(int pid)
    {
        _ctrlForegroundProcess = pid;
        if (_ctrlVirtualDriver != 0)
        {
            CtrlCallDriver(0x2C, (uint)pid, 0);
        }

        return 0;
    }

    /// <summary>NID_DA07E970 (int *pid): the foreground process the driver samples for (-1 before the first).</summary>
    [Hle("NID_DA07E970", Nid = 0xDA07E970, Library = "SceCtrlForDriver")]
    int CtrlGetForegroundProcess(uint pid)
    {
        if (pid == 0)
        {
            return CTRL_ERR_INVALID_ARG;
        }

        GuestMemory.W32(pid, (uint)_ctrlForegroundProcess);
        return 0;
    }

    /// <summary>NID_02081494 (SceDs3)(config, uint mode, int external): the low-level sampling configuration; 0.</summary>
    [Hle("NID_02081494", Nid = 0x02081494, Library = "SceCtrlForDriver")]
    int CtrlSetLowLevelConfig(uint config, uint mode, int external)
    {
        _ctrlLowLevelConfig = (external == 1 ? 0x10000000u : 0) | mode;
        return 0;
    }

    /// <summary>NID_FF9D4DD4 (ScePower, SceAVConfig, SceEnumWakeUp)(arg, callback, arg2, uint *buttons): a system
    /// sample hook; its slot, 0x803400FF when the four are taken.</summary>
    [Hle("NID_FF9D4DD4", Nid = 0xFF9D4DD4, Library = "SceCtrlForDriver")]
    int CtrlAddSampleHook(uint argument, uint callback, uint argument2, uint buttons)
    {
        if (callback == 0)
        {
            return CTRL_ERR_INVALID_ARG;
        }

        int slot = 0;
        while (slot < CtrlSampleHooks && _ctrlSampleHook[(slot * 3) + 1] != 0)
        {
            slot++;
        }

        int result = slot;
        if (slot == CtrlSampleHooks)
        {
            result = CTRL_ERR_NO_SLOT;
        }
        else
        {
            _ctrlSampleHook[slot * 3] = argument;
            _ctrlSampleHook[(slot * 3) + 1] = callback;
            _ctrlSampleHook[(slot * 3) + 2] = argument2;
        }

        if (buttons != 0)
        {
            uint current = (uint)(Emu.Host.Input?.GetController(0).Buttons ?? 0);
            GuestMemory.W32(buttons, current);
        }

        return result;
    }

    /// <summary>NID_4CCB2153 (ScePower)(int slot): frees a system sample hook; slot 0..3.</summary>
    [Hle("NID_4CCB2153", Nid = 0x4CCB2153, Library = "SceCtrlForDriver")]
    int CtrlRemoveSampleHook(uint slot)
    {
        if (slot >= CtrlSampleHooks)
        {
            return CTRL_ERR_INVALID_ARG;
        }

        _ctrlSampleHook[slot * 3] = 0;
        _ctrlSampleHook[(slot * 3) + 1] = 0;
        _ctrlSampleHook[(slot * 3) + 2] = 0;
        return 0;
    }

    /// <summary>ksceCtrlRegisterNotifyCallBack(SceUID cb): the first free of 16 slots (none free: nothing kept); 0.</summary>
    [Hle(Nid = 0xE170883E, Library = "SceCtrlForDriver")]
    internal int ksceCtrlRegisterNotifyCallBack(int callback)
    {
        for (int slot = 0; slot < CtrlNotifySlots; slot++)
        {
            if (_ctrlNotifyCallback[slot] == 0)
            {
                _ctrlNotifyCallback[slot] = callback;
                return 0;
            }
        }

        return 0;
    }

    /// <summary>ksceCtrlUnregisterNotifyCallBack(SceUID cb): frees every slot of the callback; 0.</summary>
    [Hle(Nid = 0x9DAB51C4, Library = "SceCtrlForDriver")]
    internal int ksceCtrlUnregisterNotifyCallBack(int callback)
    {
        for (int slot = 0; slot < CtrlNotifySlots; slot++)
        {
            if (_ctrlNotifyCallback[slot] == callback)
            {
                _ctrlNotifyCallback[slot] = 0;
            }
        }

        return 0;
    }

    /// <summary>ksceCtrlNotifyCallBacks(int arg): notifies every registered callback; one the kernel can no longer
    /// notify loses its slot. 0.</summary>
    [Hle(Nid = 0x6E183B9B, Library = "SceCtrlForDriver")]
    int ksceCtrlNotifyCallBacks(int argument)
    {
        for (int slot = 0; slot < CtrlNotifySlots; slot++)
        {
            if (_ctrlNotifyCallback[slot] == 0)
            {
                continue;
            }

            if (Emu.Kernel.Objects.Get<Callback>(_ctrlNotifyCallback[slot]) is not { } target)
            {
                _ctrlNotifyCallback[slot] = 0;
                continue;
            }

            target.Notify(argument);
        }

        return 0;
    }

    /// <summary>ksceCtrlSetAnalogEmulation(int port, int slot, the user's lx, ly, rx, ry, the kernel's lx, ly, rx, ry,
    /// int samples): port 0, slot 0..3.</summary>
    [Hle(Nid = 0x06577FE8, Library = "SceCtrlForDriver")]
    int ksceCtrlSetAnalogEmulation(int port, uint slot, uint leftX, uint leftY, uint rightX, uint rightY,
        uint kernelLeftX, uint kernelLeftY, uint kernelRightX, uint kernelRightY, int samples)
    {
        if (port != 0 || slot >= CtrlEmulationSlots)
        {
            return CTRL_ERR_INVALID_ARG;
        }

        ulong bytes = (byte)leftX | ((ulong)(byte)leftY << 8) | ((ulong)(byte)rightX << 16) | ((ulong)(byte)rightY << 24)
            | ((ulong)(byte)kernelLeftX << 32) | ((ulong)(byte)kernelLeftY << 40)
            | ((ulong)(byte)kernelRightX << 48) | ((ulong)(byte)kernelRightY << 56);
        lock (_ctrlEmulatedAnalog)
        {
            _ctrlEmulatedAnalog[slot] = bytes;
            _ctrlEmulatedAnalogSamples[slot] = samples;
            _ctrlEmulatedAnalogFrom[slot] = VBlank.Count;
        }

        return 0;
    }

    /// <summary>ksceCtrlSetButtonEmulation(int port, int slot, uint userButtons, uint kernelButtons, int samples): port
    /// 0, slot 0..3.</summary>
    [Hle(Nid = 0x1E750326, Library = "SceCtrlForDriver")]
    int ksceCtrlSetButtonEmulation(int port, uint slot, uint userButtons, uint kernelButtons, int samples)
    {
        if (port != 0 || slot >= CtrlEmulationSlots)
        {
            return CTRL_ERR_INVALID_ARG;
        }

        lock (_ctrlEmulatedAnalog)
        {
            _ctrlEmulatedUserButtons[slot] = userButtons;
            _ctrlEmulatedKernelButtons[slot] = kernelButtons;
            _ctrlEmulatedButtonSamples[slot] = samples;
            _ctrlEmulatedButtonFrom[slot] = VBlank.Count;
        }

        return 0;
    }

    /// <summary>Applies port 0's emulation slots to a user sample taken at vertical blank <paramref name="vcount"/>:
    /// each active analog slot adds its user offsets from 0x80 to the four sticks (saturated to 0..255), each active
    /// button slot ORs its user buttons. A slot is active for its sample count from the blank it was set at.</summary>
    internal void CtrlApplyEmulation(long vcount, ref uint buttons, ref byte leftX, ref byte leftY, ref byte rightX,
        ref byte rightY)
    {
        int offsetLeftX = 0;
        int offsetLeftY = 0;
        int offsetRightX = 0;
        int offsetRightY = 0;
        uint emulatedButtons = 0;
        lock (_ctrlEmulatedAnalog)
        {
            for (int slot = 0; slot < CtrlEmulationSlots; slot++)
            {
                if (vcount - _ctrlEmulatedAnalogFrom[slot] < _ctrlEmulatedAnalogSamples[slot])
                {
                    ulong bytes = _ctrlEmulatedAnalog[slot];
                    offsetLeftX += (int)(bytes & 0xFF) - 0x80;
                    offsetLeftY += (int)((bytes >> 8) & 0xFF) - 0x80;
                    offsetRightX += (int)((bytes >> 16) & 0xFF) - 0x80;
                    offsetRightY += (int)((bytes >> 24) & 0xFF) - 0x80;
                }

                if (vcount - _ctrlEmulatedButtonFrom[slot] < _ctrlEmulatedButtonSamples[slot])
                {
                    emulatedButtons |= _ctrlEmulatedUserButtons[slot];
                }
            }
        }

        buttons |= emulatedButtons;
        leftX = (byte)Math.Clamp(leftX + offsetLeftX, 0, 255);
        leftY = (byte)Math.Clamp(leftY + offsetLeftY, 0, 255);
        rightX = (byte)Math.Clamp(rightX + offsetRightX, 0, 255);
        rightY = (byte)Math.Clamp(rightY + offsetRightY, 0, 255);
    }
}
