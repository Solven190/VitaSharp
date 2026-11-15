using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>SceCamera NID_99B5FB62 as the SceCamera driver of bootimage.skprx 3.74 has it.</summary>
public sealed partial class Camera
{
    /// <summary>The mode NID_99B5FB62 gave this process (0 at start).</summary>
    [SnapshotMissing] // states of builds before it: the call did not exist, so it was never set
    private int _bufferMode;

    internal int BufferMode => _bufferMode;

    /// <summary>
    /// NID_99B5FB62 (int mode): chooses, for the calling process, which of the driver's two sets of image buffers
    /// (three per camera) the next sceCameraOpen uses: mode 1 the second set, mode 0 the first. The driver keeps the
    /// mode in the process's camera data and switches its current set at once; any other mode is 0x802E0000. The
    /// emulated cameras deliver host frames into the program's own buffers, so only the mode is kept.
    /// </summary>
    [Hle("NID_99B5FB62", Nid = 0x99B5FB62, Library = "SceCamera")]
    private int CameraSetBufferMode(int mode)
    {
        if ((uint)mode >= 2)
        {
            return ERR_PARAM;
        }

        _bufferMode = mode;
        return 0;
    }
}
