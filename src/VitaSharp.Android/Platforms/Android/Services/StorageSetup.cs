using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using VitaSharp.Core.Localization;
using VitaSharp.Core.Util;
using VitaSharp.Mobile.Core.Storage;

namespace VitaSharp.Android.Services;

/// <summary>
/// The device side of <see cref="DataRoot"/>: root, the all-files permission and the shared storage, and the first-start
/// flow of the screens' process (docs/android/README.md, "Storage"): without root it asks once for "All files access"
/// (Android 11+: the system's ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION screen; Android 8-10: the storage
/// permission), then moves the old app-specific folder to /storage/emulated/0/.vitasharp with a progress dialog
/// (<see cref="DataMigration"/>) and restarts the app on the new folder.
/// </summary>
public static class StorageSetup
{
    const int PermissionRequestCode = 0x5700;
    const string StateFile = "storage_setup";
    const string AskedKey = "all_files_asked";

    static bool _migrating;

    /// <summary>What <see cref="DataRoot.Resolve"/> sees on this device now.</summary>
    public static DataRootInputs Inputs(Context context)
    {
        string? shared = null;
        if (global::Android.OS.Environment.ExternalStorageState == global::Android.OS.Environment.MediaMounted)
        {
            shared = global::Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
        }

        return new DataRootInputs(context.GetExternalFilesDir(null)?.AbsolutePath, context.FilesDir!.AbsolutePath, shared,
            RootDetector.HasSu(), HasAllFilesAccess(context));
    }

