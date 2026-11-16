using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

[GenerateSnapshot]
public sealed partial class Gps
{
}

/// <summary>Save states: the open handles with their methods and confirmation dialog state (no host resource
/// behind them: the unit has no positioning hardware).</summary>
[GenerateSnapshot]
public sealed partial class Location
{
    /// <summary>Nothing to recreate: the handles are loaded with the fields.</summary>
    void LoadExtra(SnapshotReader r)
    {
    }
}
