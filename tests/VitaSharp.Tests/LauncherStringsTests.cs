using System.Reflection;
using VitaSharp.Core.Hle.Np;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Package;
using VitaSharp.Desktop.Launcher.Ui;

namespace VitaSharp.Tests;

/// <summary>
/// The launcher's texts (<see cref="Strings"/>) as they are shown, in both interface languages: every text reads a
/// translation (never the bare resource key that <see cref="Loc.Get"/> returns for a missing one), every formatted
/// text takes its arguments without a format error, and every name or path passed in appears in the result, so a
/// translation that drops or misnumbers a placeholder fails here and not on screen. LocalizationTests checks the
/// resource files themselves; this runs the code that reads them.
/// </summary>
[Collection("language")]
public sealed class LauncherStringsTests : IDisposable
{
    private readonly string _before = Loc.Culture.Name;

    public void Dispose() => Loc.SetLanguage(_before);

    public static TheoryData<string> Languages => new() { Loc.English, Loc.Portuguese };

    /// <summary>The key exists in the current language or in the English (neutral) file: a text that is its own key
    /// name ("Firmware", "VSync") is then a real translation, not the missing-key fallback.</summary>
    private static bool HasKey(string key) =>
        Loc.Table(Loc.Culture).ContainsKey(key) || Loc.Table(System.Globalization.CultureInfo.InvariantCulture).ContainsKey(key);

    private static IEnumerable<PropertyInfo> TextProperties() =>
        typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string));

    private static IEnumerable<MethodInfo> TextMethods() =>
        typeof(Strings).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(string) && !m.IsSpecialName && m.GetParameters().Length > 0);

    /// <summary>A sample argument per parameter type; strings and package names are unique markers to look for.</summary>
    private static object? Sample(Type t, int i, List<string> markers)
    {
        if (t == typeof(string))
        {
            string s = $"ARG{i}Q";
            markers.Add(s);
            return s;
        }

        if (t == typeof(PkgInfo))
        {
            markers.Add("ARGTITLEQ");
            markers.Add("ARGTIDQ");
            return new PkgInfo(PkgKind.VitaApp, "UP0000-ARGTIDQ_00-0000000000000000", "ARGTIDQ", "ARGTITLEQ");
        }

        return t switch
        {
            _ when t == typeof(int) => 3 + i,
            _ when t == typeof(long) => 125L,
            _ when t == typeof(uint) => 7u,
            _ when t == typeof(double) => 0.25,
            _ when t == typeof(DateTime) || t == typeof(DateTime?) => new DateTime(2026, 11, 5, 22, 47, 0, DateTimeKind.Local),
            _ when t == typeof(DateTimeOffset) => new DateTimeOffset(2026, 11, 5, 22, 47, 0, TimeSpan.Zero),
            _ when t.IsEnum => Enum.GetValues(t).GetValue(0),
            _ => null,
        };
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryTextHasATranslation(string language)
    {
        Loc.SetLanguage(language);
        var missing = new List<string>();
        foreach (PropertyInfo p in TextProperties())
        {
            string text = (string)p.GetValue(null)!;
            if (string.IsNullOrWhiteSpace(text) || (text == p.Name && !HasKey(p.Name)))
            {
                missing.Add(p.Name);
            }
        }

        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryFormattedTextShowsWhatItIsGiven(string language)
    {
        Loc.SetLanguage(language);
        var bad = new List<string>();
        foreach (MethodInfo m in TextMethods())
        {
            var markers = new List<string>();
            object?[] args = m.GetParameters().Select((p, i) => Sample(p.ParameterType, i, markers)).ToArray();
            string text;
            try
            {
                text = (string)m.Invoke(null, args)!;
            }
            catch (TargetInvocationException e)
            {
                bad.Add($"{m.Name}: {e.InnerException?.GetType().Name} {e.InnerException?.Message}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(text) || (text == m.Name && !HasKey(m.Name)))
            {
                bad.Add($"{m.Name}: no translation");
                continue;
            }

            foreach (string marker in markers.Where(k => !text.Contains(k, StringComparison.Ordinal)))
            {
                bad.Add($"{m.Name}: '{marker}' missing from '{text}'");
            }
        }

        Assert.Empty(bad);
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void EveryPackageKindHasAName(string language)
    {
        Loc.SetLanguage(language);
        foreach (PkgKind kind in Enum.GetValues<PkgKind>())
        {
            string name = Strings.PkgKindName(kind);
            Assert.False(string.IsNullOrWhiteSpace(name), $"{kind}");
            Assert.False(name.StartsWith("PkgKind", StringComparison.Ordinal), $"{kind} shows its key {name}");
        }

        foreach (TrophyGrade g in Enum.GetValues<TrophyGrade>())
        {
            Assert.False(Strings.Grade(g).StartsWith("Grade", StringComparison.Ordinal), $"{g}");
        }
    }

    [Fact]
    public void DurationsSizesAndDatesUseTheirUnits()
    {
        Loc.SetLanguage(Loc.English);
        // hours and minutes from 1 h, whole minutes from 1 min, then "under a minute"
        string hours = Strings.Duration(12 * 3600 + 40 * 60 + 59);
        Assert.Contains("12", hours);
        Assert.Contains("40", hours);
        Assert.Contains("35", Strings.Duration(35 * 60 + 10));
        Assert.Equal(Strings.Duration(0), Strings.Duration(59));
        Assert.NotEqual(Strings.Duration(59), Strings.Duration(60));
        // binary units, one decimal for MB/GB and whole KB below
        Assert.Equal("1.5 GB", Strings.Size(3L << 29));
        Assert.Equal("2.0 MB", Strings.Size(2L << 20));
        Assert.Equal("3 KB", Strings.Size(3 * 1024));
        // the decimal separator follows the language
        Loc.SetLanguage(Loc.Portuguese);
        Assert.Equal("1,5 GB", Strings.Size(3L << 29));
        Loc.SetLanguage(Loc.English);
        // today and yesterday are named, older days are dated
        DateTime today = DateTime.Today.AddHours(21).AddMinutes(14);
        DateTime yesterday = today.AddDays(-1);
        DateTime older = today.AddDays(-10);
        Assert.Contains("21:14", Strings.When(today));
        Assert.NotEqual(Strings.When(today).Replace("21:14", ""), Strings.When(yesterday).Replace("21:14", ""));
        Assert.Contains(older.ToString("yyyy"), Strings.When(older));
        Assert.Equal(Strings.NeverPlayed, Strings.PlayedSummary(null));
    }

    [Fact]
    public void FirmwareRowsListOnlyWhatIsKnown()
    {
        Loc.SetLanguage(Loc.English);
        Assert.Equal("Firmware 3.74", Strings.FirmwareVersionRow("3.74"));
        Assert.Equal("Firmware", Strings.FirmwareVersionRow(null));
        // build, source and install date joined by a middle dot; the unknown ones are left out
        Assert.Equal("PSVUPDAT.PUP", Strings.FirmwareDetail(0, "PSVUPDAT.PUP", null));
        string full = Strings.FirmwareDetail(3740011, "PSVUPDAT.PUP", new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc));
        string[] parts = full.Split(" \u00B7 ");
        Assert.Equal(3, parts.Length);
        Assert.Contains("3740011", parts[0]);
        Assert.Equal("PSVUPDAT.PUP", parts[1]);
    }
}
