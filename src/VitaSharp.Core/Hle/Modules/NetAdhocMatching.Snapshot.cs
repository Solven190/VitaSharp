using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the contexts (mode, port, intervals, handler, hello data, running). The offline profile has
/// no network session behind them (no peer is ever seen), so nothing is recreated on load.</summary>
[GenerateSnapshot]
public sealed partial class NetAdhocMatching
{
    /// <summary>Nothing to recreate: the contexts are loaded with the fields.</summary>
    void LoadExtra(SnapshotReader r)
    {
    }
}
