using VitaSharp.Core.Hle;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// The census of the FW 3.74 kernel exports (docs/hle/kernel-exports.md and docs/hle/data/kernel-exports.tsv), updated
/// from this build's HLE. Each row of the table (library, NID, name, status, VitaSharp method, importers; the rows come
/// from the firmware's export tables and stay) gets its status (impl, default, missing) and method again, and the
/// per-library table of the page (from its "## Por biblioteca" heading to the next heading) is written from it, with
/// the previous status as the "antes" column. The page is Portuguese, as the rest of docs/hle.
/// </summary>
public static class KernelCensus
{
    /// <summary>Where the table and the page are under the repository root.</summary>
    public static readonly string TablePath = Path.Combine("docs", "hle", "data", "kernel-exports.tsv");

    public static readonly string PagePath = Path.Combine("docs", "hle", "kernel-exports.md");

    private const string SectionHeading = "## Por biblioteca";

    /// <summary>Updates both files under <paramref name="root"/>; returns null and a one-line summary, or the
    /// error.</summary>
    public static string? Update(string root, out string summary)
    {
        summary = "";
        string table = Path.Combine(root, TablePath);
        string page = Path.Combine(root, PagePath);
        if (!File.Exists(table) || !File.Exists(page))
        {
            return $"{table} or {page} not found";
        }

        Dictionary<uint, (string Method, bool Default)> methods = ImplementedMethods();
        var output = new List<string>();
        var libraries = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(table))
        {
            string[] cells = line.Split('\t');
            if (line.StartsWith('#') || cells.Length < 6 || !cells[1].StartsWith("0x", StringComparison.Ordinal))
            {
                output.Add(line);
                continue;
            }

            uint nid = uint.Parse(cells[1].AsSpan(2), System.Globalization.NumberStyles.HexNumber);
            bool before = cells[3] != "missing";
            string status = "missing";
            string implementation = "";
            if (methods.TryGetValue(nid, out (string Method, bool Default) found))
            {
                status = found.Default ? "default" : "impl";
                implementation = found.Method;
            }

            cells[3] = status;
            cells[4] = implementation;
            output.Add(string.Join('\t', cells));

            // functions, before, impl, default, missing, missing and imported, unnamed
            if (!libraries.TryGetValue(cells[0], out int[]? counts))
            {
                counts = new int[7];
                libraries[cells[0]] = counts;
            }

            counts[0]++;
            counts[1] += before ? 1 : 0;
            counts[2] += status == "impl" ? 1 : 0;
            counts[3] += status == "default" ? 1 : 0;
            counts[4] += status == "missing" ? 1 : 0;
            counts[5] += status == "missing" && cells[5].Length > 0 ? 1 : 0;
            counts[6] += cells[2].StartsWith("NID_", StringComparison.Ordinal) ? 1 : 0;
        }

        string[] lines = File.ReadAllLines(page);
        int heading = Array.FindIndex(lines, l => l.StartsWith(SectionHeading, StringComparison.Ordinal));
        if (heading < 0)
        {
            return $"no \"{SectionHeading}\" section in {page}";
        }

        int next = Array.FindIndex(lines, heading + 1, l => l.StartsWith("## ", StringComparison.Ordinal));
        if (next < 0)
        {
            next = lines.Length;
        }

        // each library's module column comes from the previous table
        var moduleOf = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = heading + 1; i < next; i++)
        {
            string[] cells = lines[i].Split('|');
            if (cells.Length > 3 && cells[1].Trim().StartsWith("Sce", StringComparison.Ordinal))
            {
                moduleOf[cells[1].Trim()] = cells[2].Trim();
            }
        }

        var section = new List<string>
        {
            $"{SectionHeading} (gerado por `VitaSharp.Desktop kernel-census`; \"antes\" é o censo anterior)",
            "",
            "| Biblioteca | Módulo (NID da biblioteca) | Funções | HLE+default antes | HLE | default | faltam | faltam e são importadas | sem nome |",
            "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
        };
        int[] total = new int[7];
        foreach ((string library, int[] counts) in libraries)
        {
            section.Add($"| {library} | {moduleOf.GetValueOrDefault(library, "")} | {string.Join(" | ", counts)} |");
            for (int k = 0; k < total.Length; k++)
            {
                total[k] += counts[k];
            }
        }

        section.Add($"| **Total** | | {string.Join(" | ", total.Select(t => $"**{t}**"))} |");
        section.Add("");
        File.WriteAllLines(table, output);
        File.WriteAllLines(page, lines[..heading].Concat(section).Concat(lines[next..]));
        summary = $"kernel census: {total[0]} functions, {total[2]} HLE + {total[3]} default (before: {total[1]}), {total[4]} missing ({total[5]} imported)";
        return null;
    }

    /// <summary>Every NID an [Hle] method implements: its module and method, and whether every implementation of it
    /// is a default.</summary>
    private static Dictionary<uint, (string Method, bool Default)> ImplementedMethods()
    {
        var methods = new Dictionary<uint, (string Method, bool Default)>();
        foreach (HleModuleEntry module in HleBindings.Modules)
        {
            foreach (HleBinding binding in module.Bindings())
            {
                string name = binding.Name ?? binding.Method;
                uint[] nids = binding.Nid != 0
                    ? [binding.Nid]
                    : NidDatabase.LookupName(name).Where(i => binding.Library == null || i.Library == binding.Library).Select(i => i.Nid).ToArray();
                foreach (uint nid in nids)
                {
                    string method = $"{module.Type.Name}.{binding.Method}";
                    methods[nid] = methods.TryGetValue(nid, out (string Method, bool Default) had)
                        ? (had.Method, had.Default && binding.Default)
                        : (method, binding.Default);
                }
            }
        }

        return methods;
    }
}
