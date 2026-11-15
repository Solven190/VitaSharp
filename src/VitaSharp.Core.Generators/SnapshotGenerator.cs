using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;

namespace VitaSharp.Core.Generators;

/// <summary>
/// Save states (docs/notes/save-states.md):
/// <list type="bullet">
/// <item>for every partial type marked <c>[GenerateSnapshot]</c>, an <c>ISnapshotState</c> implementation that writes
/// its instance fields in declaration order, each in a length-prefixed block, and reads them back by name in the order
/// the state has them (supported field types: see <c>GenerateSnapshotAttribute</c>; any other is error VSSNAP001). A
/// type may add <c>void SaveExtra(SnapshotWriter)</c> / <c>void LoadExtra(SnapshotReader)</c> for hand-written parts
/// (one more block after the fields) and <c>void AfterSnapshotLoad()</c>, which the generated methods call;</item>
/// <item>the type's layout (field names, the shape of each field's data, a signature of its hooks) registered with
/// <c>SnapshotLayouts</c> by a module initializer: states keep the layouts they were saved with, and a load of another
/// build's state maps fields by name (a field this build added takes its <c>[SnapshotMissing]</c> value);</item>
/// <item><c>SnapshotSchema</c> (core assembly only): which HLE modules have instance state but no
/// <c>ISnapshotState</c> (a save is refused once the game used one of them).</item>
/// </list>
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class SnapshotGenerator : IIncrementalGenerator
{
    private const string AttrName = "VitaSharp.Core.SaveState.GenerateSnapshotAttribute";
    private const string IgnoreName = "VitaSharp.Core.SaveState.SnapshotIgnoreAttribute";
    private const string IgnoreFieldsName = "VitaSharp.Core.SaveState.SnapshotIgnoreFieldsAttribute";
    private const string MissingName = "VitaSharp.Core.SaveState.SnapshotMissingAttribute";
    private const string AliasName = "VitaSharp.Core.SaveState.SnapshotAliasAttribute";
    private const string WriterName = "VitaSharp.Core.SaveState.SnapshotWriter";
    private const string ReaderName = "VitaSharp.Core.SaveState.SnapshotReader";
    private const string StateName = "VitaSharp.Core.SaveState.ISnapshotState";
    private const string KernelObjectName = "VitaSharp.Core.Kernel.KernelObject";
    private const string HleModuleName = "VitaSharp.Core.Hle.HleModule";

    private static readonly DiagnosticDescriptor s_unsupported = new(
        "VSSNAP001", "Field not supported by the snapshot generator",
        "{0}.{1}: type {2} cannot be saved; mark the field [SnapshotIgnore] or write ISnapshotState by hand", "VitaSharp.SaveState", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor s_notPartial = new(
        "VSSNAP002", "[GenerateSnapshot] type must be partial", "{0} must be partial (and so must its containing types)", "VitaSharp.SaveState", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValueProvider<ImmutableArray<INamedTypeSymbol?>> types = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is TypeDeclarationSyntax,
                static (ctx, ct) => ctx.SemanticModel.GetDeclaredSymbol(ctx.Node, ct) as INamedTypeSymbol)
            .Where(static t => t is not null)
            .Collect();
        context.RegisterSourceOutput(types.Combine(context.CompilationProvider), static (spc, pair) => Emit(spc, pair.Left!, pair.Right));
    }

    private sealed class Ctx
    {
        public Compilation C = null!;
        public INamedTypeSymbol? State, KernelObject, Attr, Ignore, IgnoreFields, Missing, Alias, Writer, Reader, Module;
        /// <summary>Unique local variable numbers of the generated readers.</summary>
        public int N;
    }

    private static bool HasAttr(ISymbol s, INamedTypeSymbol? a) => a != null && s.GetAttributes().Any(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, a));

    private static bool Implements(ITypeSymbol t, INamedTypeSymbol? i) => i != null && t.AllInterfaces.Any(x => SymbolEqualityComparer.Default.Equals(x, i));

    private static bool Derives(ITypeSymbol t, INamedTypeSymbol? b)
    {
        if (b == null)
        {
            return false;
        }

        for (INamedTypeSymbol? x = t.BaseType; x != null; x = x.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(x, b))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The fields a snapshot covers: instance fields (auto-property backing fields by their property) declared
    /// in <paramref name="t"/>, without [SnapshotIgnore].</summary>
    private static IEnumerable<(string Name, ITypeSymbol Type, bool ReadOnly, ISymbol Symbol)> Fields(INamedTypeSymbol t, Ctx c)
    {
        var ignored = new HashSet<string>(t.GetAttributes().Where(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, c.IgnoreFields))
            .SelectMany(a => a.ConstructorArguments.SelectMany(x => x.Kind == TypedConstantKind.Array ? x.Values : ImmutableArray.Create(x)))
            .Select(x => x.Value as string).Where(x => x != null)!);
        foreach (ISymbol m in t.GetMembers())
        {
            if (m is not IFieldSymbol f || f.IsStatic || f.IsConst)
            {
                continue;
            }

            if (ignored.Contains(f.Name) || (f.AssociatedSymbol != null && ignored.Contains(f.AssociatedSymbol.Name)))
            {
                continue;
            }

            if (f.AssociatedSymbol is IPropertySymbol p)
            {
                if (HasAttr(p, c.Ignore) || HasAttr(f, c.Ignore))
                {
                    continue;
                }

                yield return (p.Name, f.Type, p.SetMethod == null || p.SetMethod.IsInitOnly, p);
                continue;
            }
            if (f.IsImplicitlyDeclared || HasAttr(f, c.Ignore))
            {
                continue;
            }

            yield return (f.Name, f.Type, f.IsReadOnly, f);
        }
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<INamedTypeSymbol?> candidates, Compilation compilation)
    {
        var c = new Ctx
        {
            C = compilation,
            State = compilation.GetTypeByMetadataName(StateName),
            KernelObject = compilation.GetTypeByMetadataName(KernelObjectName),
            Attr = compilation.GetTypeByMetadataName(AttrName),
            Ignore = compilation.GetTypeByMetadataName(IgnoreName),
            IgnoreFields = compilation.GetTypeByMetadataName(IgnoreFieldsName),
            Missing = compilation.GetTypeByMetadataName(MissingName),
            Alias = compilation.GetTypeByMetadataName(AliasName),
            Writer = compilation.GetTypeByMetadataName(WriterName),
            Reader = compilation.GetTypeByMetadataName(ReaderName),
            Module = compilation.GetTypeByMetadataName(HleModuleName),
        };
        if (c.State == null || c.Attr == null)
        {
            return;
        }

        var all = candidates.Where(t => t != null).Select(t => t!).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
            .OrderBy(t => t.ToDisplayString(), System.StringComparer.Ordinal).ToList();
        var layouts = new StringBuilder();
        foreach (INamedTypeSymbol? t in all.Where(t => HasAttr(t, c.Attr)))
        {
            if (!IsPartialChain(t))
            {
                spc.ReportDiagnostic(Diagnostic.Create(s_notPartial, t.Locations.FirstOrDefault(), t.Name));
                continue;
            }
            string? src = EmitType(spc, t, c, layouts);
            if (src != null)
            {
                spc.AddSource($"Snapshot.{t.ToDisplayString().Replace('<', '_').Replace('>', '_')}.g.cs", src);
            }
        }
        if (layouts.Length > 0)
        {
            var reg = new StringBuilder();
            reg.AppendLine("// <auto-generated/> by VitaSharp.Core.Generators.SnapshotGenerator");
            reg.AppendLine("namespace VitaSharp.Core.SaveState.Generated;");
            reg.AppendLine("internal static class SnapshotLayoutRegistration");
            reg.AppendLine("{");
            reg.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
            reg.AppendLine("    internal static void Register() => global::VitaSharp.Core.SaveState.SnapshotLayouts.Register(new global::VitaSharp.Core.SaveState.SnapshotLayout[]");
            reg.AppendLine("    {");
            reg.Append(layouts);
            reg.AppendLine("    });");
            reg.AppendLine("}");
            spc.AddSource("SnapshotLayoutRegistration.g.cs", reg.ToString());
        }
        if (compilation.AssemblyName != "VitaSharp.Core")
        {
            return;
        }

        INamedTypeSymbol? moduleBase = compilation.GetTypeByMetadataName(HleModuleName);
        var stateful = new List<string>();
        foreach (INamedTypeSymbol? t in all)
        {
            if (t.IsAbstract || t.TypeKind != TypeKind.Class || !Derives(t, moduleBase) || Implements(t, c.State) || HasAttr(t, c.Attr))
            {
                continue;
            }

            if (t.DeclaredAccessibility is Accessibility.Private or Accessibility.Protected)
            {
                continue;
            }

            if (Fields(t, c).Any())
            {
                stateful.Add(t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat));
            }
        }
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by VitaSharp.Core.Generators.SnapshotGenerator");
        sb.AppendLine("namespace VitaSharp.Core.SaveState;");
        sb.AppendLine("public static partial class SnapshotSchema");
        sb.AppendLine("{");
        sb.AppendLine("    static readonly global::System.Collections.Generic.HashSet<global::System.Type> StatefulModules = new()");
        sb.AppendLine("    {");
        foreach (string s in stateful)
        {
            sb.AppendLine($"        typeof({s}),");
        }

        sb.AppendLine("    };");
        sb.AppendLine("}");
        spc.AddSource("SnapshotSchema.g.cs", sb.ToString());
    }

    private static bool IsPartialChain(INamedTypeSymbol t)
    {
        for (INamedTypeSymbol x = t; x != null; x = x.ContainingType)
        {
            if (!x.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax d && d.Modifiers.Any(m => m.Text == "partial")))
            {
                return false;
            }
        }

        return true;
    }

    private static AttributeData? FindAttr(ISymbol s, INamedTypeSymbol? a)
    {
        if (a == null)
        {
            return null;
        }

        return s.GetAttributes().FirstOrDefault(x => SymbolEqualityComparer.Default.Equals(x.AttributeClass, a));
    }

    /// <summary>A C# string literal's contents.</summary>
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string? EmitType(SourceProductionContext spc, INamedTypeSymbol t, Ctx c, StringBuilder layouts)
    {
        var save = new StringBuilder();
        var cases = new StringBuilder();
        var missing = new StringBuilder();
        var fieldEntries = new List<string>();
        var names = new List<string>();
        bool ok = true;
        string key = t.ToDisplayString();
        foreach ((string? name, ITypeSymbol? type, bool ro, ISymbol? sym) in Fields(t, c))
        {
            string e = $"this.{name}";
            if (ro && !IsMutableInPlace(type, c))
            {
                continue; // configuration set at construction
            }

            if (!Supported(type, c, 0))
            {
                spc.ReportDiagnostic(Diagnostic.Create(s_unsupported, sym.Locations.FirstOrDefault(), t.Name, name, type.ToDisplayString()));
                ok = false;
                continue;
            }

            int index = names.Count;
            names.Add(name);
            AttributeData? missingAttr = FindAttr(sym, c.Missing);
            string[] aliases = FindAttr(sym, c.Alias) is { } aliasAttr
                ? aliasAttr.ConstructorArguments.SelectMany(x => x.Kind == TypedConstantKind.Array ? x.Values : ImmutableArray.Create(x))
                    .Select(x => x.Value as string).Where(x => x != null).Select(x => x!).ToArray()
                : [];
            string aliasList = aliases.Length == 0 ? "null" : "new string[] { " + string.Join(", ", aliases.Select(x => $"\"{Escape(x)}\"")) + " }";
            string hasMissing = missingAttr != null ? "true" : "false";
            fieldEntries.Add($"new global::VitaSharp.Core.SaveState.SnapshotField(\"{Escape(name)}\", \"{Escape(TypeSignature(type, c, 0))}\", {hasMissing}, {aliasList})");

            // a reference to an HLE module (a cache such as AppUtil's AppMgr) is no state: the modules are the
            // emulator's, one of each, and a load restores into them. Read as a nested state, a load made a detached
            // copy of the referenced module. It keeps its place in the layout (states of older builds have it, and
            // format 1 cannot skip a field), written empty and skipped on load.
            bool moduleReference = IsModuleReference(type, c);
            save.AppendLine("        at = w.BeginBlock();");
            if (moduleReference)
            {
                save.AppendLine("        w.Write(false); // an absent state, as SnapshotWriter.Write(null) writes it");
            }
            else
            {
                EmitWrite(save, type, e, 0, c, "        ");
            }

            save.AppendLine("        w.EndBlock(at);");

            cases.AppendLine($"                case \"{Escape(name)}\":");
            foreach (string alias in aliases)
            {
                cases.AppendLine($"                case \"{Escape(alias)}\":");
            }

            cases.AppendLine("                {");
            if (moduleReference)
            {
                cases.AppendLine("                    if (r.ReadBool())");
                cases.AppendLine("                    {");
                cases.AppendLine("                        r.ReadBlock(); // the referenced module's data an older build saved here");
                cases.AppendLine("                    }");
            }
            else if (ro)
            {
                EmitReadInPlace(cases, type, e, c, "                    ");
            }
            else
            {
                string v = EmitRead(cases, type, 0, c, "                    ", e);
                cases.AppendLine($"                    {e} = {v};");
            }

            cases.AppendLine($"                    found[{index}] = true;");
            cases.AppendLine("                    break;");
            cases.AppendLine("                }");

            missing.AppendLine($"        if (!found[{index}])");
            missing.AppendLine("        {");
            if (moduleReference)
            {
                missing.AppendLine("            // a module reference: nothing to restore");
            }
            else if (missingAttr == null)
            {
                missing.AppendLine($"            throw new global::VitaSharp.Core.SaveState.SnapshotException(\"{Escape(key)}: the state has no value for {Escape(name)}\");");
            }
            else
            {
                EmitMissing(missing, type, e, ro, missingAttr, c, "            ");
            }

            missing.AppendLine("        }");
        }

        if (!ok)
        {
            return null;
        }

        INamedTypeSymbol owner = t;
        while (owner.ContainingType != null)
        {
            owner = owner.ContainingType;
        }

        bool HasMethod(string n, int args) => t.GetMembers(n).OfType<IMethodSymbol>().Any(m => !m.IsStatic && m.Parameters.Length == args);
        bool hasSaveExtra = HasMethod("SaveExtra", 1);
        bool hasLoadExtra = HasMethod("LoadExtra", 1);
        bool extra = hasSaveExtra || hasLoadExtra;
        string hooks = HookSignature(t, c);
        layouts.AppendLine($"        new global::VitaSharp.Core.SaveState.SnapshotLayout(\"{Escape(key)}\", \"{Escape(owner.Name)}\", \"{Escape(hooks)}\",");
        layouts.AppendLine($"            new global::VitaSharp.Core.SaveState.SnapshotField[] {{ {string.Join(", ", fieldEntries)} }}),");

        var load = new StringBuilder();
        if (HasMethod("BeforeSnapshotLoad", 0))
        {
            load.AppendLine("        BeforeSnapshotLoad();");
        }

        load.AppendLine($"        string[] saved = reader.SavedFields(\"{Escape(key)}\", SnapshotFieldNames);");
        if (names.Count > 0)
        {
            load.AppendLine($"        bool[] found = new bool[{names.Count}];");
        }

        load.AppendLine("        foreach (string name in saved)");
        load.AppendLine("        {");
        if (names.Count > 0)
        {
            load.AppendLine("            global::VitaSharp.Core.SaveState.SnapshotReader r = reader.FieldReader();");
            load.AppendLine("            switch (name)");
            load.AppendLine("            {");
            load.Append(cases);
            load.AppendLine("                default:");
            load.AppendLine("                    break; // a field this build no longer has");
            load.AppendLine("            }");
        }
        else
        {
            load.AppendLine("            reader.FieldReader(); // a field this build no longer has");
        }

        load.AppendLine("        }");
        load.Append(missing);
        if (hasLoadExtra)
        {
            load.AppendLine("        LoadExtra(reader.FieldReader());");
        }
        else if (extra)
        {
            load.AppendLine("        reader.FieldReader();");
        }

        if (HasMethod("AfterSnapshotLoad", 0))
        {
            load.AppendLine("        AfterSnapshotLoad();");
        }

        var saveAll = new StringBuilder();
        saveAll.AppendLine($"        w.UseLayout(\"{Escape(key)}\");");
        if (names.Count > 0 || extra)
        {
            saveAll.AppendLine("        int at;");
        }

        saveAll.Append(save);
        if (extra)
        {
            saveAll.AppendLine("        at = w.BeginBlock();");
            if (hasSaveExtra)
            {
                saveAll.AppendLine("        SaveExtra(w);");
            }

            saveAll.AppendLine("        w.EndBlock(at);");
        }

        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/> by VitaSharp.Core.Generators.SnapshotGenerator");
        sb.AppendLine("#nullable disable");
        sb.AppendLine("#pragma warning disable CS0618");
        if (!t.ContainingNamespace.IsGlobalNamespace)
        {
            sb.AppendLine($"namespace {t.ContainingNamespace.ToDisplayString()};");
        }

        var chain = new List<INamedTypeSymbol>();
        for (INamedTypeSymbol x = t; x != null; x = x.ContainingType)
        {
            chain.Insert(0, x);
        }

        foreach (INamedTypeSymbol x in chain)
        {
            string kind = x.TypeKind == TypeKind.Struct ? (x.IsRecord ? "record struct" : "struct") : (x.IsRecord ? "record" : "class");
            string unsafeMod = x.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax d && d.Modifiers.Any(m => m.Text == "unsafe")) ? "unsafe " : "";
            string iface = SymbolEqualityComparer.Default.Equals(x, t) ? " : global::VitaSharp.Core.SaveState.ISnapshotState" : "";
            sb.AppendLine($"{unsafeMod}partial {kind} {x.Name}{TypeParams(x)}{iface}");
            sb.AppendLine("{");
        }
        sb.AppendLine("    private static readonly string[] SnapshotFieldNames = { " + string.Join(", ", names.Select(n => $"\"{Escape(n)}\"")) + " };");
        sb.AppendLine("    public void SaveState(global::VitaSharp.Core.SaveState.SnapshotWriter w)");
        sb.AppendLine("    {");
        sb.Append(saveAll);
        sb.AppendLine("    }");
        sb.AppendLine("    public void LoadState(global::VitaSharp.Core.SaveState.SnapshotReader reader)");
        sb.AppendLine("    {");
        sb.Append(load);
        sb.AppendLine("    }");
        foreach (INamedTypeSymbol _ in chain)
        {
            sb.AppendLine("}");
        }

        return sb.ToString();
    }

    /// <summary>The value of a field the saved data does not have (<c>[SnapshotMissing]</c>): the attribute's constant,
    /// or the empty value (collections and arrays emptied, a readonly nested state kept as constructed, else default).</summary>
    private static void EmitMissing(StringBuilder sb, ITypeSymbol t, string e, bool ro, AttributeData attr, Ctx c, string ind)
    {
        if (attr.ConstructorArguments.Length == 1 && !ro)
        {
            TypedConstant value = attr.ConstructorArguments[0];
            if (value.IsNull)
            {
                sb.AppendLine($"{ind}{e} = default;");
            }
            else
            {
                sb.AppendLine($"{ind}{e} = ({Fq(t)})({Literal(value)});");
            }

            return;
        }

        if (CollectionKind(t, out _) != Coll.None)
        {
            sb.AppendLine($"{ind}{e}?.Clear();");
            return;
        }

        if (t is IArrayTypeSymbol)
        {
            sb.AppendLine($"{ind}if ({e} != null)");
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{ind}    global::System.Array.Clear({e});");
            sb.AppendLine($"{ind}}}");
            return;
        }

        if (ro)
        {
            sb.AppendLine($"{ind}// kept as constructed");
            return;
        }

        sb.AppendLine($"{ind}{e} = default;");
    }

    /// <summary>An attribute constant as C# source (numbers with their type's suffix, enums through their value).</summary>
    private static string Literal(TypedConstant value)
    {
        object? v = value.Value;
        string text = v switch
        {
            bool b => b ? "true" : "false",
            string str => $"\"{Escape(str)}\"",
            char ch => $"(char){(int)ch}",
            float f => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
            double d => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d",
            long l => l.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L",
            ulong u => u.ToString(System.Globalization.CultureInfo.InvariantCulture) + "UL",
            uint u => u.ToString(System.Globalization.CultureInfo.InvariantCulture) + "U",
            System.IFormattable n => n.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => "default",
        };
        return value.Kind == TypedConstantKind.Enum && value.Type != null ? $"({Fq(value.Type)})({text})" : text;
    }

    /// <summary>
    /// The shape of a field's data as the generated code writes it, without names (a rename of the field, of a local
    /// or of a type that is not itself saved does not change it): primitives by kind, enums by their underlying type,
    /// nested states by their layout key (checked as their own layouts), blittable structs by their fields' shapes.
    /// </summary>
    private static string TypeSignature(ITypeSymbol t, Ctx c, int depth)
    {
        if (depth > 8)
        {
            return "?";
        }

        if (IsPrimitive(t))
        {
            return t.SpecialType.ToString().Replace("System_", "");
        }

        if (t.SpecialType == SpecialType.System_String)
        {
            return "String";
        }

        if (t is INamedTypeSymbol { TypeKind: TypeKind.Enum } en)
        {
            return $"enum({TypeSignature(en.EnumUnderlyingType!, c, depth + 1)})";
        }

        if (IsNullableValue(t, out ITypeSymbol? inner))
        {
            return $"opt({TypeSignature(inner, c, depth + 1)})";
        }

        if (Derives(t, c.KernelObject))
        {
            return "kobj";
        }

        if (IsState(t, c))
        {
            return $"state({t.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString()})";
        }

        if (t.IsTupleType && t is INamedTypeSymbol tuple)
        {
            return "(" + string.Join(",", tuple.TupleElements.Select(x => TypeSignature(x.Type, c, depth + 1))) + ")";
        }

        if (IsBlittableStruct(t))
        {
            return "blit(" + string.Join(",", t.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).Select(f => TypeSignature(f.Type, c, depth + 1))) + ")";
        }

        if (t is IArrayTypeSymbol array)
        {
            return $"arr({TypeSignature(array.ElementType, c, depth + 1)})";
        }

        Coll kind = CollectionKind(t, out ITypeSymbol[]? args);
        if (kind != Coll.None)
        {
            return kind.ToString().ToLowerInvariant() + "(" + string.Join(",", args.Select(x => TypeSignature(x, c, depth + 1))) + ")";
        }

        return t.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString();
    }

    /// <summary>
    /// A signature of the type's hand-written data (SaveExtra/LoadExtra): the snapshot reader and writer calls they make,
    /// with their parameter shapes, in the order and control flow they are made (also inside the methods of the build
    /// they hand the reader or writer to; branches and loops without such a call are left out). Names, comments, braces
    /// and layout are not part of it, so a rename or a reformat keeps old states loadable; a call added, removed or
    /// moved changes it.
    /// </summary>
    private static string HookSignature(INamedTypeSymbol t, Ctx c)
    {
        var sb = new StringBuilder();
        var visited = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (string hook in new[] { "SaveExtra", "LoadExtra" })
        {
            foreach (IMethodSymbol m in t.GetMembers(hook).OfType<IMethodSymbol>().Where(m => !m.IsStatic && m.Parameters.Length == 1))
            {
                sb.Append(hook).Append('{');
                WalkMethod(m, c, sb, visited, 0);
                sb.Append('}');
            }
        }

        return sb.ToString();
    }

    private static void WalkMethod(IMethodSymbol m, Ctx c, StringBuilder sb, HashSet<IMethodSymbol> visited, int depth)
    {
        foreach (SyntaxReference r in m.DeclaringSyntaxReferences)
        {
            SyntaxNode node = r.GetSyntax();
            SemanticModel model = c.C.GetSemanticModel(node.SyntaxTree);
            SyntaxNode? body = node switch
            {
                BaseMethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody,
                LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody,
                _ => node,
            };
            if (body != null)
            {
                Walk(body, model, c, sb, visited, depth);
            }
        }
    }

    private static bool IsSnapshotIo(ITypeSymbol? t, Ctx c)
    {
        return t != null && (SymbolEqualityComparer.Default.Equals(t, c.Writer) || SymbolEqualityComparer.Default.Equals(t, c.Reader));
    }

    private static void Walk(SyntaxNode node, SemanticModel model, Ctx c, StringBuilder sb, HashSet<IMethodSymbol> visited, int depth)
    {
        string? open = node switch
        {
            IfStatementSyntax => "if(",
            ElseClauseSyntax => "else(",
            ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax or WhileStatementSyntax or DoStatementSyntax => "loop(",
            SwitchStatementSyntax or SwitchExpressionSyntax => "switch(",
            SwitchSectionSyntax or SwitchExpressionArmSyntax => "case(",
            ConditionalExpressionSyntax => "?:(",
            ConditionalAccessExpressionSyntax => "?.(",
            _ => null,
        };
        if (node is ReturnStatementSyntax)
        {
            sb.Append("ret ");
        }
        else if (node is BreakStatementSyntax)
        {
            sb.Append("break ");
        }
        else if (node is ContinueStatementSyntax)
        {
            sb.Append("continue ");
        }

        int start = sb.Length;
        if (open != null)
        {
            sb.Append(open);
        }

        foreach (SyntaxNode child in node.ChildNodes())
        {
            Walk(child, model, c, sb, visited, depth);
        }

        if (node is InvocationExpressionSyntax invocation)
        {
            SymbolInfo info = model.GetSymbolInfo(invocation);
            IMethodSymbol? target = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (target != null)
            {
                Call(invocation, target, model, c, sb, visited, depth);
            }
        }

        if (open != null)
        {
            if (sb.Length == start + open.Length)
            {
                sb.Length = start; // no snapshot data inside: the construct does not shape the data
            }
            else
            {
                sb.Append(") ");
            }
        }
    }

    private static void Call(InvocationExpressionSyntax invocation, IMethodSymbol target, SemanticModel model, Ctx c, StringBuilder sb,
        HashSet<IMethodSymbol> visited, int depth)
    {
        if (IsSnapshotIo(target.ContainingType, c))
        {
            sb.Append(target.ContainingType.Name).Append('.').Append(target.Name);
            if (target.TypeArguments.Length > 0)
            {
                sb.Append('<').Append(string.Join(",", target.TypeArguments.Select(x => TypeSignature(x, c, 0)))).Append('>');
            }

            sb.Append('(').Append(string.Join(",", target.Parameters.Select(p => TypeSignature(p.Type, c, 0)))).Append(") ");
            return;
        }

        bool passesIo = invocation.ArgumentList.Arguments.Any(a => IsSnapshotIo(model.GetTypeInfo(a.Expression).Type, c))
            || (target.ReducedFrom != null && IsSnapshotIo(target.ReceiverType, c));
        if (!passesIo)
        {
            return;
        }

        if (target.Name is "SaveState" or "LoadState" || target.MethodKind == MethodKind.LocalFunction)
        {
            // a nested state (its own layout), or a local function (walked where it is declared)
            sb.Append(target.Name).Append(' ');
            return;
        }

        IMethodSymbol definition = (target.ReducedFrom ?? target).OriginalDefinition;
        if (definition.DeclaringSyntaxReferences.Length == 0 || depth > 6 || !visited.Add(definition))
        {
            sb.Append("call ");
            return;
        }

        sb.Append("call{");
        WalkMethod(definition, c, sb, visited, depth + 1);
        sb.Append("} ");
    }

    private static string TypeParams(INamedTypeSymbol t) => t.TypeParameters.Length == 0 ? "" : "<" + string.Join(", ", t.TypeParameters.Select(p => p.Name)) + ">";

    private static string Fq(ITypeSymbol t) => t.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private enum Coll
    {
        None, List, Set, Queue, Dict
    }

    private static Coll CollectionKind(ITypeSymbol t, out ITypeSymbol[] args)
    {
        args = [];
        if (t is not INamedTypeSymbol n || !n.IsGenericType)
        {
            return Coll.None;
        }

        string def = n.ConstructedFrom.ToDisplayString();
        args = n.TypeArguments.ToArray();
        return def switch
        {
            "System.Collections.Generic.List<T>" => Coll.List,
            "System.Collections.Generic.HashSet<T>" => Coll.Set,
            "System.Collections.Generic.Queue<T>" => Coll.Queue,
            "System.Collections.Generic.Dictionary<TKey, TValue>" or "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>"
                or "System.Collections.Generic.SortedDictionary<TKey, TValue>" => Coll.Dict,
            _ => Coll.None,
        };
    }

    private static bool IsNullableValue(ITypeSymbol t, out ITypeSymbol inner)
    {
        inner = t;
        if (t is INamedTypeSymbol { IsGenericType: true } n && n.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            inner = n.TypeArguments[0];
            return true;
        }
        return false;
    }

    private static bool IsPrimitive(ITypeSymbol t) => t.SpecialType is SpecialType.System_Boolean or SpecialType.System_Byte or SpecialType.System_SByte
        or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Char or SpecialType.System_Int32 or SpecialType.System_UInt32
        or SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_IntPtr;

    private static string ReadPrim(ITypeSymbol t) => t.SpecialType switch
    {
        SpecialType.System_Boolean => "ReadBool",
        SpecialType.System_Byte => "ReadByte",
        SpecialType.System_SByte => "ReadSByte",
        SpecialType.System_Int16 => "ReadInt16",
        SpecialType.System_UInt16 => "ReadUInt16",
        SpecialType.System_Char => "ReadChar",
        SpecialType.System_Int32 => "ReadInt32",
        SpecialType.System_UInt32 => "ReadUInt32",
        SpecialType.System_Int64 => "ReadInt64",
        SpecialType.System_UInt64 => "ReadUInt64",
        SpecialType.System_Single => "ReadSingle",
        SpecialType.System_Double => "ReadDouble",
        _ => "ReadIntPtr",
    };

    /// <summary>A field holding one of the emulator's HLE modules that is itself saved state (a cached reference).</summary>
    private static bool IsModuleReference(ITypeSymbol t, Ctx c) => c.Module != null && Derives(t, c.Module) && IsState(t, c);

    private static bool IsState(ITypeSymbol t, Ctx c) => t.TypeKind == TypeKind.Class && (Implements(t, c.State) || HasAttr(t, c.Attr));

    /// <summary>Unmanaged structs without pointers or host handles, written as raw bytes.</summary>
    private static bool IsBlittableStruct(ITypeSymbol t) => t.TypeKind == TypeKind.Struct && t.IsUnmanagedType && !t.IsTupleType && !IsPrimitive(t)
        && t.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic).All(f => f.Type.TypeKind != TypeKind.Pointer && f.Type.SpecialType != SpecialType.System_IntPtr && (f.Type.TypeKind != TypeKind.Struct || IsPrimitive(f.Type) || f.Type.TypeKind == TypeKind.Enum || IsBlittableStruct(f.Type)));

    private static bool Supported(ITypeSymbol t, Ctx c, int depth)
    {
        if (depth > 6)
        {
            return false;
        }

        if (IsPrimitive(t) || t.TypeKind == TypeKind.Enum || t.SpecialType == SpecialType.System_String)
        {
            return true;
        }

        if (IsNullableValue(t, out ITypeSymbol? inner))
        {
            return Supported(inner, c, depth + 1);
        }

        if (t is IArrayTypeSymbol { Rank: 1 } a)
        {
            return Supported(a.ElementType, c, depth + 1);
        }

        if (t.IsTupleType && t is INamedTypeSymbol tt)
        {
            return tt.TupleElements.All(e => Supported(e.Type, c, depth + 1));
        }

        if (Derives(t, c.KernelObject))
        {
            return true;
        }

        if (IsState(t, c))
        {
            return true;
        }

        if (IsBlittableStruct(t))
        {
            return true;
        }

        if (CollectionKind(t, out ITypeSymbol[]? args) != Coll.None)
        {
            return args.All(x => Supported(x, c, depth + 1));
        }

        return false;
    }

    /// <summary>Readonly fields that still carry state: collections and nested states (filled in place on load).</summary>
    private static bool IsMutableInPlace(ITypeSymbol t, Ctx c) => t is IArrayTypeSymbol || CollectionKind(t, out _) != Coll.None || IsState(t, c);

    private static void EmitWrite(StringBuilder sb, ITypeSymbol t, string e, int d, Ctx c, string ind)
    {
        if (IsPrimitive(t) || t.SpecialType == SpecialType.System_String)
        {
            sb.AppendLine($"{ind}w.Write({e});");
            return;
        }
        if (t is INamedTypeSymbol { TypeKind: TypeKind.Enum } en)
        {
            sb.AppendLine($"{ind}w.Write(({Fq(en.EnumUnderlyingType!)}){e});");
            return;
        }
        if (IsNullableValue(t, out ITypeSymbol? inner))
        {
            sb.AppendLine($"{ind}w.Write({e}.HasValue);");
            sb.AppendLine($"{ind}if ({e}.HasValue) {{");
            EmitWrite(sb, inner, $"{e}.Value", d + 1, c, ind + "    ");
            sb.AppendLine($"{ind}}}");
            return;
        }
        if (Derives(t, c.KernelObject))
        {
            sb.AppendLine($"{ind}w.WriteKernelObject({e});");
            return;
        }
        if (IsState(t, c))
        {
            sb.AppendLine($"{ind}w.Write((global::VitaSharp.Core.SaveState.ISnapshotState){e});");
            return;
        }
        if (t.IsTupleType && t is INamedTypeSymbol tt)
        {
            int i = 1;
            foreach (IFieldSymbol el in tt.TupleElements)
            {
                EmitWrite(sb, el.Type, $"{e}.Item{i++}", d + 1, c, ind);
            }

            return;
        }
        if (IsBlittableStruct(t))
        {
            sb.AppendLine($"{ind}w.WriteStruct({e});");
            return;
        }
        string x = $"x{d}";
        if (t is IArrayTypeSymbol a)
        {
            if (IsPrimitive(a.ElementType) || IsBlittableStruct(a.ElementType))
            {
                sb.AppendLine($"{ind}w.WriteArray({e});");
                return;
            }
            sb.AppendLine($"{ind}w.Write({e} == null ? -1 : {e}.Length);");
            sb.AppendLine($"{ind}if ({e} != null) foreach (var {x} in {e}) {{");
            EmitWrite(sb, a.ElementType, x, d + 1, c, ind + "    ");
            sb.AppendLine($"{ind}}}");
            return;
        }
        Coll kind = CollectionKind(t, out ITypeSymbol[]? args);
        sb.AppendLine($"{ind}w.Write({e} == null ? -1 : {e}.Count);");
        sb.AppendLine($"{ind}if ({e} != null) foreach (var {x} in {e}) {{");
        if (kind == Coll.Dict)
        {
            EmitWrite(sb, args[0], $"{x}.Key", d + 1, c, ind + "    ");
            EmitWrite(sb, args[1], $"{x}.Value", d + 1, c, ind + "    ");
        }
        else
        {
            EmitWrite(sb, args[0], x, d + 1, c, ind + "    ");
        }

        sb.AppendLine($"{ind}}}");
    }

    /// <summary>Appends statements that read a value of <paramref name="t"/> into a new variable; returns its name.
    /// <paramref name="existing"/> (a field) is reused for collections so their comparers stay.</summary>
    private static string EmitRead(StringBuilder sb, ITypeSymbol t, int d, Ctx c, string ind, string? existing = null)
    {
        int id = c.N++;
        string v = $"v{id}";
        string ft = Fq(t);
        if (IsPrimitive(t))
        {
            sb.AppendLine($"{ind}var {v} = r.{ReadPrim(t)}();");
            return v;
        }
        if (t.SpecialType == SpecialType.System_String)
        {
            sb.AppendLine($"{ind}var {v} = r.ReadString();");
            return v;
        }
        if (t is INamedTypeSymbol { TypeKind: TypeKind.Enum } en)
        {
            sb.AppendLine($"{ind}var {v} = ({ft})r.{ReadPrim(en.EnumUnderlyingType!)}();");
            return v;
        }
        if (IsNullableValue(t, out ITypeSymbol? inner))
        {
            sb.AppendLine($"{ind}{Fq(inner)}? {v} = null;");
            sb.AppendLine($"{ind}if (r.ReadBool()) {{");
            string iv = EmitRead(sb, inner, d + 1, c, ind + "    ");
            sb.AppendLine($"{ind}    {v} = {iv};");
            sb.AppendLine($"{ind}}}");
            return v;
        }
        if (Derives(t, c.KernelObject))
        {
            sb.AppendLine($"{ind}var {v} = r.ReadKernelObject<{ft}>();");
            return v;
        }
        if (IsState(t, c))
        {
            sb.AppendLine($"{ind}var {v} = r.ReadState(() => new {ft}());");
            return v;
        }
        if (t.IsTupleType && t is INamedTypeSymbol tt)
        {
            var parts = new List<string>();
            foreach (IFieldSymbol el in tt.TupleElements)
            {
                parts.Add(EmitRead(sb, el.Type, d + 1, c, ind));
            }

            sb.AppendLine($"{ind}{ft} {v} = ({string.Join(", ", parts)});");
            return v;
        }
        if (IsBlittableStruct(t))
        {
            sb.AppendLine($"{ind}var {v} = r.ReadStruct<{ft}>();");
            return v;
        }
        string n = $"n{id}", i2 = $"i{id}";
        if (t is IArrayTypeSymbol a)
        {
            string et = Fq(a.ElementType);
            if (IsPrimitive(a.ElementType) || IsBlittableStruct(a.ElementType))
            {
                sb.AppendLine($"{ind}var {v} = r.ReadArray<{et}>();");
                return v;
            }
            sb.AppendLine($"{ind}{ft} {v} = null;");
            sb.AppendLine($"{ind}int {n} = r.ReadInt32();");
            sb.AppendLine($"{ind}if ({n} >= 0) {{");
            sb.AppendLine($"{ind}    {v} = new {ArrayNew(a, n)};");
            sb.AppendLine($"{ind}    for (int {i2} = 0; {i2} < {n}; {i2}++) {{");
            string ev = EmitRead(sb, a.ElementType, d + 1, c, ind + "        ");
            sb.AppendLine($"{ind}        {v}[{i2}] = {ev};");
            sb.AppendLine($"{ind}    }}");
            sb.AppendLine($"{ind}}}");
            return v;
        }
        Coll kind = CollectionKind(t, out ITypeSymbol[]? args);
        sb.AppendLine($"{ind}{ft} {v} = null;");
        sb.AppendLine($"{ind}int {n} = r.ReadInt32();");
        sb.AppendLine($"{ind}if ({n} >= 0) {{");
        sb.AppendLine(existing != null ? $"{ind}    {v} = {existing} ?? new {ft}(); {v}.Clear();" : $"{ind}    {v} = new {ft}();");
        sb.AppendLine($"{ind}    for (int {i2} = 0; {i2} < {n}; {i2}++) {{");
        if (kind == Coll.Dict)
        {
            string k = EmitRead(sb, args[0], d + 1, c, ind + "        ");
            string val = EmitRead(sb, args[1], d + 1, c, ind + "        ");
            sb.AppendLine($"{ind}        {v}[{k}] = {val};");
        }
        else
        {
            string ev = EmitRead(sb, args[0], d + 1, c, ind + "        ");
            sb.AppendLine($"{ind}        {v}.{(kind == Coll.Queue ? "Enqueue" : "Add")}({ev});");
        }
        sb.AppendLine($"{ind}    }}");
        sb.AppendLine($"{ind}}}");
        return v;
    }

    /// <summary>"T[n]" for an array type, also for arrays of arrays ("T[n][]").</summary>
    private static string ArrayNew(IArrayTypeSymbol a, string n)
    {
        ITypeSymbol e = a.ElementType;
        string suffix = "";
        while (e is IArrayTypeSymbol inner)
        {
            suffix += "[]";
            e = inner.ElementType;
        }
        return $"{Fq(e)}[{n}]{suffix}";
    }

    /// <summary>Readonly collection / array / state fields: filled in place (their identity and comparers stay).</summary>
    private static void EmitReadInPlace(StringBuilder sb, ITypeSymbol t, string e, Ctx c, string ind)
    {
        if (IsState(t, c))
        {
            sb.AppendLine($"{ind}r.ReadInto({e});");
            return;
        }
        sb.AppendLine($"{ind}{{");
        if (t is IArrayTypeSymbol)
        {
            string v = EmitRead(sb, t, 0, c, ind + "    ");
            sb.AppendLine($"{ind}    if ({v} != null && {e} != null) global::System.Array.Copy({v}, {e}, global::System.Math.Min({v}.Length, {e}.Length));");
        }
        else
        {
            EmitRead(sb, t, 0, c, ind + "    ", e);
        }

        sb.AppendLine($"{ind}}}");
    }
}
