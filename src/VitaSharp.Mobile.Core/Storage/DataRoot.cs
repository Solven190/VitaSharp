namespace VitaSharp.Mobile.Core.Storage;

/// <summary>What the app knows about the device when it picks its data folder (<see cref="DataRoot.Resolve"/>).</summary>
/// <param name="AppSpecificDir">Context.GetExternalFilesDir(null): /storage/emulated/0/Android/data/&lt;package&gt;/files
/// (null when shared storage is not mounted).</param>
/// <param name="InternalDir">Context.FilesDir, the last resort when shared storage is not mounted.</param>
/// <param name="SharedStorageDir">The primary shared storage (Environment.ExternalStorageDirectory, /storage/emulated/0),
/// null when it is not mounted.</param>
/// <param name="HasRoot">The device is rooted (<see cref="RootDetector"/>).</param>
/// <param name="HasAllFilesAccess">"All files access" (MANAGE_EXTERNAL_STORAGE, Android 11+) or the storage permission
/// (WRITE_EXTERNAL_STORAGE, Android 8-10) is granted.</param>
public readonly record struct DataRootInputs(
    string? AppSpecificDir,
    string InternalDir,
    string? SharedStorageDir,
    bool HasRoot,
    bool HasAllFilesAccess);

/// <summary>The data folder to use now and what the app still has to do to reach the visible one.</summary>
/// <param name="Root">The folder the app uses in this process (<see cref="DataLayout.Root"/>).</param>
/// <param name="VisibleRoot">/storage/emulated/0/.vitasharp, the folder the user can see and edit (null when shared
/// storage is not mounted).</param>
/// <param name="NeedsPermission">No root and no all-files access: the app asks for it and shows the warning; the data
/// stays in the app-specific folder meanwhile.</param>
/// <param name="MigrateFrom">The app-specific folder still holds data (or a migration was interrupted): it is moved to
/// <see cref="VisibleRoot"/> by <see cref="DataMigration"/> before <see cref="VisibleRoot"/> is used. <see cref="Root"/>
/// is the old folder until then.</param>
public readonly record struct DataRootPlan(string Root, string? VisibleRoot, bool NeedsPermission, string? MigrateFrom)
{
    public bool NeedsMigration => MigrateFrom != null;
}

/// <summary>
/// The one place that decides the Android app's data folder (docs/android/README.md, "Storage"):
/// <list type="bullet">
/// <item>rooted device: the app-specific folder (Android/data/&lt;package&gt;/files), where it always was;</item>
/// <item>no root, all-files access granted: <c>/storage/emulated/0/.vitasharp</c>, visible and editable in any file
/// manager, after the old folder's content was moved there (<see cref="DataMigration"/>);</item>
/// <item>no root, no access yet: the app-specific folder, with <see cref="DataRootPlan.NeedsPermission"/>.</item>
/// </list>
/// Without shared storage mounted, the app-specific folder or the internal one, as before.
/// </summary>
public static class DataRoot
{
    /// <summary>The visible folder's name in the root of the shared storage.</summary>
    public const string VisibleFolderName = ".vitasharp";

    public static DataRootPlan Resolve(DataRootInputs inputs)
    {
        string old = inputs.AppSpecificDir is { Length: > 0 } ext ? ext : inputs.InternalDir;
        if (inputs.SharedStorageDir is not { Length: > 0 } shared)
        {
            return new DataRootPlan(old, null, false, null);
        }

        string visible = Path.Combine(shared, VisibleFolderName);
        if (inputs.HasRoot)
        {
            return new DataRootPlan(old, visible, false, null);
        }

        if (!inputs.HasAllFilesAccess)
        {
            return new DataRootPlan(old, visible, true, null);
        }

        if (inputs.AppSpecificDir is { Length: > 0 } source && DataMigration.IsPending(source, visible))
        {
            return new DataRootPlan(old, visible, false, source);
        }

        return new DataRootPlan(visible, visible, false, null);
    }
}

/// <summary>
/// Root detection for <see cref="DataRoot"/>: an executable <c>su</c> in a folder of PATH or in one of the folders where
/// the common root solutions install it (the list root checkers such as RootBeer use).
/// </summary>
public static class RootDetector
{
    /// <summary>Where SuperSU, Magisk and the older root packages put the su binary.</summary>
    public static readonly string[] KnownSuFolders =
    [
        "/system/bin", "/system/xbin", "/sbin", "/su/bin", "/system/sbin", "/vendor/bin", "/system/bin/failsafe",
        "/data/local", "/data/local/bin", "/data/local/xbin", "/system/sd/xbin", "/debug_ramdisk",
    ];

    /// <summary>True when <c>su</c> exists in a PATH folder (<paramref name="pathVariable"/>, ':'-separated) or a known
    /// folder; <paramref name="fileExists"/> is File.Exists on the device (a parameter for the tests).</summary>
    public static bool HasSu(string? pathVariable, Func<string, bool> fileExists)
    {
        var folders = new List<string>();
        if (!string.IsNullOrEmpty(pathVariable))
        {
            folders.AddRange(pathVariable.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        folders.AddRange(KnownSuFolders);
        foreach (string folder in folders)
        {
            if (fileExists(folder.TrimEnd('/') + "/su"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>This device: PATH and File.Exists.</summary>
    public static bool HasSu()
    {
        return HasSu(Environment.GetEnvironmentVariable("PATH"), File.Exists);
    }
}
