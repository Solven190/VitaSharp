using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// NID_28CF3F83 of np_matching2.suprx 3.74 (decompiled, study only): the system apps hand the library a 4-byte
/// option word, which it keeps with a flag that it was set (its bit 0 changes how the library's request path
/// answers). NID_B096B7A9 and NID_40585786 set and read the library's signaling port (9305 until set, and again
/// after sceNpMatching2Term).
/// </summary>
public sealed partial class NpMatching2
{
    const int ERR_NULL_OPTION = unchecked((int)0x80550C0A);
    const int ERR_NULL_PORT = unchecked((int)0x80550E15);

    /// <summary>The signaling port np_matching2 starts with (0x2459).</summary>
    const ushort DefaultSignalingPort = 9305;

    /// <summary>The signaling port NID_B096B7A9 set.</summary>
    [SnapshotMissing] // states of builds before it: the call did not exist, so the port was the default
    ushort _signalingPort = DefaultSignalingPort;

    /// <summary>The option word NID_28CF3F83 was given, and whether it was.</summary>
    [SnapshotMissing] // states of builds before it: the call did not exist, so it was never set
    uint _internalOption;

    [SnapshotMissing]
    bool _internalOptionSet;

    internal (uint Option, bool Set) InternalOption() => (_internalOption, _internalOptionSet);

    /// <summary>NID_28CF3F83(const uint32_t *option): keeps the word; 0x80550C0A for NULL.</summary>
    [Hle(Nid = 0x28CF3F83, Library = "SceNpMatching2")]
    int NID_28CF3F83(uint option)
    {
        if (option == 0)
        {
            return ERR_NULL_OPTION;
        }

        _internalOption = GuestMemory.R32(option);
        _internalOptionSet = true;
        return 0;
    }

    /// <summary>NID_B096B7A9(uint16_t port): sets the signaling port; 0.</summary>
    [Hle(Nid = 0xB096B7A9, Library = "SceNpMatching2")]
    int NID_B096B7A9(uint port)
    {
        lock (_lock)
        {
            _signalingPort = (ushort)port;
        }

        return 0;
    }

    /// <summary>NID_40585786(uint16_t *port): the signaling port; 0x80550E15 for NULL.</summary>
    [Hle(Nid = 0x40585786, Library = "SceNpMatching2")]
    int NID_40585786(Ptr<ushort> port)
    {
        if (port.IsNull)
        {
            return ERR_NULL_PORT;
        }

        lock (_lock)
        {
            port.Value = _signalingPort;
        }

        return 0;
    }
}
