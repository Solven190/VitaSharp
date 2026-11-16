using VitaSharp.Core.Hle.Modules;
using VitaSharp.Core.Memory;
using Xunit;
using static VitaSharp.Tests.HleHarness;

namespace VitaSharp.Tests;

/// <summary>
/// The variant Init exports of libcdlg.suprx 3.74 (the second entry of each family): each takes its family's slot
/// (the public GetStatus of that family sees it, Init of another family is BUSY until Term), and the net check,
/// companion and Facebook variants refuse a NULL second block as the module does. The exports are found by name so
/// the proof without them still builds.
/// </summary>
[Collection("guest")]
public sealed class CommonDialogVariantInitTests(GuestMemoryFixture fixture)
{
    private const int Busy = unchecked((int)0x80020401);
    private const int NullPointer = unchecked((int)0x80020402);
    private const int Finished = 2;

    /// <summary>(variant export, takes a block, the family's public GetStatus and Term, param size).</summary>
    private static readonly (string Variant, bool Block, string Family, uint ParamSize)[] s_variants =
    [
        ("NID_40CD30CD", false, "sceNpFriendListDialog", 0x100),
        ("NID_452DA757", true, "scePhotoImportDialog", 0x100),
        ("NID_3D3F9AD6", false, "sceNpProfileDialog", 0x100),
        ("NID_AC20DAF9", true, "scePhotoReviewDialog", 0x500),
        ("NID_FEBF87EB", true, "sceCameraImportDialog", 0x100),
        ("NID_075E4490", true, "sceNpSnsFacebookDialog", 0x100),
        ("NID_AA624C1F", false, "sceNpFriendList2Dialog", 0x100),
        ("NID_62B60B87", true, "sceCompanionUtilDialog", 0x100),
    ];

    private uint Alloc(uint size)
    {
        uint pointer = fixture.Memory.Alloc(size, "cdlg variant test");
        GuestMemory.Fill(pointer, 0, size);
        return pointer;
    }

    [Fact]
    public void EachVariantTakesItsFamilysSlot()
    {
        var dialogs = new CommonDialog();
        uint block = Alloc(0x100);
        GuestMemory.W32(block, 1);
        foreach ((string variant, bool takesBlock, string family, uint size) in s_variants)
        {
            uint param = Alloc(size);
            int result = takesBlock ? Call(dialogs, variant, param, block) : Call(dialogs, variant, param);
            Assert.Equal(0, result);
            Assert.Equal(Finished, Call(dialogs, family + "GetStatus"));
            string other = family == "sceNpProfileDialog" ? "sceNpFriendListDialogInit" : "sceNpProfileDialogInit";
            Assert.Equal(Busy, Call(dialogs, other, Alloc(0x100)));
            Assert.Equal(0, Call(dialogs, family + "Term"));
        }
    }

    [Fact]
    public void TheNetCheckVariantNeedsItsBlockAndChecksTheMode()
    {
        var dialogs = new CommonDialog();
        uint param = Alloc(0xE0);
        uint block = Alloc(0x84);
        Assert.Equal(NullPointer, Call(dialogs, "NID_243D6A36", param, 0u));
        Assert.Equal(NullPointer, Call(dialogs, "NID_243D6A36", 0u, block));
        Assert.Equal(unchecked((int)0x80100C02), Call(dialogs, "NID_243D6A36", param, block));
        GuestMemory.W32(param + 0x50, 4);
        Assert.Equal(0, Call(dialogs, "NID_243D6A36", param, block));
        Assert.Equal(Finished, Call(dialogs, "sceNetCheckDialogGetStatus"));
        Assert.Equal(0, Call(dialogs, "sceNetCheckDialogTerm"));
    }

    [Fact]
    public void TheCompanionAndFacebookVariantsRefuseAMissingBlock()
    {
        var dialogs = new CommonDialog();
        uint param = Alloc(0x100);
        uint block = Alloc(0x100);
        Assert.Equal(NullPointer, Call(dialogs, "NID_62B60B87", param, 0u));
        Assert.Equal(NullPointer, Call(dialogs, "NID_075E4490", param, 0u));
        Assert.Equal(unchecked((int)0x80106103), Call(dialogs, "NID_075E4490", param, block));
        Assert.Equal(0, Call(dialogs, "sceNpProfileDialogGetStatus"));
    }
}