    /// <summary>"All files access" (Android 11+) or the storage permission (Android 8-10).</summary>
    public static bool HasAllFilesAccess(Context context)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            return global::Android.OS.Environment.IsExternalStorageManager;
        }

        return ContextCompat.CheckSelfPermission(context, global::Android.Manifest.Permission.WriteExternalStorage) == Permission.Granted;
    }

    /// <summary>Opens the system screen that grants the permission (also from the library's warning card).</summary>
    public static void RequestAllFilesAccess(Activity activity)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
        {
            try
            {
                activity.StartActivity(new Intent(Settings.ActionManageAppAllFilesAccessPermission,
                    global::Android.Net.Uri.Parse("package:" + activity.PackageName)));
            }
            catch (ActivityNotFoundException)
            {
                activity.StartActivity(new Intent(Settings.ActionManageAllFilesAccessPermission));
            }

            return;
        }

        ActivityCompat.RequestPermissions(activity,
            [global::Android.Manifest.Permission.WriteExternalStorage, global::Android.Manifest.Permission.ReadExternalStorage],
            PermissionRequestCode);
    }

    /// <summary>
    /// Called when the screens' activity starts or comes back (from the permission screen above all): asks for the
    /// permission the first time, moves the data when there is something to move, and restarts the app when the data
    /// folder changed.
    /// </summary>
    public static void Check(Activity activity)
    {
        if (_migrating || !AppServices.Initialized)
        {
            return;
        }

        DataRootPlan plan = DataRoot.Resolve(Inputs(activity));
        AppServices.StoragePlan = plan;
        if (plan.NeedsMigration)
        {
            if (GameProcessMarker.Read(AppServices.Data.GameProcessFile, GameProcessMarker.IsGameProcess) is { } running)
            {
                // the game process writes into the old folder: the move waits until it closed
                Log.Info($"Storage: the data move waits for the game process ({running.TitleId}) to close");
                Toast.MakeText(activity, Loc.Get("MStorageMoveWaitsGame"), ToastLength.Long)?.Show();
                return;
            }

            Migrate(activity, plan);
            return;
        }

        if (!string.Equals(Path.GetFullPath(plan.Root), AppServices.PlannedRoot, StringComparison.Ordinal))
        {
            Log.Info($"Storage: the data folder is now {plan.Root}: restarting");
            Restart(activity);
            return;
        }

        if (plan.NeedsPermission)
        {
            ISharedPreferences? state = activity.GetSharedPreferences(StateFile, FileCreationMode.Private);
            if (state != null && !state.GetBoolean(AskedKey, false))
            {
                state.Edit()?.PutBoolean(AskedKey, true)?.Apply();
                AskForPermission(activity);
            }
        }
    }

    static void AskForPermission(Activity activity)
    {
        new AlertDialog.Builder(activity)
            .SetTitle(Loc.Get("MStorageAccessTitle"))!
            .SetMessage(Loc.Get("MStorageAccessExplain"))!
            .SetPositiveButton(Loc.Get("MStorageAccessAllow"), (_, _) => RequestAllFilesAccess(activity))!
            .SetNegativeButton(Loc.Get("MLater"), (_, _) => { })!
            .Show();
    }

    static void Migrate(Activity activity, DataRootPlan plan)
    {
        _migrating = true;
        var bar = new ProgressBar(activity, null, global::Android.Resource.Attribute.ProgressBarStyleHorizontal) { Max = 1000 };
        var text = new TextView(activity);
        var layout = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        int padding = (int)(20 * activity.Resources!.DisplayMetrics!.Density);
        layout.SetPadding(padding, padding / 2, padding, 0);
        layout.AddView(bar);
        layout.AddView(text);
        AlertDialog dialog = new AlertDialog.Builder(activity)
            .SetTitle(Loc.Get("MStorageMoving"))!
            .SetMessage(Loc.Format("MStorageMovingDetail", plan.VisibleRoot ?? ""))!
            .SetView(layout)!
            .SetCancelable(false)!
            .Create()!;
        dialog.Show();
        var progress = new Progress<MigrationProgress>(p =>
        {
            bar.Progress = p.BytesTotal > 0 ? (int)(p.BytesDone * 1000 / p.BytesTotal) : 0;
            text.Text = $"{p.FilesDone} / {p.FilesTotal} · {p.BytesDone >> 20} / {p.BytesTotal >> 20} MB";
        });
        string source = plan.MigrateFrom!;
        string target = plan.VisibleRoot!;
        Log.Info($"Storage: moving {source} to {target}");
        Task.Run(() =>
        {
            try
            {
                return new DataMigration(source, target).Run(progress);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new MigrationResult(MigrationStatus.Failed, e.Message, 0, 0);
            }
        }).ContinueWith(task =>
        {
            MigrationResult result = task.Result;
            Log.Info($"Storage: move {result.Status}, {result.Files} files, {result.Bytes} bytes {result.Message}");
            activity.RunOnUiThread(() =>
            {
                dialog.Dismiss();
                _migrating = false;
                if (result.Status is MigrationStatus.Done)
                {
                    Restart(activity);
                    return;
                }

                string message = result.Status switch
                {
                    MigrationStatus.NoSpace => Loc.Format("MStorageMoveNoSpace", result.Message),
                    MigrationStatus.VerifyFailed => Loc.Format("MStorageMoveProblems", target, DataMigration.ProblemsFileName),
                    _ => Loc.Format("MStorageMoveFailed", result.Message),
                };
                new AlertDialog.Builder(activity)
                    .SetTitle(Loc.Get("MStorageMoving"))!
                    .SetMessage(message)!
                    .SetPositiveButton(result.Status is MigrationStatus.VerifyFailed ? Loc.Get("MDone") : Loc.Get("MStorageMoveRetry"),
                        (_, _) =>
                        {
                            if (result.Status is MigrationStatus.VerifyFailed)
                            {
                                Restart(activity);
                            }
                            else
                            {
                                Check(activity);
                            }
                        })!
                    .SetNegativeButton(Loc.Get("MLater"), (_, _) => { })!
                    .SetCancelable(false)!
                    .Show();
            });
        });
    }

    /// <summary>A fresh process on the new data folder (every screen, the settings and the log open there).</summary>
    static void Restart(Activity activity)
    {
        Intent? launch = activity.PackageManager?.GetLaunchIntentForPackage(activity.PackageName!);
        if (launch == null)
        {
            return;
        }

        launch.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
        activity.StartActivity(launch);
        activity.FinishAffinity();
        global::Android.OS.Process.KillProcess(global::Android.OS.Process.MyPid());
    }
}
