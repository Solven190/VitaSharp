using System.Reflection;
using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

/// <summary>
/// Format 1 states (positional, before 13/11/2026) of the builds that saved the states on the test phone, loaded through
/// the layout tables generated from each build's sources (src/VitaSharp.Core/SaveState/Legacy). Every field added since
/// declares the value the older build behaved as ([SnapshotMissing]), so each build's states are accepted, and a
/// synthetic state of each build, written field by field from its table, loads into this build's types.
/// </summary>
public sealed class SaveStateLegacyTests
{
    /// <summary>The schema hashes of the format 1 states kept on the phone (13/11/2026), by the build that saved them.</summary>
    public static TheoryData<string, string> Builds => new()
    {
        { "3-c92fb0fbc64b2619", "5d2597ec (main of 13/11, also the lab APK 2e965ad6)" },
        { "3-5562ab7937189cf5", "c41f7846 (09/11 21:12)" },
        { "3-60ce916eb1ac4609", "901f2945 (09/11 17:23), f010c45d" },
        { "3-39afabc577527100", "9f216530 (09/11 01:14), ebfe72c3" },
    };

    /// <summary>Older builds whose hand-written video decoder data was different (Videodec added its picture queue
    /// on 08/11): no value of this build stands for what they saved, so their states stay refused, naming a module.</summary>
    public static TheoryData<string, string> RefusedBuilds => new()
    {
        { "3-05401a240e335be4", "282ec7d9 (08/11)" },
        { "3-55c109d1d8784688", "4a8bb94f (06/11)" },
    };

    [Theory]
    [MemberData(nameof(RefusedBuilds))]
    public void StatesOfBuildsWithDifferentHandWrittenDataAreRefusedByModule(string schema, string build)
    {
        Dictionary<string, SnapshotLayout>? layouts = SnapshotLayouts.Legacy(schema);
        Assert.True(layouts != null, $"no layout table for {build}");
        string? why = SnapshotLayouts.Incompatibility(layouts!, positional: true);
        Assert.NotNull(why);
        Assert.StartsWith("module ", why);
    }

    [Theory]
    [MemberData(nameof(Builds))]
    public void StatesOfTheBuildAreAccepted(string schema, string build)
    {
        Dictionary<string, SnapshotLayout>? layouts = SnapshotLayouts.Legacy(schema);
        Assert.True(layouts != null, $"no layout table for {build}");
        Assert.Null(SnapshotLayouts.Incompatibility(layouts!, positional: true));
        var header = new SaveStateHeader { FormatVersion = 1, Schema = schema, TitleId = "PCSC00045", EmulatorVersion = "1.0.0+" + build[..8] };
        Assert.True(SaveStateFile.IsLegacy(header));
        Assert.Null(SaveStateFile.Incompatibility(header, "PCSC00045"));
    }

    [Fact]
    public void FormatOneOfAnUnknownBuildIsRefused()
    {
        var header = new SaveStateHeader { FormatVersion = 1, Schema = "3-0123456789abcdef", TitleId = "PCSC00045", EmulatorVersion = "1.0.0+0123456" };
        Assert.False(SaveStateFile.IsLegacy(header));
        Assert.Equal(VitaSharp.Core.Localization.Loc.Format("StateOtherFormat", "1.0.0+0123456", 1, SaveStateFile.FormatVersion), SaveStateFile.Incompatibility(header, "PCSC00045"));
        Assert.Null(SnapshotLayouts.Legacy("../nids"));
    }

