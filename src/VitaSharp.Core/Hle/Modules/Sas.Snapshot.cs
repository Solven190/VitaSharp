using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the public mixer and every context a context variant made, whole (voices with their source,
/// resampler and envelope positions, the effect and its delay line). The mixer is managed code over guest memory, so
/// nothing is recreated on load.</summary>
[GenerateSnapshot]
public sealed partial class Sas
{
    /// <summary>Nothing to recreate: the engines are loaded with the fields.</summary>
    void LoadExtra(SnapshotReader r)
    {
    }
}
