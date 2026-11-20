using AdTrim.Commands;
using AdTrim.Models;

namespace AdTrim.ViewModels;

/// <summary>Finds a cleaner cut for one marker between its neighbors; null when there is nothing to report.</summary>
public delegate Task<RefineResult?> RefineOne(string sourcePath, long timeUs, long minBoundUs, long maxBoundUs,
    CancellationToken ct, int? primaryAudioIndex);

/// <summary>
/// Runs "Refine this split", "Refine selected" and "Refine all". Skips confirmed and bookend markers;
/// per-marker bounds come from the neighbors so a marker never crosses one. The whole pass is applied
/// as one BatchedRefineCommand, so it is one undo step, and nothing changes if it is cancelled or fails.
/// </summary>
/// <param name="refiner">The refiner; null when FFmpeg is not available.</param>
/// <param name="primaryAudioIndex">The recording's primary audio stream, read for each marker.</param>
public sealed class RefineCoordinator(Func<RefineOne?> refiner, Func<int?> primaryAudioIndex)
{
    private CancellationTokenSource? _cts;

    public void Cancel() => _cts?.Cancel();

    /// <param name="refineConfirmed">Asks whether a single confirmed marker should be refined anyway.</param>
    public async Task RunAsync(MainViewModel vm, IReadOnlyList<Split> targets, Func<Split, bool> refineConfirmed)
    {
        if (!vm.CanEdit) return;
        if (vm.SourcePath is null || refiner() is not { } refine)
        {
            vm.Banner = new BannerInfo(StatusKind.Danger,
                "Refine unavailable.",
                "Open a file first.",
                Array.Empty<BannerAction>());
            return;
        }

        var allowConfirmedSingle = targets.Count == 1
            && targets[0].Confirmed
            && !targets[0].IsBookend
            && refineConfirmed(targets[0]);

        var sorted = vm.Markers.OrderBy(m => m.TimeUs).ToList();
        var eligible = targets
            .Where(m => !m.IsBookend && (!m.Confirmed || allowConfirmedSingle))
            .Distinct()
            .OrderBy(m => m.TimeUs)
            .ToList();

        if (eligible.Count == 0)
        {
            vm.Banner = new BannerInfo(StatusKind.Info,
                "Nothing to refine.",
                "Bookends and confirmed splits are skipped.",
                Array.Empty<BannerAction>());
            return;
        }

        // Surface the refine pass start with both a banner AND status-bar
        // text so it's hard to miss. Banner stays until pass completes.
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var refineSource = vm.SourcePath;
        vm.IsBusy = true;
        vm.BusyOperation = "Refining";
        vm.Banner = new BannerInfo(StatusKind.Info,
            eligible.Count == 1 ? "Refining 1 split…" : $"Refining {eligible.Count} splits…",
            "Probing ±2 s around each marker for cleaner cut boundaries.",
            new[] { new BannerAction("Cancel", Cancel) });
        vm.StatusKind = StatusKind.Info;
        vm.ProgressPercent = 0.0;
        vm.StatusOverride = $"Refining 1 / {eligible.Count}…";
        // Clear the previous-pass chip while this pass runs; it'll be repopulated
        // on completion (or stay clear if cancelled / no mutations applied).
        vm.RefineSummary = null;

        var mutations = new List<RefineMutation>(eligible.Count);
        int high = 0, medium = 0, low = 0, unchanged = 0;

        for (int i = 0; i < eligible.Count; i++)
        {
            var marker = eligible[i];
            var idx = sorted.IndexOf(marker);
            long minBound = idx > 0 ? sorted[idx - 1].TimeUs : 0;
            if (idx > 0 && mutations.LastOrDefault(m => m.Marker == sorted[idx - 1]) is { } previous) minBound = previous.ToUs;
            long maxBound = idx < sorted.Count - 1 ? sorted[idx + 1].TimeUs : vm.DurationUs;

            vm.StatusOverride = $"Refining {i + 1} / {eligible.Count}…";
            vm.ProgressPercent = (i + 1) / (double)eligible.Count;

            RefineResult? result;
            try
            {
                ct.ThrowIfCancellationRequested();
                result = await refine(vm.SourcePath, marker.TimeUs, minBound, maxBound, ct, primaryAudioIndex());
            }
            catch (OperationCanceledException)
            {
                vm.ClearStatus();
                vm.Banner = new BannerInfo(StatusKind.Warning,
                    "Refine cancelled.",
                    "No marker changes were applied.",
                    Array.Empty<BannerAction>());
                vm.IsBusy = false;
                vm.BusyOperation = null;
                return;
            }
            catch (Exception ex)
            {
                vm.ClearStatus();
                vm.Banner = new BannerInfo(StatusKind.Danger,
                    $"Refine failed on split #{idx}.",
                    ex.Message,
                    Array.Empty<BannerAction>());
                vm.IsBusy = false;
                vm.BusyOperation = null;
                return;
            }

            if (ct.IsCancellationRequested || vm.SourcePath != refineSource) { EndCancelled(vm); return; }
            if (result is null || result.Confidence == Confidence.Unchanged)
            {
                unchanged++;
                continue;
            }

            switch (result.Confidence)
            {
                case Confidence.High:   high++; break;
                case Confidence.Medium: medium++; break;
                case Confidence.Low:    low++; break;
            }

            mutations.Add(new RefineMutation(
                Marker: marker,
                FromUs: marker.TimeUs,
                ToUs: result.RefinedTimeUs,
                FromConfidence: marker.Confidence,
                ToConfidence: result.Confidence,
                FromOriginalTimeUs: marker.OriginalTimeUs,
                ToOriginalTimeUs: marker.OriginalTimeUs ?? marker.TimeUs));
        }

        if (ct.IsCancellationRequested || vm.SourcePath != refineSource) { EndCancelled(vm); return; }
        vm.ClearStatus();
        vm.IsBusy = false;
        vm.BusyOperation = null;
        if (mutations.Count > 0)
            vm.CommandStack.Execute(new BatchedRefineCommand(vm, mutations));

        // Build a useful completion banner: success if anything moved, info
        // otherwise. When the user refined a single split (the most common
        // case from the right-click menu), show the actual delta so they can
        // tell the algorithm did something even when the visual shift is
        // sub-pixel on a wide timeline.
        StatusKind kind;
        string title;
        string body;
        if (mutations.Count == 1)
        {
            var m = mutations[0];
            var deltaMs = (m.ToUs - m.FromUs) / 1000.0;
            kind = StatusKind.Success;
            title = $"Refined to {FormatTimeMs(m.ToUs)} ({m.ToConfidence})";
            body = deltaMs == 0
                ? "Marker stayed on the same frame (already on a clean boundary)."
                : $"Moved {(deltaMs >= 0 ? "+" : "")}{deltaMs:0.0} ms from {FormatTimeMs(m.FromUs)}.";
        }
        else if (mutations.Count > 0)
        {
            kind = StatusKind.Success;
            title = $"Refined {mutations.Count} of {eligible.Count} splits.";
            body = $"{high} high · {medium} medium · {low} low · {unchanged} unchanged";
        }
        else
        {
            kind = StatusKind.Info;
            title = eligible.Count == 1
                ? "No movement found for this split."
                : $"No movement found for {eligible.Count} splits.";
            body = "Marker stayed put - no candidate frame scored above the threshold.";
        }
        vm.Banner = new BannerInfo(kind, title, body, Array.Empty<BannerAction>());

        // Persistent summary chip in the timeline toolbar - survives banner
        // dismissal so the user can glance at the last pass's outcome while
        // working through the markers. Only set when the pass produced data
        // (eligible > 0) so it doesn't show "0 0 0 0" for nothing-to-do.
        if (eligible.Count > 0)
            vm.RefineSummary = $"{high}H · {medium}M · {low}L · {unchanged}U";
    }

    // A cancel that lands between two markers skips the exception path; the editor must still be released.
    private static void EndCancelled(MainViewModel vm)
    {
        vm.ClearStatus();
        vm.Banner = new BannerInfo(StatusKind.Warning, "Refine cancelled.", "No marker changes were applied.", Array.Empty<BannerAction>());
        vm.IsBusy = false;
        vm.BusyOperation = null;
    }

    public static string FormatTimeMs(long us)
    {
        var totalSec = us / 1_000_000;
        var h = (int)(totalSec / 3600);
        var m = (int)((totalSec % 3600) / 60);
        var sec = (int)(totalSec % 60);
        var ms = (int)((us % 1_000_000) / 1_000);
        return h > 0 ? $"{h}:{m:00}:{sec:00}.{ms:000}" : $"{m:00}:{sec:00}.{ms:000}";
    }
}
