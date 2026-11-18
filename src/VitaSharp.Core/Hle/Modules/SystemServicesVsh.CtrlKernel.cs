using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

// SceCtrlForDriver (os0:kd/bootimage.skprx SceCtrl 3.74, read in the module, study only), the settings part: the idle
// cancel keys (four words) and stick thresholds (each -1..0x80; -1, kept as 0x81, means "any travel"; else 0x80340001)
// that reset the auto-dim timer, the two button masks ScePower and the shell update (for every process and for
// processes other than the shell: clear bits, then set bits), and the Blu-ray remote's connection flag SceDs3 sets.
// The bridge's vshCtrl* forms check the caller is a system program (0x80010058) and call these, so the state lives
// here once.
public sealed partial class SystemServicesVsh
{
    /// <summary>SceCtrl: a NULL output or a threshold out of range (0x80340001).</summary>
    const int CTRL_ERR_INVALID_ARG = unchecked((int)0x80340001);

    /// <summary>The module's value of a threshold of -1.</summary>
    const int CtrlThresholdAny = 0x81;

    [SnapshotMissing] // states of builds before it: nothing set
    readonly uint[] _ctrlIdleCancelKeys = new uint[4];

    [SnapshotMissing]
    int _ctrlIdleThresholdLeft;

    [SnapshotMissing]
    int _ctrlIdleThresholdRight;

    [SnapshotMissing]
    uint _ctrlMaskForAll;

    [SnapshotMissing]
    uint _ctrlMaskForNonShell;

    [SnapshotMissing]
    int _ctrlBdRemoconConnected;

    /// <summary>ksceCtrlSetIdleCancelKey(uint a, uint b, uint c, uint d): the four key words; 0.</summary>
    [Hle(Nid = 0x0A679512, Library = "SceCtrlForDriver")]
    internal int ksceCtrlSetIdleCancelKey(uint first, uint second, uint third, uint fourth)
    {
        _ctrlIdleCancelKeys[0] = first;
        _ctrlIdleCancelKeys[1] = second;
        _ctrlIdleCancelKeys[2] = third;
        _ctrlIdleCancelKeys[3] = fourth;
        return 0;
    }

    /// <summary>ksceCtrlGetIdleCancelKey(uint *a, uint *b, uint *c, uint *d): the four key words (each output
    /// optional); 0.</summary>
    [Hle(Nid = 0x30801AE9, Library = "SceCtrlForDriver")]
    int ksceCtrlGetIdleCancelKey(uint first, uint second, uint third, uint fourth)
    {
        ReadOnlySpan<uint> outputs = [first, second, third, fourth];
        for (int i = 0; i < 4; i++)
        {
            if (outputs[i] != 0)
            {
                GuestMemory.W32(outputs[i], _ctrlIdleCancelKeys[i]);
            }
        }

        return 0;
    }

    /// <summary>ksceCtrlSetIdleCancelThreshold(int left, int right): each -1..0x80.</summary>
    [Hle(Nid = 0x345E74FD, Library = "SceCtrlForDriver")]
    internal int ksceCtrlSetIdleCancelThreshold(int left, int right)
    {
        if ((uint)(left + 1) >= 0x82 || (uint)(right + 1) >= 0x82)
        {
            return CTRL_ERR_INVALID_ARG;
        }

        _ctrlIdleThresholdLeft = left == -1 ? CtrlThresholdAny : left;
        _ctrlIdleThresholdRight = right == -1 ? CtrlThresholdAny : right;
        return 0;
    }

    /// <summary>ksceCtrlGetIdleCancelThreshold(int *left, int *right): both outputs needed.</summary>
    [Hle(Nid = 0xB9402CEF, Library = "SceCtrlForDriver")]
    int ksceCtrlGetIdleCancelThreshold(uint left, uint right)
    {
        if (left == 0 || right == 0)
        {
            return CTRL_ERR_INVALID_ARG;
        }

        GuestMemory.W32(left, (uint)(_ctrlIdleThresholdLeft == CtrlThresholdAny ? -1 : _ctrlIdleThresholdLeft));
        GuestMemory.W32(right, (uint)(_ctrlIdleThresholdRight == CtrlThresholdAny ? -1 : _ctrlIdleThresholdRight));
        return 0;
    }

    /// <summary>ksceCtrlUpdateMaskForAll(uint clear, uint set); 0.</summary>
    [Hle(Nid = 0x12C13A72, Library = "SceCtrlForDriver")]
    int ksceCtrlUpdateMaskForAll(uint clear, uint set)
    {
        _ctrlMaskForAll = set | (_ctrlMaskForAll & ~clear);
        return 0;
    }

    /// <summary>ksceCtrlUpdateMaskForNonShell(uint clear, uint set); 0.</summary>
    [Hle(Nid = 0x03777131, Library = "SceCtrlForDriver")]
    int ksceCtrlUpdateMaskForNonShell(uint clear, uint set)
    {
        _ctrlMaskForNonShell = set | (_ctrlMaskForNonShell & ~clear);
        return 0;
    }

    /// <summary>ksceCtrlGetMaskForAll(uint *mask).</summary>
    [Hle(Nid = 0x46ABDDE8, Library = "SceCtrlForDriver")]
    int ksceCtrlGetMaskForAll(uint mask)
    {
        if (mask == 0)
        {
            return CTRL_ERR_INVALID_ARG;
        }

        GuestMemory.W32(mask, _ctrlMaskForAll);
        return 0;
    }

    /// <summary>ksceCtrlGetMaskForNonShell(uint *mask).</summary>
    [Hle(Nid = 0xB25BD8FD, Library = "SceCtrlForDriver")]
    int ksceCtrlGetMaskForNonShell(uint mask)
    {
        if (mask == 0)
        {
            return CTRL_ERR_INVALID_ARG;
        }

        GuestMemory.W32(mask, _ctrlMaskForNonShell);
        return 0;
    }

    /// <summary>ksceCtrlSetBdRemoconConnected(int connected): kept as given; 0.</summary>
    [Hle(Nid = 0xAA36F578, Library = "SceCtrlForDriver")]
    int ksceCtrlSetBdRemoconConnected(int connected)
    {
        _ctrlBdRemoconConnected = connected;
        return 0;
    }

    /// <summary>ksceCtrlIsBdRemoconConnected(): the flag SceDs3 set (0 at start).</summary>
    [Hle(Nid = 0x5B849B22, Library = "SceCtrlForDriver")]
    internal int ksceCtrlIsBdRemoconConnected()
    {
        return _ctrlBdRemoconConnected;
    }
}
