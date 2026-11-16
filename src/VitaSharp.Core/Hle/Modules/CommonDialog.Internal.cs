using VitaSharp.Core.Memory;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The second Init entries of libcdlg.suprx 3.74 (decompiled, study only): the module's family table holds, next to
/// the public Init(param), a variant Init (the "OnInit2" path) that the system apps call; it takes the same slot (the
/// family's type number) and starts the same dialog, some with a second block the module copies (the net check and
/// companion ones refuse a NULL block with SCE_COMMON_DIALOG_ERROR_NULL; the Facebook one also a block whose first
/// word is 0, with 0x80106103). The HLE's families do not draw a dialog and finish at once, so the variant starts the
/// family through its public Init after those checks; the variants' other parameter checks are not modelled.
/// </summary>
public sealed partial class CommonDialog
{
    const int FacebookErrInvalidParam = unchecked((int)0x80106103);

    /// <summary>NID_243D6A36(param, block): the variant Init of the net check dialog (libcdlg family 0x3): sceNetCheckDialogInit.</summary>
    [Hle(Nid = 0x243D6A36, Library = "SceCommonDialog")]
    int NID_243D6A36(uint param, uint block)
    {
        if (param == 0 || block == 0)
        {
            return CommonDialogError.NULL;
        }

        return sceNetCheckDialogInit(param);
    }

    /// <summary>NID_40CD30CD(param): the variant Init of the NP friend list dialog (libcdlg family 0x9): sceNpFriendListDialogInit.</summary>
    [Hle(Nid = 0x40CD30CD, Library = "SceCommonDialog")]
    int NID_40CD30CD(uint param) => sceNpFriendListDialogInit(param);

    /// <summary>NID_452DA757(param, block): the variant Init of the photo import dialog (libcdlg family 0xB): scePhotoImportDialogInit.</summary>
    [Hle(Nid = 0x452DA757, Library = "SceCommonDialog")]
    int NID_452DA757(uint param, uint block) => scePhotoImportDialogInit(param);

    /// <summary>NID_3D3F9AD6(param): the variant Init of the NP profile dialog (libcdlg family 0xC): sceNpProfileDialogInit.</summary>
    [Hle(Nid = 0x3D3F9AD6, Library = "SceCommonDialog")]
    int NID_3D3F9AD6(uint param) => sceNpProfileDialogInit(param);

    /// <summary>NID_AC20DAF9(param, block): the variant Init of the photo review dialog (libcdlg family 0xD): scePhotoReviewDialogInit.</summary>
    [Hle(Nid = 0xAC20DAF9, Library = "SceCommonDialog")]
    int NID_AC20DAF9(uint param, uint block) => scePhotoReviewDialogInit(param);

    /// <summary>NID_FEBF87EB(param, block): the variant Init of the camera import dialog (libcdlg family 0xF): sceCameraImportDialogInit.</summary>
    [Hle(Nid = 0xFEBF87EB, Library = "SceCommonDialog")]
    int NID_FEBF87EB(uint param, uint block) => sceCameraImportDialogInit(param);

    /// <summary>NID_075E4490(param, block): the variant Init of the Facebook dialog (libcdlg family 0x11): sceNpSnsFacebookDialogInit.</summary>
    [Hle(Nid = 0x075E4490, Library = "SceCommonDialog")]
    int NID_075E4490(uint param, uint block)
    {
        if (param == 0 || block == 0)
        {
            return CommonDialogError.NULL;
        }

        if (GuestMemory.R32(block) == 0)
        {
            return FacebookErrInvalidParam;
        }

        return sceNpSnsFacebookDialogInit(param);
    }

    /// <summary>NID_AA624C1F(param): the variant Init of the NP friend list 2 dialog (libcdlg family 0x17): sceNpFriendList2DialogInit.</summary>
    [Hle(Nid = 0xAA624C1F, Library = "SceCommonDialog")]
    int NID_AA624C1F(uint param) => sceNpFriendList2DialogInit(param);

    /// <summary>NID_62B60B87(param, block): the variant Init of the companion util dialog (libcdlg family 0x18): sceCompanionUtilDialogInit.</summary>
    [Hle(Nid = 0x62B60B87, Library = "SceCommonDialog")]
    int NID_62B60B87(uint param, uint block)
    {
        if (param == 0 || block == 0)
        {
            return CommonDialogError.NULL;
        }

        return sceCompanionUtilDialogInit(param);
    }
}
