using VitaSharp.Core.SaveState;

namespace VitaSharp.Tests;

// The same module as two builds have it: LayoutProbeV1 is what an older build saved, the others what a newer build
// declares. A test saves with the old type and loads into the new one through the saved layout table, keyed as the
// new type (as the same type's layout in another build would be).

[GenerateSnapshot]
public sealed partial class LayoutProbeV1
{
    public int Kept;
    public string? Dropped;
    public int OldName;
    public List<int> Items = new();
    public int Extra;

    private void SaveExtra(SnapshotWriter w)
    {
        w.Write(Extra);
        w.Write(Items.Count);
    }

    private void LoadExtra(SnapshotReader r)
    {
        Extra = r.ReadInt32();
        r.ReadInt32();
    }
}

/// <summary>A newer build: one field dropped, one renamed, two added with the value the old build had.</summary>
[GenerateSnapshot]
public sealed partial class LayoutProbeAdded
{
    public int Kept;
    [SnapshotAlias("OldName")] public int NewName;
    public List<int> Items = new();
    [SnapshotMissing(7)] public int AddedNumber = 1;
    [SnapshotMissing(true)] public bool AddedFlag;
    [SnapshotMissing] public List<int> AddedList = new() { 1, 2 };
    [SnapshotMissing(LayoutProbeMode.Second)] public LayoutProbeMode AddedMode;
    public int Extra;

    // the old hooks renamed and reformatted (the 11/11 style commits did this to the code outside Hle)
    private void SaveExtra(SnapshotWriter writer)
    {
        int extraValue = Extra;
        writer.Write(extraValue);
        writer.Write(Items.Count);
    }

    private void LoadExtra(SnapshotReader reader)
    {
        Extra = reader.ReadInt32();
        int ignoredCount = reader.ReadInt32();
        _ = ignoredCount;
    }
}

public enum LayoutProbeMode
{
    First, Second
}

/// <summary>An older build's module without hand-written parts.</summary>
[GenerateSnapshot]
public sealed partial class LayoutProbePlainV1
{
    public int Kept;
}

/// <summary>A newer build that added a field without saying what older states had.</summary>
[GenerateSnapshot]
public sealed partial class LayoutProbeNoValue
{
    public int Kept;
    public int Added;
}

/// <summary>A newer build that changed what a field holds.</summary>
[GenerateSnapshot]
public sealed partial class LayoutProbeRetyped
{
    public long Kept;
}

/// <summary>A newer build whose hand-written part writes one value more.</summary>
[GenerateSnapshot]
public sealed partial class LayoutProbeHookChanged
{
    public int Kept;
    public int Extra;

    private void SaveExtra(SnapshotWriter w)
    {
        w.Write(Extra);
        w.Write(0);
        w.Write(0L);
    }

    private void LoadExtra(SnapshotReader r)
    {
        Extra = r.ReadInt32();
        r.ReadInt32();
        r.ReadInt64();
    }
}

/// <summary>
/// Save states of other builds (13/11/2026: states of c41f784 refused by 5d2597ec because any field added to any module
/// changed one hash over every layout). The data of each [GenerateSnapshot] type is mapped by field name through the
/// layout table the state keeps, as PPSSPP's versioned DoState sections do: renames, reformatting, dropped and added
/// fields with a declared value load; a field added without one, a field whose data changed shape, or a hand-written
/// part that reads differently refuse the state with the module's name.
/// </summary>
public sealed class SaveStateLayoutTests
{
    private static LayoutProbeV1 Old()
    {
        var old = new LayoutProbeV1 { Kept = 41, Dropped = "gone", OldName = 9, Extra = 5 };
        old.Items.Add(3);
        return old;
    }

    /// <summary>Saves <paramref name="state"/> and returns its data and saved layout table (written and read back as
    /// a state file does), with the saving type's layout under <paramref name="asKey"/>.</summary>
    private static (byte[] Data, Dictionary<string, SnapshotLayout> Layouts) SaveAs(ISnapshotState state, string asKey)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var w = new SnapshotWriter { UsedLayouts = used };
        state.SaveState(w);
        var table = new SnapshotWriter();
        table.Write(used.Count);
        foreach (string key in used)
        {
            SnapshotLayout layout = SnapshotLayouts.Current(key)!;
            (layout with { Key = asKey }).Write(table);
        }

