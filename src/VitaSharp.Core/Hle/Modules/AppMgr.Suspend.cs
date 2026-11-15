using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// The two SceAppMgr syscalls of the VitaSDK the HLE still lacked, read in the SceAppMgr module of bootimage.skprx 3.74
/// (exports 0xECEC7F90 and 0x32FD0B99).
/// </summary>
public sealed partial class AppMgr
{
    private const int ErrorNotSystemProgram = unchecked((int)0x8080201F);
    private const int ErrorNoApplication = unchecked((int)0x8080201C);

    /// <summary>
    /// Set by sceAppMgrEnableCoredumpForTest. The kernel reads it only on a development kit (with
    /// /CONFIG/COREDUMP/force_dump_on_devmode, when an application faults); the emulated retail console never does.
    /// </summary>
    [SnapshotMissing] // states of builds before it: the call did not exist, so it was never set
    private bool _coredumpForTestEnabled;

    internal bool CoredumpForTestEnabled => _coredumpForTestEnabled;

    /// <summary>
    /// sceAppMgrSuspendUntilActivated(): a system program suspends its own application until the shell activates it
    /// again. The kernel refuses a caller that is not a system program (ksceSblACMgrIsSystemProgram, 0x8080201F) and
    /// one with no application slot (0x8080201C); an application that is already the active one (slot state 2) gets 0
    /// at once; any other is marked suspending and its process suspended (sceAppMgrActivateApp resumes it), then 0.
    /// The slot's counter at +0x1BC0, which also refuses the call (0x80802013) while it is not zero, is not kept by
    /// the emulator's application slots: it reads zero.
    /// </summary>
    [Hle("sceAppMgrSuspendUntilActivated")]
    private int sceAppMgrSuspendUntilActivated()
    {
        if (!Emu.Access.IsSystemProgram)
        {
            return ErrorNotSystemProgram;
        }

        if (Emu.Vita.ByAppId(Emu.AppId) != Emu)
        {
            return ErrorNoApplication;
        }

        if (!Emu.IsForeground)
        {
            Emu.Suspend();
        }

        return 0;
    }

    /// <summary>
    /// sceAppMgrEnableCoredumpForTest(): only the shell (ksceSblACMgrIsSceShell, and the process of the shell's slot)
    /// may call it; it sets the kernel's "core dump for test" flag and answers 0. Any other caller gets 0x8080201F.
    /// </summary>
    [Hle("sceAppMgrEnableCoredumpForTest")]
    private int sceAppMgrEnableCoredumpForTest()
    {
        if (!Emu.IsShell)
        {
            return ErrorNotSystemProgram;
        }

        _coredumpForTestEnabled = true;
        return 0;
    }
}
