using System.Collections.Concurrent;

namespace VitaSharp.Core.SaveState;

/// <summary>
/// One field of a <see cref="SnapshotLayout"/>: its name, the shape of its data (<see cref="Signature"/>: how the
/// generator encodes the field's type, without names), whether this build declares the value an older state without
/// the field had (<see cref="SnapshotMissingAttribute"/>) and the names it had before (<see cref="SnapshotAliasAttribute"/>).
/// </summary>
public sealed record SnapshotField(string Name, string Signature, bool HasMissingValue = false, string[]? Aliases = null)
{
    public bool Answers(string name)
    {
        return Name == name || (Aliases != null && Array.IndexOf(Aliases, name) >= 0);
    }
}

/// <summary>
/// The saved shape of a [GenerateSnapshot] type, written in every state for the types the state holds (section
/// <c>layouts</c>): its fields in the order they were written and a signature of its hand-written hooks
/// (SaveExtra/LoadExtra: the snapshot calls they make and their control flow, so a rename or a reformat does not change
/// it). A load maps the saved fields to this build's by name, so states of other builds load as long as every type they
/// hold is compatible (<see cref="SnapshotLayouts.Incompatibility"/>), as PPSSPP's versioned DoState sections do.
/// </summary>
public sealed record SnapshotLayout(string Key, string Owner, string HookSignature, SnapshotField[] Fields)
{
    private string[]? _names;

    /// <summary>The field names in the order the data has them.</summary>
    public string[] FieldNames => _names ??= Array.ConvertAll(Fields, f => f.Name);

    internal void Write(SnapshotWriter w)
    {
        w.Write(Key);
        w.Write(Owner);
        w.Write(HookSignature);
        w.Write(Fields.Length);
        foreach (SnapshotField field in Fields)
        {
            w.Write(field.Name);
            w.Write(field.Signature);
        }
    }

    internal static SnapshotLayout Read(SnapshotReader r)
    {
        string key = r.ReadStringNotNull();
        string owner = r.ReadStringNotNull();
        string hooks = r.ReadStringNotNull();
        int count = r.ReadInt32();
        if (count is < 0 or > 4096)
        {
            throw new SnapshotException("the save state's layout table is corrupt");
        }

        var fields = new SnapshotField[count];
        for (int i = 0; i < count; i++)
        {
            fields[i] = new SnapshotField(r.ReadStringNotNull(), r.ReadStringNotNull());
        }

        return new SnapshotLayout(key, owner, hooks, fields);
    }
}

/// <summary>The layouts of this build's [GenerateSnapshot] types (registered by the generated code of each assembly)
/// and the check of a state's saved layouts against them.</summary>
public static class SnapshotLayouts
{
    private static readonly ConcurrentDictionary<string, SnapshotLayout> s_current = new(StringComparer.Ordinal);

    /// <summary>Called by the generated module initializer of every assembly with [GenerateSnapshot] types.</summary>
    public static void Register(SnapshotLayout[] layouts)
    {
        foreach (SnapshotLayout layout in layouts)
        {
            s_current[layout.Key] = layout;
        }
    }

    public static SnapshotLayout? Current(string key)
    {
        return s_current.GetValueOrDefault(key);
    }

    /// <summary>Writes the layouts of <paramref name="keys"/> (the types a state holds).</summary>
    public static void Write(SnapshotWriter w, IEnumerable<string> keys)
    {
        var layouts = keys.Select(Current).Where(l => l != null).OrderBy(l => l!.Key, StringComparer.Ordinal).ToList();
        w.Write(layouts.Count);
        foreach (SnapshotLayout? layout in layouts)
        {
            layout!.Write(w);
        }
    }