        return (w.Data.ToArray(), SnapshotLayouts.Read(new SnapshotReader(table.Data.ToArray())));
    }

    private static string KeyOf<T>() => typeof(T).FullName!;

    [Fact]
    public void LayoutsOfThisBuildAreRegistered()
    {
        SnapshotLayout layout = SnapshotLayouts.Current(KeyOf<LayoutProbeAdded>())!;
        Assert.Equal(nameof(LayoutProbeAdded), layout.Owner);
        Assert.Equal(["Kept", "NewName", "Items", "AddedNumber", "AddedFlag", "AddedList", "AddedMode", "Extra"], layout.FieldNames);
        Assert.True(layout.Fields.Single(f => f.Name == "AddedNumber").HasMissingValue);
        Assert.True(layout.Fields.Single(f => f.Name == "NewName").Answers("OldName"));
        Assert.Equal("Int32", layout.Fields[0].Signature);
        Assert.Equal("list(Int32)", layout.Fields[2].Signature);
        Assert.Equal("enum(Int32)", layout.Fields[6].Signature);
        // a Vita module of this build too
        Assert.NotNull(SnapshotLayouts.Current("VitaSharp.Core.Hle.Modules.Gxm"));
    }

    [Fact]
    public void RenamedAndReformattedHooksKeepTheirSignature()
    {
        // without this the 11/11 style commits (renamed locals, braces) refused every state of the builds before them
        Assert.Equal(SnapshotLayouts.Current(KeyOf<LayoutProbeV1>())!.HookSignature, SnapshotLayouts.Current(KeyOf<LayoutProbeAdded>())!.HookSignature);
        Assert.NotEqual(SnapshotLayouts.Current(KeyOf<LayoutProbeV1>())!.HookSignature, SnapshotLayouts.Current(KeyOf<LayoutProbeHookChanged>())!.HookSignature);
    }

    [Fact]
    public void OlderStateLoadsWithTheDeclaredValuesOfNewFields()
    {
        (byte[] data, Dictionary<string, SnapshotLayout> layouts) = SaveAs(Old(), KeyOf<LayoutProbeAdded>());
        Assert.Null(SnapshotLayouts.Incompatibility(layouts));
        var loaded = new LayoutProbeAdded { Kept = -1, NewName = -1, AddedFlag = false };
        loaded.LoadState(new SnapshotReader(data) { Layouts = layouts });
        Assert.Equal(41, loaded.Kept);
        Assert.Equal(9, loaded.NewName); // saved as OldName
        Assert.Equal([3], loaded.Items);
        Assert.Equal(7, loaded.AddedNumber); // the declared value, not the constructor's 1
        Assert.True(loaded.AddedFlag);
        Assert.Empty(loaded.AddedList);
        Assert.Equal(LayoutProbeMode.Second, loaded.AddedMode);
        Assert.Equal(5, loaded.Extra); // the hand-written part, read by the renamed hooks
    }

    [Fact]
    public void SameBuildStateStillRoundTrips()
    {
        var a = new LayoutProbeAdded { Kept = 1, NewName = 2, AddedNumber = 3, AddedFlag = false, AddedMode = LayoutProbeMode.First, Extra = 4 };
        a.Items.Add(5);
        a.AddedList.Add(6);
        (byte[] data, Dictionary<string, SnapshotLayout> layouts) = SaveAs(a, KeyOf<LayoutProbeAdded>());
        Assert.Null(SnapshotLayouts.Incompatibility(layouts));
        var b = new LayoutProbeAdded();
        b.LoadState(new SnapshotReader(data) { Layouts = layouts });
        Assert.Equal((1, 2, 3, false, LayoutProbeMode.First, 4), (b.Kept, b.NewName, b.AddedNumber, b.AddedFlag, b.AddedMode, b.Extra));
        Assert.Equal([5], b.Items);
        Assert.Equal([1, 2, 6], b.AddedList);
        // without a layout table (data of this build) the fields are read in this build's order
        var c = new LayoutProbeAdded();
        c.LoadState(new SnapshotReader(data));
        Assert.Equal(3, c.AddedNumber);
    }

    [Fact]
    public void NewFieldWithoutADeclaredValueRefusesTheModule()
    {
        (_, Dictionary<string, SnapshotLayout> layouts) = SaveAs(new LayoutProbePlainV1 { Kept = 1 }, KeyOf<LayoutProbeNoValue>());
        string? why = SnapshotLayouts.Incompatibility(layouts);
        Assert.NotNull(why);
        Assert.Contains($"module {nameof(LayoutProbeNoValue)}", why);
        Assert.Contains("Added is new", why);
    }

    [Fact]
    public void FieldWhoseDataChangedShapeRefusesTheModule()
    {
        (_, Dictionary<string, SnapshotLayout> layouts) = SaveAs(new LayoutProbePlainV1 { Kept = 1 }, KeyOf<LayoutProbeRetyped>());
        string? why = SnapshotLayouts.Incompatibility(layouts);
        Assert.NotNull(why);
        Assert.Contains($"module {nameof(LayoutProbeRetyped)}", why);
        Assert.Contains("Int32 to Int64", why);
    }

    [Fact]
    public void HandWrittenPartThatChangedRefusesTheModule()
    {
        (_, Dictionary<string, SnapshotLayout> layouts) = SaveAs(Old(), KeyOf<LayoutProbeHookChanged>());
        string? why = SnapshotLayouts.Incompatibility(layouts);
        Assert.NotNull(why);
        Assert.Contains($"module {nameof(LayoutProbeHookChanged)}", why);
    }

    [Fact]
    public void TypesTheBuildNoLongerHasAreNotChecked()
    {
        (_, Dictionary<string, SnapshotLayout> layouts) = SaveAs(Old(), "VitaSharp.Tests.ATypeRemovedSince");
        Assert.Null(SnapshotLayouts.Incompatibility(layouts));
    }

    /// <summary>Format 1 data (before 13/11/2026): the fields one after the other, no length per field, read with the
    /// saving build's layout table (SnapshotLayouts.Legacy).</summary>
    [Fact]
    public void PositionalStateOfAnOlderBuildLoads()
    {
        var w = new SnapshotWriter();
        w.Write(41); // Kept
        w.Write(9); // OldName
        w.Write(1); // Items
        w.Write(3);
        w.Write(5); // Extra
        w.Write(5); // SaveExtra: Extra, Items.Count
        w.Write(1);
        string key = KeyOf<LayoutProbeAdded>();
        var old = new SnapshotLayout(key, nameof(LayoutProbeAdded), SnapshotLayouts.Current(KeyOf<LayoutProbeV1>())!.HookSignature,
            [new SnapshotField("Kept", "Int32"), new SnapshotField("OldName", "Int32"), new SnapshotField("Items", "list(Int32)"), new SnapshotField("Extra", "Int32")]);
        var layouts = new Dictionary<string, SnapshotLayout> { [key] = old };
        Assert.Null(SnapshotLayouts.Incompatibility(layouts, positional: true));
        var loaded = new LayoutProbeAdded();
        var reader = new SnapshotReader(w.Data.ToArray()) { Layouts = layouts, Positional = true };
        loaded.LoadState(reader);
        Assert.True(reader.AtEnd);
        Assert.Equal((41, 9, 7, true, 5), (loaded.Kept, loaded.NewName, loaded.AddedNumber, loaded.AddedFlag, loaded.Extra));
        Assert.Equal([3], loaded.Items);
    }

    [Fact]
    public void PositionalStateWithAFieldThisBuildDroppedIsRefused()
    {
        // without a length per field the dropped field's data cannot be skipped
        string key = KeyOf<LayoutProbeAdded>();
        SnapshotLayout v1 = SnapshotLayouts.Current(KeyOf<LayoutProbeV1>())! with { Key = key };
        string? why = SnapshotLayouts.Incompatibility(new Dictionary<string, SnapshotLayout> { [key] = v1 }, positional: true);
        Assert.NotNull(why);
        Assert.Contains("Dropped was removed", why);
    }

    [Fact]
    public void NestedStatesAreMappedByTheirOwnLayout()
    {
        var probe = new SnapshotProbe { Child = new SnapshotChild { Value = 3 } };
        var used = new HashSet<string>(StringComparer.Ordinal);
        var w = new SnapshotWriter { UsedLayouts = used };
        probe.SaveState(w);
        Assert.Contains(KeyOf<SnapshotProbe>(), used);
        Assert.Contains(KeyOf<SnapshotChild>(), used); // written inside a field: its layout goes into the state too
    }
}

