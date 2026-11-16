using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the decoder and encoder contexts by address; each loaded decoder makes its FFmpeg decoder
/// again at its next frame.</summary>
[GenerateSnapshot]
public sealed unsafe partial class G729
{
    /// <summary>The running session's host decoders are closed before the saved contexts replace them.</summary>
    void BeforeSnapshotLoad()
    {
        lock (_lock)
        {
            foreach (DecState state in _dec.Values)
            {
                state.Codec?.Dispose();
            }
        }
    }

    /// <summary>Nothing to read: the contexts are loaded with the fields.</summary>
    void LoadExtra(SnapshotReader r)
    {
    }
}