    /// <summary>A synthetic state of the build for each type whose data is only generated fields (no hand-written part,
    /// no load hooks that need a running emulator): every saved field written in the saved order, positionally, with an
    /// empty value of its shape; it loads into this build's type and is read to its end.</summary>
    [Theory]
    [MemberData(nameof(Builds))]
    public void SyntheticStateOfTheBuildLoads(string schema, string build)
    {
        Dictionary<string, SnapshotLayout> layouts = SnapshotLayouts.Legacy(schema)!;
        Assembly core = typeof(SnapshotLayouts).Assembly;
        int loaded = 0;
        foreach (SnapshotLayout old in layouts.Values)
        {
            Type? type = core.GetType(ClrName(old.Key));
            if (type == null || type.IsAbstract || type.ContainsGenericParameters || old.HookSignature.Length > 0 || HasLoadHooks(type)
                || type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) == null)
            {
                continue;
            }

            var w = new SnapshotWriter();
            bool writable = true;
            foreach (SnapshotField field in old.Fields)
            {
                writable &= WriteEmpty(w, field.Signature);
            }

            if (!writable)
            {
                continue;
            }

            var state = (ISnapshotState)Activator.CreateInstance(type, nonPublic: true)!;
            var reader = new SnapshotReader(w.Data.ToArray()) { Layouts = layouts, Positional = true };
            state.LoadState(reader);
            Assert.True(reader.AtEnd, $"{build}: {old.Key} left {reader.Remaining} bytes");
            loaded++;
        }

        Assert.True(loaded >= 10, $"{build}: only {loaded} types could be checked");
    }

    private static bool HasLoadHooks(Type type)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        return type.GetMethod("BeforeSnapshotLoad", Any) != null || type.GetMethod("AfterSnapshotLoad", Any) != null;
    }

    /// <summary>"A.B.Outer.Inner" (a layout key) as the CLR's "A.B.Outer+Inner".</summary>
    private static string ClrName(string key)
    {
        string[] parts = key.Split('.');
        Assembly core = typeof(SnapshotLayouts).Assembly;
        for (int split = parts.Length; split > 0; split--)
        {
            string outer = string.Join('.', parts[..split]);
            if (core.GetType(outer) != null)
            {
                return split == parts.Length ? outer : outer + "+" + string.Join('+', parts[split..]);
            }
        }

        return key;
    }

    /// <summary>Writes the empty value of a field shape as the generator encodes it (null strings, collections and
    /// states, zero numbers); false for a shape whose size this test cannot know (a blittable struct's padding).</summary>
    private static bool WriteEmpty(SnapshotWriter w, string signature)
    {
        int size = signature switch
        {
            "Boolean" or "Byte" or "SByte" => 1,
            "Int16" or "UInt16" or "Char" => 2,
            "Int32" or "UInt32" or "Single" or "kobj" => 4,
            "Int64" or "UInt64" or "Double" or "IntPtr" => 8,
            _ => 0,
        };
        if (size > 0)
        {
            w.WriteRaw(new byte[size]);
            return true;
        }

        if (signature == "String" || signature.StartsWith("arr(", StringComparison.Ordinal) || signature.StartsWith("list(", StringComparison.Ordinal)
            || signature.StartsWith("set(", StringComparison.Ordinal) || signature.StartsWith("queue(", StringComparison.Ordinal)
            || signature.StartsWith("dict(", StringComparison.Ordinal))
        {
            w.Write(-1);
            return true;
        }

        if (signature.StartsWith("opt(", StringComparison.Ordinal) || signature.StartsWith("state(", StringComparison.Ordinal))
        {
            w.Write(false);
            return true;
        }

        if (signature.StartsWith("enum(", StringComparison.Ordinal))
        {
            return WriteEmpty(w, signature[5..^1]);
        }

        if (signature.StartsWith('('))
        {
            foreach (string part in SplitTop(signature[1..^1]))
            {
                if (!WriteEmpty(w, part))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    private static IEnumerable<string> SplitTop(string list)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i] == '(')
            {
                depth++;
            }
            else if (list[i] == ')')
            {
                depth--;
            }
            else if (list[i] == ',' && depth == 0)
            {
                yield return list[start..i];
                start = i + 1;
            }
        }

        yield return list[start..];
    }
}
