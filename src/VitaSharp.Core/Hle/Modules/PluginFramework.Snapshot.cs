using VitaSharp.Core.Kernel;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// Save states: the hooks and data injections of the plugin framework (emulator option "Enable Kernel Plugins"), by
/// UID; the records themselves are kernel objects (<see cref="KernelSnapshot"/>) and the code they patched, the thunks,
/// trampolines and reference records are guest memory of the state. A load reinstalls what memory does not hold: the
/// redirection of the import stubs of an HLE function a hook took (also for stubs bound after the load), and gives the
/// stubs of a function hooked only in the running session back to the function. No plugin is loaded again: the
/// plugins' modules, threads and memory are the state's.
/// </summary>
[GenerateSnapshot, SnapshotIgnoreFields("_patchLock", "_redirectsBeforeLoad")]
public sealed partial class PluginFramework
{
    /// <summary>The HLE functions this framework's hooks redirected in the running session (read before a load).</summary>
    readonly HashSet<uint> _redirectsBeforeLoad = new();

    void BeforeSnapshotLoad()
    {
        lock (_patchLock)
        {
            _redirectsBeforeLoad.Clear();
            foreach (HookPatch patch in _hooks)
            {
                if (patch.RedirectedNid != 0)
                {
                    _redirectsBeforeLoad.Add(patch.RedirectedNid);
                }
            }
        }
    }

    void AfterSnapshotLoad()
    {
        lock (_patchLock)
        {
            if (_hooks.Contains(null!) || _injections.Contains(null!))
            {
                throw new SnapshotException("a hook or data injection of the plugin framework is missing from the kernel objects");
            }

            var saved = new HashSet<uint>();
            foreach (HookPatch patch in _hooks)
            {
                if (patch.RedirectedNid != 0)
                {
                    saved.Add(patch.RedirectedNid);
                }
            }

            // a function hooked only in the running session: its stubs bind as they would now
            foreach (uint nid in _redirectsBeforeLoad)
            {
                if (saved.Contains(nid))
                {
                    continue;
                }

                foreach (Emulator process in RedirectedProcesses())
                {
                    process.Hle.ClearRedirect(nid);
                }

                foreach (Emulator process in RedirectedProcesses())
                {
                    using (ProcessScope.Enter(process))
                    {
                        process.Linker.RebindFunction(nid);
                    }
                }
            }

            _redirectsBeforeLoad.Clear();
            foreach (HookPatch patch in _hooks)
            {
                if (patch.RedirectedNid != 0)
                {
                    TakeImportStubs(patch);
                }
            }
        }
    }
}
