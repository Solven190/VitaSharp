using VitaSharp.Core.Hle;
using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// FW 3.74 exports whose code is another export's with one constant changed (compared instruction by instruction,
/// study only): np_manager's requests to the shell's NP service with another method (0xA0006, 0xB0014, 0xB0018),
/// np_basic's friend query with another request, and the libcdlg variant Inits of the IME and video import families.
/// Each runs next to the export it copies: the same checks and the same answer. Found by name so the proof without
/// them still builds.
/// </summary>
[Collection("guest")]
public sealed class ServiceRequestTwinsTests(GuestMemoryFixture fixture) : IDisposable
{
    private const int NullPointer = unchecked((int)0x80020402);
    private const int InvalidArgument = unchecked((int)0x80020403);

    private readonly HleHarness _harness = new(fixture, "svctwins");

    public void Dispose() => _harness.Dispose();

    private HleModule ModuleNamed(string name) => _harness.Emu.Hle.Modules.Single(module => module.GetType().Name == name);

    [Fact]
    public void TheNpManagerRequestsAnswerLikeTheRequestTheyCopy()
    {
        HleModule np = ModuleNamed("NpManager");
        uint buffer = _harness.Alloc(0x40);
        Assert.Equal(Call(np, "NpSvcA0003", buffer), Call(np, "NpSvcA0006", buffer));
        Assert.Equal(Call(np, "NpSvcB0011"), Call(np, "NpSvcB0014"));
        Assert.Equal(unchecked((int)0x80550503), Call(np, "NpSvcB0018", 0u));
        Assert.Equal(Call(np, "NpSvcA0003", buffer), Call(np, "NpSvcB0018", buffer));
    }

    [Fact]
    public void TheNpBasicQueryChecksLikeTheFriendOnlineStatus()
    {
        HleModule social = ModuleNamed("NpSocial");
        uint output = _harness.Alloc(0x40);
        uint friend = _harness.Alloc(0x24);
        Assert.Equal(Call(social, "sceNpBasicGetFriendOnlineStatus", 0u, output), Call(social, "NID_1CAAF066", 0u, output));
        Assert.Equal(Call(social, "sceNpBasicGetFriendOnlineStatus", friend, 0u), Call(social, "NID_1CAAF066", friend, 0u));
        Assert.Equal(Call(social, "sceNpBasicGetFriendOnlineStatus", friend, output), Call(social, "NID_1CAAF066", friend, output));
    }

    private uint Zeroed(uint size)
    {
        uint pointer = _harness.Alloc(size);
        GuestMemory.Fill(pointer, 0, size);
        return pointer;
    }

    [Fact]
    public void TheImeVariantInitIsTheImeInit()
    {
        HleModule dialogs = ModuleNamed("CommonDialog");
        uint block = Zeroed(0x40);
        Assert.Equal(NullPointer, Call(dialogs, "NID_9800F871", 0u, block));
        uint param = Zeroed(0xA8);
        GuestMemory.W32(param + 0x2C, 16);
        Assert.Equal(Call(dialogs, "sceImeDialogInit", param), Call(dialogs, "NID_9800F871", param, block)); // no output buffer
        GuestMemory.W32(param + 0x34, Zeroed(64));
        Assert.Equal(0, Call(dialogs, "NID_9800F871", param, 0u));
        Assert.Equal(2, Call(dialogs, "sceImeDialogGetStatus"));
        Assert.Equal(0, Call(dialogs, "sceImeDialogTerm"));
    }

    [Fact]
    public void TheVideoImportVariantInitChecksItsBlock()
    {
        HleModule dialogs = ModuleNamed("CommonDialog");
        uint param = Zeroed(0x100);
        uint block = Zeroed(0x40);
        Assert.Equal(NullPointer, Call(dialogs, "NID_FBE52F6D", 0u, block));
        Assert.Equal(InvalidArgument, Call(dialogs, "NID_FBE52F6D", param, block)); // a count of 0
        GuestMemory.W32(block, 0x100);
        Assert.Equal(InvalidArgument, Call(dialogs, "NID_FBE52F6D", param, block));
        GuestMemory.W32(block, 3);
        GuestMemory.Write(block + 0x23, (byte)1);
        Assert.Equal(InvalidArgument, Call(dialogs, "NID_FBE52F6D", param, block));
        GuestMemory.Write(block + 0x23, (byte)0);
        GuestMemory.Write(block + 0x24, (byte)1); // past the checked bytes
        Assert.Equal(0, Call(dialogs, "NID_FBE52F6D", param, block));
        Assert.Equal(2, Call(dialogs, "sceVideoImportDialogGetStatus"));
        Assert.Equal(0, Call(dialogs, "sceVideoImportDialogTerm"));
    }
}
