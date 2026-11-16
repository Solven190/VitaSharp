using VitaSharp.Core.Kernel;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// User library exports that are the same function as another export of their module in the FW 3.74 firmware
/// (decompiled, study only; compared instruction by instruction, literal pool words included): the module exports one
/// body under two NIDs, exports two copies of the same code (np_commerce2's parsers: equal down to every function they
/// call), or two bodies that differ only in a thunk that passes the registers through to the same function (push
/// {r4, lr}; bl; pop), or exports the import stub of a syscall (libkernel's
/// 0x58DDAC4F is its stub of the threadmgr sceKernelDeleteThread). Each entry runs the HLE of the other NID on the
/// caller's registers, the way
/// <see cref="KernelTwins"/> does for the kernel entries.
/// </summary>
public sealed partial class UserTwins : HleModule
{
    /// <summary>Runs the HLE of export <paramref name="nid"/> on the current registers (its result lands in r0/r1 as
    /// usual).</summary>
    void Forward(HleContext context, uint nid)
    {
        if (Emu.Hle.ImplOf(nid) is { } implementation)
        {
            implementation(context);
        }
        else
        {
            context.Return(SceError.ERROR);
        }
    }

    // ------------------------------------------------------------------ SceCommonDialog

    /// <summary>SceCommonDialog 0xC6DB0740: the same body as 0x4535A358 (sceNpMessageDialogInit).</summary>
    [Hle(Nid = 0xC6DB0740, Library = "SceCommonDialog")]
    void SceCommonDialog_C6DB0740(HleContext context) => Forward(context, 0x4535A358);

    // ------------------------------------------------------------------ SceLibKernel

    /// <summary>SceLibKernel 0x58DDAC4F: its import stub of 0x1BBDE3D9 (sceKernelDeleteThread).</summary>
    [Hle(Nid = 0x58DDAC4F, Library = "SceLibKernel")]
    void SceLibKernel_58DDAC4F(HleContext context) => Forward(context, 0x1BBDE3D9);

    /// <summary>SceLibKernel 0x6F9C4CC1: the same body as 0xA7819967 (sceKernelLockLwMutex_0).</summary>
    [Hle(Nid = 0x6F9C4CC1, Library = "SceLibKernel")]
    void SceLibKernel_6F9C4CC1(HleContext context) => Forward(context, 0xA7819967);

    // ------------------------------------------------------------------ SceNetCtl

    /// <summary>SceNetCtl 0x16268321: the same body as 0x5AD6CBE8 (NID_5AD6CBE8).</summary>
    [Hle(Nid = 0x16268321, Library = "SceNetCtl")]
    void SceNetCtl_16268321(HleContext context) => Forward(context, 0x5AD6CBE8);

    /// <summary>SceNetCtl 0x766E2AE1: the same body as 0x8B2FEFAA (NID_8B2FEFAA).</summary>
    [Hle(Nid = 0x766E2AE1, Library = "SceNetCtl")]
    void SceNetCtl_766E2AE1(HleContext context) => Forward(context, 0x8B2FEFAA);

    /// <summary>SceNetCtl 0xA7F4F4AB: the same body as 0x777FE4C2 (NID_777FE4C2).</summary>
    [Hle(Nid = 0xA7F4F4AB, Library = "SceNetCtl")]
    void SceNetCtl_A7F4F4AB(HleContext context) => Forward(context, 0x777FE4C2);

    /// <summary>SceNetCtl 0xB0122008: the same body as 0xE99EE305 (NID_E99EE305).</summary>
    [Hle(Nid = 0xB0122008, Library = "SceNetCtl")]
    void SceNetCtl_B0122008(HleContext context) => Forward(context, 0xE99EE305);

    /// <summary>SceNetCtl 0xBBAD980D: the same body as 0xE8E8DF18 (NID_E8E8DF18).</summary>
    [Hle(Nid = 0xBBAD980D, Library = "SceNetCtl")]
    void SceNetCtl_BBAD980D(HleContext context) => Forward(context, 0xE8E8DF18);

