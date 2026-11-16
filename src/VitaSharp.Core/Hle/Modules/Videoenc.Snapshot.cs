using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>Save states: the libraries and every encoder; each loaded encoder makes its FFmpeg encoder again at its
/// next picture.</summary>
[GenerateSnapshot]
public sealed partial class Videoenc
{
    /// <summary>The running session's host encoders are closed before the saved encoders replace them.</summary>
    void BeforeSnapshotLoad()
    {
        lock (_lock)
        {
            foreach (Encoder encoder in _encoders.Values)
            {
                encoder.Codec?.Dispose();
            }
        }
    }

    /// <summary>Nothing to read: the encoders are loaded with the fields.</summary>
    void LoadExtra(SnapshotReader r)
    {
    }
}
