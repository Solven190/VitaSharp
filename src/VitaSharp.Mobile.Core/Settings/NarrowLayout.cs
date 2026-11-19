namespace VitaSharp.Mobile.Core.Settings;

/// <summary>
/// The app's screens on a narrow (portrait) phone, as the desktop launcher's narrow window: texts wrap instead of
/// ending in an ellipsis, and a setting row whose value would leave its title less than <see cref="MinLabelWidthDp"/>
/// puts the value below the title instead of squeezing it (Kit.Row on the desktop does the same at 200 px).
/// Platform-neutral, so it is unit-tested on the desktop.
/// </summary>
public static class NarrowLayout
{
    /// <summary>The least width a row's title keeps beside its value, in dp.</summary>
    public const double MinLabelWidthDp = 150;

    /// <summary>What a row loses to the card (20 dp margins), its padding (16 dp) and the chevron (about 30 dp).</summary>
    public const double RowChromeDp = 2 * 20 + 2 * 16 + 30;

    /// <summary>The widest a value beside the title takes, in dp; a longer one wraps there.</summary>
    public const double MaxValueWidthDp = 200;

    /// <summary>An estimate of the width of <paramref name="text"/> at <paramref name="fontSize"/> (Inter's average glyph
    /// is a little over half the font size wide).</summary>
    public static double TextWidthDp(string text, double fontSize) => text.Length * fontSize * 0.56;

    /// <summary>The value goes below the row's title on a screen <paramref name="screenWidthDp"/> wide.</summary>
    public static bool ValueBelow(double screenWidthDp, string? value, double fontSize = 14)
    {
        if (string.IsNullOrEmpty(value) || screenWidthDp <= 0)
        {
            return false;
        }

        double valueWidth = Math.Min(TextWidthDp(value, fontSize) + 8, MaxValueWidthDp);
        return screenWidthDp - RowChromeDp - valueWidth < MinLabelWidthDp;
    }
}
