using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

// The last functions of small firmware libraries (FW 3.74, read in the modules' code, study only): the unnamed entries
// of SceUsbSerial (os0:kd/bootimage.skprx SceUsbSerial), SceUsbAudioIn's current-volume getter (SceUsbAudio) and the
// QA-flag query of SceDrmBridge (SceVshBridge, and its user entry in driver_us). Each keeps the module's own checks and
// answers what the module answers without the device or token, so they are documented defaults.

public sealed partial class SystemServicesVsh
{
    /// <summary>The capability bit SceDrmBridge's QA-flag query asks for (SceSblACMgrForDriver_F5AE24AC tests bit
    /// 0x80 of the caller's capability).</summary>
    const int DrmBridgeCapabilityBit = 0x80;

    // ------------------------------------------------------------------ SceUsbSerial: the serial function, no USB host

    /// <summary>The unnamed SceUsbSerial syscalls: each asks ksceSblACMgrIsAllowedUsbSerial (else 0x80010058), then the
    /// function's state, which is never started on the emulated console (no USB host): 0x80244401 before any of the
    /// argument checks (0x80244402) the started function makes. NID_0ACC8A92(flags) clears the receive/send rings,
    /// NID_60D93778(flags) raises the line events, NID_405DB744(mode) opens the line (it also refuses from an interrupt
    /// with 0x80244401), NID_2C08272C() closes it, NID_923535FA() / NID_A281DF87() / NID_642F8F19() give the receive
    /// data size, the send space and the received bytes.</summary>
    [Hle("NID_0ACC8A92", Nid = 0x0ACC8A92, Library = "SceUsbSerial", Default = true)]
    [Hle("NID_2C08272C", Nid = 0x2C08272C, Library = "SceUsbSerial", Default = true)]
    [Hle("NID_405DB744", Nid = 0x405DB744, Library = "SceUsbSerial", Default = true)]
    [Hle("NID_60D93778", Nid = 0x60D93778, Library = "SceUsbSerial", Default = true)]
    [Hle("NID_642F8F19", Nid = 0x642F8F19, Library = "SceUsbSerial", Default = true)]
    [Hle("NID_923535FA", Nid = 0x923535FA, Library = "SceUsbSerial", Default = true)]
    [Hle("NID_A281DF87", Nid = 0xA281DF87, Library = "SceUsbSerial", Default = true)]
    int UsbSerialNotStarted()
    {
        return UsbSerialAllowed ? USBSERIAL_ERR_NOT_STARTED : SBL_ERR_PERM;
    }

    // ------------------------------------------------------------------ SceUsbAudioIn: no USB microphone

    /// <summary>sceUsbAudioInGetCurrentValueOfVolume (NID_198A7C33)(int id, int *value): the device is looked up
    /// first: none is attached, so 0x803E0004 (the NULL check, 0x803E0001, comes after it).</summary>
    [Hle("NID_198A7C33", Nid = 0x198A7C33, Library = "SceUsbAudioIn", Default = true)]
    int UsbAudioInGetCurrentValueOfVolume(int id, uint value)
    {
        return USBAUDIO_ERR_NO_DEVICE;
    }

    // ------------------------------------------------------------------ SceDrmBridge: the QA flags

    /// <summary>NID_B81B597A (SceDrmBridge syscall) and NID_6F5DC6CD (SceDrmBridgeUser, driver_us, which calls it): a
    /// QA flag of SceQafMgr (SceQafMgrForDriver_4BC1883F) for a caller with capability bit 0x80, else 0x80010058. A
    /// retail unit has no QAF token, so the flag is 0 (not allowed).</summary>
    [Hle("NID_B81B597A", Nid = 0xB81B597A, Library = "SceDrmBridge", Default = true)]
    [Hle("NID_6F5DC6CD", Nid = 0x6F5DC6CD, Library = "SceDrmBridgeUser", Default = true)]
    int DrmBridgeQafFlag()
    {
        return Emu.Access.HasCapabilityBit(DrmBridgeCapabilityBit) ? 0 : SBL_ERR_PERM;
    }
}