    /// <summary>SceNetCtl 0xC81AD91B: the same body as 0x27932A1B (NID_27932A1B).</summary>
    [Hle(Nid = 0xC81AD91B, Library = "SceNetCtl")]
    void SceNetCtl_C81AD91B(HleContext context) => Forward(context, 0x27932A1B);

    /// <summary>SceNetCtl 0xE03981D6: the same body as 0xAB1462C5 (NID_AB1462C5).</summary>
    [Hle(Nid = 0xE03981D6, Library = "SceNetCtl")]
    void SceNetCtl_E03981D6(HleContext context) => Forward(context, 0xAB1462C5);

    // ------------------------------------------------------------------ SceNpCommerce2

    /// <summary>SceNpCommerce2 0x2B7C71D0: a copy of 0x307290E8 (NID_307290E8), the same code down to every function it calls.</summary>
    [Hle(Nid = 0x2B7C71D0, Library = "SceNpCommerce2")]
    void SceNpCommerce2_2B7C71D0(HleContext context) => Forward(context, 0x307290E8);

    /// <summary>SceNpCommerce2 0x2BCB23B2: a copy of 0x30ABA9CA (NID_30ABA9CA), the same code down to every function it calls.</summary>
    [Hle(Nid = 0x2BCB23B2, Library = "SceNpCommerce2")]
    void SceNpCommerce2_2BCB23B2(HleContext context) => Forward(context, 0x30ABA9CA);

    /// <summary>SceNpCommerce2 0x50E50CFB: a copy of 0x05002E87 (NID_05002E87), the same code down to every function it calls.</summary>
    [Hle(Nid = 0x50E50CFB, Library = "SceNpCommerce2")]
    void SceNpCommerce2_50E50CFB(HleContext context) => Forward(context, 0x05002E87);

    /// <summary>SceNpCommerce2 0x869EA46B: a copy of 0x30ABA9CA (NID_30ABA9CA), the same code down to every function it calls.</summary>
    [Hle(Nid = 0x869EA46B, Library = "SceNpCommerce2")]
    void SceNpCommerce2_869EA46B(HleContext context) => Forward(context, 0x30ABA9CA);

    /// <summary>SceNpCommerce2 0x8B8465E2: a copy of 0x027A054E (NID_027A054E), the same code down to every function it calls.</summary>
    [Hle(Nid = 0x8B8465E2, Library = "SceNpCommerce2")]
    void SceNpCommerce2_8B8465E2(HleContext context) => Forward(context, 0x027A054E);

    /// <summary>SceNpCommerce2 0xB8F96BD2: a copy of 0x05002E87 (NID_05002E87), the same code down to every function it calls.</summary>
    [Hle(Nid = 0xB8F96BD2, Library = "SceNpCommerce2")]
    void SceNpCommerce2_B8F96BD2(HleContext context) => Forward(context, 0x05002E87);

    /// <summary>SceNpCommerce2 0xE53BD365: a copy of 0x05002E87 (NID_05002E87), the same code down to every function it calls.</summary>
    [Hle(Nid = 0xE53BD365, Library = "SceNpCommerce2")]
    void SceNpCommerce2_E53BD365(HleContext context) => Forward(context, 0x05002E87);

    /// <summary>SceNpCommerce2 0xF0E90E02: a copy of 0x05002E87 (NID_05002E87), the same code down to every function it calls.</summary>
    [Hle(Nid = 0xF0E90E02, Library = "SceNpCommerce2")]
    void SceNpCommerce2_F0E90E02(HleContext context) => Forward(context, 0x05002E87);

    /// <summary>SceNpCommerce2 0x0FA01B8E: the same wrapper as 0x17DF6D62 (NID_17DF6D62), through a pass-through thunk to the same parser.</summary>
    [Hle(Nid = 0x0FA01B8E, Library = "SceNpCommerce2")]
    void SceNpCommerce2_0FA01B8E(HleContext context) => Forward(context, 0x17DF6D62);

