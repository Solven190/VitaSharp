namespace VitaSharp.Core.SaveState;

/// <summary>
/// Which save states a build can load. The [GenerateSnapshot] types are versioned per field: every state keeps the
/// layouts it was saved with (<see cref="SnapshotLayouts"/>) and a load maps them by name, so only a change of what a
/// type holds refuses a state, naming the module (<see cref="SnapshotLayouts.Incompatibility"/>). The other hand-written
/// layouts (kernel objects, memory, the container, the HLE function table) are covered by <see cref="ManualVersion"/>,
/// which must be raised whenever one of them changes what it writes.
/// </summary>
public static partial class SnapshotSchema
{
    /// <summary>Version of the hand-written state layouts.</summary>
    public const int ManualVersion = 3;

    /// <summary>What a state's header records of the layouts (the per-type layouts are a section of the state).</summary>
    public static string Hash => $"{ManualVersion}";

    /// <summary>True for an HLE module type with instance state and no <see cref="ISnapshotState"/>: a save is
    /// refused once the game has used it.</summary>
    public static bool IsStateful(Type moduleType) => StatefulModules.Contains(moduleType);

    /// <summary>The stateful module types without save state support (diagnostics, tests).</summary>
    public static IReadOnlyCollection<Type> UnsupportedModules => StatefulModules;
}