    /// <summary>
    /// The layouts of a build that saved format 1 (positional, before 13/11/2026), by its schema hash: computed from
    /// that build's sources with this generator (SaveState/Legacy/&lt;hash&gt;.txt, one type per line: key, owner, hook
    /// signature, then name=shape per field, tab separated). Null for a build without a table.
    /// </summary>
    public static Dictionary<string, SnapshotLayout>? Legacy(string schema)
    {
        if (schema.IndexOfAny(['/', '\\', '.']) >= 0)
        {
            return null;
        }

        using Stream? stream = typeof(SnapshotLayouts).Assembly.GetManifestResourceStream($"legacy-layouts/{schema}.txt");
        if (stream == null)
        {
            return null;
        }

        using var text = new StreamReader(stream);
        var layouts = new Dictionary<string, SnapshotLayout>(StringComparer.Ordinal);
        while (text.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            var fields = new SnapshotField[parts.Length - 3];
            for (int i = 3; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                fields[i - 3] = new SnapshotField(parts[i][..eq], parts[i][(eq + 1)..]);
            }

            layouts[parts[0]] = new SnapshotLayout(parts[0], parts[1], parts[2], fields);
        }

        return layouts;
    }

    public static Dictionary<string, SnapshotLayout> Read(SnapshotReader r)
    {
        int count = r.ReadInt32();
        if (count is < 0 or > 1 << 16)
        {
            throw new SnapshotException("the save state's layout table is corrupt");
        }

        var layouts = new Dictionary<string, SnapshotLayout>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var layout = SnapshotLayout.Read(r);
            layouts[layout.Key] = layout;
        }

        return layouts;
    }

    /// <summary>
    /// Why data with the <paramref name="saved"/> layouts cannot be loaded into this build, or null. A saved type is
    /// compatible when its hooks are the same and each of this build's fields is either in the saved data with the
    /// same shape (by name or alias) or declares the value states without it had (<see cref="SnapshotMissingAttribute"/>).
    /// Fields only the saved data has are dropped. Types this build no longer has are not checked: the data of a type
    /// sits inside a field or a section, and the field's shape or the section's module decides. The reason names the
    /// module (the outermost type).
    /// </summary>
    public static string? Incompatibility(IReadOnlyDictionary<string, SnapshotLayout> saved, bool positional = false)
    {
        foreach (SnapshotLayout old in saved.Values.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            if (Current(old.Key) is not { } now)
            {
                continue;
            }

            if (old.HookSignature != now.HookSignature)
            {
                return $"module {now.Owner}: the hand-written part of {now.Key} changed";
            }

            if (positional && old.Fields.FirstOrDefault(f => !now.Fields.Any(n => n.Answers(f.Name))) is { } dropped)
            {
                // format 1 has no length per field: data of a field this build dropped cannot be skipped
                return $"module {now.Owner}: {now.Key}.{dropped.Name} was removed";
            }

            foreach (SnapshotField field in now.Fields)
            {
                SnapshotField? before = old.Fields.FirstOrDefault(f => field.Answers(f.Name));
                if (before == null)
                {
                    if (!field.HasMissingValue)
                    {
                        return $"module {now.Owner}: {now.Key}.{field.Name} is new and declares no value for older states";
                    }

                    continue;
                }

                if (before.Signature != field.Signature)
                {
                    return $"module {now.Owner}: {now.Key}.{field.Name} changed from {before.Signature} to {field.Signature}";
                }
            }
        }

        return null;
    }
}

/// <summary>
/// The value a field had in builds whose states do not have it (a field added later): what the older build's code
/// behaved as, e.g. <c>[SnapshotMissing(false)]</c> for a new flag the old code never set. Without arguments: the empty
/// value (zero, false, null; collections and arrays emptied). A field added without it makes states of older builds
/// refused with the module's name, never loaded with a guess.
/// </summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class SnapshotMissingAttribute : Attribute
{
    public SnapshotMissingAttribute()
    {
    }

    public SnapshotMissingAttribute(object? value)
    {
        Value = value;
    }

    public object? Value { get; }
}

/// <summary>Names a field had in older builds: saved data under those names loads into it (a rename).</summary>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class SnapshotAliasAttribute(params string[] names) : Attribute
{
    public string[] Names { get; } = names;
}
