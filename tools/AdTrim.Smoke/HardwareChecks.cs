using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdTrim.Encoders;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using AdTrim.Views;

internal static class HardwareChecks
{
    private sealed class Sink(Action<ExportProgress> action) : IProgress<ExportProgress>
    { public void Report(ExportProgress value) => action(value); }

    private sealed class FailedHardware(bool invalidInput = false) : IEncoderStrategy
    {
        public int Attempts { get; private set; }
        public string DisplayName => "Unavailable test adapter";
        public bool IsHardware => true;
        public IReadOnlyList<string> BuildSegmentArgs(string source, ExportSegment segment, int audio, string output)
        {
            Attempts++;
            if (invalidInput) return new[] { "-i", source + ".missing", output };
            if (Attempts == 1) return new LibX264EncoderStrategy().BuildSegmentArgs(source, segment, audio, output);
            throw new HardwareExportException("Simulated device loss after the first segment.");
        }
    }

    public static async Task RunAsync(Window owner, string directory, MainViewModel project, MediaInfo media)
    {
        var runner = new FfmpegRunner();
        var detector = new HardwareEncoderDetection(runner, directory);
        var cachePath = Path.Combine(directory, "hardware-encoders.json");
        Require(!File.Exists(cachePath), "Startup and editing do not benchmark hardware");
        var coldVm = new ExportDialogViewModel(project, media);
        await coldVm.LoadOptionsAsync(detector, "software", CancellationToken.None);
        Require(!File.Exists(cachePath) && coldVm.SelectedAcceleration.Id == "software" && coldVm.AccelerationOptions.Count >= 2,
            "Options can be listed on a cold cache without test encoding");
        var result = await detector.DetectAsync(true);
        Require(result.Error is null, result.Error ?? "Hardware enumeration completed");
        foreach (var capability in result.Results)
            Console.WriteLine($"HARDWARE: {capability.Adapter.Name}; driver={capability.Adapter.DriverVersion}; available={capability.Available}; {capability.Detail}");
        foreach (uint vendor in new uint[] { 0x10de, 0x8086, 0x1002 })
            if (!result.Results.Any(r => r.Adapter.VendorId == vendor && r.Available))
                Console.WriteLine($"UNTESTED: vendor {vendor:x4} has no validated local adapter.");
        var cached = await detector.DetectAsync();
        Require(result.Results.SequenceEqual(cached.Results), "Capability cache preserves adapter identities and results");

        var dialog = new ExportView();
        dialog.Bind(project, media);
        dialog.AttachExportRunner(runner, new Sink(_ => { }));
        var vm = (ExportDialogViewModel)dialog.DataContext;
        Require(new UserPreferences().ExportAcceleration == "automatic"
            && JsonSerializer.Deserialize<UserPreferences>("{}")!.ExportAcceleration == "automatic",
            "New and pre-acceleration preferences default to Automatic");
        Require(vm.SelectedAcceleration.Id == "automatic" && vm.Evaluation is null,
            "Export starts with Automatic and no evaluation");
        bool sawChecking = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.AccelerationLabel) && vm.IsCheckingHardware)
            {
                sawChecking = true;
                Require(vm.AccelerationLabel == "Evaluating options..." && !vm.CanCheckHardware,
                    "Evaluation label is shown while rechecking is disabled");
            }
        };
        Program.ShowExportForTest(dialog, owner);
        for (int i = 0; i < 150 && !vm.CanCheckHardware; i++) await Task.Delay(100);
        Require(!vm.IsCheckingHardware, "Export hardware check finishes without blocking the dispatcher");
        Require(vm.AccelerationOptions.Count == result.Results.Count + 2,
            "Detected adapters appear alongside Automatic and Software without benchmarking");
        Require(!sawChecking && vm.SelectedAcceleration.Id == "automatic" && vm.AccelerationLabel == "Automatic",
            "Opening Export lists options without evaluating them");
        Require(vm.Evaluation is not null,
            "Completed evaluation results remain available to the options popup");
        await vm.CheckHardwareAsync(detector, "software", false, CancellationToken.None);
        Require(vm.SelectedAcceleration.Id == "software", "An explicitly saved Software choice is preserved");
        await vm.CheckHardwareAsync(detector, "automatic", false, CancellationToken.None);
        typeof(ExportView).GetMethod("OnRecheckHardware", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(dialog, new object[] { dialog, new RoutedEventArgs() });
        Require(vm.ShowEvaluation && dialog.ActualWidth > 640, "Evaluate options opens an inline results area without squeezing the export form");
        for (int i = 0; i < 900 && vm.IsCheckingHardware; i++) await Task.Delay(100);
        Require(!vm.IsCheckingHardware && vm.EvaluationReport.Contains("Fastest in this test:"), "Inline evaluation reports the fastest successful encoder");
        Require(!Application.Current.Windows.OfType<EncoderOptionsDialog>().Any(), "Manual evaluation does not open a popup");
        Require(vm.SelectedAcceleration.Id == "automatic", "Inline evaluation preserves the selected option");
        Require(vm.HasFastest && vm.AccelerationOptions.Count(o => o.IsFastest) == 1,
            "Results card and dropdown identify exactly one fastest encoder");
        var selector = (System.Windows.Controls.ComboBox)dialog.FindName("AccelerationSelector");
        dialog.UpdateLayout();
        var inputHeight = ((FrameworkElement)dialog.FindName("OutputFilenameInput")).ActualHeight;
        Require(Math.Abs(selector.ActualHeight - inputHeight) < 1
            && Math.Abs(((FrameworkElement)dialog.FindName("EvaluationResultsButton")).ActualHeight - inputHeight) < 1,
            "Acceleration selector and action buttons match the output input height");
        for (int open = 0; open < 2; open++)
        {
            selector.IsDropDownOpen = true;
            dialog.UpdateLayout();
            await Task.Delay(100);
            for (int index = 0; index < selector.Items.Count; index++)
            {
                var item = (System.Windows.Controls.ComboBoxItem)selector.ItemContainerGenerator.ContainerFromIndex(index);
                Require(item is not null && item.ActualHeight > 0,
                    "Evaluated dropdown renders option " + index + " without a resource error");
            }
            selector.IsDropDownOpen = false;
        }
        var completedEvaluation = vm.Evaluation;
        typeof(ExportView).GetMethod("OnHideEvaluation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(dialog, new object[] { dialog, new RoutedEventArgs() });
        Require(!vm.ShowEvaluation && vm.CanOpenResults, "Hidden results remain available");
        ((System.Windows.Controls.Button)dialog.FindName("EvaluationResultsButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Require(vm.ShowEvaluation && !vm.IsCheckingHardware && ReferenceEquals(completedEvaluation, vm.Evaluation),
            "Results button reopens existing results without another benchmark");
        Require(vm.AccelerationOptions.Where(o => o.Milliseconds is not null).All(o => o.DisplayName.Contains("s)"))
            && vm.AccelerationOptions.Any(o => o.Id == "software" && o.Milliseconds is not null), "Dropdown options include measured test times");
        dialog.UpdateLayout();
        var visual = (FrameworkElement)dialog.Content;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(directory, "hardware-dialog.png"))) png.Save(file);
        vm.SelectedAcceleration = vm.AccelerationOptions.FirstOrDefault(o => o.Adapter is not null) ?? ExportAccelerationOption.Software;
        vm.OutputFolder = directory;
        vm.OutputFilename = "dialog-hardware.mp4";
        File.Delete(cachePath);
        typeof(ExportView).GetMethod("OnExport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(dialog, new object[] { dialog, new RoutedEventArgs() });
        Require(dialog.ExportTask is not null, "Export button starts the selected encoder");
        dialog.Close();
        Require(dialog.IsVisible && dialog.IsExportInFlight,
            "An active export stays attached to its recording window");
        await dialog.ExportTask!;
        for (int i = 0; i < 50 && !vm.IsCompleted; i++) await Task.Delay(20);
        Require(vm.IsCompleted && vm.ActiveEncoder == vm.CreateEncoder().DisplayName, "Dialog shows the actual encoder through completion");
        Require(!File.Exists(cachePath), "Explicit GPU export does not run an evaluation even without cached results");
        await new UserPreferences { ExportAcceleration = vm.SelectedAcceleration.Id }.SaveAsync();
        Require((await UserPreferences.LoadAsync()).ExportAcceleration == vm.SelectedAcceleration.Id, "Acceleration preference survives reload");
        dialog.Close();
        await CheckMissingAdapterRecoveryAsync(owner, directory, project, media, runner);

        var source = Path.Combine(directory, "hardware-interlaced.mp4");
        var generated = await runner.RunFfmpegAsync(new[] { "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            "testsrc2=size=1920x1080:rate=60:duration=12", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=12",
            "-vf", "tinterlace=interleave_top,setfield=tff", "-c:v", "mpeg2video", "-flags", "+ilme+ildct", "-q:v", "2", "-c:a", "ac3", "-shortest", source });
        Require(generated.Success, "Generated interlaced test video: " + generated.Stderr);
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var info = await new MediaProbeService(runner).ProbeAsync(source);
        var plan = new ExportPlan(source, Path.Combine(directory, "software.mp4"), info.DurationUs, info.PrimaryAudioIndex,
            new[] { new ExportSegment(1, 500_000, 3_500_000, "Opening #1"), new ExportSegment(2, 6_500_000, 10_500_000, "Final café") }, info.FrameRate, "ac3");
        var records = new List<object>();
        var encoders = new List<IEncoderStrategy> { new LibX264EncoderStrategy() };
        encoders.AddRange(result.Results.Where(r => r.Available).Select(r => new HardwareEncoderStrategy(r.Adapter)));
        for (int i = 0; i < encoders.Count; i++)
        {
            var encoder = encoders[i];
            var output = i == 0 ? plan.OutputPath : Path.Combine(directory, $"hardware-{i}.mp4");
            var timer = Stopwatch.StartNew();
            var export = new ExportService(runner, encoder).RunExportAsync(plan with { OutputPath = output });
            var maxUiDelay = TimeSpan.Zero;
            var uiClock = Stopwatch.StartNew();
            while (!export.IsCompleted)
            {
                await Task.Delay(25);
                if (uiClock.Elapsed > maxUiDelay) maxUiDelay = uiClock.Elapsed;
                uiClock.Restart();
            }
            await export;
            timer.Stop();
            await ValidateAsync(runner, output, plan);
            double? ssim = null;
            if (i > 0)
            {
                var score = await runner.RunFfmpegAsync(new[] { "-hide_banner", "-i", plan.OutputPath, "-i", output,
                    "-filter_complex", "[0:v]setpts=PTS-STARTPTS[a];[1:v]setpts=PTS-STARTPTS[b];[a][b]ssim", "-an", "-f", "null", "-" });
                var match = Regex.Match(score.Stderr, @"All:([0-9.]+)");
                Require(score.Success && match.Success, "Quality comparison completed");
                ssim = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                Require(ssim > 0.95, "Hardware output stays close to the software reference");
            }
            records.Add(new { encoder = encoder.DisplayName, seconds = timer.Elapsed.TotalSeconds,
                bytes = new FileInfo(output).Length, ssim, maximumDispatcherDelayMs = maxUiDelay.TotalMilliseconds });
            Console.WriteLine(JsonSerializer.Serialize(records[^1]));
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "hardware-results.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));

        var unavailable = new FailedHardware();
        int restarts = 0;
        string? finalNotice = null;
        var fallback = plan with { OutputPath = Path.Combine(directory, "fallback.mp4") };
        await new ExportService(runner, unavailable).RunExportAsync(fallback, new Sink(p =>
        { if (p.Phase == ExportPhase.Restarting) restarts++; finalNotice = p.FallbackNotice; }), allowSoftwareFallback: true);
        Require(unavailable.Attempts == 2 && restarts == 1 && finalNotice is not null, "Automatic retries once and retains a visible fallback notice");
        await ValidateAsync(runner, fallback.OutputPath, plan);
        try
        {
            await new ExportService(runner, new FailedHardware()).RunExportAsync(plan with { OutputPath = Path.Combine(directory, "explicit.mp4") });
            throw new Exception("Explicit hardware failure was not reported");
        }
        catch (HardwareExportException) { Require(!File.Exists(Path.Combine(directory, "explicit.mp4")), "Explicit hardware failure leaves no published output"); }
        var badInput = new FailedHardware(true);
        try
        {
            await new ExportService(runner, badInput).RunExportAsync(plan with { OutputPath = Path.Combine(directory, "bad-input.mp4") }, allowSoftwareFallback: true);
            throw new Exception("Invalid input was accepted");
        }
        catch (ExportException) { Require(badInput.Attempts == 1, "Input failure never triggers software retry"); }
        foreach (var encoder in encoders)
        {
            using var cancel = new CancellationTokenSource();
            var cancelled = plan with { OutputPath = Path.Combine(directory, "cancelled.mp4") };
            try
            {
                await new ExportService(runner, encoder).RunExportAsync(cancelled,
                    new Sink(p => { if (p.Phase == ExportPhase.EncodingSegment && p.SegmentPercent > 0) cancel.Cancel(); }), cancel.Token, true);
                throw new Exception("Cancellation was ignored");
            }
            catch (OperationCanceledException) { Require(!File.Exists(cancelled.OutputPath), encoder.DisplayName + " cancellation leaves no output"); }
        }
        var finalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        Require(originalHash.SequenceEqual(finalHash), "Hardware checks leave source unchanged");
    }

    private static async Task ValidateAsync(FfmpegRunner runner, string output, ExportPlan plan)
    {
        var probe = await runner.RunFfprobeAsync(new[] { "-v", "error", "-count_frames", "-show_streams", "-show_chapters", "-of", "json", output });
        Require(probe.Success, "Output probe succeeds");
        using var json = JsonDocument.Parse(probe.Stdout);
        var streams = json.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.Single(s => s.GetProperty("codec_type").GetString() == "video");
        var audio = streams.Single(s => s.GetProperty("codec_type").GetString() == "audio");
        Require(video.GetProperty("nb_read_frames").GetString() == "210", "Cuts retain exactly 210 frames");
        Require(video.GetProperty("field_order").GetString() == "progressive", "Interlaced input exports progressive video");
        double start(JsonElement stream) => double.Parse(stream.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture);
        Require(Math.Abs(start(video) - start(audio)) < 0.08, "Audio and video start within packet tolerance");
        Require(json.RootElement.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("tags").GetProperty("title").GetString())
            .SequenceEqual(plan.KeptSegments.Select(s => s.PartTitle)), "Hardware export preserves chapter names and order");
    }
    private static async Task CheckMissingAdapterRecoveryAsync(Window owner, string directory, MainViewModel project, MediaInfo media, FfmpegRunner runner)
    {
        var dialog = new ExportView();
        dialog.Bind(project, media, "missing-test-adapter");
        dialog.AttachExportRunner(runner, new Sink(_ => { }));
        Program.ShowExportForTest(dialog, owner);
        var vm = (ExportDialogViewModel)dialog.DataContext;
        for (int i = 0; i < 150 && !vm.CanCheckHardware; i++) await Task.Delay(100);
        Require(vm.SelectedAcceleration.Id == "missing-test-adapter", "Missing saved GPU is retained instead of silently choosing software");
        vm.OutputFolder = directory;
        vm.OutputFilename = "recovered.mp4";
        bool sawRecovery = false;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var popup = Application.Current.Windows.OfType<EncoderOptionsDialog>().FirstOrDefault(w => w.Owner == Window.GetWindow(dialog) && w.IsVisible);
            if (popup is null) return;
            timer.Stop();
            sawRecovery = true;
            popup.ShowInTaskbar = false;
            var list = (System.Windows.Controls.ListBox)popup.FindName("OptionsList");
            var rows = list.Items.Cast<EncoderOptionsDialog.ResultRow>().ToArray();
            Require(rows.Count(r => r.Fastest) == 1, "Recovery highlights one fastest successful option");
            popup.UpdateLayout();
            var visual = (FrameworkElement)popup;
            var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(directory, "recovery-options.png"))) png.Save(file);
            list.SelectedItem = rows.Single(r => r.Option.Id == "software");
            ((System.Windows.Controls.Button)popup.FindName("RestartButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        };
        timer.Start();
        try
        {
            typeof(ExportView).GetMethod("OnExport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(dialog, new object[] { dialog, new RoutedEventArgs() });
            await dialog.ExportTask!;
            for (int i = 0; i < 50 && !vm.IsCompleted; i++) await Task.Delay(20);
            Require(sawRecovery && vm.IsCompleted && File.Exists(Path.Combine(directory, "recovered.mp4")),
                "Missing GPU evaluates options and restarts only after the user's popup choice");
        }
        finally { timer.Stop(); dialog.Close(); }
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}
