using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using VitaSharp.Core.Util;
using VitaSharp.Desktop;
using VitaSharp.Desktop.Launcher;

namespace VitaSharp.Tests;

/// <summary>
/// Deleting an item of a launcher list (uninstalling a game, deleting a capture) keeps the list where it was scrolled,
/// with the keyboard focus on the item that followed the deleted one, instead of jumping back to the top. Avalonia's
/// headless platform with a scratch data folder (never the user's); the confirmation dialog is answered by the test.
/// </summary>
[Collection("launcher")]
public sealed class ListDeleteKeepsPositionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vitasharp-listdel-" + Guid.NewGuid().ToString("N"));
    private readonly string? _savedData = Environment.GetEnvironmentVariable(EnvVars.Data);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVars.Data, _savedData);
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData(new[] { "a", "b", "c", "d" }, new[] { "b" }, "c")]
    [InlineData(new[] { "a", "b", "c", "d" }, new[] { "d" }, "c")]
    [InlineData(new[] { "a", "b", "c", "d" }, new[] { "b", "c" }, "d")]
    [InlineData(new[] { "a", "b", "c", "d" }, new[] { "c", "d" }, "b")]
    [InlineData(new[] { "a", "b", "c", "d" }, new[] { "a", "c" }, "b")]
    [InlineData(new[] { "a" }, new[] { "a" }, null)]
    [InlineData(new[] { "a", "b" }, new[] { "x" }, null)]
    public void TheFocusGoesToTheItemThatFollowedTheDeletedOne(string[] before, string[] removed, string? expected)
        => Assert.Equal(expected, ListPosition.NextAfterRemoval(before, removed));

    private static void Pump()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Runs a launcher action that awaits (a file deletion on the thread pool) to its end on the UI thread.</summary>
    private static void RunToEnd(Func<Task> action)
    {
        SynchronizationContext? saved = SynchronizationContext.Current;
        AvaloniaSynchronizationContext.InstallIfNeeded();
        try
        {
            Task task = action();
            // a guard against a hang only: the result is checked after the task has finished
            for (int i = 0; i < 4000 && !task.IsCompleted; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }

            Assert.True(task.IsCompleted, "the launcher action did not finish");
            task.GetAwaiter().GetResult();
            Pump();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(saved);
        }
    }

    private LauncherSettings PrepareData(int games)
    {
        string data = Path.Combine(_root, "data");
        for (int i = 0; i < games; i++)
        {
            string id = $"PCSB{i:D5}";
            string game = Path.Combine(data, "fs", "ux0", "app", id);
            Directory.CreateDirectory(Path.Combine(game, "sce_sys"));
            File.WriteAllBytes(Path.Combine(game, "eboot.bin"), new byte[64]);
            DesktopLogicTests.WriteSfo(Path.Combine(game, "sce_sys", "param.sfo"), ("APP_VER", "01.00"), ("CATEGORY", "gd"), ("TITLE", $"Game {i:D2}"), ("TITLE_ID", id));
        }

        File.WriteAllText(Path.Combine(data, "launcher.json"), "{ \"CheckUpdates\": false }");
        Environment.SetEnvironmentVariable(EnvVars.Data, data);
        LauncherApp.SetupHeadless();
        LauncherSettings settings = LauncherSettings.Load();
        settings.View = LauncherSettings.ViewGrid;
        return settings;
    }

    [Theory]
    [InlineData(LauncherSettings.ViewGrid)]
    [InlineData(LauncherSettings.ViewList)]
    public void UninstallingAGameKeepsTheLibraryScrolledWithTheNextGameFocused(string view)
    {
        LauncherSettings settings = PrepareData(48);
        settings.View = view;
        MainWindow main = new(settings) { Width = 1440, Height = 900 };
        main.Show();
        try
        {
            Pump();
            ScrollViewer scroll = main.PageScrollForTests!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 800, $"the library must scroll ({scroll.Extent.Height} / {scroll.Viewport.Height})");
            scroll.Offset = new Vector(0, 700);
            Pump();
            Vector before = scroll.Offset;
            Assert.Equal(700, before.Y);

            // a game on screen at that position: its page, then "Desinstalar"
            List<string> order = main.ListOrderForTests.ToList();
            Control target = order.Select(id => main.ListItemForTests(id)!).First(c => c.TranslatePoint(default, scroll) is { Y: > 40 } p && p.Y < scroll.Viewport.Height - 300);
            string targetId = order.First(id => main.ListItemForTests(id) == target);
            string expectedNext = order[order.IndexOf(targetId) + 1];
            GameEntry game = main.Games.First(g => g.TitleId == targetId);
            main.Navigate(MainWindow.PageGame, game);
            Pump();
            main.ConfirmForTests = _ => true;
            RunToEnd(() => main.UninstallForTests(game));

            Assert.Equal(MainWindow.PageLibrary, main.CurrentPage);
            Assert.DoesNotContain(main.Games, g => g.TitleId == targetId);
            ScrollViewer after = main.PageScrollForTests!;
            Assert.NotSame(scroll, after);
            Control? next = main.ListItemForTests(expectedNext);
            Assert.True(before.Y == after.Offset.Y, $"offset {after.Offset.Y} (extent {after.Extent.Height}, viewport {after.Viewport.Height}), next at {next?.TranslatePoint(default, after)}, focused {next?.IsFocused}");
            Assert.NotNull(next);
            Assert.True(next.IsFocused, $"{expectedNext} should have the focus");
        }
        finally
        {
            main.Close();
        }
    }

    [Fact]
    public void BackFromAGamePageTheLibraryIsWhereItWas()
    {
        LauncherSettings settings = PrepareData(48);
        MainWindow main = new(settings) { Width = 1440, Height = 900 };
        main.Show();
        try
        {
            Pump();
            ScrollViewer scroll = main.PageScrollForTests!;
            scroll.Offset = new Vector(0, 900);
            Pump();
            // a game on screen at that position
            string id = main.ListOrderForTests.First(key => main.ListItemForTests(key)!.TranslatePoint(default, scroll) is { Y: > 40 } p && p.Y < scroll.Viewport.Height - 300);
            GameEntry game = main.Games.First(g => g.TitleId == id);
            main.Navigate(MainWindow.PageGame, game);
            Pump();
            main.Navigate(MainWindow.PageLibrary);
            Pump();
            Assert.Equal(900, main.PageScrollForTests!.Offset.Y);
            Assert.True(main.ListItemForTests(game.TitleId)!.IsFocused);

            // another page in between: the library opens at the top as before
            main.Navigate(MainWindow.PageGame, game);
            Pump();
            main.Navigate(MainWindow.PageCaptures);
            Pump();
            main.Navigate(MainWindow.PageLibrary);
            Pump();
            Assert.Equal(0, main.PageScrollForTests!.Offset.Y);
        }
        finally
        {
            main.Close();
        }
    }

    [Fact]
    public void DeletingACaptureKeepsTheListScrolledWithTheNextCaptureFocused()
    {
        LauncherSettings settings = PrepareData(1);
        Directory.CreateDirectory(SessionCaptureControl.Root);
        for (int i = 0; i < 40; i++)
        {
            string file = Path.Combine(SessionCaptureControl.Root, $"PCSB00000_20261101_{i:D6}_tudo.zip");
            File.WriteAllBytes(file, new byte[16]);
            // newest first on the page: capture 39 at the top
            File.SetLastWriteTime(file, new DateTime(2026, 11, 1, 12, 0, 0).AddMinutes(i));
        }

        MainWindow main = new(settings) { Width = 1440, Height = 900 };
        main.Show();
        try
        {
            Pump();
            main.Navigate(MainWindow.PageCaptures);
            Pump();
            ScrollViewer scroll = main.PageScrollForTests!;
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 800, $"the captures must scroll ({scroll.Extent.Height} / {scroll.Viewport.Height})");
            scroll.Offset = new Vector(0, 800);
            Pump();
            List<string> order = main.ListOrderForTests.ToList();
            string target = order.First(path => main.ListItemForTests(path)!.TranslatePoint(default, scroll) is { Y: > 40 } p && p.Y < scroll.Viewport.Height - 200);
            string expectedNext = order[order.IndexOf(target) + 1];

            main.ConfirmForTests = _ => true;
            RunToEnd(() => main.DeleteCapturesForTests([target]));

            Assert.False(File.Exists(target));
            ScrollViewer after = main.PageScrollForTests!;
            Assert.NotSame(scroll, after);
            Assert.Equal(800, after.Offset.Y);
            Assert.True(main.ListItemForTests(expectedNext)!.IsFocused, $"{Path.GetFileName(expectedNext)} should have the focus");
        }
        finally
        {
            main.Close();
        }
    }
}
