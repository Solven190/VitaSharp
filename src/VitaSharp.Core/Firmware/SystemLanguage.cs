using System.Globalization;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// The system language (SceSystemParamLang) a fresh system starts with: the host's UI language when the Vita has it,
/// else English (US). It is the factory default of /CONFIG/SYSTEM/language (the initial setup's preselected language)
/// and what games read through SceAppUtil/SceRegMgrForGame.
/// </summary>
public static class SystemLanguage
{
    public const int EnglishUs = 1;

    /// <summary>How many system languages the firmware has (SceSystemParamLang 0..19).</summary>
    public const int Count = 20;

    /// <summary>Each SceSystemParamLang's name in its own language, as the Vita's Settings lists them (index = the value).</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "日本語", "English (United States)", "Français", "Español", "Deutsch", "Italiano", "Nederlands",
        "Português (Portugal)", "Русский", "한국어", "繁體中文", "简体中文",
        "Suomi", "Svenska", "Dansk", "Norsk", "Polski", "Português (Brasil)", "English (United Kingdom)", "Türkçe",
    ];

    /// <summary>A SceSystemParamLang the firmware has.</summary>
    public static bool IsValid(int language) => language is >= 0 and < Count;

    /// <summary>SceSystemParamLang of the host's UI language, read once.</summary>
    public static int Host { get; } = FromCulture(CultureInfo.CurrentUICulture);

    /// <summary>SceSystemParamLang of a culture (TROP_&lt;nn&gt;.SFM): 0 ja, 1 en-US, 2 fr, 3 es, 4 de, 5 it, 6 nl,
    /// 7 pt-PT, 8 ru, 9 ko, 10 zh-Hant, 11 zh-Hans, 12 fi, 13 sv, 14 da, 15 no, 16 pl, 17 pt-BR, 18 en-GB, 19 tr;
    /// English (US) for any other.</summary>
    public static int FromCulture(CultureInfo c) => c.Name switch
    {
        "pt-BR" => 17,
        "en-GB" => 18,
        "zh-TW" or "zh-HK" or "zh-MO" or "zh-Hant" => 10,
        "zh-CN" or "zh-SG" or "zh-Hans" => 11,
        _ => c.TwoLetterISOLanguageName switch
        {
            "ja" => 0,
            "en" => 1,
            "fr" => 2,
            "es" => 3,
            "de" => 4,
            "it" => 5,
            "nl" => 6,
            "pt" => 7,
            "ru" => 8,
            "ko" => 9,
            "fi" => 12,
            "sv" => 13,
            "da" => 14,
            "nb" or "nn" or "no" => 15,
            "pl" => 16,
            "tr" => 19,
            _ => EnglishUs,
        },
    };
}
