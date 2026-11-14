using System.Collections.Concurrent;
using VitaSharp.Core.Loader;
using VitaSharp.Core.Loader.Self;
using VitaSharp.Core.Util;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// The kernel modules of the installed firmware: every module of os0:kd (sysmem.skprx, modulemgr.skprx,
/// processmgr.skprx, ...) and the ones the kernel image bootimage.skprx carries inside it (SceNpDrm, SceAppMgr,
/// SceSysmodule, ...; the image keeps them as whole ELFs one after another). Each is read with
/// <see cref="ModuleImage"/>: its name, module NID and export tables, which is what the kernel's module manager
/// shows of a loaded module (ksceKernelGetModuleList / ksceKernelGetModuleCB). Read once per firmware folder.
/// </summary>
public sealed class FirmwareKernelImage
{
    /// <summary>One kernel module of the firmware: where it is, its ELF and its tables.</summary>
    public sealed record Module(string VitaPath, byte[] Elf, ModuleImage Image);

    static readonly ConcurrentDictionary<string, FirmwareKernelImage> s_cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The modules in load order of the firmware: the image's own modules first, then the os0:kd files by name.</summary>
    public IReadOnlyList<Module> Modules { get; }

    /// <summary>The kernel modules of the firmware installed under <paramref name="fsRoot"/> (none without one).</summary>
    public static FirmwareKernelImage For(string fsRoot) => s_cache.GetOrAdd(Path.GetFullPath(fsRoot), root => new FirmwareKernelImage(root));

    /// <summary>Forgets what was read under <paramref name="fsRoot"/> (tests that install a firmware of their own).</summary>
    internal static void Forget(string fsRoot) => s_cache.TryRemove(Path.GetFullPath(fsRoot), out _);

    /// <summary>The module named <paramref name="name"/> (the name in its module_info), or null.</summary>
    public Module? Named(string name) => Modules.FirstOrDefault(m => m.Image.Name == name);

    FirmwareKernelImage(string fsRoot)
    {
        var modules = new List<Module>();
        string folder = Io.FirmwareLayout.KernelModules.Host(fsRoot);
        if (Directory.Exists(folder))
        {
            string image = Path.Combine(folder, Io.FirmwareLayout.KernelImage);
            if (File.Exists(image) && ReadElf(image) is { } kernelImage)
            {
                foreach (byte[] elf in EmbeddedElfs(kernelImage))
                {
                    if (TryRead(elf) is { } embedded)
                    {
                        modules.Add(new Module(Io.FirmwareLayout.KernelModules.Guest(Io.FirmwareLayout.KernelImage), elf, embedded));
                    }
                }
            }

            foreach (string file in Directory.EnumerateFiles(folder, "*.skprx").Order(StringComparer.OrdinalIgnoreCase))
            {
                if (string.Equals(Path.GetFileName(file), Io.FirmwareLayout.KernelImage, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ReadElf(file) is { } elf && TryRead(elf) is { } read)
                {
                    modules.Add(new Module(Io.FirmwareLayout.KernelModules.Guest(Path.GetFileName(file)), elf, read));
                }
            }
        }

        Modules = modules;
    }

    static byte[]? ReadElf(string file)
    {
        try
        {
            return SelfDecryptor.ToElf(File.ReadAllBytes(file), null);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or IndexOutOfRangeException or NotSupportedException)
        {
            Log.Debug($"Firmware kernel module {Path.GetFileName(file)} not readable ({e.Message})");
            return null;
        }
    }

    static ModuleImage? TryRead(byte[] elf)
    {
        try
        {
            ModuleImage image = ModuleImage.Read(elf);
            return image.Name.Length > 0 ? image : null;
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or IndexOutOfRangeException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The ELFs inside the kernel image (after its own header): each runs to the next ELF magic.</summary>
    static IEnumerable<byte[]> EmbeddedElfs(byte[] kernelImage)
    {
        ReadOnlySpan<byte> magic = [0x7F, (byte)'E', (byte)'L', (byte)'F'];
        var starts = new List<int>();
        int from = 4;
        while (from < kernelImage.Length)
        {
            int next = kernelImage.AsSpan(from).IndexOf(magic);
            if (next < 0)
            {
                break;
            }

            starts.Add(from + next);
            from += next + 4;
        }

        var elfs = new List<byte[]>();
        for (int k = 0; k < starts.Count; k++)
        {
            int end = k + 1 < starts.Count ? starts[k + 1] : kernelImage.Length;
            elfs.Add(kernelImage[starts[k]..end]);
        }

        return elfs;
    }
}
