using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using AdTrim;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using AdTrim.Views;

internal static partial class Program
{
    // Recordings whose full path, and folder, exceed the classic 260-character Windows limit.
    private static int RunLongPathSmoke()
    {
        string fixture = PrepareFixture().GetAwaiter().GetResult();
        string root = Path.GetDirectoryName(fixture)!;
        string state = Path.Combine(root, "long-path-state");
        Directory.CreateDirectory(state);
        Environment.SetEnvironmentVariable("ADTRIM_DATA_DIR", state);
        File.WriteAllText(Path.Combine(state, "preferences.json"), "{\"AddSourceExportComment\":true}");
        string deep = root;
        for (int i = 1; deep.Length < 275; i++) deep = Path.Combine(deep, $"Long Path Season {i:00} with a descriptive folder name");
        Directory.CreateDirectory(deep);
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!);
        int result = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckLongPaths(app, fixture, deep); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        }));
        app.Run();
        return result;
    }

    private static async Task CheckLongPaths(App app, string fixture, string deep)
    {
        var runner = new FfmpegRunner();
        string mp4 = Path.Combine(deep, "Long Recording Name (2024) - S01E01 - Episode é.mp4");
        string ts = Path.Combine(deep, "Long Recording Name (2024) - S01E02 - Episode é.ts");
        File.Copy(fixture, mp4);
        var made = await runner.RunFfmpegAsync(["-y", "-v", "error", "-i", fixture, "-map", "0:v", "-map", "0:a", "-c", "copy", "-f", "mpegts", ts]);
        Require(made.Success && File.Exists(ts), $"FFmpeg writes a recording at a long path: {made.Stderr}");
        Console.WriteLine($"Long paths: folder {deep.Length}, MP4 {mp4.Length}, TS {ts.Length} characters");
        foreach (var source in new[] { mp4, ts })
        {
            string kind = Path.GetExtension(source).ToUpperInvariant().TrimStart('.');
            var media = await new MediaProbeService(runner).ProbeAsync(source);
            Require(media.DurationUs > 10_000_000, $"{kind}: probing reads a long-path recording");
            var store = new ProjectStore();
            store.Save(new AdTrimProject(AdTrimProject.CurrentSchemaVersion, source, ProjectStore.FingerprintOf(source, media.DurationUs), media,
                new() { new("long-cut-start", 4_000_000, SplitSource.Manual, null, null, false),
                    new("long-cut-end", 8_000_000, SplitSource.Manual, null, null, false) },
                new() { "segment-4000000-8000000" }, SidecarLocation.NextToSource));
            Require(File.Exists(store.SidecarPathFor(source)), $"{kind}: the project sidecar is written beside a long-path recording");
            var window = app.NewWindow(source);
            try
            {
                await window.Ready.WaitAsync(TimeSpan.FromSeconds(30));
                window.UpdateLayout();
                var titlePath = (TextBlock)window.FindName("TitleRecordingPath");
                var titleViewport = (ScrollViewer)window.FindName("TitlePathViewport");
                var minButton = (Button)window.FindName("MinButton");
                window.WindowState = WindowState.Normal;
                foreach (var width in new[] { 1280.0, 1600.0, 1280.0 })
                {
                    window.Width = width;
                    window.UpdateLayout();
                    await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    window.UpdateLayout();
                    var pathBounds = titlePath.TransformToAncestor(window).TransformBounds(new Rect(titlePath.RenderSize));
                    var controlsBounds = minButton.TransformToAncestor(window).TransformBounds(new Rect(minButton.RenderSize));
                    var viewportBounds = titleViewport.TransformToAncestor(window).TransformBounds(new Rect(titleViewport.RenderSize));
                    Require(window.Title == "AdTrim - " + source && titlePath.Text == source
                        && viewportBounds.Right <= controlsBounds.Left && titleViewport.ScrollableWidth > 0
                        && ((Border)window.FindName("TitlePathEllipsis")).Visibility == Visibility.Visible
                        && Math.Abs(titleViewport.HorizontalOffset - titleViewport.ScrollableWidth) < 1
                        && pathBounds.Right <= viewportBounds.Right + 1 && pathBounds.Right >= viewportBounds.Right - 1,
                        $"{kind}: resizing to {width} shows a leading ellipsis and keeps the path's final character visible without overlapping controls");
                }
                Capture(window, Path.Combine(SmokeRoot(), $"title-{kind}.png"));
                var vm = (MainViewModel)window.DataContext;
                Require(vm.IsFileLoaded && vm.SourcePath == source && vm.Segments.Any(s => s.IsExcluded),
                    $"{kind}: opening loads the recording and its long-path sidecar");
                var after = (MpvPreviewViewModel)typeof(MainWindow).GetField("_previewAfter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var before = (MpvPreviewViewModel)typeof(MainWindow).GetField("_previewBefore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                async Task Settle()
                {
                    await Task.Delay(300);
                    for (int i = 0; i < 500 && (after.IsLoading || before.IsLoading || after.IsSeeking || before.IsSeeking); i++) await Task.Delay(20);
                }
                await Settle();
                Invoke(window, "SeekTo", vm, 10_000_000L, true);
                await Settle();
                Require(Math.Abs(after.PositionUs - 10_000_000) < 100_000 && before.PositionUs > 9_800_000 && before.PositionUs < after.PositionUs,
                    $"{kind}: both preview panes decode frames at a long path ({before.PositionUs}, {after.PositionUs})");
                for (int i = 0; i < 500 && (vm.ThumbnailTiles.Count == 0 || vm.AudioLoudness is null); i++) await Task.Delay(20);
                Require(vm.ThumbnailTiles.Count > 0 && vm.AudioLoudness is not null, $"{kind}: thumbnails and the waveform read a long-path recording");

                Invoke(window, "SetCollapsed", vm, true);
                await Settle();
                Invoke(window, "SeekTo", vm, 10_000_000L, true);
                await Settle();
                var collapsedPath = (string)typeof(MpvPreviewViewModel).GetField("_currentPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(after)!;
                Require(collapsedPath.StartsWith("edl://") && Math.Abs(after.PositionUs - 6_000_000) < 100_000 && vm.Banner is null,
                    $"{kind}: collapsed playback previews kept material from a long path ({after.PositionUs}; {vm.Banner?.Body})");
                after.Play();
                await Task.Delay(700);
                after.Pause();
                Require(after.PositionUs > 6_100_000, $"{kind}: collapsed playback advances ({after.PositionUs})");
                Invoke(window, "SetCollapsed", vm, false);
                await Settle();

                vm.CommandStack.Execute(new AdTrim.Commands.ToggleConfirmedCommand(vm.Markers[1]));
                Require(await (Task<bool>)Invoke(window, "SaveProjectAsync", vm)!, $"{kind}: edits save to the long-path sidecar");
                var saved = new ProjectStore().Load(source, media);
                Require(saved.Status == SidecarLoadStatus.Loaded && saved.Project!.Splits.Any(s => s.Confirmed),
                    $"{kind}: the saved long-path sidecar reloads with the edit");

                string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
                Invoke(window, "OnExport", window, new RoutedEventArgs());
                await WaitForExportSetupAsync(window);
                var export = (ExportView)((ContentControl)window.FindName("ExportHost")).Content;
                var setup = (ExportDialogViewModel)export.DataContext;
                for (int i = 0; i < 250 && !setup.CanCheckHardware; i++) await Task.Delay(20);
                setup.SelectedAcceleration = ExportAccelerationOption.Software;
                // Each source exports in its own format, covering both the MP4 and TS assembly paths.
                setup.SelectedFormat = kind == "TS" ? ExportFormat.Ts : ExportFormat.Mp4;
                setup.OutputFolder = deep;
                setup.OutputFilename = $"Long Recording Export {kind}.{kind.ToLowerInvariant()}";
                setup.DeleteSidecarAfterExport = false;
                Invoke(export, "OnExport", export, new RoutedEventArgs());
                for (int i = 0; i < 3000 && export.IsExportInFlight; i++) await Task.Delay(20);
                string output = Path.Combine(deep, setup.OutputFilename);
                Require(setup.IsCompleted && !export.SourceCommentFailed && File.Exists(output),
                    $"{kind}: export to a long path completes and records itself: {export.SourceCommentNote}; mode={setup.Mode}; error={setup.ErrorMessage}");
                var exported = await new MediaProbeService(runner).ProbeAsync(output);
                Require(Math.Abs(exported.DurationUs - 8_000_000) < 300_000, $"{kind}: the long-path export keeps only the kept material ({exported.DurationUs})");
                var current = await new MediaProbeService(runner).ProbeAsync(source);
                var record = new ProjectStore().Load(source, current);
                Require(record.Project?.LastExport is { Application: "AdTrim" }, $"{kind}: the export is recorded in the long-path sidecar");
                if (kind == "MP4")
                    Require(Mp4Comments.HasExportLabel(Mp4Comments.Read(source)) && !SourceCommentService.HasPendingUpdate(source),
                        "MP4: the long-path source receives its export comment");
                else
                    Require(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))) == hash,
                        "TS: the long-path source stays byte-for-byte unchanged");
                export.Close();
            }
            finally
            {
                typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close();
            }
        }
    }
}