    /// <summary>SceNpCommerce2 0x37610D66: the same wrapper as 0x0FFC4396 (NID_0FFC4396), through a pass-through thunk to the same parser.</summary>
    [Hle(Nid = 0x37610D66, Library = "SceNpCommerce2")]
    void SceNpCommerce2_37610D66(HleContext context) => Forward(context, 0x0FFC4396);

    /// <summary>SceNpCommerce2 0x4B49C464: the same wrapper as 0x0FFC4396 (NID_0FFC4396), through a pass-through thunk to the same parser.</summary>
    [Hle(Nid = 0x4B49C464, Library = "SceNpCommerce2")]
    void SceNpCommerce2_4B49C464(HleContext context) => Forward(context, 0x0FFC4396);

    /// <summary>SceNpCommerce2 0x5F4626FA: the same wrapper as 0x2A9DB98C (NID_2A9DB98C), through a pass-through thunk to the same parser.</summary>
    [Hle(Nid = 0x5F4626FA, Library = "SceNpCommerce2")]
    void SceNpCommerce2_5F4626FA(HleContext context) => Forward(context, 0x2A9DB98C);

    /// <summary>SceNpCommerce2 0x7C13533C: the same wrapper as 0x2A9DB98C (NID_2A9DB98C), through a pass-through thunk to the same parser.</summary>
    [Hle(Nid = 0x7C13533C, Library = "SceNpCommerce2")]
    void SceNpCommerce2_7C13533C(HleContext context) => Forward(context, 0x2A9DB98C);

    /// <summary>SceNpCommerce2 0x85C461FE: the same wrapper as 0x2A9DB98C (NID_2A9DB98C), through a pass-through thunk to the same parser.</summary>
    [Hle(Nid = 0x85C461FE, Library = "SceNpCommerce2")]
    void SceNpCommerce2_85C461FE(HleContext context) => Forward(context, 0x2A9DB98C);

    /// <summary>SceNpCommerce2 0xA18757A4: the same wrapper as 0x16FE6A6E (NID_16FE6A6E), through a pass-through thunk to the same parser.</summary>
    [Hle(Nid = 0xA18757A4, Library = "SceNpCommerce2")]
    void SceNpCommerce2_A18757A4(HleContext context) => Forward(context, 0x16FE6A6E);

    /// <summary>SceNpCommerce2 0xB523FE1A: the same wrapper as 0xA1E8C739 (NID_A1E8C739), through a pass-through thunk to the same parser.</summary>
    [Hle(Nid = 0xB523FE1A, Library = "SceNpCommerce2")]
    void SceNpCommerce2_B523FE1A(HleContext context) => Forward(context, 0xA1E8C739);

    /// <summary>SceNpCommerce2 0xCCF71DBE: the same wrapper as 0xA1E8C739 (NID_A1E8C739), through a pass-through thunk to the same parser.</summary>
    [Hle(Nid = 0xCCF71DBE, Library = "SceNpCommerce2")]
    void SceNpCommerce2_CCF71DBE(HleContext context) => Forward(context, 0xA1E8C739);

    /// <summary>SceNpCommerce2 0xFC459CE8: the same wrapper as 0xA1E8C739 (NID_A1E8C739), through a pass-through thunk to the same parser.</summary>
    [Hle(Nid = 0xFC459CE8, Library = "SceNpCommerce2")]
    void SceNpCommerce2_FC459CE8(HleContext context) => Forward(context, 0xA1E8C739);

    /// <summary>SceNpCommerce2 0x08E8D89D: the same body as 0x513940B4 (NID_513940B4).</summary>
    [Hle(Nid = 0x08E8D89D, Library = "SceNpCommerce2")]
    void SceNpCommerce2_08E8D89D(HleContext context) => Forward(context, 0x513940B4);

