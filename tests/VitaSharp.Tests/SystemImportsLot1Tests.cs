using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// System-imported functions without public names of SceHttp, SceNpBasic, SceNpManager and SceTriggerUtil, each as
/// its FW 3.74 module answers (libhttp, np_basic, np_manager, trigger_util read in their code); the entitlement check
/// never reports an entitlement the PSN server did not give.
/// </summary>
[Collection("guest")]
public sealed class SystemImportsLot1Tests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "sys-lot1");
    private readonly PsnAccount _savedAccount = PsnAccount.Current;

    public void Dispose()
    {
        PsnAccount.Current = _savedAccount;
        _harness.Dispose();
    }

    [Theory]
    [InlineData(0x43A7CC70u, false)] // SceHttp
    [InlineData(0xA9A39BA1u, false)]
    [InlineData(0x7F13243Du, false)] // SceNpBasic
    [InlineData(0xFB1B7762u, false)]
    [InlineData(0x44DF26F5u, false)] // SceNpManager
    [InlineData(0xBF8921C8u, false)]
    [InlineData(0x2C43AC0Cu, true)] // SceTriggerUtil
    [InlineData(0xDA93949Au, true)]
    [InlineData(0x257BA954u, true)]
    public void TheLibrarysSystemFunctionsHaveAnEntry(uint nid, bool isDefault)
    {
        var bindings = HleBindings.Modules.SelectMany(m => m.Bindings()).Where(b => b.Nid == nid).ToList();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, b => Assert.Equal(isDefault, b.Default));
        Assert.NotNull(_harness.Emu.Hle.ImplOf(nid));
    }

    [Fact]
    public void HttpOption13AndSslVerifyCallbackCheckLikeLibhttp()
    {
        Http http = _harness.Module<Http>();
        Assert.Equal(unchecked((int)0x80431001), Call(http, "HttpSetOption13", 1, 1));
        Assert.Equal(0, Call(http, "sceHttpInit", 0x10000u));
        int template = Call(http, "sceHttpCreateTemplate", _harness.Str("ua"), 1, 0);
        Assert.True(template > 0);
        Assert.Equal(unchecked((int)0x80431100), Call(http, "HttpSetOption13", template + 100, 1));
        Assert.Equal(0, Call(http, "HttpSetOption13", template, 1));
        Assert.Equal(0, Call(http, "sceHttpTerm"));

        Assert.Equal(unchecked((int)0x80435801), Call(http, "HttpSslSetVerifyCallback", 0u, 0x81000001u));
        Assert.Equal(unchecked((int)0x80435801), Call(http, "HttpSslSetVerifyCallback", 0x81000000u, 0u));
        Assert.Equal(0, Call(http, "HttpSslSetVerifyCallback", 0x81000000u, 0x81000001u));
    }

    [Fact]
    public void NpBasicSystemPresenceChecksTheTypeThenFollowsTheAccount()
    {
        NpSocial social = _harness.Module<NpSocial>();
        uint presence = _harness.Alloc(0x1CC);
        Assert.Equal(unchecked((int)0x80551D02), Call(social, "NpBasicSystemSetPresence", 0u));
        GuestMemory.W32(presence + 4, 3);
        Assert.Equal(unchecked((int)0x80551D02), Call(social, "NpBasicSystemSetPresence", presence));
        GuestMemory.W32(presence + 4, 1);
        PsnAccount.Current = PsnAccount.Create(true, "Alice");
        Assert.Equal(0, Call(social, "NpBasicSystemSetPresence", presence));
        Assert.Equal(0, Call(social, "NpBasicSystemClearPresence"));
        PsnAccount.Current = PsnAccount.Create(true, "Alice", localServices: false);
        Assert.Equal(PsnAccount.Current.ServerError, Call(social, "NpBasicSystemSetPresence", presence));
        Assert.Equal(PsnAccount.Current.ServerError, Call(social, "NpBasicSystemClearPresence"));
    }

    [Fact]
    public void NpManagerRequestNeedsTheLibraryAndTheEntitlementIsNeverInvented()
    {
        NpManager np = _harness.Module<NpManager>();
        uint output = _harness.Word(0xAAAAAAAA);
        Assert.Equal(unchecked((int)0x80550003), Call(np, "NpManagerRequest90038", 0u));
        Assert.Equal(NpManager.ERR_NOT_INITIALIZED, Call(np, "NpManagerRequest90038", output));
        Assert.Equal(0xAAAAAAAAu, GuestMemory.R32(output));

        PsnAccount.Current = PsnAccount.Create(true, "Alice", localServices: false);
        Assert.Equal(PsnAccount.Current.ServerError, Call(np, "NpManagerHasEntitlement", output));
        Assert.Equal(0u, GuestMemory.R32(output));
        PsnAccount.Current = PsnAccount.Create(true, "Alice");
        GuestMemory.W32(output, 0xAAAAAAAA);
        Assert.Equal(0, Call(np, "NpManagerHasEntitlement", output));
        Assert.Equal(0u, GuestMemory.R32(output));
    }

    [Fact]
    public void TriggerUtilRequestsCheckKindAndArguments()
    {
        TriggerUtil trigger = _harness.Module<TriggerUtil>();
        uint title = _harness.Str("PCSA00001");
        uint parameter = _harness.Alloc(16);
        Assert.Equal(unchecked((int)0x80103605), Call(trigger, "TriggerUtilRequestKind", 4u, 0u, 0u));
        Assert.Equal(0, Call(trigger, "TriggerUtilRequestKind", 3u, 0u, 0u));
        Assert.Equal(unchecked((int)0x80103604), Call(trigger, "TriggerUtilRequestTitle", 0u, parameter, 0u, 0u, 0u));
        Assert.Equal(unchecked((int)0x80103604), Call(trigger, "TriggerUtilRequestTitle", title, 0u, 0u, 0u, 0u));
        Assert.Equal(unchecked((int)0x80103605), Call(trigger, "TriggerUtilRequestTitle", title, parameter, 0u, 0u, 0u)); // +8 is 0
        GuestMemory.W32(parameter + 8, 1);
        Assert.Equal(unchecked((int)0x80103605), Call(trigger, "TriggerUtilRequestTitle", title, parameter, 4u, 0u, 0u));
        Assert.Equal(0, Call(trigger, "TriggerUtilRequestTitle", title, parameter, 1u, 0u, 0u));
        Assert.Equal(0, Call(trigger, "TriggerUtilSetRequestByte", 1, (byte)1));
    }
}
