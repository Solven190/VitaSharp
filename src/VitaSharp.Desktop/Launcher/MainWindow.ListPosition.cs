using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace VitaSharp.Desktop.Launcher;

/// <summary>Which item of a list takes the focus once some of its items are gone (deleted, uninstalled).</summary>
internal static class ListPosition
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

/// <summary>
/// The scroll position and the focused item of the launcher's lists across a rebuild of the page: deleting a capture,
/// uninstalling a game (from its page, back to the library) or coming back from a game's page keeps the list where it
/// was, with the focus on the item that followed the deleted one.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The items of the lists shown now that can be deleted, by key (title id, file path).</summary>
    readonly Dictionary<string, Control> _listItems = new();

    /// <summary>The keys of <see cref="_listItems"/> in the order they are shown.</summary>
    readonly List<string> _listOrder = new();

    /// <summary>The main scroller of the page shown now (<see cref="PageScroll"/>).</summary>
    ScrollViewer? _pageScroll;

    /// <summary>Kept for the next page built: its scroll offset and the key of the item to focus.</summary>
    Vector? _keptOffset;
    string? _keptFocus;

    /// <summary>The library's position when a game's page was opened from it, given back when the library is
    /// shown again from that page (Escape, back, after uninstalling the game).</summary>
    Vector? _libraryReturnOffset;
    string? _libraryReturnFocus;

    /// <summary>Remembers where the page's list is (and which item gets the focus) for the rebuild that follows.</summary>
    void KeepListPosition(string? focusKey)
    {
        _keptOffset = _pageScroll?.Offset;
        _keptFocus = focusKey;
    }

    /// <summary>An item of a list that can lose items: it can take the keyboard focus, found by its key.</summary>
    void ListItem(string key, Control item)
    {
        item.Focusable = true;
        if (_listItems.TryAdd(key, item))
        {
            _listOrder.Add(key);
        }
    }

    /// <summary>The page's lists are built again: their items are registered anew.</summary>
    void ClearListItems()
    {
        _listItems.Clear();
        _listOrder.Clear();
    }

    /// <summary>The item to focus once <paramref name="removed"/> are gone from the lists shown now.</summary>
    string? NextListItem(IReadOnlyCollection<string> removed) => ListPosition.NextAfterRemoval(_listOrder, removed);

    /// <summary>Registers the page's main scroller and gives it the position kept by <see cref="KeepListPosition"/>
    /// once it is laid out.</summary>
    ScrollViewer PageScroll(ScrollViewer scroll)
    {
        _pageScroll = scroll;
        if (_keptOffset == null && _keptFocus == null)
        {
            return scroll;
        }

        // given once the list is laid out (the grid picks its column count from its width, which moves every card),
        // and only to the page's current scroller: the list may be built again right after (the search box, the view
        // switch), and the kept position goes to the last one built
        void Restore()
        {
            scroll.ScrollChanged -= OnScrollChanged;
            if (_pageScroll != scroll || (_keptOffset == null && _keptFocus == null))
            {
                return;
            }

            Vector? offset = _keptOffset;
            string? focus = _keptFocus;
            _keptOffset = null;
            _keptFocus = null;
            RestoreListPosition(scroll, offset, focus);
        }
        void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
        {
            double extent = scroll.Extent.Height;
            if (extent > 0 && (_keptOffset is not { } kept || extent - scroll.Viewport.Height >= kept.Y))
            {
                Dispatcher.UIThread.Post(Restore, DispatcherPriority.Loaded);
            }
        }
        scroll.ScrollChanged += OnScrollChanged;
        // a list that became too short for the kept offset: restored (clamped) once the page is laid out
        scroll.Loaded += (_, _) => Dispatcher.UIThread.Post(Restore, DispatcherPriority.Background);
        return scroll;
    }

    void RestoreListPosition(ScrollViewer scroll, Vector? offset, string? focus)
    {
        Control? item = null;
        if (focus != null && _listItems.TryGetValue(focus, out Control? found) && found.GetVisualRoot() != null)
        {
            item = found;
            // no scrolling from the focus itself: the list stays where it was unless the item is off screen
            item.Focus(NavigationMethod.Unspecified);
        }

        if (offset is { } kept)
        {
            scroll.Offset = kept;
        }

        if (item != null && item.TranslatePoint(default, scroll) is { } top
            && (top.Y + item.Bounds.Height <= 0 || top.Y >= scroll.Viewport.Height))
        {
            item.BringIntoView();
        }
    }

    /// <summary>Called when the page changes: the library's position is kept for the way back from a game's page.</summary>
    void KeepLibraryReturn(string fromPage, string toPage, GameEntry? game)
    {
        // a position kept for the page being left (never laid out) is not given to another page
        _keptOffset = null;
        _keptFocus = null;
        if (fromPage == PageLibrary && toPage == PageGame)
        {
            _libraryReturnOffset = _pageScroll?.Offset;
            _libraryReturnFocus = game?.TitleId;
        }
        else if (fromPage == PageGame && toPage == PageLibrary && (_libraryReturnOffset != null || _libraryReturnFocus != null))
        {
            _keptOffset = _libraryReturnOffset;
            _keptFocus = _libraryReturnFocus;
            _libraryReturnOffset = null;
            _libraryReturnFocus = null;
        }
        else if (toPage != PageGame)
        {
            _libraryReturnOffset = null;
            _libraryReturnFocus = null;
        }
    }

    internal ScrollViewer? PageScrollForTests => _pageScroll;

    internal Control? ListItemForTests(string key) => _listItems.GetValueOrDefault(key);

    internal IReadOnlyList<string> ListOrderForTests => _listOrder;

    internal Task UninstallForTests(GameEntry game) => Uninstall(game);

    internal Task DeleteCapturesForTests(IReadOnlyList<string> paths) => DeleteCaptures(CaptureFiles().Where(c => paths.Contains(c.Path)).ToList());

    /// <summary>Answers the confirmation dialogs in tests (null: the real dialog).</summary>
    internal Func<string, bool>? ConfirmForTests { get; set; }
}
