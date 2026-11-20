using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using AdTrim;
using AdTrim.Services;
using AdTrim.ViewModels;
using AdTrim.Views;

internal static partial class Program
{
    private static int RunSourceCommentSmoke()
    {
        string source = PrepareFixture().GetAwaiter().GetResult();
        string directory = Path.Combine(Path.GetDirectoryName(source)!, "source-comment-state");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("ADTRIM_DATA_DIR", directory);
        File.WriteAllText(Path.Combine(directory, "preferences.json"), "{\"AddSourceExportComment\":false}");
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!);
        int result = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckSourceComments(app, source, directory); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        }));
        app.Run();
        return result;
    }

    private static async Task CheckSourceComments(App app, string original, string directory)
    {
        string source = Path.Combine(directory, "comment-source.mp4");
        File.Copy(original, source);
        var sourceMedia = await new MediaProbeService(new FfmpegRunner()).ProbeAsync(source);
        new ProjectStore().Save(new AdTrim.Models.AdTrimProject(AdTrim.Models.AdTrimProject.CurrentSchemaVersion,
            source, ProjectStore.FingerprintOf(source, sourceMedia.DurationUs), sourceMedia,
            new() { new("comment-cut-start", 4_000_000, AdTrim.Models.SplitSource.Manual, null, null, false),
                new("comment-cut-end", 8_000_000, AdTrim.Models.SplitSource.Manual, null, null, false) },
            new() { "segment-4000000-8000000" }, AdTrim.Models.SidecarLocation.NextToSource));
        var window = app.NewWindow(source);
        try
        {
            await window.Ready;
            var vm = (MainViewModel)window.DataContext;
            window.Test.ReleaseAudioPass(vm);
            window.Test.CancelThumbnailLoad(vm, true);
            string thumbnailRoot = Path.Combine(directory, "comment-thumbnails"), audioRoot = Path.Combine(directory, "comment-levels");
            var thumbnails = new ThumbnailService(new FfmpegRunner(), thumbnailRoot);
            var tiles = await thumbnails.ExtractAsync(source, sourceMedia.DurationUs, [0]);
            Require(tiles.Count == 1, "Comment-cache regression has a real cached thumbnail");
            var audio = new AudioLevelService(new FfmpegRunner(), audioRoot);
            var levels = new AudioLevels([20, 40, 60], [true]);
            audio.SaveCached(source, sourceMedia.PrimaryAudioIndex, sourceMedia.DurationUs, levels);
            window.Test.Thumbnails = thumbnails;
            window.Test.AudioLevels = audio;
            var suggestions = new MarkerSuggestionStore();
            var proposal = AdTrim.Commands.ReplaceMarkerSetCommand.Capture(vm) with { Source = AdTrim.Models.MarkerSetSource.AdTrim, BaselineId = null };
            proposal = proposal with { BaselineId = proposal.ContentId() };
            suggestions.Save(suggestions.Key(source, vm.SourceFingerprint!), proposal);
            Require(!WindowPreferences(window).AddSourceExportComment, "Source-comment opt-out loads from the isolated preferences");
            var fileMenu = (MenuItem)((Menu)window.FindName("MainMenu")).Items[0];
            Require(fileMenu.Items.Contains(window.FindName("SettingsMenuItem")) && window.FindName("SourceCommentMenu") is null,
                "The source-comment choice is in Settings, not a File menu toggle");
            var items = fileMenu.Items.OfType<MenuItem>().ToArray();
            int openIndex = Array.FindIndex(items, i => i.Header?.ToString() == "Open…");
            Require(items[openIndex + 1].Header?.ToString() == "View Unexported Files…", "Unexported-file entry is immediately below Open");
            Require(items[openIndex + 1].InputGestureText == "Ctrl+Shift+O" && items[openIndex].InputGestureText == "Ctrl+O",
                "File menu shows distinct shortcuts for opening a recording and viewing unexported files");
            await ChangeSettings(window, p => p with { AddSourceExportComment = true });
            Require((await UserPreferences.LoadAsync()).AddSourceExportComment && WindowPreferences(window).AddSourceExportComment,
                "The source-comment setting persists between windows and sessions");
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
            window.Test.Export();
            await WaitForExportSetupAsync(window);
            var export = (ExportView)((ContentControl)window.FindName("ExportHost")).Content;
            var setup = (ExportDialogViewModel)export.DataContext;
            for (int i = 0; i < 250 && !setup.CanCheckHardware; i++) await Task.Delay(20);
            setup.SelectedAcceleration = ExportAccelerationOption.Software;
            setup.OutputFolder = directory;
            setup.OutputFilename = "comment-export.mp4";
            setup.DeleteSidecarAfterExport = false;
            Invoke(export, "OnExport", export, new RoutedEventArgs());
            for (int i = 0; i < 3000 && export.IsExportInFlight; i++) await Task.Delay(20);
            Require(setup.IsCompleted && !export.SourceCommentFailed, $"Actual export completes its source-comment phase: {export.SourceCommentNote}; mode={setup.Mode}; error={setup.ErrorMessage}; validation={string.Join("; ", setup.Validate().Select(v => v.Message))}");
            Require(Mp4Comments.HasExportLabel(Mp4Comments.Read(source)), "Source receives a discoverable successful-export comment");
            Require(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))) != hash, "Commenting changes source metadata only after successful export");
            var updatedFingerprint = vm.SourceFingerprint!;
            Require(vm.HasAdTrimAnalysis && suggestions.Load(suggestions.Key(source, updatedFingerprint), sourceMedia.DurationUs)?.BaselineId == proposal.BaselineId,
                "Actual export retains the original ad-break proposal under the updated source identity");
            Require(thumbnails.LoadCached(source, sourceMedia.DurationUs).Count >= 1,
                "Actual export retains cached thumbnails after adding its comment");
            Require(audio.TryLoadCached(source, sourceMedia.PrimaryAudioIndex, sourceMedia.DurationUs, out var retainedLevels)
                && retainedLevels.Loudness.SequenceEqual(levels.Loudness), "Actual export retains cached audio levels");
            var media = await new MediaProbeService(new FfmpegRunner()).ProbeAsync(source);
            var saved = new ProjectStore().Load(source, media);
            Require(saved.Project is not null && saved.Status != SidecarLoadStatus.FingerprintMismatch, "Retained project matches the marked source");
            Require(saved.Project!.LastExport is { Application: "AdTrim" } record && record.Version == AppVersion.Numeric
                && record.SourceFingerprint == saved.Project.Fingerprint && record.ExportedAtUtc.Offset == TimeSpan.Zero,
                "Successful export records structured version, UTC time and source identity in the sidecar");
            export.Close();
            Require(!vm.IsExportScreen && vm.IsFileLoaded, "Marked recording returns to its editor");
            string unmarkedSource = Path.Combine(directory, "unmarked-comment-source.mp4");
            File.Copy(original, unmarkedSource);
            string[] episodePaths = { "Zebra/Season 2/S02E10.mp4", "Alpha/Season 10/S10E01.mp4",
                "Alpha/Season 2/S02E10.mp4", "Alpha/Season 2/S02E02.mp4", "Alpha Extra/Season 1/S01E01.mp4" };
            foreach (string episode in episodePaths)
            {
                string path = Path.Combine(directory, episode);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Copy(original, path);
            }
            Directory.CreateDirectory(Path.Combine(directory, "Empty season"));
            Directory.CreateDirectory(Path.Combine(directory, "Empty show", "Season 08"));
            Directory.CreateDirectory(Path.Combine(directory, "Alpha", "Season 99"));
            Directory.CreateDirectory(Path.Combine(directory, "Exported show", "Season 08"));
            File.Copy(source, Path.Combine(directory, "Exported show", "Season 08", "exported.mp4"));
            Directory.CreateDirectory(Path.Combine(directory, "Needs checking"));
            File.WriteAllText(Path.Combine(directory, "Needs checking", "broken.mp4"), "Cannot determine export status");
            Directory.CreateDirectory(Path.Combine(directory, ".ts_archive", "nested"));
            File.WriteAllText(Path.Combine(directory, ".ts_archive", "nested", "ignored.mp4"), "Must never be inspected");
            Directory.CreateDirectory(Path.Combine(directory, "Alpha", ".cache"));
            File.WriteAllText(Path.Combine(directory, "Alpha", ".cache", "ignored.mp4"), "Must never be inspected");
            var list = new UnexportedFilesDialog(directory) { Owner = window, ShowActivated = false };
            list.Show();
            list.UpdateLayout();
            for (int i = 0; i < 250 && ((TextBlock)list.FindName("StatusLabel")).Text.StartsWith("Scanning"); i++) await Task.Delay(20);
            Require(((TextBlock)list.FindName("StatusLabel")).Text.StartsWith("Scan complete"), "Unexported-file dialog completes scanning");
            var rows = ((ListBox)list.FindName("FilesList")).Items.Cast<UnexportedFilesDialog.FileRow>().ToArray();
            Require(rows.Any(r => r.FullPath == unmarkedSource && r.CanOpen) && rows.All(r => r.FullPath != source),
                "Root selection shows unmarked files throughout the subtree and hides marked sources");
            var episodes = rows.Select(r => r.RelativePath.Replace('\\', '/')).Where(p => episodePaths.Contains(p)).ToArray();
            Require(episodes.SequenceEqual(new[] { episodePaths[3], episodePaths[2], episodePaths[1], episodePaths[4], episodePaths[0] }),
                "Picker groups show folders alphabetically and sorts season and episode numbers naturally");
            Capture(list, Path.Combine(directory, "unexported-files.png"));
            Require(rows.Where(r => r.CanOpen).All(r => r.Detail == ""), "Ordinary rows do not repeat the export-comment explanation");
            var tree = (TreeView)list.FindName("FolderTree");
            var root = (TreeViewItem)tree.Items[0];
            Require(tree.Visibility == Visibility.Visible && root.IsSelected && root.IsExpanded && list.FindName("ViewToggle") is null,
                "The only view opens with its root selected and expanded");
            Require(root.Items.Cast<TreeViewItem>().All(c => !((string)c.Header).StartsWith('.'))
                && rows.All(r => !r.RelativePath.Contains(".ts_archive") && !r.RelativePath.Contains(".cache")),
                "Leading-dot folders and their contents are absent from the tree and file list");
            var children = root.Items.Cast<TreeViewItem>().Where(c => new[] { "Alpha", "Zebra" }.Contains((string)c.Header)).ToArray();
            Require(children.Select(c => (string)c.Header).SequenceEqual(new[] { "Alpha", "Zebra" })
                && root.Items.Cast<TreeViewItem>().All(c => !new[] { "Empty season", "Empty show", "Exported show", "export-cleanup" }.Contains((string)c.Header)),
                "Empty and fully exported folders and their empty parent shows are absent");
            Require(root.Items.Cast<TreeViewItem>().Any(c => (string)c.Header == "Needs checking"),
                "Folders containing scan errors remain accessible");
            var alpha = children[0];
            Require(alpha.Items.Cast<TreeViewItem>().Select(c => (string)c.Header).SequenceEqual(new[] { "Season 2", "Season 10" }),
                "Folder tree orders season numbers naturally");
            alpha.IsExpanded = true;
            alpha.IsSelected = true;
            Require(((ListBox)list.FindName("FilesList")).Items.Cast<UnexportedFilesDialog.FileRow>()
                .Select(r => r.FileName).SequenceEqual(new[] { "S02E02.mp4", "S02E10.mp4", "S10E01.mp4" }),
                "Selecting a show includes every season while excluding similarly named sibling shows");
            Capture(list, Path.Combine(directory, "unexported-show.png"));
            ((TreeViewItem)alpha.Items[0]).IsSelected = true;
            list.UpdateLayout();
            var selectedRows = ((ListBox)list.FindName("FilesList")).Items.Cast<UnexportedFilesDialog.FileRow>().ToArray();
            Require(selectedRows.Select(r => r.FileName).SequenceEqual(new[] { "S02E02.mp4", "S02E10.mp4" }),
                "Selecting a season shows only that folder's episodes in numeric order");
            var area = (Rect)typeof(MainWindow).Assembly.GetType("AdTrim.Services.MonitorBounds")!
                .GetMethod("WorkArea", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, new object?[] { list, null })!;
            Require(list.Width == Math.Min(1150, area.Width) && list.Height == Math.Min(760, area.Height)
                && list.Left >= area.Left && list.Top >= area.Top && list.Left + list.Width <= area.Right + 1
                && list.Top + list.Height <= area.Bottom + 1, "Larger dialog fits the current monitor work area");
            string refreshedFile = Path.Combine(directory, "Alpha", "Season 2", "S02E03.mp4");
            File.Copy(original, refreshedFile);
            File.Delete(selectedRows[0].FullPath);
            string outsideFile = Path.Combine(directory, "Zebra", "Season 2", "not-refreshed.mp4");
            File.Copy(original, outsideFile);
            var refresh = (Button)list.FindName("RefreshButton");
            Invoke(list, "OnRefresh", refresh, new RoutedEventArgs());
            for (int i = 0; i < 250 && !refresh.IsEnabled; i++) await Task.Delay(20);
            Require(((ListBox)list.FindName("FilesList")).Items.Cast<UnexportedFilesDialog.FileRow>()
                .Select(r => r.FileName).SequenceEqual(new[] { "S02E03.mp4", "S02E10.mp4" })
                && ((TreeViewItem)alpha.Items[0]).IsSelected,
                "Refresh replaces stale results with new files in the selected folder and keeps its selection");
            File.Delete(outsideFile);
            Capture(list, Path.Combine(directory, "unexported-folders.png"));
            alpha.IsSelected = true;
            string newSeason = Path.Combine(directory, "Alpha", "Season 3");
            Directory.CreateDirectory(newSeason);
            File.Copy(original, Path.Combine(newSeason, "S03E01.mp4"));
            File.Delete(Path.Combine(directory, "Alpha", "Season 10", "S10E01.mp4"));
            Directory.Delete(Path.Combine(directory, "Alpha", "Season 10"));
            Invoke(list, "OnRefresh", refresh, new RoutedEventArgs());
            for (int i = 0; i < 250 && !refresh.IsEnabled; i++) await Task.Delay(20);
            Require(alpha.IsSelected && alpha.IsExpanded
                && alpha.Items.Cast<TreeViewItem>().Select(c => (string)c.Header).SequenceEqual(new[] { "Season 2", "Season 3" })
                && ((ListBox)list.FindName("FilesList")).Items.Cast<UnexportedFilesDialog.FileRow>()
                    .Select(r => r.RelativePath.Replace('\\', '/')).SequenceEqual(new[] { "Alpha/Season 2/S02E03.mp4", "Alpha/Season 2/S02E10.mp4", "Alpha/Season 3/S03E01.mp4" }),
                "Refreshing a show updates descendant folders and files while retaining expansion and root-relative paths");
            root.IsSelected = true;
            Require(((ListBox)list.FindName("FilesList")).Items.Count == rows.Length
                && ((ListBox)list.FindName("FilesList")).Items.Cast<UnexportedFilesDialog.FileRow>().All(r => r.FullPath != outsideFile),
                "Refresh leaves sibling-folder results intact without scanning them");
            alpha.IsSelected = true;
            foreach (var row in ((ListBox)list.FindName("FilesList")).Items.Cast<UnexportedFilesDialog.FileRow>().ToArray()) File.Delete(row.FullPath);
            Invoke(list, "OnRefresh", refresh, new RoutedEventArgs());
            for (int i = 0; i < 250 && !refresh.IsEnabled; i++) await Task.Delay(20);
            Require(root.IsSelected && root.Items.Cast<TreeViewItem>().All(c => (string)c.Header != "Alpha")
                && ((ListBox)list.FindName("FilesList")).Items.Count == rows.Length - 3,
                "Refreshing the last files away removes empty seasons and their show, then selects the nearest retained ancestor");
            rows = ((ListBox)list.FindName("FilesList")).Items.Cast<UnexportedFilesDialog.FileRow>().ToArray();
            list.Close();
            var reopened = new UnexportedFilesDialog(directory) { Owner = window, ShowActivated = false };
            reopened.Show();
            for (int i = 0; i < 250 && ((TextBlock)reopened.FindName("StatusLabel")).Text.StartsWith("Scanning"); i++) await Task.Delay(20);
            Require(((TreeViewItem)((TreeView)reopened.FindName("FolderTree")).Items[0]).IsSelected
                && ((ListBox)reopened.FindName("FilesList")).Items.Count == rows.Length, "Reopening the picker selects the root and shows all its descendants");
            reopened.Close();
            var doubleClickDialog = new UnexportedFilesDialog(directory) { Owner = window };
            Exception? doubleClickError = null;
            string? clickedPath = null;
            doubleClickDialog.Loaded += async (_, _) =>
            {
                try
                {
                    var files = (ListBox)doubleClickDialog.FindName("FilesList");
                    for (int i = 0; i < 250 && !((Button)doubleClickDialog.FindName("RefreshButton")).IsEnabled; i++) await Task.Delay(20);
                    doubleClickDialog.UpdateLayout();
                    var item = (ListBoxItem)files.ItemContainerGenerator.ContainerFromIndex(0);
                    clickedPath = ((UnexportedFilesDialog.FileRow)item.DataContext).FullPath;
                    item.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                        { RoutedEvent = Control.MouseDoubleClickEvent });
                }
                catch (Exception ex) { doubleClickError = ex; doubleClickDialog.Close(); }
            };
            bool? doubleClickResult = doubleClickDialog.ShowDialog();
            if (doubleClickError is not null) throw doubleClickError;
            Require(doubleClickResult == true && doubleClickDialog.SelectedPath == clickedPath && clickedPath is not null,
                "Double-clicking a real file row selects that file and closes the modal through its normal open flow");
            string emptyFolder = Path.Combine(directory, "Completely empty");
            Directory.CreateDirectory(emptyFolder);
            var emptyDialog = new UnexportedFilesDialog(emptyFolder) { Owner = window, ShowActivated = false };
            emptyDialog.Show();
            for (int i = 0; i < 250 && !((Button)emptyDialog.FindName("RefreshButton")).IsEnabled; i++) await Task.Delay(20);
            var emptyRoot = (TreeViewItem)((TreeView)emptyDialog.FindName("FolderTree")).Items[0];
            Require(emptyRoot.IsSelected && emptyRoot.IsExpanded && emptyRoot.Items.Count == 0
                && ((TextBlock)emptyDialog.FindName("EmptyLabel")).Visibility == Visibility.Visible,
                "An entirely empty scan retains only its selected root with an empty-state message");
            emptyDialog.Close();
        }
        finally
        {
            var latest = await UserPreferences.LoadAsync();
            await (latest with { AddSourceExportComment = false }).SaveChangesAsync(latest, null);
            window.Test.AllowClose();
            window.Close();
        }
        await CheckSidecarOnlyExport(app, original, directory);
    }

    private static async Task CheckSidecarOnlyExport(App app, string original, string directory)
    {
        string source = Path.Combine(directory, "sidecar-only-source.mp4");
        File.Copy(original, source);
        var media = await new MediaProbeService(new FfmpegRunner()).ProbeAsync(source);
        var store = new ProjectStore();
        store.Save(new AdTrim.Models.AdTrimProject(AdTrim.Models.AdTrimProject.CurrentSchemaVersion,
            source, ProjectStore.FingerprintOf(source, media.DurationUs), media,
            new() { new("sidecar-cut-start", 4_000_000, AdTrim.Models.SplitSource.Manual, null, null, false),
                new("sidecar-cut-end", 8_000_000, AdTrim.Models.SplitSource.Manual, null, null, false) },
            new() { "segment-4000000-8000000" }, AdTrim.Models.SidecarLocation.NextToSource));
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        var window = app.NewWindow(source);
        try
        {
            await window.Ready;
            Require(((MainViewModel)window.DataContext).LastExport is null, "Opening an unexported project does not create an export record");
            window.Test.Export();
            await WaitForExportSetupAsync(window);
            var export = (ExportView)((ContentControl)window.FindName("ExportHost")).Content;
            var setup = (ExportDialogViewModel)export.DataContext;
            for (int i = 0; i < 250 && !setup.CanCheckHardware; i++) await Task.Delay(20);
            setup.SelectedAcceleration = ExportAccelerationOption.Software;
            setup.OutputFolder = directory;
            setup.OutputFilename = "sidecar-only-export.mp4";
            setup.DeleteSidecarAfterExport = false;
            Invoke(export, "OnExport", export, new RoutedEventArgs());
            for (int i = 0; i < 3000 && export.IsExportInFlight; i++) await Task.Delay(20);
            Require(setup.IsCompleted && !export.SourceCommentFailed, "Export with source comments disabled still saves its sidecar record: " + export.SourceCommentNote);
            Require(hash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))), "Sidecar-only export leaves every source byte unchanged");
            Require(store.HasExportRecord(source) && store.Load(source, media).Project?.LastExport is not null,
                "Sidecar-only export is recorded durably in the existing project");
            var rows = new List<UnexportedFile>();
            await foreach (var row in UnexportedFiles.ScanAsync(directory)) rows.Add(row);
            Require(rows.Single(r => r.Path == source).Exported, "Empty MP4 Comments fall back to the successful sidecar export record");
            export.Close();
            window.Test.CloseProject();
            await window.OpenFileAsync(source);
            Require(((MainViewModel)window.DataContext).LastExport is not null, "Reopening a project restores its export record");
        }
        finally
        {
            window.Test.AllowClose();
            window.Close();
        }
    }
}
