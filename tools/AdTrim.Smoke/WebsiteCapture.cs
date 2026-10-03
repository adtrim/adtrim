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
                Invoke(window, "OnExport", window, new RoutedEventArgs());
                var view = (AdTrim.Views.ExportView)((ContentControl)window.FindName("ExportHost")).Content;
                var exportVm = (ExportDialogViewModel)view.DataContext;
                exportVm.OutputFolder = @"C:\Videos\Big Buck Bunny";
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