/// <summary>The load decides with the game still running whether a state fits this build (SaveStates.CheckSections
/// runs before the threads are stopped): a refusal names the module and leaves nothing changed.</summary>
[Collection("guest")]
public sealed class SaveStateSectionCheckTests(GuestMemoryFixture fixture)
{
    private static SaveStateHeader Header() => new() { FormatVersion = SaveStateFile.FormatVersion, Schema = SnapshotSchema.Hash, EmulatorVersion = "1.0.0+0123456789", TitleId = "PCSE00000" };

    /// <summary>The sections a save of <paramref name="emu"/> has for its modules, with their layout table.</summary>
    private static List<(string Name, byte[] Data)> Sections(VitaSharp.Core.Emulator emu, Func<SnapshotLayout, SnapshotLayout>? edit = null)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var sections = new List<(string Name, byte[] Data)>();
        var names = new HashSet<string>();
        foreach (VitaSharp.Core.Hle.HleModule module in emu.Hle.Modules)
        {
            if (module is ISnapshotState state && names.Add(module.GetType().Name))
            {
                var w = new SnapshotWriter { UsedLayouts = used };
                state.SaveState(w);
                sections.Add(("hle:" + module.GetType().Name, w.Data.ToArray()));
            }
        }

        var table = new SnapshotWriter();
        table.Write(used.Count);
        foreach (string key in used)
        {
            SnapshotLayout layout = SnapshotLayouts.Current(key)!;
            (edit?.Invoke(layout) ?? layout).Write(table);
        }

