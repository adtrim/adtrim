using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using AdTrim;
using AdTrim.Commands;
using AdTrim.Encoders;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;
using AdTrim.Views;

internal static partial class Program
{
    private static async Task CheckSidecarPreferenceInFreshProcess(bool expected)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("--check-sidecar-preference");
        start.ArgumentList.Add(expected.ToString());
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Require(process.ExitCode == 0, $"A fresh process reloads sidecar deletion as {expected}");
    }

    private static int RunTransportStreamSmoke()
    {
        string fixture = PrepareFixture().GetAwaiter().GetResult();
        string directory = Path.Combine(Path.GetDirectoryName(fixture)!, "ts-state");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("ADTRIM_DATA_DIR", directory);
        File.WriteAllText(Path.Combine(directory, "preferences.json"), "{\"AddSourceExportComment\":true}");
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!);
        int result = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await CheckTransportStreams(app, fixture, directory); result = 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally { app.Shutdown(); }
        }));
        app.Run();
        return result;
    }
    private static async Task CheckTransportStreams(App app, string fixture, string directory)
    {
        var runner = new FfmpegRunner();
        await CheckTransportStreamPreroll(app, runner, fixture, directory);
        string source = Path.Combine(directory, "A Recording's Long Show Name (2021) - S06E01 - A Long Episode Title with Unicode é.ts");
        var converted = await runner.RunFfmpegAsync(new[] { "-y", "-v", "error", "-i", fixture,
            "-map", "0:v:0", "-map", "0:a:0", "-map_metadata", "-1", "-map_chapters", "-1",
            "-c:a", "copy", "-c:v", "mpeg2video", "-flags", "+ilme+ildct", "-top", "1", "-g", "60",
            "-output_ts_offset", "60000", source });
        Require(converted.Success, "Generated TS fixture: " + converted.Stderr);
        byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var media = await new MediaProbeService(runner).ProbeAsync(source);
        var store = new ProjectStore();
        store.Save(new(AdTrimProject.CurrentSchemaVersion, source, ProjectStore.FingerprintOf(source, media.DurationUs), media,
            new() { new("cut-start", 4_000_000, SplitSource.Manual, null, null, false), new("cut-end", 8_000_000, SplitSource.Manual, null, null, false) },
            new() { "segment-4000000-8000000" }, SidecarLocation.NextToSource));
        var window = app.NewWindow(source);
        window.Opacity = 0; window.ShowInTaskbar = false;
        await window.Ready;
        var vm = (MainViewModel)window.DataContext;
        Require(vm.IsFileLoaded && vm.SourcePath == source && vm.Segments[1].IsExcluded, "TS source and existing editing project open normally");
        Require(vm.FrameStartPhaseUs < 1_000_000, "TS transport PTS does not become a frame-grid offset");
        Require(await app.OpenRecordingAsync(source.ToUpperInvariant()) == window, "External TS opens activate the existing window");
        using (var pipeCancellation = new CancellationTokenSource())
        {
            var server = (Task)typeof(App).GetMethod("RunPipeServerAsync", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { pipeCancellation.Token })!;
            string? acknowledgement = await Task.Run(async () =>
            {
                string pipe = (string)typeof(App).GetProperty("PipeName", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut);
                await client.ConnectAsync(2000);
                using var writer = new StreamWriter(client, System.Text.Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(source);
                using var reader = new StreamReader(client, System.Text.Encoding.UTF8, leaveOpen: true);
                return await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            });
            await Task.Delay(200);
            pipeCancellation.Cancel();
            await server;
            Require(acknowledgement == "OK" && app.Windows.OfType<MainWindow>().Count() == 1 && app.FindRecordingWindow(source) == window,
                "Explorer-style TS handoff preserves spaces and Unicode and activates the existing recording");
        }
        await CheckFramePair(window, vm);
        string droppedSource = Path.Combine(directory, "dropped recording.TS");
        var h264 = await runner.RunFfmpegAsync(new[] { "-v", "error", "-i", fixture, "-map", "0:v:0", "-map", "0:a:0",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "60", "-c:a", "aac", "-output_ts_offset", "120", droppedSource });
        Require(h264.Success, "Generated H.264/AAC transport stream: " + h264.Stderr);
        var dropWindow = app.NewWindow();
        dropWindow.Opacity = 0; dropWindow.ShowInTaskbar = false;
        await dropWindow.Ready;
        var dropData = new DataObject(DataFormats.FileDrop, new[] { Path.Combine(directory, "ignore.txt"), droppedSource });
        var drop = (DragEventArgs)typeof(DragEventArgs).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(new object[] { dropData, DragDropKeyStates.None, DragDropEffects.Copy, dropWindow, new Point() });
        typeof(MainWindow).GetMethod("OnDrop", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
            .Invoke(dropWindow, new object[] { dropWindow, drop });
        var droppedVm = (MainViewModel)dropWindow.DataContext;
        for (int i = 0; i < 500 && (droppedVm.SourcePath != droppedSource || droppedVm.IsBusy); i++) await Task.Delay(20);
        Require(droppedVm.IsFileLoaded && droppedVm.SourcePath == droppedSource, "Dropping an uppercase TS file opens it and ignores unsupported files");
        await CheckFramePair(dropWindow, droppedVm);
        dropWindow.Close();
        for (int i = 0; i < 250 && app.Windows.OfType<MainWindow>().Contains(dropWindow); i++) await Task.Delay(20);
        vm.CommandStack.Execute(new MoveSplitCommand(vm, vm.Markers[1], vm.Markers[1].TimeUs, 4_040_000));
        vm.CommandStack.Undo();
        Require(vm.Markers[1].TimeUs == 4_000_000 && vm.Segments[1].IsExcluded, "TS undo preserves the cut and exclusion decision");
        Invoke(window, "SeekTo", vm, 3_000_000L, true);
        await Task.Delay(200);
        Invoke(window, "SetCollapsed", vm, true);
        await Task.Delay(200);
        Require(vm.IsCollapsed && vm.KeptTimeline.DurationUs > 0, "TS collapsed preview uses the normal kept timeline");
        var after = (MpvPreviewViewModel)typeof(MainWindow).GetField("_previewAfter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        for (int i = 0; i < 250 && (after.IsLoading || after.IsSeeking); i++) await Task.Delay(20);
        Invoke(window, "SeekTo", vm, 8_200_000L, true);
        await Task.Delay(300);
        for (int i = 0; i < 250 && (after.IsLoading || after.IsSeeking); i++) await Task.Delay(20);
        Require(!after.IsLoading && !after.IsSeeking && Math.Abs(after.PositionUs - 4_200_000) < 100_000
            && Math.Abs(vm.PlayheadUs - 8_200_000) < 100_000, "Collapsed TS seek displays kept source footage on the output clock");
        Invoke(window, "SeekTo", vm, 3_700_000L, true);
        await Task.Delay(300);
        Invoke(window, "PlayPauseBoth");
        await Task.Delay(900);
        Invoke(window, "PlayPauseBoth");
        Require(vm.IsCollapsed && vm.PlayheadUs > 8_000_000 && vm.PlayheadUs < 9_500_000,
            "Collapsed TS playback crosses the excluded scene instead of playing it");
        Invoke(window, "SetCollapsed", vm, false);
        await CheckFramePair(window, vm);
        async Task<ExportView> OpenExport(MainWindow owner)
        {
            Invoke(owner, "OnExport", owner, new RoutedEventArgs());
            await WaitForExportSetupAsync(owner);
            var view = (ExportView)((ContentControl)owner.FindName("ExportHost")).Content;
            var setup = (ExportDialogViewModel)view.DataContext;
            for (int i = 0; i < 250 && !setup.CanCheckHardware; i++) await Task.Delay(20);
            Require(setup.CanCheckHardware, "Export options are ready");
            return view;
        }
        var export = await OpenExport(window);
        var setup = (ExportDialogViewModel)export.DataContext;
        Require(!setup.DeleteSidecarAfterExport, "Existing preferences default sidecar deletion to off");
        var beforeSidecarChoice = await UserPreferences.LoadAsync();
        ((CheckBox)export.FindName("DeleteSidecarCheckBox")).IsChecked = true;
        await export.SidecarPreferenceSaveTask;
        Require((await UserPreferences.LoadAsync()).DeleteSidecarAfterExport, "Sidecar deletion is saved immediately without exporting");
        await CheckSidecarPreferenceInFreshProcess(true);
        await (beforeSidecarChoice with { ShowThumbnails = !beforeSidecarChoice.ShowThumbnails })
            .SaveChangesAsync(beforeSidecarChoice, null);
        Require((await UserPreferences.LoadAsync()).DeleteSidecarAfterExport,
            "An unrelated stale-window save preserves the sidecar choice");
        string preferencePeerSource = Path.Combine(directory, "preference-peer.mp4");
        File.Copy(fixture, preferencePeerSource);
        var preferencePeer = app.NewWindow(preferencePeerSource);
        preferencePeer.Opacity = 0; preferencePeer.ShowInTaskbar = false;
        await preferencePeer.Ready;
        var peerExport = await OpenExport(preferencePeer);
        Require(((ExportDialogViewModel)peerExport.DataContext).DeleteSidecarAfterExport,
            "Another recording window starts with the saved sidecar choice");
        ((CheckBox)export.FindName("DeleteSidecarCheckBox")).IsChecked = false;
        await export.SidecarPreferenceSaveTask;
        Require(((ExportDialogViewModel)peerExport.DataContext).DeleteSidecarAfterExport,
            "An already-open export keeps its current sidecar selection");
        peerExport.Close();
        preferencePeer.Close();
        export.Close();
        export = await OpenExport(window);
        setup = (ExportDialogViewModel)export.DataContext;
        Require(!setup.DeleteSidecarAfterExport, "Turning sidecar deletion off is remembered by the next export");
        await CheckSidecarPreferenceInFreshProcess(false);
        var selector = (ComboBox)export.FindName("FormatSelector");
        Require(setup.SelectedFormat == ExportFormat.Mp4 && setup.OutputFilename.EndsWith(".mp4"), "Existing preferences default to MP4");
        Require(((TextBlock)export.FindName("TsChapterNotice")).Visibility == Visibility.Collapsed, "MP4 has no TS chapter notice");
        setup.OutputFilename = "custom.mp4";
        setup.OverwriteConfirmed = true;
        selector.SelectedItem = ExportFormat.Ts;
        await export.FormatSaveTask;
        Require(setup.OutputFilename == "custom.ts" && !setup.OverwriteConfirmed, "Format changes preserve the chosen name and reset overwrite permission");
        Require(((TextBlock)export.FindName("TsChapterNotice")).Visibility == Visibility.Visible && selector.Tag?.ToString()?.Contains(".ts") == true,
            "TS selection displays the correct format and chapter limitation");
        Require((await UserPreferences.LoadAsync()).ExportFormat == ExportFormat.Ts, "TS preference is saved before any export starts");
        CheckExportSetupLayout(window, export, directory);
        window.Width = 1280; window.Height = 720; window.UpdateLayout();
        Capture(window, Path.Combine(directory, "ts-export-setup.png"));
        setup.SelectedAcceleration = ExportAccelerationOption.Software;
        setup.OutputFilename = "finished.ts";
        Invoke(export, "OnExport", export, new RoutedEventArgs());
        for (int i = 0; i < 3000 && export.IsExportInFlight; i++) await Task.Delay(20);
        Require(setup.IsCompleted && !export.SourceCommentFailed, "TS export completes and records provenance without attempting MP4 Comments: " + setup.ErrorMessage + " " + export.SourceCommentNote);
        var afterHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        Require(hash.SequenceEqual(afterHash), "TS source bytes are unchanged even with source Comments enabled");
        Require(store.HasExportRecord(source) && vm.LastExport is not null, "Successful TS export is recorded in the existing project sidecar");
        Require(!Directory.EnumerateFiles(directory).Any(p => p.Contains(".adtrim-export-comment") || p.EndsWith(".xmp")), "TS creates no extra provenance sidecars");
        export.Close();
        var reopened = await OpenExport(window);
        Require(((ExportDialogViewModel)reopened.DataContext).SelectedFormat == ExportFormat.Ts, "The next export starts as TS");
        reopened.Close();
        var stalePreferences = await UserPreferences.LoadAsync();
        await UserPreferences.SaveExportFormatAsync(ExportFormat.Mp4);
        await (stalePreferences with { ShowWaveform = !stalePreferences.ShowWaveform }).SaveChangesAsync(stalePreferences, null);
        Require((await UserPreferences.LoadAsync()).ExportFormat == ExportFormat.Mp4, "A stale window cannot overwrite a newer format selection when saving unrelated settings");
        reopened = await OpenExport(window);
        setup = (ExportDialogViewModel)reopened.DataContext;
        Require(setup.SelectedFormat == ExportFormat.Mp4, "Returning to MP4 is remembered");
        ((ComboBox)reopened.FindName("FormatSelector")).SelectedItem = ExportFormat.Ts;
        await reopened.FormatSaveTask;
        ((ComboBox)reopened.FindName("FormatSelector")).SelectedItem = ExportFormat.Mp4;
        await reopened.FormatSaveTask;
        Require((await UserPreferences.LoadAsync()).ExportFormat == ExportFormat.Mp4, "Repeated MP4 / TS / MP4 selections remember the latest choice");
        setup.SelectedAcceleration = ExportAccelerationOption.Software;
        setup.OutputFilename = "tracking-warning.mp4";
        string primarySidecar = store.SidecarPathFor(source);
        byte[] savedProject = await File.ReadAllBytesAsync(primarySidecar);
        string fallbackDirectory = Path.GetDirectoryName(store.AppDataFallbackPathFor(source))!;
        Require(!Directory.Exists(fallbackDirectory), "Tracking-failure fixture has an unused fallback directory");
        await File.WriteAllTextAsync(fallbackDirectory, "Block fallback writes for this disposable test");
        File.SetAttributes(primarySidecar, FileAttributes.ReadOnly);
        try
        {
            Invoke(reopened, "OnExport", reopened, new RoutedEventArgs());
            for (int i = 0; i < 3000 && reopened.IsExportInFlight; i++) await Task.Delay(20);
            Require(setup.IsCompleted && reopened.SourceCommentFailed && reopened.SourceCommentNote?.Contains("Export record was not saved") == true,
                "TS-to-MP4 output succeeds with a visible warning when both sidecar save locations fail");
            byte[] projectAfterFailure = await File.ReadAllBytesAsync(primarySidecar);
            Require(File.Exists(Path.Combine(directory, "tracking-warning.mp4")) && savedProject.SequenceEqual(projectAfterFailure),
                "Tracking failure preserves the finished output and the previous project");
        }
        finally { File.SetAttributes(primarySidecar, FileAttributes.Normal); File.Delete(fallbackDirectory); }
        reopened.Close();
        await (Task<bool>)Invoke(window, "SaveProjectAsync", vm)!;
        window.Close();
        for (int i = 0; i < 250 && app.Windows.OfType<MainWindow>().Contains(window); i++) await Task.Delay(20);
        Require(!app.Windows.OfType<MainWindow>().Contains(window), "The original recording window has finished closing");
        var replacement = app.NewWindow(source);
        replacement.Opacity = 0; replacement.ShowInTaskbar = false;
        await replacement.Ready;
        var restored = (MainViewModel)replacement.DataContext;
        Require(restored.LastExport is not null && restored.Markers[1].TimeUs == 4_000_000
            && restored.Markers[2].TimeUs == 8_000_000 && restored.Segments[1].IsExcluded,
            "Export status, exact cuts and exclusion survive reopening the TS project");
        reopened = await OpenExport(replacement);
        Require(((ExportDialogViewModel)reopened.DataContext).SelectedFormat == ExportFormat.Mp4, "A new recording window reads the persisted format");
        reopened.Close();
        replacement.Close();
        for (int i = 0; i < 250 && app.Windows.OfType<MainWindow>().Contains(replacement); i++) await Task.Delay(20);
        var rows = new List<UnexportedFile>();
        await foreach (var row in UnexportedFiles.ScanAsync(directory)) rows.Add(row);
        Require(rows.Any(row => row.Path == source && row.Exported), "Folder discovery recognizes exported TS sidecars");
        string mp4Source = Path.Combine(directory, "mp4-source.mp4");
        File.Copy(fixture, mp4Source);
        var mp4Media = await new MediaProbeService(runner).ProbeAsync(mp4Source);
        store.Save(new(AdTrimProject.CurrentSchemaVersion, mp4Source, ProjectStore.FingerprintOf(mp4Source, mp4Media.DurationUs), mp4Media,
            new() { new("cut-start", 4_000_000, SplitSource.Manual, null, null, false), new("cut-end", 8_000_000, SplitSource.Manual, null, null, false) },
            new() { "segment-4000000-8000000" }, SidecarLocation.NextToSource));
        var mp4Window = await app.OpenRecordingAsync(mp4Source);
        mp4Window.Opacity = 0; mp4Window.ShowInTaskbar = false;
        var mp4Export = await OpenExport(mp4Window);
        var mp4Setup = (ExportDialogViewModel)mp4Export.DataContext;
        ((ComboBox)mp4Export.FindName("FormatSelector")).SelectedItem = ExportFormat.Ts;
        await mp4Export.FormatSaveTask;
        mp4Setup.SelectedAcceleration = ExportAccelerationOption.Software;
        mp4Setup.OutputFilename = "mp4-source-to-ts.ts";
        Invoke(mp4Export, "OnExport", mp4Export, new RoutedEventArgs());
        for (int i = 0; i < 3000 && mp4Export.IsExportInFlight; i++) await Task.Delay(20);
        Require(mp4Setup.IsCompleted && !mp4Export.SourceCommentFailed && Mp4Comments.HasExportLabel(Mp4Comments.Read(mp4Source))
            && store.HasExportRecord(mp4Source), "MP4-to-TS export retains source Comments and sidecar tracking independently of output format: "
            + mp4Setup.ErrorMessage + "; " + mp4Export.SourceCommentNote + "; " + string.Join("; ", mp4Setup.Validate().Select(i => i.Message)));
        mp4Export.Close(); mp4Window.Close();
        for (int i = 0; i < 250 && app.Windows.OfType<MainWindow>().Contains(mp4Window); i++) await Task.Delay(20);
        var hardware = await new HardwareEncoderDetection(runner, directory).DetectAsync(true);
        Require(hardware.Error is null, hardware.Error ?? "Encoder evaluation completed");
        foreach (var capability in hardware.Results)
        {
            Console.WriteLine($"HARDWARE: {capability.Adapter.Name}; available={capability.Available}; {capability.Detail}");
            if (!capability.Available) continue;
            string output = Path.Combine(directory, $"hardware-{capability.Adapter.Index}.ts");
            var plan = new ExportPlan(source, output, media.DurationUs, media.PrimaryAudioIndex,
                new[] { new ExportSegment(1, 640_000, 1_640_000, "Opening"), new ExportSegment(2, 8_200_000, 9_200_000, "Closing") },
                media.FrameRate, media.PrimaryAudio!.Codec, Format: ExportFormat.Ts);
            await new ExportService(runner, new HardwareEncoderStrategy(capability.Adapter)).RunExportAsync(plan);
            var decoded = await runner.RunFfmpegAsync(new[] { "-v", "error", "-xerror", "-i", output, "-f", "null", "-" });
            Require(decoded.Success, $"TS export decodes with {capability.Adapter.Name}: " + decoded.Stderr);
        }
        foreach (uint vendor in new uint[] { 0x10de, 0x8086, 0x1002 })
            if (!hardware.Results.Any(r => r.Adapter.VendorId == vendor && r.Available))
                Console.WriteLine($"UNTESTED: vendor {vendor:x4} has no validated local adapter.");
        foreach (var path in store.CandidateSidecarPathsFor(source).Where(File.Exists)) File.Delete(path);
        Require(!store.HasExportRecord(source), "Deleting the TS project intentionally loses export tracking");
        Console.WriteLine("PASS: TS opening, undo, preview, sticky format, export, source protection, provenance and project reopening.");
    }
}
