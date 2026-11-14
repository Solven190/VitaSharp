using VitaSharp.Core.Io;

namespace VitaSharp.Core.Package;

/// <summary>
/// Earlier versions of the PSM installer left a Unity title's own runtime inside RO (ux0:psm/&lt;id&gt;/RO/runtime, the
/// package's "contents/runtime"), where the console's promote never leaves it: it moves it to the title's folder
/// (<see cref="FirmwareLayout.PsmOwnRuntime"/>) and stores the runtime's licence in ux0:license/app/&lt;id&gt;
/// (<see cref="PsmPkgInstaller.PrepareOwnRuntime"/>). Without
/// that the shell took the title for a plain PSM one and asked for a PSM Runtime Package update it can never satisfy.
/// The first installer also left the package's "contents/" items outside RO (see <see cref="MoveContents"/>).
/// <see cref="Run"/> brings those titles to the console's layout once and, when it changed one, makes the shell
/// rebuild the memory card's part of its database (so the title is registered again, as a Unity one). Linked folders
/// (another data folder's, shared by a test data folder) are not touched.
/// </summary>
public static class PsmLayoutMigration
{
    private static readonly object s_lock = new();

    /// <summary>Moves the misplaced runtimes under <paramref name="fsRoot"/>; returns how many titles changed.</summary>
    public static int Run(string fsRoot, Action<string>? log = null)
    {
        string psmRoot = FirmwareLayout.PsmTitles.Host(fsRoot);
        lock (s_lock)
        {
            if (!Directory.Exists(psmRoot) || IsLink(psmRoot))
            {
                return 0;
            }

            string[] titles;
            try
            {
                titles = Directory.GetDirectories(psmRoot);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return 0;
            }

            string ux0 = Path.Combine(fsRoot, FirmwareLayout.PsmTitles.Device);
            int changed = 0;
            foreach (string title in titles)
            {
                if (IsLink(title))
                {
                    continue;
                }

                try
                {
                    bool contentsMoved = MoveContents(title, log);
                    if (MoveRuntime(title, log) | PsmPkgInstaller.PrepareOwnRuntime(ux0, Path.GetFileName(title), log) | contentsMoved)
                    {
                        changed++;
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"PSM title {Path.GetFileName(title)}: runtime not prepared ({e.Message})");
                }
            }
            if (changed > 0)
            {
                AppMeta.InvalidateCardId(ux0, log);
            }

            return changed;
        }
    }

    /// <summary>
    /// The package's "contents/" (or "content/") items into RO, where the console's promote puts them
    /// (ux0:psm/&lt;id&gt;/RO/Application/app.info is what the shell's database rebuild reads, and the PSM runtime
    /// mounts RO as its application folder): the first PSM installer left them in ux0:psm/&lt;id&gt;/contents. Items RO
    /// already has are kept; a Unity title's runtime lands in RO/runtime and <see cref="MoveRuntime"/> takes it on.
    /// </summary>
    private static bool MoveContents(string title, Action<string>? log)
    {
        bool moved = false;
        string readOnly = Path.Combine(title, FirmwareLayout.PsmReadOnly);
        foreach (string folder in PsmPkgInstaller.ContentFolders)
        {
            string misplaced = Path.Combine(title, folder);
            if (!Directory.Exists(misplaced) || string.Equals(Path.GetFullPath(misplaced), Path.GetFullPath(readOnly), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(readOnly);
            MergeInto(misplaced, readOnly);
            moved = true;
            log?.Invoke($"PSM title {Path.GetFileName(title)}: the package's {folder}/ moved into {FirmwareLayout.PsmReadOnly}");
        }
        return moved;
    }

    /// <summary>Moves every entry of <paramref name="source"/> into <paramref name="target"/> (folders merged, files
    /// the target has kept), then deletes <paramref name="source"/>.</summary>
    private static void MergeInto(string source, string target)
    {
        foreach (string dir in Directory.GetDirectories(source))
        {
            string existing = Vfs.ResolveCaseInsensitive(target, Path.GetFileName(dir));
            if (Directory.Exists(existing))
            {
                MergeInto(dir, existing);
            }
            else
            {
                Directory.Move(dir, Path.Combine(target, Path.GetFileName(dir)));
            }
        }

        foreach (string file in Directory.GetFiles(source))
        {
            string existing = Vfs.ResolveCaseInsensitive(target, Path.GetFileName(file));
            if (!File.Exists(existing))
            {
                File.Move(file, Path.Combine(target, Path.GetFileName(file)));
            }
        }

        Directory.Delete(source, true);
    }

    /// <summary>RO/runtime to the title's folder, when the title has no runtime there yet.</summary>
    private static bool MoveRuntime(string title, Action<string>? log)
    {
        string misplaced = Vfs.ResolveCaseInsensitive(Path.Combine(title, FirmwareLayout.PsmReadOnly), FirmwareLayout.PsmOwnRuntime);
        string target = Path.Combine(title, FirmwareLayout.PsmOwnRuntime);
        if (!Directory.Exists(misplaced) || Directory.Exists(Vfs.ResolveCaseInsensitive(title, FirmwareLayout.PsmOwnRuntime)))
        {
            return false;
        }

        Directory.Move(misplaced, target);
        log?.Invoke($"PSM title {Path.GetFileName(title)}: its own runtime moved out of {FirmwareLayout.PsmReadOnly} to {target}");
        return true;
    }

    private static bool IsLink(string dir) => new DirectoryInfo(dir).LinkTarget != null;
}
