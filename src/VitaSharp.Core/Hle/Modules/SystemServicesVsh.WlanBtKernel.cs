using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceWlanBtForDriver (os0:kd/bootimage.skprx SceWlanBt, FW 3.74, read in the module, study only): the power
// configuration of the Wi-Fi/Bluetooth combo chip. The module keeps a configuration word (bit 0 the chip powered,
// 1 Wi-Fi, 2 Bluetooth, 3 a resumed link, 4 both suspended, 5/6/7 the wake-up sources, 8 the first power-up) and the
// state of each side (1 on, 2 suspending, 3 suspended, 4 off; both start off). SceBt and SceNetPs drive it through
// ksceWlanBtSetConfiguration; Wi-Fi and Bluetooth switching on and off are written to the kernel event log (code
// 0x4E21). The chip powers up at once on the emulated unit; a PCH-1000 has Bluetooth. The monitor callback is kept
// (the module calls it from its interrupt thread, which the emulated chip never raises).
//
// SceUsbEtherRtlForDriver (SceUsbEtherRtl): the register access of the Realtek USB Ethernet adapter of the PS TV. Each
// entry is a USB control transfer to the adapter; with none attached ksceUsbdControlTransfer refuses the pipe
// (0x80240003).
public sealed partial class SystemServicesVsh
{
    const int WlanBtPowered = 1, WlanBtWlan = 2, WlanBtBt = 4, WlanBtResumed = 8, WlanBtSuspended = 0x10;
    const int WlanBtFirstPowerUp = 0x100;
    const int WlanBtOn = 1, WlanBtSuspending = 2, WlanBtSuspend = 3, WlanBtOff = 4;

    /// <summary>The event log code of the module's Wi-Fi/Bluetooth switches.</summary>
    const int WlanBtEventCode = 0x4E21;

    /// <summary>SceWlanBt: Bluetooth asked of a unit without it (0x80418007).</summary>
    const int WLANBT_ERR_NO_BT = unchecked((int)0x80418007);

    [SnapshotMissing] // states of builds before it: the chip was never configured
    int _wlanBtConfig;

    [SnapshotMissing(WlanBtOff)]
    int _wlanBtWlanState = WlanBtOff;

    [SnapshotMissing(WlanBtOff)]
    int _wlanBtBtState = WlanBtOff;

    [SnapshotMissing]
    int _wlanBtForceOff;

    [SnapshotMissing]
    uint _wlanBtMonitor;

    /// <summary>Whether the unit has Bluetooth (the module's start reads it from the unit's configuration): a
    /// PCH-1000 has.</summary>
    static bool WlanBtHasBluetooth => true;

    /// <summary>The monitor callback ksceWlanBtAttachMonitor keeps (0: none).</summary>
    internal uint WlanBtMonitor => _wlanBtMonitor;

    /// <summary>ksceWlanBtAttachMonitor(callback): keeps the monitor callback; 0.</summary>
    [Hle(Nid = 0xC5AA6F43, Library = "SceWlanBtForDriver")]
    int ksceWlanBtAttachMonitor(uint callback)
    {
        _wlanBtMonitor = callback;
        return 0;
    }

    /// <summary>ksceWlanBtDetachMonitor(): drops the monitor callback; 0.</summary>
    [Hle(Nid = 0xA7FDE9DC, Library = "SceWlanBtForDriver")]
    int ksceWlanBtDetachMonitor()
    {
        _wlanBtMonitor = 0;
        return 0;
    }

    /// <summary>ksceWlanBtGetConfiguration(): the configuration word.</summary>
    [Hle(Nid = 0x0FC89113, Library = "SceWlanBtForDriver")]
    int ksceWlanBtGetConfiguration()
    {
        return _wlanBtConfig;
    }

