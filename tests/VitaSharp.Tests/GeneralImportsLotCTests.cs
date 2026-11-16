using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Exports without public names of the General group in SceNpTrophy and SceNpSnsFacebook, each as its FW 3.74 module
/// answers (np_trophy, np_sns_facebook read in their code); without the services the requests end neutrally.
/// </summary>
[Collection("guest")]
public sealed class GeneralImportsLotCTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "gen-lotc");
    private readonly PsnAccount _savedAccount = PsnAccount.Current;

    public void Dispose()
    {
        PsnAccount.Current = _savedAccount;
        _harness.Dispose();
    }

    public static TheoryData<uint, bool> Entries()
    {
        TheoryData<uint, bool> data = [];
        foreach (uint nid in new uint[]
        {
            0x0B4BCB51, 0x11A4F171, 0x4173F2C7, 0x4DA9A081, 0x6B41EC32, 0xEF32F9F6, 0x6F17BD4E, 0xC1406504, 0x722D9BAD, 0x69532B6A,
            0x72446B6B, 0xF44BFD5F, 0xBF78B2BC, 0xE363C13A, 0x21676448, 0x32473A4B, 0x86C16F67, 0xDCFBE482, 0xADEAD655, 0xED1908F7,
            0x7BD69297, 0x87E4C1B5, 0x5672DAE5, 0x03D7BB80, 0x47456D7E, 0x4D16AF03, 0x9E483C50, 0xAAF30D25,
            0x836AA54A, 0x0CDD6B52, 0x0FEBCA93, 0x1375638F, 0x20B68EBC, 0x2814F3DB, 0x42CD6A87, 0x58CAC784, 0x5EAB459F, 0x61DDCBB7,
            0x76B234BD, 0x78636540, 0x7C1286C0, 0x7CE37858, 0xA3785735, 0xB45419A5, 0xC87C016D, 0xC9798074, 0xCB51675B, 0xDC06BD48,
            0xDCCE1BB3, 0xDEDD3ECA, 0xF3956995,
        })
        {
            data.Add(nid, true);
        }

        foreach (uint nid in new uint[] { 0x34A88B2D, 0x9663A2EC, 0xEF957AD0, 0x8CB58B88, 0xC15158EE, 0xD08701B6, 0x249B3C57, 0x72A0685C })
        {
            data.Add(nid, false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void TheLibrarysFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void TrophyRequestsCheckIdsHandlesAndContexts()
    {
        NpTrophy t = _harness.Module<NpTrophy>();
        const int NotInit = unchecked((int)0x80551601), Invalid = unchecked((int)0x80551604), Handle = unchecked((int)0x80551608),
            Context = unchecked((int)0x80551609);
        uint handleOut = _harness.Word(0), a = _harness.Alloc(16), filter = _harness.Alloc(8);
        Assert.Equal(NotInit, Call(t, "NpTrophyRequest86C16F67", 1, a));
        Assert.Equal(0, Call(t, "NpTrophyService03D7BB80")); // no library flag needed
        Assert.Equal(0, Call(t, "sceNpTrophyInit", 0u));
        Assert.Equal(0, Call(t, "sceNpTrophyCreateHandle", new Ptr<int>(handleOut)));
        int handle = (int)GuestMemory.R32(handleOut);
        Assert.Equal(0, Call(t, "NpTrophyRequest86C16F67", handle, a));
        Assert.Equal(Handle, Call(t, "NpTrophyRequest86C16F67", handle + 50, a));
        Assert.Equal(Invalid, Call(t, "NpTrophyRequest11A4F171", handle, a, 1, 128, a));
        Assert.Equal(Context, Call(t, "NpTrophyRequest11A4F171", handle, a, 77, 127, a));
        Assert.Equal(Invalid, Call(t, "NpTrophyRequest4DA9A081", handle, a, 1, 32, 0u, 0u));
        Assert.Equal(Context, Call(t, "NpTrophyRequest4DA9A081", handle, a, 77, -1, 0u, 0u));
        GuestMemory.W32(filter, 3);
        Assert.Equal(Invalid, Call(t, "NpTrophyRequest6F17BD4E", handle, a, 77, filter, a));
        GuestMemory.W32(filter, 2);
        GuestMemory.W32(filter + 4, 40);
        Assert.Equal(Invalid, Call(t, "NpTrophyRequest6F17BD4E", handle, a, 77, filter, a));
        GuestMemory.W32(filter + 4, 0xFFFFFFFF);
        Assert.Equal(Context, Call(t, "NpTrophyRequest6F17BD4E", handle, a, 77, filter, a));
        Assert.Equal(0, Call(t, "NpTrophyRequest5672DAE5", a));
        Assert.Equal(0, Call(t, "sceNpTrophyTerm"));
    }

    [Fact]
    public void FacebookSlotsOpenCloseAndAnswerWithTheNetworksError()
    {
        NpSnsFacebook f = _harness.Module<NpSnsFacebook>();
        PsnAccount.Current = PsnAccount.Create(true, "Alice");
        uint a = _harness.Alloc(16);
        const int NotInit = unchecked((int)0x80559402), Already = unchecked((int)0x80559401), Invalid = unchecked((int)0x80559403);
        Assert.Equal(NotInit, Call(f, "NpSnsFacebookRequest7CE37858", 3, a));
        Assert.Equal(Invalid, Call(f, "NpSnsFacebookOpenSlot", 3, 0u, a));
        Assert.Equal(0, Call(f, "NpSnsFacebookOpenSlot", 3, a, a));
        Assert.Equal(Already, Call(f, "NpSnsFacebookOpenSlot", 3, a, a));
        Assert.Equal(PsnAccount.Current.ServerError, Call(f, "NpSnsFacebookRequest7CE37858", 3, a));
        Assert.Equal(Invalid, Call(f, "NpSnsFacebookRequest0FEBCA93", 3, 0u, 0u, a));
        Assert.Equal(0, Call(f, "NpSnsFacebookCloseSlot", 3));
        Assert.Equal(NotInit, Call(f, "NpSnsFacebookCloseSlot", 3));

        Assert.Equal(NotInit, Call(f, "NpSnsFacebookSystemPoll", a));
        Assert.Equal(Invalid, Call(f, "NpSnsFacebookSystemInitWithPool", 0u, 0u));
        Assert.Equal(0, Call(f, "NpSnsFacebookSystemInitWithPool", a, 16u));
        Assert.Equal(Already, Call(f, "NpSnsFacebookSystemInit2"));
        Assert.Equal(unchecked((int)0x80559404), Call(f, "NpSnsFacebookSystemRequestRoom"));
        Assert.Equal(0, Call(f, "NpSnsFacebookSystemDeleteRequest"));
        Assert.Equal(PsnAccount.Current.ServerError, Call(f, "NpSnsFacebookSystemPoll", a));
        Assert.Equal(0, Call(f, "NpSnsFacebookSystemTerm2"));
        Assert.Equal(NotInit, Call(f, "NpSnsFacebookSystemTerm2"));
    }
}
