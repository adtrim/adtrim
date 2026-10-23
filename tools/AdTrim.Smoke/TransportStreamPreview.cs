using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using AdTrim;
using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;

internal static partial class Program
{
    private static async Task CheckTransportStreamPreroll(App app, FfmpegRunner runner, string fixture, string directory)
    {
        string source = Path.Combine(directory, "long-gop.ts");
        var generated = await runner.RunFfmpegAsync(["-v", "error", "-i", fixture, "-map", "0:v:0", "-map", "0:a:0",
            "-c:v", "mpeg2video", "-g", "90", "-bf", "2", "-sc_threshold", "1000000000", "-c:a", "copy",
            "-output_ts_offset", "60000", source]);
        Require(generated.Success, generated.Stderr);
        var media = await new MediaProbeService(runner).ProbeAsync(source);
        var window = app.NewWindow(source);
        window.Opacity = 0; window.ShowInTaskbar = false;
        await window.Ready;
        var vm = (MainViewModel)window.DataContext;
        MpvPreviewViewModel Player(string name) => (MpvPreviewViewModel)typeof(MainWindow)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var before = Player("_previewBefore"); var after = Player("_previewAfter");
        async Task Settle()
        {
            await Task.Delay(300);
            for (int i = 0; i < 750 && (after.IsLoading || after.IsSeeking || (!vm.IsCollapsed &&
                (before.IsLoading || before.IsSeeking || typeof(MainWindow).GetField("_frameRecovery",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is not null))); i++) await Task.Delay(20);
            Require(!after.IsLoading && !after.IsSeeking, "Long-GOP preview finishes seeking");
        }
        async Task<long[]> Frames(long time)
        {
            string Seconds(long us) => (us / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);
            var result = await runner.RunFfprobeAsync(["-v", "error", "-select_streams", "v:0", "-read_intervals",
                "%" + Seconds(time + media.TimelineStartTimeUs + 1_000_000), "-show_entries",
                "frame=best_effort_timestamp_time", "-of", "json", source]);
            Require(result.Success, result.Stderr);
            using var json = JsonDocument.Parse(result.Stdout);
            return json.RootElement.GetProperty("frames").EnumerateArray()
                .Where(f => f.TryGetProperty("best_effort_timestamp_time", out _))
                .Select(f => (long)Math.Round(decimal.Parse(f.GetProperty("best_effort_timestamp_time").GetString()!,
                    CultureInfo.InvariantCulture) * 1_000_000) - media.TimelineStartTimeUs).Distinct().Order().ToArray();
        }
        foreach (long target in new long[] { 3_135_000, 6_735_000, 8_135_000 })
        {
            Invoke(window, "SeekTo", vm, target, false); await Settle();
            var frames = await Frames(after.PositionUs);
            int index = Array.FindIndex(frames, t => Math.Abs(t - after.PositionUs) <= 2);
            Require(index > 0 && Math.Abs(before.PositionUs - frames[index - 1]) <= 2
                && ((FrameworkElement)window.FindName("NoFrameOverlay")).Visibility != Visibility.Visible,
                $"Long-GOP timeline click displays the actual preceding frame: {before.PositionUs}, {after.PositionUs}");
        }
        long[] bounds = [1_350_000, 2_350_000, 4_350_000, 5_350_000, 8_350_000, 9_350_000];
        var splits = bounds.Select((t, i) => new PersistedSplit("gop-" + i, t, SplitSource.Manual, null, null, false)).ToArray();
        long[] all = [0, .. bounds, media.DurationUs];
        var excluded = Enumerable.Range(0, all.Length - 1).Where(i => i % 2 == 0)
            .Select(i => $"segment-{all[i]}-{all[i + 1]}").ToArray();
        vm.CommandStack.Execute(new ReplaceMarkerSetCommand(vm, new(splits, excluded, "Opening", MarkerSetSource.Blank, null), "Preview regression"));
        Invoke(window, "SetCollapsed", vm, true);
        Invoke(window, "SeekTo", vm, 4_450_000L, true); await Settle();
        Require(Math.Abs(after.PositionUs - vm.KeptTimeline.ToOutput(4_450_000)) < 40_000,
            "A seek issued during timeline loading replaces the original load position");
        foreach (long target in new long[] { 4_450_000, 8_450_000, 1_450_000, 4_450_000 })
        {
            Invoke(window, "SeekTo", vm, target, true); await Settle();
            Require(Math.Abs(after.PositionUs - vm.KeptTimeline.ToOutput(target)) < 40_000
                && Math.Abs(vm.PlayheadUs - vm.KeptTimeline.ToSource(after.PositionUs)) <= 2,
                $"Mid-GOP kept scene seeks use the native player clock: source={vm.PlayheadUs}, output={after.PositionUs}, target={target}");
        }
        // A settled native frame may not change on another seek. Its clock must
        // still replace a pending scrub position in the editor.
        long nativePosition = after.PositionUs;
        vm.PlayheadUs = bounds[^1];
        after.SeekUsExact(nativePosition); await Settle();
        Require(Math.Abs(vm.PlayheadUs - vm.KeptTimeline.ToSource(after.PositionUs)) <= 2,
            "A repeated seek reconciles the editor clock even without a new native frame");
        Invoke(window, "SeekTo", vm, 2_150_000L, true); await Settle();
        Invoke(window, "PlayPauseBoth"); await Task.Delay(500); Invoke(window, "PlayPauseBoth"); await Settle();
        Require(after.PositionUs > 1_000_000 && after.PositionUs < 1_700_000
            && vm.PlayheadUs >= bounds[2] && vm.PlayheadUs < bounds[3], "Long-GOP playback crosses a mid-GOP join");
        window.Close();
        for (int i = 0; i < 250 && app.Windows.OfType<MainWindow>().Contains(window); i++) await Task.Delay(20);
        Console.WriteLine("PASS: long-GOP TS predecessor, mid-GOP collapsed seeks, repeated seeks and playback join");
    }
}