    /// <summary>ksceWlanBtSetConfiguration(int request): one step of the chip's power sequence: 1/2 the chip on/off,
    /// 3/4 Wi-Fi on/off, 5/6 Bluetooth on/off (0x80418007 without Bluetooth), 7/8 and 10/11 Wi-Fi and Bluetooth
    /// suspending/suspended (both: the chip is restarted, or both suspended), 13..18 the wake-up sources and the
    /// forced power-off of a resume. Other requests change nothing. The result of the step (0).</summary>
    [Hle(Nid = 0x4C96C3B7, Library = "SceWlanBtForDriver")]
    int ksceWlanBtSetConfiguration(int request)
    {
        int result = 0;
        int config = _wlanBtConfig;
        switch (request)
        {
            case 1:
                config |= WlanBtPowered;
                break;
            case 2:
                if ((config & WlanBtPowered) != 0)
                {
                    config = 0;
                }

                break;
            case 3:
                config = WlanBtSwitchOn(config, WlanBtWlan);
                _wlanBtWlanState = WlanBtOn;
                WlanBtLog(1, result);
                break;
            case 4:
                config = WlanBtSwitchOff(config, WlanBtWlan, WlanBtBt);
                _wlanBtWlanState = WlanBtOff;
                WlanBtLog(2, result);
                break;
            case 5:
                if (!WlanBtHasBluetooth)
                {
                    return WLANBT_ERR_NO_BT;
                }

                config = WlanBtSwitchOn(config, WlanBtBt);
                _wlanBtBtState = WlanBtOn;
                WlanBtLog(3, result);
                break;
            case 6:
                if (!WlanBtHasBluetooth)
                {
                    return WLANBT_ERR_NO_BT;
                }

                config = WlanBtSwitchOff(config, WlanBtBt, WlanBtWlan);
                _wlanBtBtState = WlanBtOff;
                WlanBtLog(4, result);
                break;
            case 7:
                _wlanBtWlanState = WlanBtSuspending;
                config = WlanBtBothSuspending(config, _wlanBtBtState);
                break;
            case 8:
                _wlanBtWlanState = WlanBtSuspend;
                config = WlanBtBothSuspended(config, _wlanBtBtState);
                break;
            case 10:
                _wlanBtBtState = WlanBtSuspending;
                config = WlanBtBothSuspending(config, _wlanBtWlanState);
                break;
            case 11:
                _wlanBtBtState = WlanBtSuspend;
                config = WlanBtBothSuspended(config, _wlanBtWlanState);
                break;
            case 13:
                _wlanBtWlanState = WlanBtOff;
                config |= 0x40;
                if (_wlanBtBtState == WlanBtOff)
                {
                    config = WlanBtBothOff(config, config & 0xC0);
                }

                break;
            case 14:
                _wlanBtWlanState = WlanBtOff;
                if (_wlanBtBtState != WlanBtOff)
                {
                    config &= ~0x40;
                }
                else if (_wlanBtForceOff != 0)
                {
                    config = WlanBtForcedOff();
                }
                else
                {
                    config = WlanBtBothOff(config & ~0x40, config & 0x80);
                }

                break;
            case 15:
                config |= 0xA0;
                config = WlanBtBluetoothWakeOff(config);
                break;
            case 16:
                config = (config & ~0x80) | 0x20;
                config = WlanBtBluetoothWakeOff(config);
                break;
            case 17:
                _wlanBtWlanState = WlanBtOff;
                _wlanBtForceOff |= 1;
                config &= ~0x40;
                if (_wlanBtBtState == WlanBtOff)
                {
                    config = WlanBtForcedOff();
                }

                break;
            case 18:
                _wlanBtBtState = WlanBtOff;
                _wlanBtForceOff |= 2;
                config = (config & ~0x80) | 0x20;
                if (_wlanBtWlanState == WlanBtOff)
                {
                    config = WlanBtForcedOff();
                }

                break;
        }

        _wlanBtConfig = config;
        return result;
    }

    /// <summary>A side switched on: the chip is powered first if it is not (the first power-up with both sides
    /// off).</summary>
    int WlanBtSwitchOn(int config, int side)
    {
        if ((config & WlanBtResumed) != 0)
        {
            config = WlanBtStandbyWake(config);
        }

        if ((config & WlanBtPowered) != 0)
        {
            return config | side;
        }

        config |= WlanBtPowered | side;
        if (_wlanBtWlanState == WlanBtOff && _wlanBtBtState == WlanBtOff)
        {
            config |= WlanBtFirstPowerUp;
        }

        return config;
    }

    /// <summary>A side switched off: the chip powers down when the other side is off too.</summary>
    static int WlanBtSwitchOff(int config, int side, int other)
    {
        if ((config & WlanBtPowered) == 0)
        {
            return config;
        }

        bool otherOn = (config & other) != 0;
        config &= ~(side | WlanBtFirstPowerUp);
        return otherOn ? config : 0;
    }

    /// <summary>Both sides suspending: the chip is restarted (powered again, resumed).</summary>
    static int WlanBtBothSuspending(int config, int otherState)
    {
        if (otherState != WlanBtSuspending)
        {
            return config;
        }

        return config | WlanBtPowered | WlanBtResumed;
    }

