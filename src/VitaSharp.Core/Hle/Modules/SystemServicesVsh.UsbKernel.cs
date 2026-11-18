using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceUsbServForDriver and SceUsbAudioForDriver (os0:kd/bootimage.skprx SceUsbServ, SceUsbAudio, FW 3.74, read in the
// modules, study only).
// SceUsbServ: the USB PHY selector of the three USB channels (0 the multi connector, 1 the host port, 2 the PS TV's
// ports; every channel starts selected 1 when the module starts) and the PS TV's USB Ethernet power: on a unit that is
// not a genuine PS TV the Ethernet entries answer 0x80248002.
// SceUsbAudio: the USB audio input class driver: its device list and its input entries look the device up (none is
// attached to the emulated unit), and its callbacks are kept in a list keyed by the caller's id.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceUsbServ: an Ethernet entry on a unit that is not a PS TV (0x80248002).</summary>
    const int USBSERV_ERR_NOT_DOLCE = unchecked((int)0x80248002);

    /// <summary>SceUsbServ: a channel above 2 or a selection above 1 (0x80248003).</summary>
    const int USBSERV_ERR_INVALID = unchecked((int)0x80248003);

    /// <summary>SceUsbAudio: a callback id already registered (0x803E0002).</summary>
    const int USBAUDIO_ERR_ALREADY_REGISTERED = unchecked((int)0x803E0002);

    /// <summary>The block of USB audio input: 0xC0 bytes (1 ms of 48 kHz 16-bit stereo); one call takes 1..8.</summary>
    const uint UsbAudioInputBlock = 0xC0;

    // the module's start selects 1 on every channel
    [SnapshotMissing(1)]
    int _usbServMacConnector = 1;

    [SnapshotMissing(1)]
    int _usbServMacHost = 1;

    [SnapshotMissing(1)]
    int _usbServMacDolce = 1;

    [SnapshotMissing(1)]
    int _usbServEtherSuspend = 1;

    [SnapshotMissing]
    readonly List<int> _usbAudioCallbacks = [];

    /// <summary>Whether the PS TV's Ethernet is powered down through a suspend (ksceUsbServDisableEtherSuspend: 0).</summary>
    internal int UsbServEtherSuspend => _usbServEtherSuspend;

    /// <summary>ksceUsbServMacGet(uint channel): the PHY selection of a channel (0..2); 0x80248003 above 2.</summary>
    [Hle(Nid = 0xF0553A69, Library = "SceUsbServForDriver")]
    int ksceUsbServMacGet(uint channel)
    {
        if (channel >= 3)
        {
            return USBSERV_ERR_INVALID;
        }

        return UsbServMacSelection((int)channel);
    }

    /// <summary>ksceUsbServMacSelect(int channel, uint selection): selects the PHY of a channel (0..2, selection
    /// 0..1; 0x80248003 otherwise); the same selection again is 0. The PHY is ready at once on the emulated unit.</summary>
    [Hle(Nid = 0x7AD36284, Library = "SceUsbServForDriver")]
    int ksceUsbServMacSelect(int channel, uint selection)
    {
        if (selection > 1 || (uint)channel >= 3)
        {
            return USBSERV_ERR_INVALID;
        }

        if (channel == 0)
        {
            _usbServMacConnector = (int)selection;
        }
        else if (channel == 1)
        {
            _usbServMacHost = (int)selection;
        }
        else
        {
            _usbServMacDolce = (int)selection;
        }

        return 0;
    }

    /// <summary>ksceUsbServEtherEnable(): powers the PS TV's USB Ethernet; 0x80248002 on a handheld.</summary>
    [Hle(Nid = 0x30AE5F66, Library = "SceUsbServForDriver")]
    int ksceUsbServEtherEnable()
    {
        return AimgrIsGenuineDolce() ? 0 : USBSERV_ERR_NOT_DOLCE;
    }

    /// <summary>ksceUsbServEtherDisable(): powers the PS TV's USB Ethernet down; 0x80248002 on a handheld.</summary>
    [Hle(Nid = 0xD787B191, Library = "SceUsbServForDriver")]
    int ksceUsbServEtherDisable()
    {
        return AimgrIsGenuineDolce() ? 0 : USBSERV_ERR_NOT_DOLCE;
    }

    /// <summary>ksceUsbServDisableEtherSuspend(): the PS TV's Ethernet stays powered through a suspend; 0x80248002
    /// on a handheld.</summary>
    [Hle(Nid = 0x6D738018, Library = "SceUsbServForDriver")]
    int ksceUsbServDisableEtherSuspend()
    {
        if (!AimgrIsGenuineDolce())
        {
            return USBSERV_ERR_NOT_DOLCE;
        }

        _usbServEtherSuspend = 0;
        return 0;
    }

    /// <summary>ksceUsbAudioGetDeviceIdList(int *ids, int max): the ids of the attached USB audio devices, up to max
    /// (8 at most), and their count: none, 0. A NULL list is 0x803E0001.</summary>
    [Hle(Default = true, Nid = 0x96EC3E18, Library = "SceUsbAudioForDriver")]
    int ksceUsbAudioGetDeviceIdList(uint ids, int max)
    {
        if (ids == 0)
        {
            return USBAUDIO_ERR_NULL;
        }

        return 0;
    }

    /// <summary>ksceUsbAudioInput(int id, void *buf, uint size): reads input blocks; size a multiple of 0xC0 from 1
    /// to 8 blocks and a buffer (0x803E0001 otherwise); then the device is looked up: none (0x803E0004).</summary>
    [Hle(Default = true, Nid = 0x863B0617, Library = "SceUsbAudioForDriver")]
    int ksceUsbAudioInput(int id, uint buffer, uint size)
    {
        return UsbAudioInputBlocks(buffer, size) ? USBAUDIO_ERR_NO_DEVICE : USBAUDIO_ERR_NULL;
    }

    /// <summary>ksceUsbAudioInput2(int id, void *buf, uint size): the input entry with per-packet lengths; the same
    /// checks as ksceUsbAudioInput.</summary>
    [Hle(Default = true, Nid = 0xEFB4E3E4, Library = "SceUsbAudioForDriver")]
    int ksceUsbAudioInput2(int id, uint buffer, uint size)
    {
        return UsbAudioInputBlocks(buffer, size) ? USBAUDIO_ERR_NO_DEVICE : USBAUDIO_ERR_NULL;
    }

    /// <summary>ksceUsbAudioRegisterCallback(int id, ...): keeps a callback under the caller's id (0x803E0002 when
    /// the id has one); 0.</summary>
    [Hle(Nid = 0x974DBE03, Library = "SceUsbAudioForDriver")]
    int ksceUsbAudioRegisterCallback(int id, uint callback, uint argument, uint unused)
    {
        if (_usbAudioCallbacks.Contains(id))
        {
            return USBAUDIO_ERR_ALREADY_REGISTERED;
        }

        _usbAudioCallbacks.Add(id);
        return 0;
    }

    /// <summary>ksceUsbAudioUnregisterCallback(int id): drops the id's callback (none: nothing); 0.</summary>
    [Hle(Nid = 0x23A07D29, Library = "SceUsbAudioForDriver")]
    int ksceUsbAudioUnregisterCallback(int id)
    {
        _usbAudioCallbacks.Remove(id);
        return 0;
    }

    int UsbServMacSelection(int channel)
    {
        return channel switch
        {
            0 => _usbServMacConnector,
            1 => _usbServMacHost,
            _ => _usbServMacDolce,
        };
    }

    static bool UsbAudioInputBlocks(uint buffer, uint size)
    {
        uint blocks = size / UsbAudioInputBlock;
        return buffer != 0 && size == blocks * UsbAudioInputBlock && blocks - 1 <= 7;
    }
}
