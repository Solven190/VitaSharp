using VitaSharp.Core.Util;

namespace VitaSharp.Mobile.Core.Library;

/// <summary>
/// Deleting from a list of the Android app (uninstalling a game from the library, deleting a "Gravar tudo" recording):
/// the item leaves the list in place, without reloading the screen, so the list stays where it was scrolled, and the
/// item that followed it (the one before, when it was the last; <see cref="ListPosition"/>, the desktop launcher's
/// rule) becomes the highlighted one the screen scrolls to.
/// </summary>
public static class ListRemoval
{
    /// <summary><paramref name="items"/> without the ones whose key is in <paramref name="removed"/>, and the key of the
    /// item to focus: the next one in <paramref name="shownKeys"/> (the order on screen, which a search or filter may make
    /// different from <paramref name="items"/>), null when nothing is left.</summary>
    public static (List<T> Remaining, string? Focus) Remove<T>(IReadOnlyList<T> items, Func<T, string> key, IReadOnlyList<string> shownKeys,
        IReadOnlyCollection<string> removed)
    {
        string? focus = ListPosition.NextAfterRemoval(shownKeys, removed);
        var remaining = new List<T>(items.Count);
        foreach (T item in items)
        {
            if (!removed.Contains(key(item)))
            {
                remaining.Add(item);
            }
        }

        return (remaining, focus);
    }
}
