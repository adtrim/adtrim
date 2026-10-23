using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdTrim;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;

internal static partial class Program
{
    private static int CaptureWebsite(string original, string output)
    {
        original = Path.GetFullPath(original);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var scratch = Path.Combine(AppContext.BaseDirectory, "website-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        byte[] HashSource() { using var stream = File.OpenRead(original); return SHA256.HashData(stream); }
        var originalHash = HashSource();
        var source = Path.Combine(scratch, "Big Buck Bunny (2008).mp4");
        File.Copy(original, source);
        Environment.SetEnvironmentVariable("ADTRIM_DATA_DIR", Path.Combine(scratch, "settings"));
        UpdatePreferences.Save(UserPreferences.DataDirectory, false);
        var media = new MediaProbeService(new FfmpegRunner()).ProbeAsync(source).GetAwaiter().GetResult();
        var splits = new List<PersistedSplit>
        {
            new("break-one", 130_000_000, SplitSource.Chapter, null, null, true, "Excluded scene 1"),
            new("return-one", 179_700_000, SplitSource.Chapter, null, null, false, "Part 2"),
            new("break-two", 300_000_000, SplitSource.Refined, 300_050_000, Confidence.Medium, false, "Excluded scene 2"),
            new("return-two", 340_000_000, SplitSource.Refined, 340_050_000, Confidence.Low, false, "Part 3"),
            new("break-three", 480_000_000, SplitSource.Chapter, null, null, false, "Excluded scene 3"),
            new("return-three", 520_000_000, SplitSource.Refined, 520_050_000, Confidence.High, true, "Part 4"),
        };
        new ProjectStore(Path.Combine(scratch, "fallback")).Save(new AdTrimProject(
            AdTrimProject.CurrentSchemaVersion, source, ProjectStore.FingerprintOf(source, media.DurationUs), media,
            splits, new(), SidecarLocation.NextToSource, "Part 1"));
        var app = new App();
        app.InitializeComponent();
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!);
        var window = new MainWindow(source) { Width = 1440, Height = 900, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        var result = 1;
        window.Show();
        window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await window.Ready;
                window.Width = 1440;
                window.Height = 900;
                window.UpdateLayout();
                var vm = (MainViewModel)window.DataContext;
                await WaitForTimelineMedia(window, vm);
                // Consecutive frames here show clear head and ear movement without a scene cut.
                await SeekWebsite(window, vm, 179_700_000);
                CaptureWebsiteEditor(window, Path.Combine(output, "walkthrough-open.png"), scratch);
                foreach (var start in new long[] { 130_000_000, 300_000_000, 480_000_000 })
                    vm.Segments.Single(s => s.StartUs == start).State = SegmentState.Excluded;
                vm.RefreshKeptTimeline();
                vm.SelectMarker(vm.Markers.Single(m => m.TimeUs == 179_700_000));
                await SeekWebsite(window, vm, 179_700_000);
                CaptureWebsiteEditor(window, Path.Combine(output, "walkthrough-edit.png"), scratch);
                CaptureWebsiteEditor(window, Path.Combine(output, "timeline-hero.png"), scratch);
                await CaptureWebsiteTimelineAsync(window, vm, output, media.DurationUs);
                Invoke(window, "OnExport", window, new RoutedEventArgs());
                await WaitForExportSetupAsync(window);
                var view = (AdTrim.Views.ExportView)((ContentControl)window.FindName("ExportHost")).Content;
                var exportVm = (ExportDialogViewModel)view.DataContext;
                exportVm.OutputFolder = @"C:\Videos\Big Buck Bunny";
                for (int i = 0; i < 250 && !exportVm.CanCheckHardware; i++) await Task.Delay(20);
                Require(exportVm.CanCheckHardware, "Website export setup is ready");
                exportVm.SelectedFormat = ExportFormat.Mp4;
                Capture(window, Path.Combine(output, "export-setup-mp4.png"));
                exportVm.SelectedFormat = ExportFormat.Ts;
                Capture(window, Path.Combine(output, "export-setup-ts.png"));
                exportVm.SelectedFormat = ExportFormat.Mp4;
                var plan = new ExportPlan(source, Path.Combine(exportVm.OutputFolder, exportVm.OutputFilename), media.DurationUs,
                    1, vm.Segments.Where(s => s.State != SegmentState.Excluded).Select((s, i) => new ExportSegment(i + 1, s.StartUs, s.EndUs, s.Label ?? $"Part {i + 1}")).ToArray(), vm.FrameRate);
                exportVm.BeginExport(plan);
                exportVm.UpdateProgress(new(ExportPhase.EncodingSegment, 3, plan.KeptSegments.Count, 0.5, 0.42,
                    "Encoding part 3/4", "Software"));
                ((System.Windows.Threading.DispatcherTimer?)typeof(ExportDialogViewModel).GetField("_tickTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(exportVm))?.Stop();
                typeof(ExportDialogViewModel).GetProperty("ElapsedFormatted")!.SetValue(exportVm, "00:42");
                typeof(ExportDialogViewModel).GetProperty("RemainingFormatted")!.SetValue(exportVm, "~00:58 remaining");
                Capture(window, Path.Combine(output, "walkthrough-export.png"));
                exportVm.MarkCancelled();
                view.Close();
                Require(originalHash.SequenceEqual(HashSource()), "Website capture leaves the original recording unchanged");
                Console.WriteLine("Website screenshots: " + output);
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { window.Close(); app.Shutdown(result); }
        }));
        app.Run();
        // This directory is created by this invocation and contains only its disposable copy.
        if (Path.GetDirectoryName(scratch) == Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))
            Directory.Delete(scratch, true);
        return result;
    }

    private static async Task SeekWebsite(MainWindow window, MainViewModel vm, long time)
    {
        Invoke(window, "SeekTo", vm, time, true);
        await Task.Delay(300);
        for (int attempt = 0; attempt < 250; attempt++)
        {
            var players = new[] { "_previewBefore", "_previewAfter" }.Select(field =>
                (MpvPreviewViewModel)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).ToArray();
            if (players.All(p => !p.IsLoading && !p.IsSeeking && p.PositionUs > 0)) return;
            await Task.Delay(20);
        }
        throw new InvalidOperationException("Preview frames did not settle for the screenshot.");
    }

    // Close-ups for the What's new and home pages, rendered at 2x so they stay sharp on
    // high-density screens. The video surfaces are not part of any crop.
    private static void CaptureWebsiteCrops(MainWindow window, string output, params (string Name, Rect Area)[] crops)
    {
        const double scale = 2;
        var content = (FrameworkElement)window.Content;
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)(content.ActualWidth * scale), (int)(content.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content);
        foreach (var (name, crop) in crops)
        {
            var area = crop;
            area.Intersect(new Rect(content.RenderSize));
            var pixels = new Int32Rect((int)Math.Round(area.X * scale), (int)Math.Round(area.Y * scale),
                (int)Math.Round(area.Width * scale), (int)Math.Round(area.Height * scale));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(bitmap, pixels)));
            using var file = File.Create(Path.Combine(output, name));
            encoder.Save(file);
        }
    }

    private static Rect WebsiteBounds(MainWindow window, FrameworkElement element) =>
        element.TransformToAncestor((Visual)window.Content).TransformBounds(new Rect(element.RenderSize));

    private static async Task CaptureWebsiteTimelineAsync(MainWindow window, MainViewModel vm, string output, long durationUs)
    {
        var timeline = (AdTrim.Controls.TimelineView)window.FindName("Timeline");
        var whole = WebsiteBounds(window, timeline);
        whole.Inflate(8, 8);
        var toolbar = Rect.Union(WebsiteBounds(window, (FrameworkElement)window.FindName("MarkersButton")),
            WebsiteBounds(window, (FrameworkElement)window.FindName("ClearAllMarkingsButton")));
        toolbar.Inflate(6, 6);
        CaptureWebsiteCrops(window, output, ("timeline.png", whole), ("analyze-toolbar.png", toolbar));

        // Zoom in on the second excluded scene (300-340 s) and the silences just after it.
        vm.ZoomFactor = 3;
        window.UpdateLayout();
        ((ScrollViewer)window.FindName("TimelineScroll")).ScrollToHorizontalOffset(timeline.ActualWidth * 280_000_000 / durationUs);
        await Task.Delay(300);
        await WaitForTimelineMedia(window, vm);
        var thumbs = WebsiteBounds(window, (FrameworkElement)timeline.FindName("ThumbCanvas"));
        var wave = WebsiteBounds(window, (FrameworkElement)timeline.FindName("WaveformCanvas"));
        double X(long us) => thumbs.X + thumbs.Width * us / durationUs;
        CaptureWebsiteCrops(window, output,
            ("timeline-detail.png", new Rect(new Point(X(285_000_000), thumbs.Top - 4), new Point(X(375_000_000), wave.Bottom + 4))));
        vm.ZoomFactor = 1;
        window.UpdateLayout();
    }

    // The site shows the finished thumbnail strip and waveform, not a partly loaded timeline.
    private static async Task WaitForTimelineMedia(MainWindow window, MainViewModel vm)
    {
        object? Field(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window);
        for (int attempt = 0; attempt < 6000; attempt++)
        {
            if (Field("_audioPass") is AudioLevelPass { Completion.IsCompletedSuccessfully: true } && vm.AudioLoudness is not null
                && Field("_thumbnailLoader") is ThumbnailLoader { IsBusy: false } && vm.ThumbnailTiles.Count > 0)
            {
                await Task.Delay(500);
                if (Field("_thumbnailLoader") is ThumbnailLoader { IsBusy: false }) return;
            }
            await Task.Delay(20);
        }
        throw new InvalidOperationException("Thumbnails and the waveform did not finish loading for the screenshot.");
    }

    private static void CaptureWebsiteEditor(MainWindow window, string path, string scratch)
    {
        var content = (FrameworkElement)window.Content;
        window.UpdateLayout();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(content.RenderSize));
            foreach (var pair in new[] { ("_previewBefore", "MpvViewBefore"), ("_previewAfter", "MpvViewAfter") })
            {
                var player = typeof(MainWindow).GetField(pair.Item1, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                var ctx = (IntPtr)typeof(MpvPreviewViewModel).GetField("_ctx", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
                var imagePath = Path.Combine(scratch, pair.Item1 + ".png");
                var command = typeof(App).Assembly.GetType("AdTrim.Services.LibMpv")!.GetMethod("Command")!;
                var status = (int)command.Invoke(null, new object[] { ctx, new[] { "screenshot-to-file", imagePath, "video" } })!;
                if (status < 0) throw new InvalidOperationException("Could not capture the decoded preview frame.");
                var frame = new BitmapImage();
                frame.BeginInit(); frame.CacheOption = BitmapCacheOption.OnLoad; frame.UriSource = new Uri(imagePath); frame.EndInit(); frame.Freeze();
                var host = (FrameworkElement)window.FindName(pair.Item2);
                var bounds = host.TransformToAncestor(content).TransformBounds(new Rect(host.RenderSize));
                drawing.DrawRectangle(Brushes.Black, null, bounds);
                var scale = Math.Min(bounds.Width / frame.PixelWidth, bounds.Height / frame.PixelHeight);
                var size = new Size(frame.PixelWidth * scale, frame.PixelHeight * scale);
                drawing.DrawImage(frame, new Rect(bounds.X + (bounds.Width - size.Width) / 2, bounds.Y + (bounds.Height - size.Height) / 2, size.Width, size.Height));
            }
        }
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
