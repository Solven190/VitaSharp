using VitaSharp.Core.Localization;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Mobile.Core.Library;

/// <summary>One save state slot of a game as the game page shows it: its picture and date, or why it cannot be loaded.</summary>
public sealed record StateSlot(int Slot, string Path, SaveStateHeader? Header, string? Problem)
{
    /// <summary>Something is saved in the slot (loadable or not).</summary>
    public bool Exists => Header != null || Problem != null;

    /// <summary>The slot can open the game already in its state.</summary>
    public bool CanLoad => Header != null && Problem == null;
}

/// <summary>A "Gravar tudo" recording of a game (captures/&lt;TITLEID&gt;_&lt;date&gt;_&lt;time&gt;_tudo.zip).</summary>
public sealed record GameRecording(string Path, long Bytes, DateTime ModifiedUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// The save states and "Gravar tudo" recordings of one game for the Android game page, the counterpart of the
/// desktop's game page: slots 1-6 (the pause menu's) in &lt;data&gt;/savestates/&lt;TITLEID&gt;/, and the recordings in
/// &lt;data&gt;/captures. A slot opens the game directly in its state (<see cref="LaunchSlot"/>, the desktop's --load-state).
/// </summary>
public static class GameStates
{
    /// <summary>Slots of the pause menu and of the game page.</summary>
    public const int SlotCount = 6;

    /// <summary>Slots 1 to <see cref="SlotCount"/> of <paramref name="titleId"/>, empty ones included.</summary>
    public static IReadOnlyList<StateSlot> Slots(string dataRoot, string titleId)
    {
        var slots = new StateSlot[SlotCount];
        for (int slot = 1; slot <= SlotCount; slot++)
        {
            slots[slot - 1] = Read(slot, SaveStates.SlotPath(dataRoot, titleId, slot), titleId);
        }

        return slots;
    }

    private static StateSlot Read(int slot, string path, string titleId)
    {
        if (!File.Exists(path))
        {
            return new StateSlot(slot, path, null, null);
        }

        try
        {
            SaveStateHeader header = SaveStateFile.ReadHeader(path);
            return new StateSlot(slot, path, header, SaveStateFile.Incompatibility(header, titleId));
        }
        catch (Exception e) when (e is SnapshotException or IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            return new StateSlot(slot, path, null, Loc.Get("StateUnreadable"));
        }
    }

    /// <summary>The slot a game start asked to load (the launch's extra): 1 to <see cref="SlotCount"/>, else 0 (a
    /// normal start).</summary>
    public static int LaunchSlot(int requested)
    {
        return requested is >= 1 and <= SlotCount ? requested : 0;
    }

    /// <summary>The "Gravar tudo" recordings of <paramref name="titleId"/> in <paramref name="capturesRoot"/>, newest first
    /// (the file name starts with the title ID, as SessionCapture writes it and the desktop's Capturas page reads it).</summary>
    public static IReadOnlyList<GameRecording> Recordings(string capturesRoot, string titleId)
    {
        var list = new List<GameRecording>();
        if (!Directory.Exists(capturesRoot))
        {
            return list;
        }

        try
        {
            foreach (FileInfo file in new DirectoryInfo(capturesRoot).EnumerateFiles("*.zip"))
            {
                string name = file.Name;
                int underscore = name.IndexOf('_');
                if (underscore > 0 && name.AsSpan(0, underscore).Equals(titleId, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(new GameRecording(file.FullName, file.Length, file.LastWriteTimeUtc));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return list;
        }

        list.Sort(static (a, b) => b.ModifiedUtc.CompareTo(a.ModifiedUtc));
        return list;
    }
}
