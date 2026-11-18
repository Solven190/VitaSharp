using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceTouchForDriver (os0:kd/bootimage.skprx SceTouch 3.74, read in the module, study only), the entries that only keep
// a value or that the module leaves empty: ksceTouchRegisterVirtualTouchDriver and ksceTouchSetTouchEmulationData
// answer 0 without doing anything on 3.74; the CtrlP callback (NULL: 0x80350001) and NID_384F6C68's per-port word
// (port 0 front, 1 back, else 0x80350001) are kept for the module's sampling.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceTouch: a NULL argument or a port above 1 (0x80350001).</summary>
    const int TOUCH_ERR_INVALID_ARG = unchecked((int)0x80350001);

    [SnapshotMissing] // states of builds before it: nothing set
    uint _touchCtrlpCallback;

    [SnapshotMissing]
    readonly uint[] _touchPortWord = new uint[2];

    /// <summary>The CtrlP callback ksceTouchSetCtrlpCallback keeps, and NID_384F6C68's word of a port.</summary>
    internal (uint Callback, uint Front, uint Back) TouchKernelState => (_touchCtrlpCallback, _touchPortWord[0], _touchPortWord[1]);

    /// <summary>ksceTouchRegisterVirtualTouchDriver(driver): empty on 3.74; 0.</summary>
    [Hle(Nid = 0x03C3AC7C, Library = "SceTouchForDriver")]
    int ksceTouchRegisterVirtualTouchDriver(uint driver)
    {
        return 0;
    }

    /// <summary>ksceTouchSetTouchEmulationData(data): empty on 3.74; 0.</summary>
    [Hle(Nid = 0xA32529B8, Library = "SceTouchForDriver")]
    int ksceTouchSetTouchEmulationData(uint data)
    {
        return 0;
    }

    /// <summary>ksceTouchSetCtrlpCallback(callback): kept; NULL is 0x80350001.</summary>
    [Hle(Nid = 0x628D9324, Library = "SceTouchForDriver")]
    int ksceTouchSetCtrlpCallback(uint callback)
    {
        if (callback == 0)
        {
            return TOUCH_ERR_INVALID_ARG;
        }

        _touchCtrlpCallback = callback;
        return 0;
    }

    /// <summary>NID_384F6C68 (SceTouchForDriver)(uint port, uint value): a word kept per port.</summary>
    [Hle("NID_384F6C68", Nid = 0x384F6C68, Library = "SceTouchForDriver")]
    int TouchSetPortWord(uint port, uint value)
    {
        if (port >= 2)
        {
            return TOUCH_ERR_INVALID_ARG;
        }

        _touchPortWord[port] = value;
        return 0;
    }
}
