using VitaSharp.Core.Input;
using VitaSharp.Core.Localization;
using VitaSharp.Mobile.Core.Input;

namespace VitaSharp.Tests;

[CollectionDefinition("language", DisableParallelization = true)]
public sealed class LanguageCollection
{
}

/// <summary>
/// The on-screen controls' texts follow the app's language. The layouts are built once per process (static), and the
/// Android game process is kept between games: the rear touch pad's button kept the language of the first game
/// ("TRÁS" with the app switched to English, 15/11/2026). Each translated label is now looked up when drawn.
/// </summary>
[Collection("language")]
public sealed class OnScreenLanguageTests : IDisposable
{
    private readonly string _before = Loc.Culture.Name;

    public void Dispose() => Loc.SetLanguage(_before);

    private static OnScreenControl Rear() => OnScreenPad.DefaultLayout.Single(c => c.Control == VitaControl.TouchBack);

    [Fact]
    public void TheRearButtonFollowsALanguageChosenAfterTheLayoutWasBuilt()
    {
        Loc.SetLanguage(Loc.Portuguese);
        Assert.Equal("TRÁS", Rear().Text); // the layout exists from here on, built in Portuguese
        Loc.SetLanguage(Loc.English);
        Assert.Equal("REAR", Rear().Text);
        Loc.SetLanguage(Loc.Portuguese);
        Assert.Equal("TRÁS", Rear().Text);
    }
}
