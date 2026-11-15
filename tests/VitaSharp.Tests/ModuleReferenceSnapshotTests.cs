using System.Reflection;
using VitaSharp.Core.Hle;
using VitaSharp.Core.SaveState;
using Xunit;

namespace VitaSharp.Tests;

/// <summary>
/// A module's cached reference to another module (AppUtil's AppMgr, Fios2's IoFileMgr, Paf's Libc...) is not state: the
/// modules are the emulator's, one of each, and a save state restores into them. The snapshot generator used to write
/// such a field as a nested state of the referenced module, and a load then made a detached copy of it (AppUtil's
/// bookkeeping talked to an AppMgr nobody else saw).
/// </summary>
[Collection("guest")]
public sealed class ModuleReferenceSnapshotTests(GuestMemoryFixture fixture) : IDisposable
{
    private readonly HleHarness _harness = new(fixture, "module-refs");

    public void Dispose() => _harness.Dispose();

    private static IEnumerable<FieldInfo> ModuleFields(Type type)
    {
        for (Type? t = type; t != null && t != typeof(HleModule); t = t.BaseType)
        {
            foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (typeof(HleModule).IsAssignableFrom(field.FieldType) && !field.IsInitOnly)
                {
                    yield return field;
                }
            }
        }
    }

    [Fact]
    public void AModuleReferenceSurvivesASaveAndLoadAsTheEmulatorsOwnModule()
    {
        var modules = _harness.Emu.Hle.Modules.ToList();
        int checkedFields = 0;
        foreach (HleModule module in modules)
        {
            if (module is not ISnapshotState state)
            {
                continue;
            }

            var fields = ModuleFields(module.GetType()).ToList();
            if (fields.Count == 0)
            {
                continue;
            }

            foreach (FieldInfo field in fields)
            {
                HleModule? live = modules.FirstOrDefault(m => field.FieldType.IsInstanceOfType(m));
                field.SetValue(module, live);
            }

            var writer = new SnapshotWriter();
            state.SaveState(writer);
            state.LoadState(new SnapshotReader(writer.Data.ToArray()));
            foreach (FieldInfo field in fields)
            {
                object? after = field.GetValue(module);
                Assert.True(after == null || modules.Any(m => ReferenceEquals(m, after)),
                    $"{module.GetType().Name}.{field.Name}: a load left a {field.FieldType.Name} that is not the emulator's module");
                checkedFields++;
            }
        }

        // AppUtil._appMgr at least (the case the roadmap names)
        Assert.True(checkedFields > 0);
    }
}
