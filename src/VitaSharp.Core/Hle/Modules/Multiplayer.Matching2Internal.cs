using VitaSharp.Core.Memory;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// NID_28CF3F83 of np_matching2.suprx 3.74 (decompiled, study only): the system apps hand the library a 4-byte
/// option word, which it keeps with a flag that it was set (its bit 0 changes how the library's request path
/// answers).
/// </summary>
public sealed partial class NpMatching2
{
    const int ERR_NULL_OPTION = unchecked((int)0x80550C0A);

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
}
