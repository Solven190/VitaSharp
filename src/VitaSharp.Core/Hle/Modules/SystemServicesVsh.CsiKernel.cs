using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceCsiForDriver (os0:kd/lowio.skprx 3.74, read in the module, study only): the two MIPI CSI-2 receivers the camera
// driver (SceCamera) programs: per port (0..1, else 0x803F0900) a timing block, an interrupt callback, the receiver
// started with a data format (the low byte 0, 8, 0x10..0x12, 0x30 or 0x40, else 0x803F0901) and the frame DMA started
// and stopped. The emulated unit has no CSI receiver (its camera frames come from the camera library itself): the
// entries keep their checks and state and answer as the module does, as defaults.
public sealed partial class SystemServicesVsh
{
    const int CsiPorts = 2, CsiTimingBytes = 0x2C;

    /// <summary>SceCsi: a port above 1 (0x803F0900).</summary>
    const int CSI_ERR_PORT = unchecked((int)0x803F0900);

    /// <summary>SceCsi: a data format the receiver does not take (0x803F0901).</summary>
    const int CSI_ERR_FORMAT = unchecked((int)0x803F0901);

    [SnapshotMissing] // states of builds before it: nothing programmed
    readonly byte[] _csiTiming = new byte[CsiPorts * CsiTimingBytes];

    [SnapshotMissing]
    readonly uint[] _csiCallback = new uint[CsiPorts * 2];

    [SnapshotMissing]
    readonly int[] _csiFormat = new int[CsiPorts];

    [SnapshotMissing]
    readonly int[] _csiDmaRunning = new int[CsiPorts];

    /// <summary>The data format a port's receiver was started with (-1 never started), and whether its frame DMA
    /// runs.</summary>
    internal (int Format, bool DmaRunning) CsiPortState(int port) => (_csiFormat[port] - 1, _csiDmaRunning[port] != 0);

    /// <summary>NID_B508822B (SceCsiForDriver)(uint port, callback, argument): the port's interrupt callback.</summary>
    [Hle("NID_B508822B", Nid = 0xB508822B, Library = "SceCsiForDriver", Default = true)]
    int CsiSetCallback(uint port, uint callback, uint argument)
    {
        if (port >= CsiPorts)
        {
            return CSI_ERR_PORT;
        }

        _csiCallback[port * 2] = callback;
        _csiCallback[port * 2 + 1] = argument;
        return 0;
    }

    /// <summary>NID_10545393 (SceCsiForDriver)(uint port, const void *timing): keeps the port's 0x2C-byte timing
    /// block for the next start.</summary>
    [Hle("NID_10545393", Nid = 0x10545393, Library = "SceCsiForDriver", Default = true)]
    int CsiSetTiming(uint port, uint timing)
    {
        if (port >= CsiPorts)
        {
            return CSI_ERR_PORT;
        }

        GuestMemory.ReadSpan(timing, CsiTimingBytes).CopyTo(_csiTiming.AsSpan((int)port * CsiTimingBytes));
        return 0;
    }

    /// <summary>NID_2AFEA1B0 (SceCsiForDriver)(uint port, uint format): starts the port's receiver with a data
    /// format.</summary>
    [Hle("NID_2AFEA1B0", Nid = 0x2AFEA1B0, Library = "SceCsiForDriver", Default = true)]
    int CsiStartReceiver(uint port, uint format)
    {
        if (port >= CsiPorts)
        {
            return CSI_ERR_PORT;
        }

        uint kind = format & 0xFF;
        if (kind is not (0 or 8 or 0x10 or 0x11 or 0x12 or 0x30 or 0x40))
        {
            return CSI_ERR_FORMAT;
        }

        _csiFormat[port] = (int)format + 1;
        return 0;
    }

    /// <summary>NID_A1D1805D (SceCsiForDriver)(uint port): stops the port's receiver (the module waits up to 300 ms
    /// for the frame in flight); 0.</summary>
    [Hle("NID_A1D1805D", Nid = 0xA1D1805D, Library = "SceCsiForDriver", Default = true)]
    int CsiStopReceiver(uint port)
    {
        if (port >= CsiPorts)
        {
            return CSI_ERR_PORT;
        }

        _csiFormat[port] = 0;
        return 0;
    }

    /// <summary>NID_FC165297 (SceCsiForDriver)(uint port, uint buffer): starts the port's frame DMA into a
    /// buffer.</summary>
    [Hle("NID_FC165297", Nid = 0xFC165297, Library = "SceCsiForDriver", Default = true)]
    int CsiStartDma(uint port, uint buffer)
    {
        if (port >= CsiPorts)
        {
            return CSI_ERR_PORT;
        }

        _csiDmaRunning[port] = 1;
        return 0;
    }

    /// <summary>NID_4AE39F26 (SceCsiForDriver)(uint port): stops the port's frame DMA.</summary>
    [Hle("NID_4AE39F26", Nid = 0x4AE39F26, Library = "SceCsiForDriver", Default = true)]
    int CsiStopDma(uint port)
    {
        if (port >= CsiPorts)
        {
            return CSI_ERR_PORT;
        }

        _csiDmaRunning[port] = 0;
        return 0;
    }
}
