using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the library and every encoder (bit rate, frame size, kind, context). The CELP frames carry no
/// host encoder state (there is no CELP encoder: the payload is zero), so nothing is recreated on load.</summary>
[GenerateSnapshot]
public sealed partial class Audioenc
{
    /// <summary>Nothing to recreate: the encoders are loaded with the fields.</summary>
    void LoadExtra(SnapshotReader r)
    {
    }
}
