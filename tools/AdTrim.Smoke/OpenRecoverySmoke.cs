using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AdTrim;
using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using AdTrim.Views;

internal static partial class Program
{
    private static int RunOpenRecoverySmoke()
    {
        var source = PrepareFixture().GetAwaiter().GetResult();
        var directory = Path.Combine(Path.GetDirectoryName(source)!, "recovery-state");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("ADTRIM_DATA_DIR", directory);
        new UserPreferences { ShowThumbnails = false, ShowWaveform = false, AddSourceExportComment = false }
            .SaveAsync().GetAwaiter().GetResult();
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!);
        // When watching playback, the error stays up long enough for a playing preview to show itself.
        Func<bool>? playingDuringDialog = null;
        bool sawPlayingDuringDialog = false;
        DateTime? dialogSeen = null;
        var dialogs = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        dialogs.Tick += (_, _) => RecoveryEnumWindows((handle, _) =>
        {
            RecoveryGetWindowThreadProcessId(handle, out var process);
            if (process != Environment.ProcessId) return true;
            var title = new StringBuilder(256);
            RecoveryGetWindowText(handle, title, 256);
            if (title.ToString() != "Failed to open") return true;
            if (playingDuringDialog is not null)
            {
                dialogSeen ??= DateTime.UtcNow;
                sawPlayingDuringDialog |= playingDuringDialog();
                if (DateTime.UtcNow - dialogSeen < TimeSpan.FromMilliseconds(800)) return true;
            }
            RecoveryEndDialog(handle, new IntPtr(1));
            return true;
        }, IntPtr.Zero);
        dialogs.Start();
        int result = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow(source) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                window.Show();
                await window.Ready.WaitAsync(TimeSpan.FromSeconds(20));
                var vm = (MainViewModel)window.DataContext;
                var media = (MediaInfo)typeof(MainWindow).GetField("_media", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var fingerprint = vm.SourceFingerprint;
                vm.ZoomFactor = 2.25;
                var selected = vm.Markers[1];
                vm.SelectMarker(selected);
                vm.CommandStack.Execute(new ToggleConfirmedCommand(selected));
                var baseline = ReplaceMarkerSetCommand.Capture(vm).ContentId();
                var runner = new FfmpegRunner();
                string other = Path.Combine(directory, "other.mp4"), corrupt = Path.Combine(directory, "corrupt.mp4");
                var made = await runner.RunFfmpegAsync(["-y", "-v", "error", "-f", "lavfi", "-i",
                    "testsrc2=size=320x180:rate=25:duration=18", "-f", "lavfi", "-i", "sine=duration=18",
                    "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", other]);
                Require(made.Success, "Recovery tests generate a distinct recording");
                await File.WriteAllTextAsync(corrupt, "Not an MP4");

                void RequireOriginal(string reason)
                {
                    var restoredMedia = (MediaInfo)typeof(MainWindow).GetField("_media", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    Require(ReferenceEquals(window.DataContext, vm) && vm.SourcePath == source
                        && vm.SourceFingerprint == fingerprint && vm.DurationUs == media.DurationUs && vm.FrameRate == media.FrameRate
                        && ReferenceEquals(restoredMedia, media) && ReplaceMarkerSetCommand.Capture(vm).ContentId() == baseline, reason);
                    Require(vm.CommandStack.CanUndo && ReferenceEquals(vm.SelectedMarker, selected) && vm.ZoomFactor == 2.25,
                        "Failed open preserves undo, selection and zoom");
                    var scroll = (ScrollViewer)window.FindName("TimelineScroll");
                    var timeline = (FrameworkElement)window.FindName("Timeline");
                    Require(Math.Abs(timeline.Width - scroll.ViewportWidth * vm.ZoomFactor) < 1,
                        "Recovered timeline layout agrees with the restored zoom");
                    var preview = typeof(MainWindow).GetField("_previewAfter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                    var previewPath = (string)typeof(MpvPreviewViewModel).GetField("_currentPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
                    Require(vm.IsCollapsed ? previewPath.StartsWith("edl://") && previewPath.Contains(source) : previewPath == source,
                        "Recovered preview belongs to the original recording and view mode");
                    Require(new ExportDialogViewModel(vm, restoredMedia).BuildPlan()?.SourcePath == source,
                        "Export after a failed open still targets the original recording");
                }
                var afterPane = (MpvPreviewViewModel)typeof(MainWindow).GetField("_previewAfter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var beforePane = (MpvPreviewViewModel)typeof(MainWindow).GetField("_previewBefore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                Invoke(window, "PlayPauseBoth");
                for (int i = 0; i < 150 && !afterPane.IsPlaying; i++) await Task.Delay(20);
                Require(afterPane.IsPlaying, "The original recording is playing before the failed open");
                playingDuringDialog = () => afterPane.IsPlaying || beforePane.IsPlaying;
                await window.OpenFileAsync(corrupt).WaitAsync(TimeSpan.FromSeconds(20));
                playingDuringDialog = null;
                Require(dialogSeen is not null && !sawPlayingDuringDialog, "Restored playback stays paused while the failed-open error is shown");
                for (int i = 0; i < 150 && !(afterPane.IsPlaying && beforePane.IsPlaying); i++) await Task.Delay(20);
                Require(afterPane.IsPlaying && beforePane.IsPlaying, "Playback resumes after the failed-open error is dismissed");
                Invoke(window, "PlayPauseBoth");
                for (int i = 0; i < 150 && (afterPane.IsPlaying || beforePane.IsPlaying); i++) await Task.Delay(20);
                RequireOriginal("Probe failure leaves the original recording intact");
                var otherMedia = await new MediaProbeService(runner).ProbeAsync(other);
                var cacheRoot = Path.Combine(directory, "thumbnails");
                var denied = Directory.CreateDirectory(ThumbnailService.CacheDirectoryFor(other, otherMedia.DurationUs, cacheRoot));
                var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
                try
                {
                    var acl = FileSystemAclExtensions.GetAccessControl(denied);
                    acl.AddAccessRule(rule);
                    FileSystemAclExtensions.SetAccessControl(denied, acl);
                    typeof(MainWindow).GetField("_thumbnails", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(window, new ThumbnailService(runner, cacheRoot));
                    vm.ShowThumbnails = true;
                    await window.OpenFileAsync(other).WaitAsync(TimeSpan.FromSeconds(20));
                    RequireOriginal("Cache read failure cannot mix the next recording's identity with old cuts");
                    var empty = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                    try
                    {
                        empty.Show();
                        await empty.Ready;
                        typeof(MainWindow).GetField("_thumbnails", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .SetValue(empty, new ThumbnailService(runner, cacheRoot));
                        ((MainViewModel)empty.DataContext).ShowThumbnails = true;
                        await empty.OpenFileAsync(other).WaitAsync(TimeSpan.FromSeconds(20));
                        Require(!((MainViewModel)empty.DataContext).IsFileLoaded
                            && typeof(MainWindow).GetField("_media", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(empty) is null,
                            "A failed first open returns to an empty editor instead of leaving a partial recording");
                    }
                    finally
                    {
                        typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(empty, true);
                        empty.Close();
                    }
                }
                finally
                {
                    var acl = FileSystemAclExtensions.GetAccessControl(denied);
                    acl.RemoveAccessRuleSpecific(rule);
                    FileSystemAclExtensions.SetAccessControl(denied, acl);
                }
                vm.ShowThumbnails = false;
                Invoke(window, "OnToggleCollapsed", window, new RoutedEventArgs());
                var collapsedBaseline = ReplaceMarkerSetCommand.Capture(vm).ContentId();
                DependencyPropertyChangedEventHandler inject = (_, change) =>
                {
                    if (change.NewValue is not MainViewModel draft || ReferenceEquals(draft, vm)) return;
                    NotifyCollectionChangedEventHandler? fail = null;
                    fail = (_, _) =>
                    {
                        draft.Markers.CollectionChanged -= fail;
                        throw new IOException("Injected marker hydration failure.");
                    };
                    draft.Markers.CollectionChanged += fail;
                };
                window.DataContextChanged += inject;
                try { await window.OpenFileAsync(other).WaitAsync(TimeSpan.FromSeconds(20)); }
                finally { window.DataContextChanged -= inject; }
                baseline = collapsedBaseline;
                // Collapsing intentionally clears selection, so restore it after testing the mode.
                Require(ReferenceEquals(window.DataContext, vm) && vm.IsCollapsed && vm.SourcePath == source
                    && ReplaceMarkerSetCommand.Capture(vm).ContentId() == baseline && vm.CommandStack.CanUndo,
                    "Failure after marker hydration starts restores the original collapsed editor and undo history");
                var collapsedPreview = typeof(MainWindow).GetField("_previewAfter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var collapsedPath = (string)typeof(MpvPreviewViewModel).GetField("_currentPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(collapsedPreview)!;
                Require(collapsedPath.StartsWith("edl://") && collapsedPath.Contains(source)
                    && new ExportDialogViewModel(vm, media).BuildPlan()?.SourcePath == source,
                    "Recovered collapsed preview and export plan both belong to the original recording");
                Invoke(window, "OnToggleCollapsed", window, new RoutedEventArgs());
                vm.CommandStack.Undo();
                Require(!selected.Confirmed, "Undo still acts on the original marker after recovery");
                vm.CommandStack.Redo();
                Require(selected.Confirmed, "Redo still acts on the original marker after recovery");
                await CheckUnreadableSourcePreferences(window, vm, media, source, directory);
                await window.OpenFileAsync(other);
                var opened = (MainViewModel)window.DataContext;
                Require(!ReferenceEquals(opened, vm) && opened.SourcePath == other && opened.DurationUs == otherMedia.DurationUs
                    && !opened.CommandStack.CanUndo && opened.Markers.All(m => m.Id != selected.Id),
                    "Successful open commits an independent editor without carrying the old recording's cuts or undo");
                Require(opened.ZoomFactor == 1 && Math.Abs(((FrameworkElement)window.FindName("Timeline")).Width
                    - ((ScrollViewer)window.FindName("TimelineScroll")).ViewportWidth) < 1,
                    "Successful open resets both the zoom value and the rendered timeline width");
                Require(window.MinWidth == 1280 && window.MinHeight == 640, "The requested minimum window size is unchanged");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                dialogs.Stop();
                if (window is not null)
                {
                    typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                    window.Close();
                }
                app.Shutdown();
            }
        }));
        app.Run();
        return result;
    }

    private static async Task CheckUnreadableSourcePreferences(MainWindow window, MainViewModel vm, MediaInfo media, string source, string directory)
    {
        var menu = (MenuItem)window.FindName("SourceCommentMenu");
        string path = Path.Combine(directory, "preferences.json");
        var hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var export = new ExportView();
        typeof(ExportView).GetProperty("AcceptedPlan")!.SetValue(export, new ExportDialogViewModel(vm, media).BuildPlan());
        foreach (bool enabled in new[] { false, true })
        {
            menu.IsChecked = enabled;
            Invoke(window, "OnSourceCommentToggle", menu, new RoutedEventArgs());
            for (int i = 0; i < 100 && !menu.IsEnabled; i++) await Task.Delay(20);
            Require((await UserPreferences.ReadAsync()).AddSourceExportComment == enabled, "A valid source-comment choice can be saved");
            foreach (string contents in new[] { "{", "null" })
            {
                await File.WriteAllTextAsync(path, contents);
                var note = await (Task<string?>)Invoke(window, "AddSourceCommentAsync", vm, export, CancellationToken.None)!;
                var actualHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
                Require(hash.SequenceEqual(actualHash) && export.SourceCommentFailed
                    && note?.Contains("preferences could not be read") == true, "Unreadable preferences block source writes and report why");
                var fileMenu = (MenuItem)menu.Parent;
                fileMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, fileMenu));
                await Task.Delay(50);
                Require(menu.IsChecked == enabled, "An unreadable settings file cannot silently change the known menu choice");
                try { await UserPreferences.SaveExportFormatAsync(ExportFormat.Ts); throw new InvalidOperationException("Expected a settings read failure."); }
                catch (IOException) { }
                Require(await File.ReadAllTextAsync(path) == contents, "Saving another preference does not overwrite a damaged settings file");
            }
            await new UserPreferences { ShowThumbnails = false, ShowWaveform = false, AddSourceExportComment = enabled }.SaveAsync();
        }
        // A missing settings file (first launch) follows the File menu's known choice.
        foreach (bool enabled in new[] { false, true })
        {
            menu.IsChecked = enabled;
            Invoke(window, "OnSourceCommentToggle", menu, new RoutedEventArgs());
            for (int i = 0; i < 100 && !menu.IsEnabled; i++) await Task.Delay(20);
            Require((await UserPreferences.ReadAsync()).AddSourceExportComment == enabled, "The known source-comment choice is saved before its file is removed");
            File.Delete(path);
            var note = await (Task<string?>)Invoke(window, "AddSourceCommentAsync", vm, export, CancellationToken.None)!;
            var currentHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            if (enabled)
                Require(Mp4Comments.HasExportLabel(Mp4Comments.Read(source)) && !export.SourceCommentFailed,
                    $"Missing settings with comments turned on still add the comment: {note}");
            else
                Require(note is null && hash.SequenceEqual(currentHash),
                    "Missing settings with comments turned off leave the source unchanged");
        }
        await new UserPreferences { ShowThumbnails = false, ShowWaveform = false, AddSourceExportComment = true }.SaveAsync();
        await (Task<string?>)Invoke(window, "AddSourceCommentAsync", vm, export, CancellationToken.None)!;
        Require(Mp4Comments.HasExportLabel(Mp4Comments.Read(source)) && !export.SourceCommentFailed,
            "Repairing settings restores the explicitly enabled source-comment feature");

        // A damaged settings file is reset from the banner, keeping the damaged bytes and the window's choices.
        bool knownChoice = menu.IsChecked;
        await File.WriteAllTextAsync(path, "{");
        await (Task)Invoke(window, "SavePreferencesSnapshotAsync", vm, false)!;
        var reset = vm.Banner?.Actions.FirstOrDefault(a => a.Label == "Reset settings");
        Require(reset is not null && vm.StatusOverride?.Contains("could not be saved") == true, "A blocked save offers to reset damaged settings");
        reset!.Invoke();
        for (int i = 0; i < 100 && await UserPreferences.IsDamagedAsync(); i++) await Task.Delay(20);
        var kept = Directory.GetFiles(directory, "preferences.damaged-*.json");
        Require(kept.Length == 1 && await File.ReadAllTextAsync(kept[0]) == "{", "Reset keeps the damaged settings file");
        Require((await UserPreferences.ReadAsync()).AddSourceExportComment == knownChoice && menu.IsChecked == knownChoice
            && vm.Banner?.Kind == StatusKind.Success, "Reset saves the window's known choices without changing source comments");
        vm.StatusOverride = null;
        await (Task)Invoke(window, "SavePreferencesSnapshotAsync", vm, false)!;
        Require(vm.StatusOverride is null, "Settings save normally after the reset");
    }

    private delegate bool RecoveryWindowCallback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "EnumWindows")] private static extern bool RecoveryEnumWindows(RecoveryWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint RecoveryGetWindowThreadProcessId(IntPtr handle, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] private static extern int RecoveryGetWindowText(IntPtr handle, StringBuilder text, int count);
    [DllImport("user32.dll", EntryPoint = "EndDialog")] private static extern bool RecoveryEndDialog(IntPtr handle, IntPtr result);
}
