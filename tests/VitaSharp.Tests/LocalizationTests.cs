using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using VitaSharp.Core.Localization;

namespace VitaSharp.Tests;

/// <summary>
/// The interface texts (src/VitaSharp.Core/Localization/Texts.resx in English, Texts.pt-BR.resx in Portuguese): both
/// files have the same keys with the same placeholders, every key the code asks for exists, and the front-end sources
/// hold no interface text of their own (they are ASCII-only, Ryubing rule 16: a Portuguese text in the code fails).
/// </summary>
public partial class LocalizationTests
{
    private static readonly IReadOnlyDictionary<string, string> s_english = Loc.Table(CultureInfo.InvariantCulture);
    private static readonly IReadOnlyDictionary<string, string> s_portuguese = Loc.Table(CultureInfo.GetCultureInfo(Loc.Portuguese));

    [GeneratedRegex(@"\{(\d+)")]
    private static partial Regex Placeholder();

    private static string Root([CallerFilePath] string source = "")
    {
        for (var d = new DirectoryInfo(Path.GetDirectoryName(source)!); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "VitaSharp.sln")))
            {
                return d.FullName;
            }
        }

        throw new DirectoryNotFoundException("repository root");
    }

    [Fact]
    public void BothLanguagesHaveTheSameKeysAndPlaceholders()
    {
        Assert.True(s_english.Count > 400, $"English table has {s_english.Count} texts");
        Assert.Empty(s_english.Keys.Except(s_portuguese.Keys).Select(k => "missing in pt-BR: " + k));
        Assert.Empty(s_portuguese.Keys.Except(s_english.Keys).Select(k => "missing in en: " + k));
        var bad = new List<string>();
        foreach ((string? key, string? en) in s_english)
        {
            string pt = s_portuguese[key];
            if (en.Trim().Length == 0 || pt.Trim().Length == 0)
            {
                bad.Add($"{key}: empty");
            }

            IOrderedEnumerable<string> a = Placeholder().Matches(en).Select(m => m.Groups[1].Value).Distinct().Order();
            IOrderedEnumerable<string> b = Placeholder().Matches(pt).Select(m => m.Groups[1].Value).Distinct().Order();
            if (!a.SequenceEqual(b))
            {
                bad.Add($"{key}: placeholders {string.Join(",", a)} vs {string.Join(",", b)}");
            }

            try
            {
                _ = string.Format(CultureInfo.InvariantCulture, en, new object[10]);
                _ = string.Format(CultureInfo.InvariantCulture, pt, new object[10]);
            }
            catch (FormatException)
            {
                bad.Add($"{key}: not a valid format text");
            }
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void LanguageFollowsTheChoiceAndFallsBackToEnglish()
    {
        Assert.Equal(Loc.Portuguese, Loc.Pick("pt-BR").Name);
        Assert.Equal(Loc.Portuguese, Loc.Pick("pt").Name);
        Assert.Equal(Loc.English, Loc.Pick("en").Name);
        Assert.Equal(Loc.English, Loc.Pick("fr-FR").Name);
        Assert.Equal("Biblioteca", s_portuguese["Library"]);
        Assert.Equal("Library", s_english["Library"]);
    }

    /// <summary>Every key the sources ask for: Loc.Get/Format("Key"), T("Key"), and the launcher's Strings properties and
    /// methods that read the key of their own name (T()).</summary>
    [Fact]
    public void EveryKeyUsedInTheCodeExists()
    {
        string src = Path.Combine(Root(), "src");
        var used = new SortedSet<string>();
        var call = new Regex(@"\b(?:Loc\.(?:Get|Format)|T)\(""(\w+)""");
        var own = new Regex(@"public static string (\w+)(?:\([^)]*\))? => (?:T\(\)|F\(T\(\))");
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (Match m in call.Matches(text))
            {
                used.Add(m.Groups[1].Value);
            }

            if (Path.GetFileName(file) == "Strings.cs")
            {
                foreach (Match m in own.Matches(text))
                {
                    used.Add(m.Groups[1].Value);
                }
            }
        }
        Assert.True(used.Count > 400, $"only {used.Count} keys found in the sources");
        var missing = used.Where(k => !s_english.ContainsKey(k)).ToList();
        Assert.Empty(missing);
    }

    /// <summary>The arguments after the key of the Loc.Format call that starts at <paramref name="open"/> (the index
    /// of its opening parenthesis): top-level commas, outside strings, characters and nested brackets.</summary>
    private static int FormatArguments(string text, int open)
    {
        int depth = 0;
        int commas = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' || c == '\'')
            {
                // skip a string or char literal (verbatim strings double their quotes; good enough for the sources)
                char quote = c;
                for (i++; i < text.Length && text[i] != quote; i++)
                {
                    if (text[i] == '\\')
                    {
                        i++;
                    }
                }

                continue;
            }

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0)
                {
                    return commas;
                }
            }
            else if (c == ',' && depth == 1)
            {
                commas++;
            }
        }

        return -1;
    }

    /// <summary>
    /// Every Loc.Format("Key", ...) in the sources passes as many values as the text has placeholders: a text that
    /// wants {3} with three values throws at run time, and one with fewer placeholders than values drops the extra
    /// ones silently (the Android data folder move's progress line, 1597d72b, takes four).
    /// </summary>
    [Fact]
    public void EveryFormatCallPassesOneValuePerPlaceholder()
    {
        string src = Path.Combine(Root(), "src");
        var call = new Regex(@"\bLoc\.Format\(""(\w+)""");
        var bad = new List<string>();
        int checkedCalls = 0;
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (Match m in call.Matches(text))
            {
                if (!s_english.TryGetValue(m.Groups[1].Value, out string? en))
                {
                    continue; // EveryKeyUsedInTheCodeExists reports it
                }

                int wanted = Placeholder().Matches(en).Select(p => int.Parse(p.Groups[1].Value, CultureInfo.InvariantCulture) + 1).DefaultIfEmpty(0).Max();
                int passed = FormatArguments(text, m.Index + "Loc.Format".Length);
                checkedCalls++;
                if (passed != wanted)
                {
                    int line = text.Take(m.Index).Count(ch => ch == '\n') + 1;
                    bad.Add($"{Path.GetRelativePath(src, file)}:{line}: {m.Groups[1].Value} has {wanted} placeholders, the call passes {passed}");
                }
            }
        }
        Assert.True(checkedCalls > 50, $"only {checkedCalls} Loc.Format calls found");
        Assert.Empty(bad);
    }

    [Fact]
    public void TheDataFolderMoveTextsAreLocalized()
    {
        foreach (IReadOnlyDictionary<string, string> table in new[] { s_english, s_portuguese })
        {
            string progress = string.Format(CultureInfo.InvariantCulture, table["MStorageMovingProgress"], 3, 10, 20, 400);
            Assert.Contains("3", progress);
            Assert.Contains("400", progress);
        }
        Assert.Contains("arquivos", s_portuguese["MStorageMovingProgress"]);
        Assert.Contains("files", s_english["MStorageMovingProgress"]);
    }

    /// <summary>The front-end sources (desktop launcher, game window and notices; the Android app and its shared
    /// mobile logic) are ASCII-only: every interface text lives in the language files, symbols are \u escapes.</summary>
    [Theory]
    [InlineData("VitaSharp.Desktop")]
    [InlineData("VitaSharp.Android")]
    [InlineData("VitaSharp.Mobile.Core")]
    public void FrontEndSourcesHaveNoTextOfTheirOwn(string project)
    {
        string dir = Path.Combine(Root(), "src", project);
        var bad = new List<string>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Any(c => c > 127 && c != '\uFEFF'))
                {
                    bad.Add($"{Path.GetRelativePath(dir, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }
        Assert.Empty(bad);
    }
}
