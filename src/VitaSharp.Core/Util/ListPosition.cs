namespace VitaSharp.Core.Util;

/// <summary>Which item of a list takes the focus once some of its items are gone (deleted, uninstalled): the desktop
/// launcher's lists and the Android app's (library, recordings).</summary>
public static class ListPosition
{
    /// <summary>The item that followed the first removed one in <paramref name="keysBefore"/> (the list as it was
    /// shown); the one before it when the removed items were at the end; null when nothing is left.</summary>
    public static string? NextAfterRemoval(IReadOnlyList<string> keysBefore, IReadOnlyCollection<string> removed)
    {
        int first = -1;
        for (int i = 0; i < keysBefore.Count; i++)
        {
            if (removed.Contains(keysBefore[i]))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return null;
        }

        for (int i = first + 1; i < keysBefore.Count; i++)
        {
            if (!removed.Contains(keysBefore[i]))
            {
                return keysBefore[i];
            }
        }

        for (int i = first - 1; i >= 0; i--)
        {
            if (!removed.Contains(keysBefore[i]))
            {
                return keysBefore[i];
            }
        }

        return null;
    }
}