    /// <summary>SceNpCommerce2 0x188B27D3: the same body as 0x8B4BEA99 (NID_8B4BEA99).</summary>
    [Hle(Nid = 0x188B27D3, Library = "SceNpCommerce2")]
    void SceNpCommerce2_188B27D3(HleContext context) => Forward(context, 0x8B4BEA99);

    /// <summary>SceNpCommerce2 0x2C3EF0F5: the same body as 0xA200696D (NID_A200696D).</summary>
    [Hle(Nid = 0x2C3EF0F5, Library = "SceNpCommerce2")]
    void SceNpCommerce2_2C3EF0F5(HleContext context) => Forward(context, 0xA200696D);

    /// <summary>SceNpCommerce2 0x3449647C: the same body as 0xD3BCDADD (NID_D3BCDADD).</summary>
    [Hle(Nid = 0x3449647C, Library = "SceNpCommerce2")]
    void SceNpCommerce2_3449647C(HleContext context) => Forward(context, 0xD3BCDADD);

    /// <summary>SceNpCommerce2 0x398B0940: the same body as 0x3E8B077C (NID_3E8B077C).</summary>
    [Hle(Nid = 0x398B0940, Library = "SceNpCommerce2")]
    void SceNpCommerce2_398B0940(HleContext context) => Forward(context, 0x3E8B077C);

    /// <summary>SceNpCommerce2 0x3D49ABFA: the same body as 0x8B4BEA99 (NID_8B4BEA99).</summary>
    [Hle(Nid = 0x3D49ABFA, Library = "SceNpCommerce2")]
    void SceNpCommerce2_3D49ABFA(HleContext context) => Forward(context, 0x8B4BEA99);

    /// <summary>SceNpCommerce2 0x4121F2A1: the same body as 0x8B4BEA99 (NID_8B4BEA99).</summary>
    [Hle(Nid = 0x4121F2A1, Library = "SceNpCommerce2")]
    void SceNpCommerce2_4121F2A1(HleContext context) => Forward(context, 0x8B4BEA99);

    /// <summary>SceNpCommerce2 0x5541AF3C: the same body as 0x5730F4A4 (NID_5730F4A4).</summary>
    [Hle(Nid = 0x5541AF3C, Library = "SceNpCommerce2")]
    void SceNpCommerce2_5541AF3C(HleContext context) => Forward(context, 0x5730F4A4);

    /// <summary>SceNpCommerce2 0x592C3D4A: the same body as 0x8B4BEA99 (NID_8B4BEA99).</summary>
    [Hle(Nid = 0x592C3D4A, Library = "SceNpCommerce2")]
    void SceNpCommerce2_592C3D4A(HleContext context) => Forward(context, 0x8B4BEA99);

    /// <summary>SceNpCommerce2 0x61634358: the same body as 0x838F1DAB (NID_838F1DAB).</summary>
    [Hle(Nid = 0x61634358, Library = "SceNpCommerce2")]
    void SceNpCommerce2_61634358(HleContext context) => Forward(context, 0x838F1DAB);

    /// <summary>SceNpCommerce2 0x7575795A: the same body as 0xA200696D (NID_A200696D).</summary>
    [Hle(Nid = 0x7575795A, Library = "SceNpCommerce2")]
    void SceNpCommerce2_7575795A(HleContext context) => Forward(context, 0xA200696D);

    /// <summary>SceNpCommerce2 0x791774B7: the same body as 0x1A53A505 (NID_1A53A505).</summary>
    [Hle(Nid = 0x791774B7, Library = "SceNpCommerce2")]
    void SceNpCommerce2_791774B7(HleContext context) => Forward(context, 0x1A53A505);

    /// <summary>SceNpCommerce2 0x87D67D0B: the same body as 0x838F1DAB (NID_838F1DAB).</summary>
    [Hle(Nid = 0x87D67D0B, Library = "SceNpCommerce2")]
    void SceNpCommerce2_87D67D0B(HleContext context) => Forward(context, 0x838F1DAB);