        sections.Insert(0, ("layouts", table.Data.ToArray()));
        return sections;
    }

    [Fact]
    public void StateOfThisBuildPasses()
    {
        using var harness = new HleHarness(fixture, "sectioncheck");
        (IReadOnlyDictionary<string, SnapshotLayout> layouts, bool positional) = VitaSharp.Core.SaveState.SaveStates.CheckSections(harness.Emu, Header(), Sections(harness.Emu));
        Assert.False(positional);
        Assert.NotNull(layouts.GetValueOrDefault("VitaSharp.Core.Hle.Modules.Gxm"));
    }

    [Fact]
    public void ModuleWhoseHandWrittenPartChangedIsRefusedBeforeAnythingStops()
    {
        using var harness = new HleHarness(fixture, "sectioncheck");
        List<(string Name, byte[] Data)> sections = Sections(harness.Emu, l => l.Key == "VitaSharp.Core.Hle.Modules.Gxm" ? l with { HookSignature = l.HookSignature + "W.Write(Int32) " } : l);
        SaveStateIncompatibleException e = Assert.Throws<SaveStateIncompatibleException>(() => VitaSharp.Core.SaveState.SaveStates.CheckSections(harness.Emu, Header(), sections));
        Assert.Contains("module Gxm", e.Message);
        Assert.Contains("1.0.0+0123456", e.Message);
    }

    [Fact]
    public void ModuleTheSavingBuildDidNotHaveIsKeptWhenUnused()
    {
        using var harness = new HleHarness(fixture, "sectioncheck");
        List<(string Name, byte[] Data)> sections = Sections(harness.Emu);
        sections.RemoveAll(s => s.Name == "hle:Gxm"); // a module added since (the game has not used it here)
        VitaSharp.Core.SaveState.SaveStates.CheckSections(harness.Emu, Header(), sections);
    }

    [Fact]
    public void StateWithoutALayoutTableIsRefused()
    {
        using var harness = new HleHarness(fixture, "sectioncheck");
        List<(string Name, byte[] Data)> sections = Sections(harness.Emu);
        sections.RemoveAt(0);
        Assert.Throws<SaveStateIncompatibleException>(() => VitaSharp.Core.SaveState.SaveStates.CheckSections(harness.Emu, Header(), sections));
    }
}
