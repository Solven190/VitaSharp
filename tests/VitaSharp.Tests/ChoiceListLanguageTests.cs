using System.Globalization;
using VitaSharp.Core.Firmware;
using VitaSharp.Core.Localization;
using VitaSharp.Desktop.Launcher;
using VitaSharp.Mobile.Core.Settings;

namespace VitaSharp.Tests;

/// <summary>
/// The choice lists of both launchers in the interface language of the moment. The Android app's lists
/// (<see cref="SettingsChoices"/>) were built once per process, at the first read: the labels and the FPS overlay's
/// mode kept the language the process had then (after switching the app's language without restarting, and in the
/// suite whenever another test had switched to Portuguese at that moment: "Seguir o sistema" against the desktop's
/// "Follow the system" on the macOS CI, 18/11/2026). In the "language" collection, which never runs beside other tests.
/// </summary>
[Collection("language")]
public sealed class ChoiceListLanguageTests
{
    private const int BrazilianPortuguese = 17;

    private static IEnumerable<(string List, string Label)> AndroidLabels()
    {
        foreach ((string name, IReadOnlyList<Choice> choices) in new[]
        {
            ("Clocks", SettingsChoices.Clocks),
            ("GpuProfiles", SettingsChoices.GpuProfiles),
            ("ResScales", SettingsChoices.ResScales),
            ("FrameRates", SettingsChoices.FrameRates),
            ("HapticStrengths", SettingsChoices.HapticStrengths),
            ("MsaaModes", SettingsChoices.MsaaModes),
        })
        {
            foreach (Choice choice in choices)
            {
                yield return (name, choice.Label);
            }
        }

        // the other entries are each language in its own name, the same in every interface language
        yield return ("SystemLanguages", SettingsChoices.SystemLanguages[0].Label);
    }

    /// <summary>Switching the language back and forth: every label of every Android list is a text of the language of
    /// that moment, whichever language the lists were first read in.</summary>
    [Fact]
    public void TheAndroidChoicesFollowALanguageChange()
    {
        IReadOnlyDictionary<string, string> english = Loc.Table(CultureInfo.InvariantCulture);
        IReadOnlyDictionary<string, string> portuguese = Loc.Table(CultureInfo.GetCultureInfo(Loc.Portuguese));
        foreach (string language in new[] { Loc.Portuguese, Loc.English, Loc.Portuguese, Loc.English })
        {
            using var scope = new LanguageScope(language);
            IReadOnlyDictionary<string, string> texts = language == Loc.English ? english : portuguese;
            foreach ((string list, string label) in AndroidLabels())
            {
                Assert.True(texts.Values.Contains(label), $"{language}: {list} shows \"{label}\", not a {language} text");
            }

            Assert.Equal(Loc.Format("MFpsWithMode", 30, Loc.Get("RateFixed30")), SettingsChoices.FpsText(30, "30"));
            Assert.Equal(language == Loc.English ? "Follow the system" : "Seguir o sistema", SettingsChoices.SystemLanguages[0].Label);
        }
    }

    /// <summary>The desktop launcher's game page and the Android app's game page offer the same system languages, with
    /// the same labels, in each interface language.</summary>
    [Theory]
    [InlineData(Loc.English, "Follow the system")]
    [InlineData(Loc.Portuguese, "Seguir o sistema")]
    public void BothLaunchersOfferTheSameLanguages(string language, string follow)
    {
        using var scope = new LanguageScope(language);
        (int? Value, string Label)[] desktop = GameOptions.SystemLanguages;
        IReadOnlyList<Choice> android = SettingsChoices.SystemLanguages;
        Assert.Equal(SystemLanguage.Count + 1, desktop.Length);
        Assert.Equal(desktop.Length, android.Count);
        Assert.Equal(follow, desktop[0].Label);
        for (int i = 0; i < desktop.Length; i++)
        {
            Assert.Equal(desktop[i].Value?.ToString(CultureInfo.InvariantCulture) ?? "", android[i].Value);
            Assert.Equal(desktop[i].Label, android[i].Label);
        }

        Assert.Equal("English (United States)", SystemLanguage.Names[SystemLanguage.EnglishUs]);
        Assert.Equal("Português (Brasil)", SystemLanguage.Names[BrazilianPortuguese]);
    }
}
