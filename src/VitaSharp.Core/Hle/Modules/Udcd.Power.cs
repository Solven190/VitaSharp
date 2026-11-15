using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceUdcd NID_FCD876FB as the SceUdcd driver of bootimage.skprx 3.74 has it.</summary>
public sealed partial class SystemServicesVsh
{
    /// <summary>Whether the USB device side announces itself bus-powered (charging from the host).</summary>
    [SnapshotMissing] // states of builds before it: the call did not exist, so it was never set
    private bool _udcdBusPowered;

    internal bool UdcdBusPowered => _udcdBusPowered;

    /// <summary>
    /// NID_FCD876FB (int mode): the USB device's power mode. Callers the driver allows are system programs (the QA
    /// mini settings and the USB serial capabilities also pass, none of them a retail application's); others get
    /// 0x80010058. Mode 1 marks the configuration descriptors bus-powered (0x80, 500 mA) and turns USB charging on,
    /// any other self-powered (0xC0, 2 mA) with charging off; the call answers 0. No USB host is emulated, so only the
    /// mode is kept.
    /// </summary>
    [Hle("NID_FCD876FB", Nid = 0xFCD876FB, Library = "SceUdcd")]
    private int UdcdSetPowerMode(int mode)
    {
        if (!Emu.Access.IsSystemProgram)
        {
            return unchecked((int)0x80010058);
        }

        _udcdBusPowered = mode == 1;
        return 0;
    }
}
