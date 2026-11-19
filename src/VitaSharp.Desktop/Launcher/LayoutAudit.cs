using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace VitaSharp.Desktop.Launcher;

/// <summary>
/// Checks a laid-out launcher page for what a narrow window cuts off: a control that sticks out sideways past the
/// control holding it (a row of buttons wider than its card, a fixed-width field past the window), and a label that is
/// cut short (a button's text with an ellipsis, or text drawn wider than the space it got). Titles and details may end
/// in an ellipsis; button labels may not. The recreated Vita screens (home screen, LiveArea) are drawn scaled to fit
/// and are not checked. Used by the headless tests and <c>ui-render --layout-check</c>.
/// </summary>
internal static class LayoutAudit
{
    /// <summary>The overflow tolerated (rounding of the layout), in device-independent pixels.</summary>
    const double Slack = 1;

    /// <summary>Every problem found under <paramref name="root"/>, one line each ("what" sticks out of / is cut in "where").</summary>
    public static List<string> Check(Visual root)
    {
        List<string> problems = new();
        Walk(root, root, null, false, problems);
        return problems;
    }

    static void Walk(Visual visual, Visual root, Rect? parentRect, bool inButton, List<string> problems)
    {
        if (!visual.IsVisible || visual is Shell.ShellView or Shell.HomeScreenView or Shell.LiveAreaView or Popup)
        {
            return;
        }

        Rect rect = Placement(visual, root);
        if (rect.Width <= 0)
        {
            return;
        }

        // a panel that draws nothing only places its children: what counts is where they land (a grid of cards may
        // reach past the page by the gap that is each card's right margin), so they are checked against the area above it
        bool layoutOnly = visual is Panel { Background: null } and not Canvas;
        if (!layoutOnly && parentRect is { } outer && (rect.Left < outer.Left - Slack || rect.Right > outer.Right + Slack))
        {
            problems.Add($"{Describe(visual)} [{rect.Left:F0}..{rect.Right:F0}] sticks out of {Describe(visual.GetVisualParent()!)} [{outer.Left:F0}..{outer.Right:F0}]");
        }

        bool button = inButton || visual is Button;
        if (visual is TextBlock text && IsCut(text, button))
        {
            problems.Add($"{Describe(text)} is cut (width {text.Bounds.Width:F0})");
        }

        // the content of a sideways-scrolling area may be wider than the area (only its viewport is checked), and a
        // canvas places its children where it wants (a toggle switch's knob slides past its track)
        bool free = visual is ScrollContentPresenter { CanHorizontallyScroll: true } or Canvas;
        foreach (Visual child in visual.GetVisualChildren())
        {
            Walk(child, root, free ? null : layoutOnly ? parentRect : rect, button, problems);
        }
    }

    /// <summary>Where <paramref name="visual"/> is drawn, in <paramref name="root"/>'s coordinates.</summary>
    static Rect Placement(Visual visual, Visual root)
    {
        Matrix? transform = visual.TransformToVisual(root);
        return transform is { } matrix ? new Rect(visual.Bounds.Size).TransformToAABB(matrix) : default;
    }

    /// <summary>A label cut short: an ellipsis on a button's text, or a line drawn wider than the text block.</summary>
    static bool IsCut(TextBlock text, bool inButton)
    {
        if (string.IsNullOrEmpty(text.Text) || text.TextLayout is not { } layout)
        {
            return false;
        }

        foreach (TextLine line in layout.TextLines)
        {
            if (line.HasCollapsed && inButton)
            {
                return true;
            }
        }

        double room = text.Bounds.Width - text.Padding.Left - text.Padding.Right;
        return text.TextTrimming == TextTrimming.None && layout.Width > room + Slack;
    }

    static string Describe(Visual visual)
    {
        string name = visual.GetType().Name;
        string? label = visual switch
        {
            TextBlock text => text.Text,
            ContentControl { Content: string content } => content,
            _ => FirstText(visual),
        };
        return label == null ? name : $"{name} \"{(label.Length > 40 ? label[..40] + "..." : label)}\"";
    }

    static string? FirstText(Visual visual)
    {
        foreach (Visual descendant in visual.GetVisualDescendants())
        {
            if (descendant is TextBlock { Text.Length: > 0 } text)
            {
                return text.Text;
            }
        }

        return null;
    }
}