    static int WlanBtBothSuspended(int config, int otherState)
    {
        return otherState == WlanBtSuspend ? config | WlanBtSuspended : config;
    }

    /// <summary>Both sides off: the chip powers down, or stays up for its wake-up sources.</summary>
    int WlanBtBothOff(int config, int wakeSources)
    {
        if (_wlanBtForceOff != 0)
        {
            return WlanBtForcedOff();
        }

        return wakeSources == 0 ? 0 : config & ~WlanBtFirstPowerUp;
    }

    int WlanBtBluetoothWakeOff(int config)
    {
        _wlanBtBtState = WlanBtOff;
        if (_wlanBtWlanState != WlanBtOff)
        {
            return config;
        }

        return WlanBtBothOff(config, config & 0xC0);
    }

    int WlanBtForcedOff()
    {
        _wlanBtForceOff = 0;
        return 0;
    }

    /// <summary>The chip leaves its standby with both sides off and a wake-up source set: the sources are
    /// cleared.</summary>
    int WlanBtStandbyWake(int config)
    {
        if (_wlanBtWlanState == WlanBtOff && _wlanBtBtState == WlanBtOff && (config & 0xC0) != 0)
        {
            return 0;
        }

        return config;
    }

    void WlanBtLog(int subcode, int result)
    {
        Span<byte> data = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(data, result);
        int thread = GuestThread.Current?.Uid ?? 0;
        EventLogWrite(KernelSpace.KernelPid, thread, WlanBtEventCode, subcode, 0, data);
    }

    // ------------------------------------------------------------------ SceUsbEtherRtlForDriver: no adapter
    /// <summary>NID_1E0E3E59 (SceUsbEtherRtlForDriver)(ushort reg, ushort index, uint value): writes an adapter
    /// register: no adapter (0x80240003).</summary>
    [Hle("NID_1E0E3E59", Nid = 0x1E0E3E59, Library = "SceUsbEtherRtlForDriver", Default = true)]
    int UsbEtherRtlWriteRegister(int register, int index, uint value)
    {
        return USBD_ERR_INVALID;
    }

    /// <summary>NID_7473CB10 (SceUsbEtherRtlForDriver)(ushort reg, ushort index, uint *value): reads an adapter
    /// register (NULL: -1): no adapter (0x80240003).</summary>
    [Hle("NID_7473CB10", Nid = 0x7473CB10, Library = "SceUsbEtherRtlForDriver", Default = true)]
    int UsbEtherRtlReadRegister(int register, int index, uint value)
    {
        return value == 0 ? -1 : USBD_ERR_INVALID;
    }

    /// <summary>NID_BAF95A47 (SceUsbEtherRtlForDriver)(const byte *mac): writes the MAC address into the adapter's
    /// EFUSE map: the first register write finds no adapter (0x80240003).</summary>
    [Hle("NID_BAF95A47", Nid = 0xBAF95A47, Library = "SceUsbEtherRtlForDriver", Default = true)]
    int UsbEtherRtlWriteMac(uint mac)
    {
        return USBD_ERR_INVALID;
    }

    // ------------------------------------------------------------------ SceBtForDriver: no device paired
    /// <summary>SceBt: the Bluetooth host never became ready (0x802F1802).</summary>
    const int BT_ERR_NOT_READY = unchecked((int)0x802F1802);

    /// <summary>ksceBtGetVidPid(uint mac0, uint mac1, uint *vidPid): the USB ids of a paired or connected device, looked
    /// up by its address: none is (0x802F5001), as sceBtGetDeviceId answers.</summary>
    [Hle(Default = true, Nid = 0x780A5557, Library = "SceBtForDriver")]
    int ksceBtGetVidPid(uint mac0, uint mac1, uint vidPid)
    {
        return BT_ERR_DEVICE_ID;
    }

    /// <summary>NID_10D68FAE (SceBtForDriver)(...): SceDs3's registration of a controller met over USB into the
    /// pairing table; the module waits up to 30 s for the Bluetooth host, which the emulated unit's radio never
    /// starts: 0x802F1802.</summary>
    [Hle("NID_10D68FAE", Nid = 0x10D68FAE, Library = "SceBtForDriver", Default = true)]
    int BtRegisterUsbPairing()
    {
        return BT_ERR_NOT_READY;
    }
}