    /// <summary>SceNpCommerce2 0x8A804C00: the same body as 0xD0831EFE (NID_D0831EFE).</summary>
    [Hle(Nid = 0x8A804C00, Library = "SceNpCommerce2")]
    void SceNpCommerce2_8A804C00(HleContext context) => Forward(context, 0xD0831EFE);

    /// <summary>SceNpCommerce2 0x8D4FDF78: the same body as 0x8B4BEA99 (NID_8B4BEA99).</summary>
    [Hle(Nid = 0x8D4FDF78, Library = "SceNpCommerce2")]
    void SceNpCommerce2_8D4FDF78(HleContext context) => Forward(context, 0x8B4BEA99);

    /// <summary>SceNpCommerce2 0x935C25D1: the same body as 0x3E8B077C (NID_3E8B077C).</summary>
    [Hle(Nid = 0x935C25D1, Library = "SceNpCommerce2")]
    void SceNpCommerce2_935C25D1(HleContext context) => Forward(context, 0x3E8B077C);

    /// <summary>SceNpCommerce2 0x94C1EA04: the same body as 0xD0831EFE (NID_D0831EFE).</summary>
    [Hle(Nid = 0x94C1EA04, Library = "SceNpCommerce2")]
    void SceNpCommerce2_94C1EA04(HleContext context) => Forward(context, 0xD0831EFE);

    /// <summary>SceNpCommerce2 0x9CF6BDA3: the same body as 0x7D2A014E (NID_7D2A014E).</summary>
    [Hle(Nid = 0x9CF6BDA3, Library = "SceNpCommerce2")]
    void SceNpCommerce2_9CF6BDA3(HleContext context) => Forward(context, 0x7D2A014E);

    /// <summary>SceNpCommerce2 0x9FEE1E0E: the same body as 0xC1542070 (NID_C1542070).</summary>
    [Hle(Nid = 0x9FEE1E0E, Library = "SceNpCommerce2")]
    void SceNpCommerce2_9FEE1E0E(HleContext context) => Forward(context, 0xC1542070);

    /// <summary>SceNpCommerce2 0xA3A80BFA: the same body as 0xD0831EFE (NID_D0831EFE).</summary>
    [Hle(Nid = 0xA3A80BFA, Library = "SceNpCommerce2")]
    void SceNpCommerce2_A3A80BFA(HleContext context) => Forward(context, 0xD0831EFE);

    /// <summary>SceNpCommerce2 0xA62BBCE3: the same body as 0x3E8B077C (NID_3E8B077C).</summary>
    [Hle(Nid = 0xA62BBCE3, Library = "SceNpCommerce2")]
    void SceNpCommerce2_A62BBCE3(HleContext context) => Forward(context, 0x3E8B077C);

    /// <summary>SceNpCommerce2 0xD494B4CE: the same body as 0x8B4BEA99 (NID_8B4BEA99).</summary>
    [Hle(Nid = 0xD494B4CE, Library = "SceNpCommerce2")]
    void SceNpCommerce2_D494B4CE(HleContext context) => Forward(context, 0x8B4BEA99);

    /// <summary>SceNpCommerce2 0xD7ED457F: the same body as 0xD3BCDADD (NID_D3BCDADD).</summary>
    [Hle(Nid = 0xD7ED457F, Library = "SceNpCommerce2")]
    void SceNpCommerce2_D7ED457F(HleContext context) => Forward(context, 0xD3BCDADD);

    /// <summary>SceNpCommerce2 0xEC0BD12F: the same body as 0x1A53A505 (NID_1A53A505).</summary>
    [Hle(Nid = 0xEC0BD12F, Library = "SceNpCommerce2")]
    void SceNpCommerce2_EC0BD12F(HleContext context) => Forward(context, 0x1A53A505);

    /// <summary>SceNpCommerce2 0xFC8B048C: the same body as 0xD7B43E47 (NID_D7B43E47).</summary>
    [Hle(Nid = 0xFC8B048C, Library = "SceNpCommerce2")]
    void SceNpCommerce2_FC8B048C(HleContext context) => Forward(context, 0xD7B43E47);
}
