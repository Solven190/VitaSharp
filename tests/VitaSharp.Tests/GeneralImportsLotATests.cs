using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// Exports without public names of the General group in SceAppUtil, SceNpCommon, SceCommonDialog, SceNpWebApi,
/// SceTriggerUtil, SceShell, SceNpBasic and SceNpManager, each as its FW 3.74 module answers (read in their code).
/// </summary>
[Collection("guest")]
public sealed class GeneralImportsLotATests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "gen-lota");
    private readonly PsnAccount _savedAccount = PsnAccount.Current;

    public void Dispose()
    {
        PsnAccount.Current = _savedAccount;
        _harness.Dispose();
    }

    public static TheoryData<uint, bool> Entries()
    {
        TheoryData<uint, bool> data = [];
        foreach (uint nid in new uint[] { 0x1E2A6158, 0x7DA34311, 0x13293ADC, 0x92DF18F6, 0x2CD0649B, 0xD474A749, 0x64D21971, 0x93BBA612, 0x1140FA82, 0x6E6AFA39, 0x999CC1E9 })
        {
            data.Add(nid, false);
        }

        foreach (uint nid in new uint[]
        {
            0x1B615264, 0x2AD6A378, 0x921FF8E1, 0x845BF533, 0x5928D835, 0x6B8772A2, 0x794BF5BE, 0x7AD86CE1, 0x871A8E92, 0xA25CD3F2,
            0x0C9BAF57, 0xB93ADCDA, 0x375EABF2, 0x748925A4, 0xA1C1A8E2, 0xA6BFBBFF, 0x06EBD8B1, 0x2EA038D8, 0xC80D8C3E, 0x8C6EACC4,
            0xBA49FD24, 0xBBCD1B6D, 0xC7F6ADC4,
        })
        {
            data.Add(nid, true);
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

    private uint Bytes(params byte[] data)
    {
        uint address = _harness.Alloc((uint)data.Length + 1);
        data.CopyTo(GuestMemory.Span(address, data.Length));
        return address;
    }

    [Fact]
    public void AppUtilChecksUtf8AndTheSecondSaveNeedsInit()
    {
        AppUtil u = _harness.Module<AppUtil>();
        const int Bad = unchecked((int)0x801006FF);
        Assert.Equal(0, Call(u, "AppUtilCheckUtf8", _harness.Str("Título"), 0x40u));
        Assert.Equal(Bad, Call(u, "AppUtilCheckUtf8", Bytes(0x41, 0xC3, 0x28), 0x40u));
        Assert.Equal(Bad, Call(u, "AppUtilCheckUtf8", Bytes(0xEF, 0xBB, 0xBF, 0x41), 0x40u));
        Assert.Equal(AppUtil.ERR_NOT_INITIALIZED, Call(u, "AppUtilSaveDataDataSave2", 0u, 0u, 0u, 0u, 0u));
    }

    [Fact]
    public void NpCommonTakesOneUtf8Character()
    {
        NpManager np = _harness.Module<NpManager>();
        uint output = _harness.Alloc(8), taken = _harness.Word(0), needed = _harness.Word(0);
        Assert.Equal(unchecked((int)0x80550601), Call(np, "NpCommonUtf8Character", output, 8u, 0u, 1u, taken, needed));
        Assert.Equal(0, Call(np, "NpCommonUtf8Character", output, 8u, Bytes(0xC3, 0xA9, 0x41), 3u, taken, needed));
        Assert.Equal(2u, GuestMemory.R32(taken));
        Assert.Equal(0xA9C3, GuestMemory.Read<ushort>(output));
        Assert.Equal(unchecked((int)0x80550602), Call(np, "NpCommonUtf8Character", output, 8u, Bytes(0xE2, 0x82), 2u, taken, needed));
        Assert.Equal(3u, GuestMemory.R32(needed));
        Assert.Equal(0, Call(np, "NpCommonUtf8Character", output, 8u, Bytes(0x80, 0x41), 2u, taken, needed));
        Assert.Equal(1u, GuestMemory.R32(taken));
        Assert.Equal(unchecked((int)0x80550602), Call(np, "NpCommonCopyField", output, 0x25u));
        uint field = _harness.Alloc(0x40);
        GuestMemory.Fill(field, 0xAA, 0x40);
        Assert.Equal(0, Call(np, "NpCommonCopyField", field, 0x26u));
        Assert.Equal(0, GuestMemory.Read<byte>(field));
    }

    [Fact]
    public void ServiceRequestsCheckTheirLibrariesFirst()
    {
        PsnAccount.Current = PsnAccount.Create(true, "Alice", localServices: false);
        NpSocial social = _harness.Module<NpSocial>();
        uint word = _harness.Word(0);
        Assert.Equal(unchecked((int)0x80551D04), Call(social, "NpBasicSystemRequest9002B"));
        Assert.Equal(0, Call(social, "NpBasicSystemInit", 0u));
        Assert.Equal(unchecked((int)0x80551D05), Call(social, "NpBasicSystemInit", 0u));
        Assert.Equal(unchecked((int)0x80551D02), Call(social, "NpBasicSystemFriendPresence", 0u, word));
        Assert.Equal(PsnAccount.Current.ServerError, Call(social, "NpBasicSystemFriendPresence", word, word));

        NpManager np = _harness.Module<NpManager>();
        Assert.Equal(unchecked((int)0x80550503), Call(np, "NpManagerReadTicket", 0u, word));
        Assert.Equal(PsnAccount.Current.ServerError, Call(np, "NpManagerReadTicket", word, word));
        Assert.Equal(unchecked((int)0x80550002), Call(np, "NpManagerServiceRequest"));
        Assert.Equal(unchecked((int)0x80550509), Call(np, "NpManagerUnregisterServiceCallback", 1));
        Assert.Equal(unchecked((int)0x80550503), Call(np, "NpManagerAuthRequestB003C", 0u, 0u, 0u, 0u, 0u, 0u));
        Assert.Equal(unchecked((int)0x80550502), Call(np, "NpManagerAuthRequestB003C", 0u, word, 0u, 0u, 0u, word));

        TriggerUtil trigger = _harness.Module<TriggerUtil>();
        Assert.Equal(unchecked((int)0x80103605), Call(trigger, "TriggerUtilRequest100004", 4u, 0u, 0u));
        Assert.Equal(0, Call(trigger, "TriggerUtilRequest100004", 3u, 0u, 0u));
        Assert.Equal(0, Call(trigger, "TriggerUtilRequest100009", 7u, 0u, 0u));
        Assert.Equal(unchecked((int)0x80103605), Call(trigger, "TriggerUtilRequest10000E", 8u, 0u, 0u));

        NpWebApi webApi = _harness.Module<NpWebApi>();
        int ctx = Call(webApi, "sceNpWebApiInitialize", 1, 0x10000u);
        uint requestId = _harness.Alloc(8);
        Assert.Equal(0, Call(webApi, "NpWebApiCreateRequestForSystem2", ctx, _harness.Str("g"), _harness.Str("/p"), 0, requestId));
        Assert.Equal(ctx, (int)(GuestMemory.Read<long>(requestId) >> 16));
        Assert.Equal(unchecked((int)0x80552904), Call(webApi, "NpWebApiDeletePushFilter", ctx + 1000, 1));
        Assert.Equal(0, Call(webApi, "NpWebApiUnregisterPushCallback", ctx, 1));

        HleModule shell = _harness.Emu.Hle.Modules.Single(m => m.GetType().Name == "Shell");
        Assert.Equal(unchecked((int)0x80412181), Call(shell, "ShellNetA25CD3F2", 0u, 0u));

        CommonDialog dialog = _harness.Module<CommonDialog>();
        Assert.Equal(unchecked((int)0x80020411), Call(dialog, "CommonDialogFacebookRequest", 0u, 0u));
    }
}
