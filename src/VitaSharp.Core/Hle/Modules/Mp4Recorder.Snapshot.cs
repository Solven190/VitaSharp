using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: every recorder with its mode and sample counts. Samples are counted and dropped (no file is
/// written), so there is no host recording to recreate on load.</summary>
[GenerateSnapshot]
public sealed partial class Mp4Recorder
{
    /// <summary>Nothing to recreate: the recorders are loaded with the fields.</summary>
    void LoadExtra(SnapshotReader r)
    {
    }
}
