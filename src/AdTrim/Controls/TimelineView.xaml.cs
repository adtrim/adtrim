using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AdTrim.Commands;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;

namespace AdTrim.Controls;

public partial class TimelineView : UserControl
{
    public TimelineView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private MainViewModel? _vm;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.Markers.CollectionChanged -= OnCollectionChanged;
            _vm.Segments.CollectionChanged -= OnCollectionChanged;
            _vm.Thumbnails.CollectionChanged -= OnCollectionChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as MainViewModel;
        if (_vm != null)
        {
            _vm.Markers.CollectionChanged += OnCollectionChanged;
            _vm.Segments.CollectionChanged += OnCollectionChanged;
            _vm.Thumbnails.CollectionChanged += OnCollectionChanged;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
        Redraw();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Hot path during playback: mpv reports time-pos at 10-30 Hz. Full
        // Redraw() rebuilds every Canvas (thumbnails, ruler, segments, markers,
        // waveform, playhead) which drowns the UI thread. Only the playhead
        // moves when PlayheadUs changes - everything else is static - so just
        // repaint that one Canvas.
        if (e.PropertyName == nameof(MainViewModel.PlayheadUs))
        {
            DrawPlayhead(LayoutRoot.ActualWidth);
            return;
        }
        if (e.PropertyName is nameof(MainViewModel.SelectionKind)
            or nameof(MainViewModel.SelectedMarker)
            or nameof(MainViewModel.SelectedSegment)
            or nameof(MainViewModel.DurationUs))
        {
            Redraw();
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.WaveformPeaks))
        {
            DrawWaveform(LayoutRoot.ActualWidth);
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.ShowWaveform))
        {
            DrawWaveform(LayoutRoot.ActualWidth);
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.ShowThumbnails))
        {
            DrawThumbnails(LayoutRoot.ActualWidth);
            return;
        }
        if (e.PropertyName == nameof(MainViewModel.ShowRulerTicks))
        {
            DrawRuler(LayoutRoot.ActualWidth);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_vm is null) return;
        var width = LayoutRoot.ActualWidth;
        if (double.IsNaN(width) || width <= 0) return;

        if (ReferenceEquals(sender, _vm.Thumbnails))
        {
            DrawThumbnails(width);
            return;
        }

        Redraw();
    }

    private void LayoutRoot_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        if (_vm is null) return;
        var width = LayoutRoot.ActualWidth;
        if (double.IsNaN(width) || width <= 0) return;

        DrawThumbnails(width);
        DrawRuler(width);
        DrawWaveform(width);
        DrawSegments(width);
        DrawMarkers(width);
        DrawPlayhead(width);
    }

    private double XOf(long timeUs, double width)
        => _vm!.DurationUs == 0 ? 0 : (timeUs / (double)_vm.DurationUs) * width;

    private long TimeOfX(double x, double width)
        => _vm!.DurationUs == 0 ? 0 : (long)Math.Round((x / width) * _vm.DurationUs);

    private void DrawThumbnails(double width)
    {
        ThumbCanvas.Children.Clear();
        if (_vm is null || !_vm.ShowThumbnails) return;

        var thumbnails = _vm.Thumbnails.OrderBy(t => t.Index).ToList();
        var thumbnailByIndex = new Dictionary<int, TimelineThumbnail>();
        foreach (var thumbnail in thumbnails)
            thumbnailByIndex[thumbnail.Index] = thumbnail;
        var tiles = thumbnails.Count > 0 ? Math.Max(1, thumbnails.Max(t => t.Total)) : ThumbnailService.DefaultTileCount;
        var tw = width / tiles;
        var laneBrush = (Brush)(Application.Current.TryFindResource("Bg.Lane") ?? Brushes.Black);
        var border = (Brush)(Application.Current.TryFindResource("Border.Subtle") ?? Brushes.DimGray);

        for (int i = 0; i < tiles; i++)
        {
            thumbnailByIndex.TryGetValue(i, out var thumb);
            Brush fill = laneBrush;
            if (thumb is not null)
            {
                try
                {
                    fill = GetThumbnailBrush(thumb.ImagePath);
                }
                catch
                {
                    fill = laneBrush;
                }
            }

            var rect = new Rectangle
            {
                Width = Math.Ceiling(tw),
                Height = 56,
                Fill = fill,
            };
            Canvas.SetLeft(rect, i * tw);
            Canvas.SetTop(rect, 0);
            ThumbCanvas.Children.Add(rect);

            if (i < tiles - 1)
            {
                var div = new Rectangle
                {
                    Width = 1, Height = 56,
                    Fill = border,
                };
                Canvas.SetLeft(div, (i + 1) * tw - 0.5);
                Canvas.SetTop(div, 0);
                ThumbCanvas.Children.Add(div);
            }
        }
        var v = new Rectangle
        {
            Width = width, Height = 56,
            Fill = new LinearGradientBrush(
                Color.FromArgb(0x0A, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x40, 0, 0, 0),
                new Point(0, 0), new Point(0, 1)),
            IsHitTestVisible = false,
        };
        ThumbCanvas.Children.Add(v);
    }

    private readonly Dictionary<string, ImageBrush> _thumbnailBrushCache = new(StringComparer.OrdinalIgnoreCase);

    private ImageBrush GetThumbnailBrush(string imagePath)
    {
        if (_thumbnailBrushCache.TryGetValue(imagePath, out var cached))
            return cached;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();

        var brush = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        _thumbnailBrushCache[imagePath] = brush;
        return brush;
    }

    private void DrawRuler(double width)
    {
        RulerCanvas.Children.Clear();
        if (_vm is null || !_vm.ShowRulerTicks) return;

        var durationSec = _vm.DurationUs / 1_000_000.0;
        var step = 30.0 / _vm.ZoomFactor;
        var tertiary = (Brush)(Application.Current.TryFindResource("Text.Tertiary") ?? Brushes.Gray);
        var subtle = (Brush)(Application.Current.TryFindResource("Border.Strong") ?? Brushes.DimGray);

        for (double t = 0; t <= durationSec + 0.01; t += step)
        {
            var x = (t / durationSec) * width;
            var major = Math.Abs(t % 120) < 0.001;
            var tick = new Rectangle
            {
                Width = 1,
                Height = major ? 8 : 4,
                Fill = major ? tertiary : subtle,
            };
            Canvas.SetLeft(tick, x);
            Canvas.SetBottom(tick, 0);
            RulerCanvas.Children.Add(tick);

            if (major)
            {
                var lbl = new TextBlock
                {
                    Text = FormatTime(t, false),
                    FontFamily = (FontFamily)(Application.Current.TryFindResource("Font.Mono") ?? new FontFamily("Consolas")),
                    FontSize = 10,
                    Foreground = tertiary,
                };
                Canvas.SetLeft(lbl, x + 3);
                Canvas.SetTop(lbl, 3);
                RulerCanvas.Children.Add(lbl);
            }
        }
    }

    // Pixel columns whose peak is below this fraction of full scale are
    // flagged as "quiet". Contiguous runs get a yellow background tint so
    // silences (commercial transitions, dialogue gaps) pop visually. Tuned
    // by eye - roughly -26 dB.
    private const double QuietThreshold = 0.05;
    // Lane is 38 px tall (Grid row 5). Envelope is vertically centered.
    private const double WaveformLaneHeight = 38;

    // Memoize the last draw. Redraw() is called on many unrelated events
    // (selection, marker drag, segment edit); each one would otherwise
    // rebuild a 40k-vertex geometry and turn the UI thread into molasses.
    private float[]? _waveformPeaksCache;
    private double _waveformWidthCache = -1;

    private void DrawWaveform(double width)
    {
        if (_vm is null) return;
        if (!_vm.ShowWaveform)
        {
            _waveformPeaksCache = null;
            _waveformWidthCache = -1;
            WaveformCanvas.Children.Clear();
            return;
        }
        var peaks = _vm.WaveformPeaks;

        // Skip when neither peaks nor width changed - Redraw() fires for
        // selection/marker/segment changes which don't affect the waveform.
        if (ReferenceEquals(peaks, _waveformPeaksCache)
            && Math.Abs(width - _waveformWidthCache) < 0.5)
        {
            return;
        }
        _waveformPeaksCache = peaks;
        _waveformWidthCache = width;

        WaveformCanvas.Children.Clear();

        // Still extracting (or no audio stream) - leave the lane blank rather
        // than show fake data. Drawing nothing is the honest signal.
        if (peaks is null || peaks.Length == 0) return;

        int columns = (int)Math.Floor(width);
        if (columns < 2) return;

        // Per-pixel-column peak: take the max across the peak bins that map
        // into this column. When zoomed in, each column covers <1 bin and we
        // just sample.
        var colPeaks = new float[columns];
        for (int x = 0; x < columns; x++)
        {
            int startIdx = (int)((long)x * peaks.Length / columns);
            int endIdx = (int)((long)(x + 1) * peaks.Length / columns);
            if (endIdx <= startIdx) endIdx = startIdx + 1;
            if (endIdx > peaks.Length) endIdx = peaks.Length;
            float pk = 0f;
            for (int k = startIdx; k < endIdx; k++)
                if (peaks[k] > pk) pk = peaks[k];
            colPeaks[x] = pk;
        }

        var lineBrush = (Brush)(Application.Current.TryFindResource("Text.Secondary") ?? Brushes.DimGray);
        var fillBrush = (Brush)(Application.Current.TryFindResource("Border.Strong") ?? Brushes.DimGray);
        var quietTint = (Brush)(Application.Current.TryFindResource("State.WarningTint") ?? Brushes.Transparent);
        var quietLine = (Brush)(Application.Current.TryFindResource("State.Warning") ?? Brushes.Gold);

        // 1) Quiet runs - collect contiguous spans, draw as one Path geometry
        //    each for tint background + centerline. One frozen Path beats N
        //    Rectangle/Line elements for layout/render cost.
        var tintGeo = new StreamGeometry { FillRule = FillRule.Nonzero };
        var lineGeo = new StreamGeometry();
        bool hasQuiet = false;
        using (var tCtx = tintGeo.Open())
        using (var lCtx = lineGeo.Open())
        {
            int runStart = -1;
            double half = WaveformLaneHeight / 2.0;
            for (int x = 0; x <= columns; x++)
            {
                bool quiet = x < columns && colPeaks[x] < QuietThreshold;
                if (quiet && runStart < 0) runStart = x;
                else if (!quiet && runStart >= 0)
                {
                    int runLen = x - runStart;
                    if (runLen >= 2)
                    {
                        hasQuiet = true;
                        tCtx.BeginFigure(new Point(runStart, 0), isFilled: true, isClosed: true);
                        tCtx.LineTo(new Point(runStart + runLen, 0), false, false);
                        tCtx.LineTo(new Point(runStart + runLen, WaveformLaneHeight), false, false);
                        tCtx.LineTo(new Point(runStart, WaveformLaneHeight), false, false);
                        lCtx.BeginFigure(new Point(runStart, half), isFilled: false, isClosed: false);
                        lCtx.LineTo(new Point(runStart + runLen, half), true, false);
                    }
                    runStart = -1;
                }
            }
        }
        if (hasQuiet)
        {
            tintGeo.Freeze();
            lineGeo.Freeze();
            WaveformCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = tintGeo,
                Fill = quietTint,
                IsHitTestVisible = false,
            });
        }

        // 2) Symmetric envelope as a single StreamGeometry path: top edge
        //    L→R, bottom edge R→L, closed. Freeze for cross-thread/GPU-friendly
        //    rendering. ~10x faster than a Polygon with 40k Points.
        var envGeo = new StreamGeometry { FillRule = FillRule.Nonzero };
        double half2 = WaveformLaneHeight / 2.0;
        double amp = half2 - 2;   // 2 px padding top/bottom
        using (var ctx = envGeo.Open())
        {
            ctx.BeginFigure(new Point(0, half2 - colPeaks[0] * amp), isFilled: true, isClosed: true);
            for (int x = 1; x < columns; x++)
                ctx.LineTo(new Point(x, half2 - colPeaks[x] * amp), true, false);
            for (int x = columns - 1; x >= 0; x--)
                ctx.LineTo(new Point(x, half2 + colPeaks[x] * amp), true, false);
        }
        envGeo.Freeze();
        WaveformCanvas.Children.Add(new System.Windows.Shapes.Path
        {
            Data = envGeo,
            Fill = fillBrush,
            Stroke = lineBrush,
            StrokeThickness = 0.5,
            IsHitTestVisible = false,
        });

        // 3) Yellow centerline on top of the envelope for quiet runs (drawn
        //    last so it survives over the dim envelope hairline).
        if (hasQuiet)
        {
            WaveformCanvas.Children.Add(new System.Windows.Shapes.Path
            {
                Data = lineGeo,
                Stroke = quietLine,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            });
        }
    }

    private void DrawSegments(double width)
    {
        SegmentLayer.Children.Clear();
        if (_vm is null) return;
        foreach (var s in _vm.Segments)
        {
            var left = XOf(s.StartUs, width);
            var w = Math.Max(2, XOf(s.EndUs, width) - left);
            var band = new SegmentBand
            {
                Source = s,
                Width = w,
                Height = 56,
                Cursor = Cursors.Hand,
                Tag = s,
                ContextMenu = BuildSegmentContextMenu(s),
            };
            band.MouseLeftButtonDown += OnSegmentClicked;
            band.MouseRightButtonDown += (_, _) => _vm.SelectSegment(s);
            Canvas.SetLeft(band, left);
            Canvas.SetTop(band, 0);
            SegmentLayer.Children.Add(band);
        }
    }

    private System.Windows.Controls.ContextMenu BuildSegmentContextMenu(Segment seg)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var excludedItem = new MenuItem
        {
            Header = seg.IsExcluded ? "Un-exclude segment" : "Mark segment excluded",
            InputGestureText = "X",
        };
        excludedItem.Click += (_, _) =>
        {
            if (_vm is null) return;
            _vm.SelectSegment(seg);
            _vm.CommandStack.Execute(new ToggleExcludedCommand(seg));
        };
        menu.Items.Add(excludedItem);
        var play = new MenuItem { Header = "Play segment", InputGestureText = "Enter" };
        play.Click += (_, _) => SeekRequested?.Invoke(this, (seg.StartUs, StartPlaying: true, Exact: true, StopAtUs: seg.EndUs));
        menu.Items.Add(play);
        return menu;
    }

    private void OnSegmentClicked(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;
        if (sender is FrameworkElement fe && fe.Tag is Segment seg)
        {
            _vm.SelectSegment(seg);
            Redraw();
            e.Handled = true;
        }
    }

    // -------------------------------------------------------------------
    // Markers (with click + drag)
    // -------------------------------------------------------------------

    private Split? _draggingSplit;
    private SplitMarker? _draggingVisual;     // keep a handle to the live UIElement so mid-drag Redraw doesn't destroy it
    private long _dragStartUs;
    private long _dragCurrentUs;
    private Point _dragStartPoint;
    private double _dragWidth;
    private bool _dragMovedPastThreshold;
    private const double DragThresholdPx = 3.0;

    private void DrawMarkers(double width)
    {
        MarkerLane.Children.Clear();
        if (_vm is null) return;
        foreach (var m in _vm.Markers)
        {
            var marker = new SplitMarker
            {
                Width = 30,
                Height = 58,
                Cursor = m.IsBookend ? Cursors.Arrow : Cursors.Hand,
                Tag = m,
            };
            // Bind the visual DPs to the Split model so toggling Confirmed /
            // IsSelected / Confidence / ShowAudition updates the icon
            // immediately - otherwise the marker would only refresh on the
            // next full Redraw (collection change, selection change, resize).
            BindingOperations.SetBinding(marker, SplitMarker.ConfirmedProperty,
                new Binding(nameof(Split.Confirmed)) { Source = m });
            BindingOperations.SetBinding(marker, SplitMarker.IsSelectedProperty,
                new Binding(nameof(Split.IsSelected)) { Source = m });
            BindingOperations.SetBinding(marker, SplitMarker.ConfidenceProperty,
                new Binding(nameof(Split.Confidence))
                {
                    Source = m,
                    TargetNullValue = Confidence.Neutral,
                    FallbackValue = Confidence.Neutral,
                });
            BindingOperations.SetBinding(marker, SplitMarker.ShowAuditionProperty,
                new Binding(nameof(Split.ShowAudition)) { Source = m });
            BindingOperations.SetBinding(marker, SplitMarker.LabelProperty,
                new Binding(nameof(Split.Label)) { Source = m });
            if (!m.IsBookend)
            {
                marker.MouseLeftButtonDown += OnMarkerMouseDown;
                marker.MouseLeftButtonUp += OnMarkerMouseUp;
                marker.MouseMove += OnMarkerMouseMove;
                // Mark the right-click handled - otherwise it bubbles to the
                // timeline background's MouseRightButtonDown which manually
                // opens its own context menu ("Add split here" / "Play from
                // here") and pre-empts WPF's automatic ContextMenu opening
                // for the marker. Result: right-clicking a marker showed the
                // empty-timeline menu instead of marker-specific actions.
                marker.MouseRightButtonDown += (_, args) =>
                {
                    _vm.SelectMarker(m);
                    args.Handled = true;
                };
                marker.ContextMenu = BuildMarkerContextMenu(m);
                marker.ToolTip = BuildMarkerTooltip(m);
                System.Windows.Controls.ToolTipService.SetInitialShowDelay(marker, 250);
            }
            Canvas.SetLeft(marker, XOf(m.TimeUs, width) - 15);
            Canvas.SetTop(marker, 0);
            Panel.SetZIndex(marker, m.IsSelected ? 5 : 2);
            MarkerLane.Children.Add(marker);
        }
    }

    private static object BuildMarkerTooltip(Split split)
    {
        // First line: the marker's current time. Second line (when refined):
        // where it used to be + delta + confidence, so the user can read
        // "what refinement did" without a banner.
        var lines = new System.Collections.Generic.List<string>
        {
            FormatTimeMs(split.TimeUs),
        };
        if (split.OriginalTimeUs is { } orig && orig != split.TimeUs)
        {
            var delta = split.TimeUs - orig;
            var sign = delta >= 0 ? "+" : "";
            lines.Add($"refined from {FormatTimeMs(orig)} ({sign}{delta / 1000.0:0.0} ms)");
        }
        if (split.Confidence is { } c && c != Confidence.Neutral)
            lines.Add($"confidence: {c}");
        if (split.Confirmed) lines.Add("confirmed");
        return string.Join("\n", lines);
    }

    private System.Windows.Controls.ContextMenu BuildMarkerContextMenu(Split split)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var preview = new MenuItem { Header = "Preview split", InputGestureText = "P" };
        preview.Click += (_, _) => AuditionRequested?.Invoke(this, split);
        menu.Items.Add(preview);

        var confirm = new MenuItem
        {
            Header = split.Confirmed ? "Unconfirm split" : "Confirm split",
            InputGestureText = "C",
        };
        confirm.Click += (_, _) =>
        {
            if (_vm is null) return;
            _vm.CommandStack.Execute(new ToggleConfirmedCommand(split));
        };
        menu.Items.Add(confirm);

        var refine = new MenuItem { Header = "Refine this split", InputGestureText = "R" };
        refine.Click += (_, _) => RefineRequested?.Invoke(this, split);
        menu.Items.Add(refine);

        menu.Items.Add(new Separator());

        var prev = new MenuItem { Header = "−1 frame", InputGestureText = "←" };
        prev.Click += (_, _) => NudgeRequested?.Invoke(this, (split, -1));
        menu.Items.Add(prev);
        var next = new MenuItem { Header = "+1 frame", InputGestureText = "→" };
        next.Click += (_, _) => NudgeRequested?.Invoke(this, (split, +1));
        menu.Items.Add(next);

        menu.Items.Add(new Separator());

        var delete = new MenuItem { Header = "Delete split", InputGestureText = "Del" };
        delete.Click += (_, _) =>
        {
            if (_vm is null) return;
            _vm.CommandStack.Execute(new DeleteSplitCommand(_vm, split));
            _vm.ClearSelection();
        };
        menu.Items.Add(delete);
        return menu;
    }

    /// <summary>Raised when the user picks "Preview split" on a marker.</summary>
    public event EventHandler<Split>? AuditionRequested;
    /// <summary>Raised when the user picks "Refine this split".</summary>
    public event EventHandler<Split>? RefineRequested;
    public event EventHandler<Split>? ConfirmedMarkerUnlockRequested;
    /// <summary>Raised on ±N-frame nudge. Item2 is the signed step count.</summary>
    public event EventHandler<(Split split, int direction)>? NudgeRequested;
    /// <summary>Raised when the user wants to move the playhead.
    /// <list type="bullet">
    /// <item><c>StartPlaying</c> - true for "Play from here"/"Play segment"
    ///   context-menu actions, false for casual timeline clicks.</item>
    /// <item><c>Exact</c> - true when the target is a precise time the user
    ///   has placed (marker click, segment-edge click). False for empty-
    ///   timeline clicks where keyframe alignment is fine and faster.</item>
    /// </list></summary>
    public event EventHandler<(long TimeUs, bool StartPlaying, bool Exact, long? StopAtUs)>? SeekRequested;

    private void OnMarkerMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null || sender is not SplitMarker fe || fe.Tag is not Split split) return;
        // Always record drag-start state, even for confirmed markers - a plain
        // click should just select, not trigger the unconfirm prompt. The
        // confirmed-marker check is deferred to OnMarkerMouseMove, fired only
        // when the user has actually moved past the drag threshold.
        _draggingSplit = split;
        _draggingVisual = fe;
        _dragStartUs = split.TimeUs;
        _dragCurrentUs = split.TimeUs;
        _dragWidth = LayoutRoot.ActualWidth;
        _dragStartPoint = e.GetPosition(LayoutRoot);
        _dragMovedPastThreshold = false;
        fe.CaptureMouse();
        e.Handled = true;
    }

    private void OnMarkerMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingSplit is null || _draggingVisual is null || _vm is null
            || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(LayoutRoot);
        var dxPx = now.X - _dragStartPoint.X;
        if (!_dragMovedPastThreshold)
        {
            if (Math.Abs(dxPx) < DragThresholdPx) return;
            _dragMovedPastThreshold = true;
            // First time crossing the drag threshold on a confirmed marker:
            // surface the unlock prompt instead of moving. Release capture
            // and abort the drag so the user re-attempts after unconfirming.
            // Snap the marker visual back to the model's TimeUs first -
            // otherwise any incidental displacement during the drag attempt
            // stays on screen until the next full Redraw, making it look
            // like the marker was moved when the user said "No".
            if (_draggingSplit.Confirmed && !_draggingSplit.IsBookend)
            {
                var split = _draggingSplit;
                var visual = _draggingVisual;
                visual.ReleaseMouseCapture();
                _draggingSplit = null;
                _draggingVisual = null;
                Canvas.SetLeft(visual, XOf(split.TimeUs, _dragWidth) - 15);
                ConfirmedMarkerUnlockRequested?.Invoke(this, split);
                return;
            }
        }
        var deltaUs = (long)Math.Round((dxPx / _dragWidth) * _vm.DurationUs);
        var candidate = _dragStartUs + deltaUs;
        candidate = ClampAgainstNeighbors(_draggingSplit, candidate);
        candidate = FrameSnap.Snap(candidate, _vm.FrameRate, _vm.FrameStartPhaseUs);
        candidate = ClampAgainstNeighbors(_draggingSplit, candidate); // re-clamp after snap
        if (candidate == _dragCurrentUs) return;
        _dragCurrentUs = candidate;

        // Mid-drag, only move the captured visual. The model is committed once
        // on mouse-up so drag does not dirty/autosave/rebuild timeline state.
        Canvas.SetLeft(_draggingVisual, XOf(candidate, _dragWidth) - 15);
    }

    private void OnMarkerMouseUp(object sender, MouseButtonEventArgs e)
    {
        var split = _draggingSplit;
        var visual = _draggingVisual;
        _draggingSplit = null;
        _draggingVisual = null;

        if (split is null || _vm is null) return;
        visual?.ReleaseMouseCapture();

        if (!_dragMovedPastThreshold)
        {
            // Click, not drag - select the marker AND seek the video preview
            // to it. SelectMarker only updates the VM's PlayheadUs; the host
            // (MainWindow) needs the SeekRequested signal to call LibVLC's
            // seek. StartPlaying=false because the user clicked, not "Play
            // from here".
            _vm.SelectMarker(split);
            // Markers are precise positions (chapter-imported, refined, or
            // user-placed). Exact seek lands ON the marker time, not on
            // the nearest preceding keyframe (~500 ms off in fast-seek mode).
            SeekRequested?.Invoke(this, (split.TimeUs, StartPlaying: false, Exact: true, StopAtUs: null));
            Redraw();
            e.Handled = true;
            return;
        }

        // Commit move as a single undoable command. The model has not changed
        // during drag, so this is the first dirty/autosave-triggering edit.
        var finalUs = _dragCurrentUs;
        if (finalUs != _dragStartUs)
            _vm.CommandStack.Execute(new MoveSplitCommand(_vm, split, _dragStartUs, finalUs));
        Redraw();
        e.Handled = true;
    }

    private void CancelDrag()
    {
        if (_draggingSplit is not null)
        {
            _draggingSplit = null;
            _draggingVisual = null;
            Redraw();
        }
    }

    /// <summary>
    /// Hard invariant: splits cannot cross neighbors and must
    /// stay inside [0, duration]. Refuse at the neighbor's position rather
    /// than allowing a swap.
    /// </summary>
    private long ClampAgainstNeighbors(Split split, long candidateUs)
    {
        if (_vm is null) return candidateUs;
        var sorted = _vm.Markers.OrderBy(m => m.TimeUs).ToList();
        var idx = sorted.IndexOf(split);
        long minUs = idx > 0 ? sorted[idx - 1].TimeUs + 1 : 0;
        long maxUs = idx < sorted.Count - 1 ? sorted[idx + 1].TimeUs - 1 : _vm.DurationUs;
        return FrameSnap.Clamp(candidateUs, minUs, maxUs);
    }

    private void OnTimelineHover(object sender, MouseEventArgs e)
    {
        if (_vm is null) return;
        var width = LayoutRoot.ActualWidth;
        if (width <= 0) return;
        var pos = e.GetPosition(LayoutRoot);
        if (pos.X < 0 || pos.X > width) { HoverCanvas.Visibility = Visibility.Collapsed; return; }

        var timeUs = TimeOfX(pos.X, width);
        timeUs = FrameSnap.Clamp(timeUs, 0, _vm.DurationUs);
        DrawHoverIndicator(pos.X, timeUs);
    }

    private void OnTimelineLeave(object sender, MouseEventArgs e)
    {
        HoverCanvas.Visibility = Visibility.Collapsed;
    }

    private Rectangle? _hoverLine;
    private Border? _hoverChip;
    private TextBlock? _hoverLabel;

    private void DrawHoverIndicator(double x, long timeUs)
    {
        EnsureHoverVisuals();
        if (_hoverLine is null || _hoverChip is null || _hoverLabel is null) return;

        HoverCanvas.Visibility = Visibility.Visible;
        _hoverLine.Height = Math.Max(0, LayoutRoot.ActualHeight - 14);
        Canvas.SetLeft(_hoverLine, x - 0.5);
        Canvas.SetTop(_hoverLine, 0);

        _hoverLabel.Text = FormatTimeMs(timeUs);
        // Measure to position smartly: don't let the chip overflow the right edge.
        _hoverChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var chipWidth = _hoverChip.DesiredSize.Width;
        var chipX = Math.Min(Math.Max(0, x + 6), LayoutRoot.ActualWidth - chipWidth);
        Canvas.SetLeft(_hoverChip, chipX);
        Canvas.SetTop(_hoverChip, -4);
    }

    private void EnsureHoverVisuals()
    {
        if (_hoverLine is not null) return;

        var subtle = (Brush)(Application.Current.TryFindResource("Border.Strong") ?? Brushes.DimGray);
        var primary = (Brush)(Application.Current.TryFindResource("Text.Primary") ?? Brushes.White);
        var surface = (Brush)(Application.Current.TryFindResource("Bg.Elevated") ?? Brushes.Black);
        var border = (Brush)(Application.Current.TryFindResource("Border.Strong") ?? Brushes.Gray);

        _hoverLine = new Rectangle
        {
            Width = 1,
            Fill = subtle,
            Opacity = 0.7,
        };

        _hoverLabel = new TextBlock
        {
            FontFamily = (FontFamily)(Application.Current.TryFindResource("Font.Mono") ?? new FontFamily("Consolas")),
            FontSize = 11,
            Foreground = primary,
        };
        _hoverChip = new Border
        {
            Background = surface,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 2),
            Child = _hoverLabel,
        };

        HoverCanvas.Children.Clear();
        HoverCanvas.Children.Add(_hoverLine);
        HoverCanvas.Children.Add(_hoverChip);
    }

    private static string FormatTimeMs(long us)
    {
        var totalSec = us / 1_000_000;
        var h = (int)(totalSec / 3600);
        var m = (int)((totalSec % 3600) / 60);
        var sec = (int)(totalSec % 60);
        var ms = (int)((us % 1_000_000) / 1_000);
        var basePart = h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m:00}:{sec:00}";
        return $"{basePart}.{ms:000}";
    }

    private void OnBackgroundClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || _vm is null) return;
        _vm.ClearSelection();

        // Standard video-editor behavior: clicking an empty timeline area
        // seeks the playhead to that position. SeekRequested bubbles up to
        // the host (MainWindow) which seeks LibVLC.
        var width = LayoutRoot.ActualWidth;
        if (width > 0)
        {
            var pos = e.GetPosition(LayoutRoot);
            var timeUs = FrameSnap.Clamp(TimeOfX(pos.X, width), 0, _vm.DurationUs);
            _vm.PlayheadUs = timeUs;
            // Empty-area click - keyframe alignment is fine, prioritize speed.
            SeekRequested?.Invoke(this, (timeUs, StartPlaying: false, Exact: false, StopAtUs: null));
        }
        Redraw();
    }

    private void OnBackgroundContextMenu(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || _vm is null) return;
        var pos = e.GetPosition(LayoutRoot);
        var width = LayoutRoot.ActualWidth;
        if (width <= 0) return;
        var timeUs = TimeOfX(pos.X, width);
        timeUs = FrameSnap.Clamp(timeUs, 0, _vm.DurationUs);

        var menu = new System.Windows.Controls.ContextMenu();
        var add = new MenuItem { Header = $"Add split here ({timeUs / 1_000_000.0:0.00}s)", InputGestureText = "S" };
        add.Click += (_, _) =>
        {
            if (_vm is null) return;
            var snapped = FrameSnap.Snap(timeUs, _vm.FrameRate, _vm.FrameStartPhaseUs);
            var minDelta = (long)Math.Round(1_000_000.0 / Math.Max(1.0, _vm.FrameRate.AsDouble));
            if (!_vm.Markers.Any(m => Math.Abs(m.TimeUs - snapped) < minDelta))
                _vm.CommandStack.Execute(new AddSplitCommand(_vm, snapped, SplitSource.Manual));
        };
        menu.Items.Add(add);
        var play = new MenuItem { Header = "Play from here", InputGestureText = "Enter" };
        play.Click += (_, _) => SeekRequested?.Invoke(this, (timeUs, StartPlaying: true, Exact: false, StopAtUs: null));
        menu.Items.Add(play);
        menu.IsOpen = true;
        e.Handled = true;
    }

    // -------------------------------------------------------------------
    // Playhead
    // -------------------------------------------------------------------

    private void DrawPlayhead(double width)
    {
        PlayheadCanvas.Children.Clear();
        if (_vm is null) return;
        var x = XOf(_vm.PlayheadUs, width);

        var accent = (Brush)(Application.Current.TryFindResource("Accent.Base") ?? Brushes.DodgerBlue);
        var accentColor = ((SolidColorBrush)accent).Color;

        var totalH = LayoutRoot.ActualHeight - 14;
        var line = new Rectangle
        {
            Width = 1.5,
            Height = totalH,
            Fill = accent,
            Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Color = accentColor, Opacity = 0.55 },
        };
        Canvas.SetLeft(line, x - 0.75);
        Canvas.SetTop(line, 0);
        PlayheadCanvas.Children.Add(line);

        // Diamond cap sits INSIDE the marker lane (the parent Border has
        // CornerRadius=4 which clips content extending above the top edge -
        // see screenshot regression). Position the centre at y=6 so the
        // whole 10×10 diamond fits within the marker lane's vertical band.
        var cap = new Polygon
        {
            Points = new PointCollection { new(0, -5), new(5, 0), new(0, 5), new(-5, 0) },
            Fill = accent,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
        };
        Canvas.SetLeft(cap, x);
        Canvas.SetTop(cap, 6);
        PlayheadCanvas.Children.Add(cap);
    }

    private static string FormatTime(double s, bool withFrames)
    {
        var h = (int)(s / 3600);
        var m = (int)((s % 3600) / 60);
        var sec = (int)(s % 60);
        var basePart = h > 0
            ? $"{h}:{m:00}:{sec:00}"
            : $"{m:00}:{sec:00}";
        if (!withFrames) return basePart;
        var frames = (int)((s % 1.0) * 30);
        return $"{basePart}.{frames:00}";
    }
}
