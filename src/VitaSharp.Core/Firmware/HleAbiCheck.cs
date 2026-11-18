using System.Text;
using System.Text.RegularExpressions;
using VitaSharp.Core.Hle;

namespace VitaSharp.Core.Firmware;

/// <summary>
/// Compares the argument registers each [Hle] binding reads (its generated layout) with the C prototype of the same
/// function in the VitaSDK headers (vita-headers include/psp2, user side), under the Vita's AAPCS-VFP convention:
/// 32-bit values and pointers in r0-r3 then the stack, 64-bit integers in an even register pair or an 8-byte aligned
/// stack slot (shifting what follows), floats in s0-s15 and doubles in d0-d7. A binding that reads an argument from
/// another register than the prototype puts it in reads the wrong value, and when that argument is an output it
/// writes at the wrong address. Used by build/hle-abi-check (a report: the headers can be wrong too, so a mismatch
/// is checked against the firmware's module before changing the HLE).
/// </summary>
public static partial class HleAbiCheck
{
    /// <summary>A C parameter's class: "w32", "w64", "f32", "f64", "va" (the ellipsis), "struct" (by value) or
    /// "?name" (an unknown type).</summary>
    public sealed record Prototype(string Name, string[] Kinds, string File, string Parameters);

    /// <summary>One reported difference.</summary>
    public sealed record Finding(string Status, string Function, string Module, string HleLayout, string HeaderLayout, string File, string Parameters);

