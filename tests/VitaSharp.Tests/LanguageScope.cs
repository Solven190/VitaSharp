using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;

namespace VitaSharp.Tests;

/// <summary>
/// The interface language for one test: <see cref="Loc"/> is process-wide, so a test that switches it puts back, when
/// disposed, the language and the <see cref="EnvVars.Language"/> it found (null = only keep them). Test classes that read
/// texts in a fixed language run in the "language" collection, which never runs beside other tests.
/// </summary>
internal sealed class LanguageScope : IDisposable
{
    private readonly string _culture = Loc.Culture.Name;
    private readonly string? _environment = Environment.GetEnvironmentVariable(EnvVars.Language);

    public LanguageScope(string? language = null)
    {
        if (language != null)
        {
            Loc.SetLanguage(language);
        }
    }

    public void Dispose()
    {
        Loc.SetLanguage(_culture);
        Environment.SetEnvironmentVariable(EnvVars.Language, _environment);
    }
}