    private static readonly Dictionary<string, string> s_baseTypes = new(StringComparer.Ordinal)
    {
        ["void"] = "void",
        ["char"] = "w32",
        ["short"] = "w32",
        ["int"] = "w32",
        ["long"] = "w32",
        ["unsigned"] = "w32",
        ["signed"] = "w32",
        ["float"] = "f32",
        ["double"] = "f64",
        ["bool"] = "w32",
        ["_Bool"] = "w32",
        ["int8_t"] = "w32",
        ["uint8_t"] = "w32",
        ["int16_t"] = "w32",
        ["uint16_t"] = "w32",
        ["int32_t"] = "w32",
        ["uint32_t"] = "w32",
        ["int64_t"] = "w64",
        ["uint64_t"] = "w64",
        ["size_t"] = "w32",
        ["ssize_t"] = "w32",
        ["uintptr_t"] = "w32",
        ["intptr_t"] = "w32",
        ["wchar_t"] = "w32",
        ["va_list"] = "w32",
    };

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\n]*")]
    private static partial Regex LineComment();

    [GeneratedRegex(@"^\s*#[^\n]*(\\\n[^\n]*)*", RegexOptions.Multiline)]
    private static partial Regex Preprocessor();

    [GeneratedRegex(@"typedef\s+(?:struct|union)\s*\w*\s*\{")]
    private static partial Regex TypedefAggregate();

    [GeneratedRegex(@"typedef\s+enum\s*\w*\s*\{[^}]*\}\s*(\w+)\s*;")]
    private static partial Regex TypedefEnum();

    [GeneratedRegex(@"typedef\s+[^;{}()]+?\s*\(\s*\*\s*(\w+)\s*\)\s*\([^;]*\)\s*;")]
    private static partial Regex TypedefFunctionPointer();

    [GeneratedRegex(@"typedef\s+([^;{}()]+?)\s+(\**)\s*(\w+)\s*(\[[^\]]*\])?\s*;")]
    private static partial Regex TypedefSimple();

    [GeneratedRegex(@"([A-Za-z_][\w\s\*]*?)\b((?:sce|_sce)\w+)\s*\(([^;{}()]*(?:\([^()]*\)[^;{}()]*)*)\)\s*(?:__attribute__\s*\(\([^;]*?\)\)\s*)?;")]
    private static partial Regex FunctionPrototype();

    [GeneratedRegex(@"\b(const|volatile|struct|enum|union|restrict|__restrict__|SCE_\w+)\b")]
    private static partial Regex Qualifiers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static string StripC(string text)
    {
        text = BlockComment().Replace(text, " ");
        text = LineComment().Replace(text, " ");
        return Preprocessor().Replace(text, " ");
    }

    private static string KindOf(string type, Dictionary<string, string> typedefs)
    {
        if (type.Contains('*') || type.Contains('['))
        {
            return "w32";
        }

        string[] words = Qualifiers().Replace(type, " ").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return "?";
        }

        if (words.Count(word => word == "long") >= 2)
        {
            return "w64";
        }

        string last = words[^1];
        if (s_baseTypes.TryGetValue(last, out string? baseKind))
        {
            return baseKind;
        }

        return typedefs.TryGetValue(last, out string? kind) ? kind : "?" + last;
    }

    /// <summary>The prototypes of a set of header texts (path, text), with typedefs resolved across all of them; only the
    /// files <paramref name="prototypeFile"/> accepts (default: all) give prototypes (the user side, include/psp2, while
    /// include/psp2common holds the shared types). The first prototype of a name wins.</summary>
    public static Dictionary<string, Prototype> ParseHeaders(IEnumerable<(string Path, string Text)> headers, Func<string, bool>? prototypeFile = null)
    {
        var texts = headers.Select(header => (header.Path, Text: StripC(header.Text))).ToList();
        var typedefs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int pass = 0; pass < 6; pass++) // typedefs of typedefs, in any file order
        {
            foreach ((string _, string text) in texts)
            {
                CollectTypedefs(text, typedefs);
            }
        }

        var prototypes = new Dictionary<string, Prototype>(StringComparer.Ordinal);
        foreach ((string path, string text) in texts)
        {
            if (prototypeFile != null && !prototypeFile(path))
            {
                continue;
            }

            string flat = Spaces().Replace(text, " ");
            foreach (Match match in FunctionPrototype().Matches(flat))
            {
                string returnType = match.Groups[1].Value;
                if (returnType.Contains("typedef"))
                {
                    continue;
                }

                string name = match.Groups[2].Value;
                string parameters = match.Groups[3].Value.Trim();
                if (!prototypes.ContainsKey(name))
                {
                    prototypes[name] = new Prototype(name, ParameterKinds(parameters, typedefs), path, parameters);
                }
            }
        }

        return prototypes;
    }

    private static void CollectTypedefs(string text, Dictionary<string, string> typedefs)
    {
        foreach (Match match in TypedefAggregate().Matches(text))
        {
            int depth = 0;
            int i = match.Index + match.Length - 1;
            for (; i < text.Length; i++)
            {
                if (text[i] == '{')
                {
                    depth++;
                }
                else if (text[i] == '}' && --depth == 0)
                {
                    break;
                }
            }
            int end = text.IndexOf(';', Math.Min(i + 1, text.Length));
            if (end < 0)
            {
                continue;
            }

            foreach (string declarator in text[(i + 1)..end].Split(','))
            {
                string trimmed = Qualifiers().Replace(declarator, " ").Trim();
                if (trimmed.StartsWith('*'))
                {
                    typedefs[trimmed.TrimStart('*', ' ')] = "w32";
                }
                else if (trimmed.Length > 0 && !trimmed.Contains('('))
                {
                    typedefs[trimmed] = "struct";
                }
            }
        }
        foreach (Match match in TypedefEnum().Matches(text))
        {
            typedefs[match.Groups[1].Value] = "w32";
        }

        foreach (Match match in TypedefFunctionPointer().Matches(text))
        {
            typedefs[match.Groups[1].Value] = "w32";
        }

        foreach (Match match in TypedefSimple().Matches(text))
        {
            string source = match.Groups[1].Value.Trim();
            string name = match.Groups[3].Value;
            if (match.Groups[2].Length > 0 || match.Groups[4].Success)
            {
                typedefs[name] = "w32";
                continue;
            }
            if (Regex.IsMatch(source, @"^(struct|union)\s+\w+$"))
            {
                typedefs.TryAdd(name, "struct");
                continue;
            }
            if (Regex.IsMatch(source, @"^enum\s+\w+$"))
            {
                typedefs[name] = "w32";
                continue;
            }
            string kind = KindOf(source, typedefs);
            if (!kind.StartsWith('?'))
            {
                typedefs[name] = kind;
            }
        }
    }

    private static string[] ParameterKinds(string parameters, Dictionary<string, string> typedefs)
    {
        if (parameters.Length == 0 || parameters == "void")
        {
            return [];
        }

        var parts = new List<string>();
        int depth = 0;
        var current = new StringBuilder();
        foreach (char c in parameters)
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }

            if (c == ',' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }
        parts.Add(current.ToString());
        var kinds = new List<string>();
        foreach (string raw in parts)
        {
            string parameter = raw.Trim();
            if (parameter == "...")
            {
                kinds.Add("va");
                continue;
            }
            if (parameter.Contains('('))
            {
                kinds.Add("w32"); // function pointer
                continue;
            }
            kinds.Add(KindOf(TypeOfParameter(parameter, typedefs), typedefs));
        }

        return kinds.ToArray();
    }

    /// <summary>The type part of "type name" / "type name[n]" / "type" (an unnamed parameter).</summary>
    private static string TypeOfParameter(string parameter, Dictionary<string, string> typedefs)
    {
        Match match = Regex.Match(parameter, @"^(.*?)(\w+)\s*(\[[^\]]*\])?$");
        if (!match.Success)
        {
            return parameter;
        }

        string head = match.Groups[1].Value.Trim();
        string last = match.Groups[2].Value;
        if (head.Length == 0 || s_baseTypes.ContainsKey(last) || typedefs.ContainsKey(last) || Regex.IsMatch(head, @"\b(struct|enum|union)$"))
        {
            return parameter; // an unnamed parameter: the last word is part of the type
        }

        return head + match.Groups[3].Value;
    }

    /// <summary>The argument registers of a list of C parameter kinds, as the binder's layout tokens (r0, r2r3, sp0,
    /// sp8+8, s0, d1, va@N); null when a parameter is a struct by value or of an unknown type.</summary>
    public static string[]? Layout(IReadOnlyList<string> kinds)
    {
        var tokens = new List<string>();
        int gpr = 0;
        int stack = 0;
        uint vfpUsed = 0;
        foreach (string kind in kinds)
        {
            switch (kind)
            {
                case "va":
                    tokens.Add($"va@{gpr}");
                    return tokens.ToArray();
                case "f32":
                case "f64":
                    int singles = kind == "f64" ? 2 : 1;
                    int single = -1;
                    for (int i = 0; i + singles <= 16; i += singles)
                    {
                        uint mask = (singles == 2 ? 3u : 1u) << i;
                        if ((vfpUsed & mask) == 0)
                        {
                            vfpUsed |= mask;
                            single = i;
                            break;
                        }
                    }
                    if (single < 0)
                    {
                        return null;
                    }

                    tokens.Add(singles == 2 ? $"d{single / 2}" : $"s{single}");
                    break;
                case "w64":
                    if ((gpr & 1) != 0)
                    {
                        gpr++;
                    }

                    if (gpr + 2 <= 4)
                    {
                        tokens.Add($"r{gpr}r{gpr + 1}");
                        gpr += 2;
                    }
                    else
                    {
                        gpr = 4;
                        stack = (stack + 7) & ~7;
                        tokens.Add($"sp{stack}+8");
                        stack += 8;
                    }
                    break;
                case "w32":
                    if (gpr < 4)
                    {
                        tokens.Add($"r{gpr}");
                        gpr++;
                    }
                    else
                    {
                        tokens.Add($"sp{stack}");
                        stack += 4;
                    }
                    break;
                default:
                    return null;
            }
        }

        return tokens.ToArray();
    }

    /// <summary>The register tokens of a binding's layout string ("r0=uint,s0=float->int"), without the HleContext.</summary>
    public static string[] BindingTokens(string layout)
    {
        string arguments = layout[..layout.IndexOf("->", StringComparison.Ordinal)];
        if (arguments.Length == 0)
        {
            return [];
        }

        return arguments.Split(',').Select(part => part.Split('=')[0]).Where(token => token != "ctx").ToArray();
    }

    /// <summary>Every binding of <paramref name="modules"/> with a prototype: "MISMATCH" when the registers differ,
    /// "FEWER" when the binding reads a prefix of the prototype's arguments (it ignores the rest; harmless unless one
    /// of them is an output it should fill), "UNRESOLVED" when the prototype has a struct by value or an unknown type.</summary>
    public static List<Finding> Compare(IReadOnlyDictionary<string, Prototype> prototypes, IEnumerable<HleModuleEntry> modules, bool includeFewer)
    {
        var findings = new List<Finding>();
        foreach (HleModuleEntry module in modules)
        {
            foreach (HleBinding binding in HleBindings.For(module.Type) ?? [])
            {
                string name = binding.Name ?? binding.Method;
                if (!prototypes.TryGetValue(name, out Prototype? prototype))
                {
                    continue;
                }

                string[] hle = BindingTokens(binding.Layout);
                string[]? header = Layout(prototype.Kinds);
                string status;
                if (header == null)
                {
                    status = "UNRESOLVED";
                }
                else if (hle.SequenceEqual(header))
                {
                    continue;
                }
                else if (hle.Length < header.Length && header.Take(hle.Length).SequenceEqual(hle))
                {
                    if (!includeFewer)
                    {
                        continue;
                    }

                    status = "FEWER";
                }
                else
                {
                    status = "MISMATCH";
                }
                findings.Add(new Finding(status, name, module.Type.Name, string.Join(",", hle),
                    header == null ? string.Join(",", prototype.Kinds) : string.Join(",", header), prototype.File, prototype.Parameters));
            }
        }

        return findings.OrderBy(finding => finding.Status, StringComparer.Ordinal).ThenBy(finding => finding.Function, StringComparer.Ordinal).ToList();
    }
}
