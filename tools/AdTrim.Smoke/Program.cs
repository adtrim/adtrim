using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdTrim;
using AdTrim.Commands;
using AdTrim.ViewModels;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.Controls;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 1) throw new ArgumentException("Pass no arguments to generate a fixture, or one disposable fixture path.");
        var source = args.Length == 0 ? PrepareFixture().GetAwaiter().GetResult() : Path.GetFullPath(args[0]);
        var directory = Path.Combine(Path.GetDirectoryName(source)!, "smoke-state");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("ADTRIM_DATA_DIR", directory);
        File.WriteAllText(Path.Combine(directory, "preferences.json"),
            System.Text.Json.JsonSerializer.Serialize(new { OutputFolder = directory }));
        var hash = SHA256.HashData(File.ReadAllBytes(source));
        var app = new App();
        app.InitializeComponent();
        using var updateKey = RSA.Create(3072);
        var updateFeed = new UpdateFeed(1, 1, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1),
            "1.2.0", "Update notification smoke test.", []);
        var updateBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(updateFeed,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        var updateSignature = updateKey.SignData(updateBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        var updateService = new UpdateService(directory, updateKey.ExportSubjectPublicKeyInfoPem(),
            new System.Net.Http.HttpClient(new SmokeUpdateHandler(updateBytes, updateSignature)));
        typeof(App).GetField("_updates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, updateService);
        // The harness owns its windows; do not join the installed app's single-instance lifecycle.
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!);
        var window = new MainWindow(source) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        int result = 1;
        window.Show();
        window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await updateService.CheckAsync();
                Require(updateService.Error is null && updateService.Notice is not null, "Signed update reaches the UI without a live network request");
                Require(((Button)window.FindName("UpdateStatusButton")).Visibility == Visibility.Visible,
                    "Available update has a persistent status-bar link");
                Require(((System.Windows.Shapes.Ellipse)window.FindName("UpdateHelpDot")).Visibility == Visibility.Visible,
                    "Help has an update indicator");
                Require(((MenuItem)window.FindName("CheckUpdatesMenu")).Header.ToString()!.Contains("1.2.0"),
                    "Help identifies the available version");
                var about = new AdTrim.Views.AboutDialog { Owner = window, ShowActivated = false, ShowInTaskbar = false };
                about.Show();
                about.UpdateLayout();
                var closeButton = (Button)about.FindName("TitleCloseButton");
                var closeBounds = closeButton.TransformToAncestor(about).TransformBounds(new Rect(closeButton.RenderSize));
                Require(Math.Abs(closeBounds.Top) < 0.1 && Math.Abs(closeBounds.Right - about.ActualWidth) < 0.1,
                    "Dialog close button reaches the top and right window edges");
                var closeBackground = (Border)closeButton.Template.FindName("Bd", closeButton);
                Require(closeBackground.CornerRadius.TopRight == 10 && closeBackground.CornerRadius.TopLeft == 0,
                    "Dialog close background follows the rounded top-right corner");
                closeBackground.Background = new SolidColorBrush(Color.FromRgb(232, 17, 35));
                Capture(about, Path.Combine(directory, "about.png"));
                var escape = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(about), 0, System.Windows.Input.Key.Escape)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                about.RaiseEvent(escape);
                Require(escape.Handled && !about.IsVisible, "Escape closes About");
                foreach (var importance in new[] { "regular", "security", "urgent" })
                {
                    var notice = new UpdateNotice("1.2.0", importance,
                        importance == "regular" ? "Clearer export details and improvements to reviewing your edits."
                        : "This update fixes a vulnerability in video processing. Updating is recommended before opening videos from unfamiliar sources.",
                        importance == "regular" ? [] : ["sample-advisory"]);
                    var update = new AdTrim.Views.UpdateDialog(notice, reminder: importance != "regular")
                        { Owner = window, ShowActivated = false, ShowInTaskbar = false };
                    update.Show();
                    update.UpdateLayout();
                    Require(((TextBlock)update.FindName("Heading")).Text == notice.Title, "Update importance is prominent");
                    Require(((TextBlock)update.FindName("Versions")).Text.Contains("1.2.0"), "Security update version remains visible");
                    var action = (Button)update.FindName("ReleaseButton");
                    Require(action.TransformToAncestor(update).TransformBounds(new Rect(action.RenderSize)).Bottom <= update.ActualHeight,
                        "Update actions fit inside dialog");
                    Capture(update, Path.Combine(directory, "update-" + importance + ".png"));
                    update.Close();
                }
                var emptyView = new AdTrim.Views.EmptyStateView();
                var dropBox = (Grid)((Grid)emptyView.Content).Children[0];
                foreach (double height in new[] { 480.0, 630.0, 1000.0 })
                {
                    emptyView.Measure(new Size(1280, height));
                    emptyView.Arrange(new Rect(0, 0, 1280, height));
                    emptyView.UpdateLayout();
                    var bounds = dropBox.TransformToAncestor(emptyView).TransformBounds(new Rect(dropBox.RenderSize));
                    Require(bounds.Top >= 40 && bounds.Bottom <= height - 40 && dropBox.ActualHeight <= 600,
                        "Welcome drop box fits available height with margins at " + height);
                    Require(Math.Abs(dropBox.ActualHeight - Math.Min(600, height - 80)) < 1,
                        "Welcome drop box grows to its original maximum height");
                }
                var earlyVm = new MainViewModel { DurationUs = 12_000_000 };
                var earlyTimeline = new TimelineView { DataContext = earlyVm };
                earlyVm.PlayheadUs = 1_000_000;
                Require(((Canvas)earlyTimeline.FindName("PlayheadCanvas")).Children
                    .OfType<System.Windows.Shapes.Rectangle>().All(line => line.Height >= 0),
                    "Playback updates before layout cannot produce a negative height");
                earlyTimeline.DataContext = null;
                var vm = (MainViewModel)window.DataContext;
                for (int i = 0; i < 250 && (!vm.IsFileLoaded || vm.IsBusy); i++) await Task.Delay(20);
                Require(window.Ready.IsCompletedSuccessfully && vm.IsFileLoaded && !vm.IsBusy, "Launch-file startup completes");
                await CheckMultipleWindows(app, window, source, directory);
                Require(vm.IsFileLoaded && vm.Markers.Count == 4, "Fixture and sidecar loaded");
                var loadedSource = vm.SourcePath;
                vm.SourcePath = null;
                window.UpdateLayout();
                Require(!((MenuItem)window.FindName("ExportMenuItem")).IsEnabled, "Export menu is disabled without a source video");
                Invoke(window, "OnExport", window, new RoutedEventArgs());
                Require(typeof(MainWindow).GetField("_activeExportDialog", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window) is null,
                    "Shared export handler cannot open an empty export dialog");
                vm.SourcePath = loadedSource;
                window.UpdateLayout();
                Require(((MenuItem)window.FindName("ExportMenuItem")).IsEnabled, "Export menu re-enables when a video is loaded");
                await CheckFramePair(window, vm);
                Require(vm.Segments[1].IsExcluded, "Exclusion restored");
                foreach (var resetName in new[] { "ResetZoomButton", "StatusResetZoomButton" })
                {
                    vm.ZoomFactor = 4;
                    ((Button)window.FindName(resetName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(vm.ZoomFactor == 1, resetName + " restores the full timeline in one click");
                }
                vm.SelectMarker(vm.Markers[1]);
                var keyHandler = typeof(MainWindow).GetMethod("OnPreviewKeyDown", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!;
                for (int toggle = 0; toggle < 2; toggle++)
                {
                    var key = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window), 0, System.Windows.Input.Key.P)
                        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                    keyHandler.Invoke(window, new object[] { window, key });
                    Require(key.Handled && vm.IsCollapsed == (toggle == 0),
                        "P toggles collapsed scenes even with a split selected");
                    foreach (var boundaryKey in new[] { System.Windows.Input.Key.End, System.Windows.Input.Key.Home })
                    {
                        var boundaryEvent = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                            PresentationSource.FromVisual(window), 0, boundaryKey)
                            { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                        keyHandler.Invoke(window, new object[] { window, boundaryEvent });
                        long expectedPosition = boundaryKey == System.Windows.Input.Key.Home ? 0 : vm.TimelineDurationUs;
                        Require(boundaryEvent.Handled && vm.TimelinePositionUs == expectedPosition,
                            boundaryKey + " reaches the timeline boundary in " + (vm.IsCollapsed ? "collapsed" : "full") + " view");
                    }
                }
                await Task.Delay(250);
                RequireSubtitlesDisabled(window);
                var savedBounds = vm.Markers.Select(m => m.TimeUs).ToArray();
                var expectedDuration = vm.ExpectedOutputDurationUs;
                var timeline = window.FindName("Timeline") as FrameworkElement;
                window.Width = 1280; window.Height = 720; window.UpdateLayout();
                Require(timeline is { ActualHeight: > 0 }, "Timeline fits short window");
                Capture(window, Path.Combine(directory, "edit.png"));
                CheckSegmentToolbar(window, vm, directory);
                CheckNearbyReviewButtons();
                const string chapterName = "Opening = café; #1 \\ intro";
                _ = window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = app.Windows.OfType<AdTrim.Views.RenameSegmentDialog>().Single();
                    var name = (TextBox)dialog.FindName("NameBox");
                    var accept = (Button)dialog.FindName("RenameButton");
                    name.Text = "  ";
                    Require(!accept.IsEnabled, "Rename rejects blank names");
                    name.Text = chapterName;
                    accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }), DispatcherPriority.ApplicationIdle);
                Invoke(window, "OnToggleCollapsed", window, new RoutedEventArgs());
                var band = ((Canvas)((TimelineView)timeline!).FindName("SegmentLayer")).Children.OfType<SegmentBand>().First();
                band.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount, System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                var segmentMenu = band.ContextMenu!;
                Require(segmentMenu.IsOpen, "Right-click opens segment actions instead of background actions");
                segmentMenu.IsOpen = false;
                ((MenuItem)segmentMenu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(vm.IsCollapsed && segmentMenu.Items.Count == 1, "Preview menu allows rename without boundary-edit actions");
                Require(vm.Segments[0].Label == chapterName && vm.IsDirty, "Preview context menu renames the segment");
                vm.CommandStack.Undo();
                Require(vm.Segments[0].Label != chapterName, "Preview rename supports undo");
                vm.CommandStack.Redo();
                Require(vm.Segments[0].Label == chapterName, "Preview rename supports redo");
                Invoke(window, "OnToggleCollapsed", window, new RoutedEventArgs());
                vm.CommandStack.Execute(new RenameSegmentCommand(vm, vm.Segments[2], "Final scene"));
                Invoke(window, "OnToggleCollapsed", window, new RoutedEventArgs());
                await Task.Delay(300);
                Require(vm.IsCollapsed, "Collapsed mode enabled");
                RequireSubtitlesDisabled(window);
                Capture(window, Path.Combine(directory, "collapsed.png"));
                Require(vm.TimelineDurationUs == expectedDuration, "Kept duration displayed");
                Require(((System.Windows.Controls.Primitives.ToggleButton)window.FindName("CollapseToggle")).IsChecked == true, "Visible toggle agrees");
                var reviewCut = ((Canvas)((TimelineView)timeline!).FindName("MarkerLane")).Children.OfType<Button>().Single();
                Require(System.Windows.Automation.AutomationProperties.GetName(reviewCut) == "Review this split",
                    "Review icon has an accessible action name");
                reviewCut.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(vm.IsCollapsed && vm.TimelinePositionUs == 2_000_000,
                    "Review starts two kept seconds before the cut without expanding the timeline");
                await Task.Delay(600);
                Require(vm.TimelinePositionUs > 2_000_000, "Review starts playback from paused state");
                reviewCut.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(vm.TimelinePositionUs == 2_000_000, "Review restarts the lead-in during playback");
                await Task.Delay(2600);
                Require(vm.IsCollapsed && vm.PlayheadUs >= 8_000_000 && vm.TimelinePositionUs > 4_000_000,
                    "Review continues across the excluded scene into kept footage");
                Invoke(window, "PlayPauseBoth");
                Invoke(window, "SeekTo", vm, vm.Markers[2].TimeUs, true);
                Invoke(window, "OnPreviousSplit", window, new RoutedEventArgs());
                Require(vm.TimelinePositionUs == 0, "Previous split moves backward across a collapsed join");
                Invoke(window, "OnToggleCollapsed", window, new RoutedEventArgs());
                await Task.Delay(200);
                Require(!vm.IsCollapsed && savedBounds.SequenceEqual(vm.Markers.Select(m => m.TimeUs)), "Toggle preserves cuts");
                vm.CommandStack.Execute(new MoveSplitCommand(vm, vm.Markers[1], vm.Markers[1].TimeUs, vm.Markers[1].TimeUs + 100_000));
                Require(vm.Segments[1].IsExcluded, "Moved exclusion survives");
                var save = (Task<bool>)Invoke(window, "SaveProjectAsync", vm)!;
                Require(await save && !vm.IsDirty && vm.SaveStatus == "Saved", "Save completes truthfully");
                vm.CommandStack.Undo();
                Require(await (Task<bool>)Invoke(window, "SaveProjectAsync", vm)!, "Undo saved: " + vm.Banner?.Body);
                vm.CommandStack.Execute(new ToggleConfirmedCommand(vm.Markers[1]));
                var pending = (Task<bool>)Invoke(window, "SaveProjectAsync", vm)!;
                vm.CommandStack.Execute(new ToggleConfirmedCommand(vm.Markers[2]));
                Require(await pending && vm.IsDirty, "An older save cannot clear newer edits");
                Require(await (Task<bool>)Invoke(window, "SaveProjectAsync", vm)!, "Newer edits saved");
                vm.CommandStack.Undo();
                vm.CommandStack.Undo();
                for (int i = 0; i < 10; i++) Invoke(window, "OnToggleCollapsed", window, new RoutedEventArgs());
                await Task.Delay(300);
                Require(!vm.IsCollapsed && vm.PlayheadUs >= 0 && vm.PlayheadUs <= vm.DurationUs, "Rapid toggling stays in sync");
                Invoke(window, "OnToggleCollapsed", window, new RoutedEventArgs());
                Invoke(window, "OnCloseProject", window, new RoutedEventArgs());
                for (int i = 0; i < 100 && vm.SourcePath is not null; i++) await Task.Delay(20);
                Require(vm.SourcePath is null, "Close flushes pending changes");
                await window.OpenFileAsync(source);
                Require(!vm.IsCollapsed && ((System.Windows.Controls.Primitives.ToggleButton)window.FindName("CollapseToggle")).IsChecked == false,
                    "Closing collapsed playback restores Edit view on reopen");
                Require(!vm.Markers[1].Confirmed && !vm.Markers[2].Confirmed, "Reopen restores final saved decisions");
                Require(vm.Segments[0].Label == chapterName && vm.Segments[2].Label == "Final scene",
                    "Reopen restores first and later chapter names");
                var runner = new FfmpegRunner();
                var media = await new MediaProbeService(runner).ProbeAsync(source);
                var exportVm = new ExportDialogViewModel(vm, media) { OutputFolder = directory, OutputFilename = "renamed.mp4" };
                var protectionVm = new ExportDialogViewModel(vm, media)
                {
                    OutputFolder = Path.GetDirectoryName(source)!, OutputFilename = Path.GetFileName(source), OverwriteConfirmed = true,
                };
                Require(protectionVm.Validate().Any(v => v.Kind == ExportValidationKind.Blocking && v.Message.Contains("source MP4")),
                    "Source overwrite stays blocked even with overwrite confirmation");
                Invoke(window, "OnExport", window, new RoutedEventArgs());
                var folderDialog = (AdTrim.Views.ExportView)((ContentControl)window.FindName("ExportHost")).Content;
                var folderVm = (ExportDialogViewModel)folderDialog.DataContext;
                Require(folderVm.OutputFolder == Path.GetDirectoryName(source), "Export ignores the previous recording's saved destination");
                folderVm.OutputFolder = directory;
                folderDialog.Close();
                Invoke(window, "OnExport", window, new RoutedEventArgs());
                folderDialog = (AdTrim.Views.ExportView)((ContentControl)window.FindName("ExportHost")).Content;
                Require(((ExportDialogViewModel)folderDialog.DataContext).OutputFolder == Path.GetDirectoryName(source),
                    "Every new export dialog defaults to the current recording's folder");
                Require(vm.IsExportScreen && !vm.CanEdit && !vm.CanInteract, "Export screen disables editor commands");
                var savedSelection = vm.SelectionKind;
                var savedMarker = vm.SelectedMarker;
                var savedSegment = vm.SelectedSegment;
                var savedZoom = vm.ZoomFactor;
                var savedPosition = vm.PlayheadUs;
                Invoke(window, "OnFitTimeline", window, new RoutedEventArgs());
                Require(vm.ZoomFactor == savedZoom, "Export screen blocks timeline commands outside the hidden editor");
                window.UpdateLayout();
                Capture(window, Path.Combine(directory, "export-page-setup.png"));
                folderVm = (ExportDialogViewModel)folderDialog.DataContext;
                for (int i = 0; i < 250 && !folderVm.CanCheckHardware; i++) await Task.Delay(20);
                Require(folderVm.CanCheckHardware, "Export options finish listing before the test starts exporting");
                folderVm.SelectedAcceleration = ExportAccelerationOption.Software;
                folderVm.OutputFolder = directory;
                folderVm.OutputFilename = "page-export.mp4";
                Invoke(folderDialog, "OnExport", folderDialog, new RoutedEventArgs());
                Require(folderDialog.IsExportInFlight && folderDialog.ExportTask is not null, "Start an export inside the recording window");
                folderDialog.Close();
                Require(vm.IsExportScreen, "A running export cannot be detached from its recording window");
                window.UpdateLayout();
                Capture(window, Path.Combine(directory, "export-page-progress.png"));
                await folderDialog.ExportTask!;
                for (int i = 0; i < 100 && folderDialog.IsExportInFlight; i++) await Task.Delay(20);
                Require(folderVm.IsCompleted, "Embedded export reaches completion");
                folderDialog.Close();
                Require(!vm.IsExportScreen && vm.ZoomFactor == savedZoom && vm.PlayheadUs == savedPosition,
                    "Back to editing restores timeline position and zoom");
                Require(vm.SelectionKind == savedSelection && vm.SelectedMarker == savedMarker && vm.SelectedSegment == savedSegment,
                    "Back to editing preserves the selected boundary or segment");
                await CheckConcurrentExports(app, window, directory);
                var plan = exportVm.BuildPlan()!;
                var progressDialog = new AdTrim.Views.ExportView();
                progressDialog.Bind(vm, media, "software");
                var progressVm = (ExportDialogViewModel)progressDialog.DataContext;
                progressVm.BeginExport(plan);
                Require(progressVm.Parts.Last().Label == "Stitch parts" && progressVm.Parts.Last().TimeRange == "",
                    "Final export row describes stitching without repeating the output filename");
                Require(progressVm.Parts.All(p => p.Index >= 0), "Export has no evaluation step until tests actually start");
                progressVm.BeginEncoderEvaluation();
                Require(progressVm.Parts[0].Index == -1 && progressVm.Parts[0].IsInProgress,
                    "Encoder evaluation appears first and active before encoding");
                progressVm.CompleteEncoderEvaluation(true);
                progressVm.UpdateProgress(new(ExportPhase.EncodingSegment, 1, plan.KeptSegments.Count, 0.1, 0.1, "Encoding"));
                Require(progressVm.Parts[0].IsDone && progressVm.Parts.First(p => p.Index == 1).IsInProgress,
                    "Evaluation row does not shift encoding progress to the wrong part");
                progressVm.BeginExport(plan);
                progressVm.BeginEncoderEvaluation();
                progressVm.MarkCancelled();
                Require(progressVm.Parts[0].IsFailed, "Cancellation stops the active evaluation row");
                progressVm.BeginExport(plan);
                var progressHost = ShowExportForTest(progressDialog, window);
                Require(progressVm.ExportMethodLabel == "Choosing automatically...", "Automatic export does not claim a device before selection");
                foreach (var encoder in new[] { "AMD Radeon RX 9060 XT (hardware H.264)", "Software (H.264)" })
                {
                    progressVm.UpdateProgress(new(ExportPhase.EncodingSegment, 1, 2, 0, 0, "Encoding", encoder));
                    progressDialog.UpdateLayout();
                    var expectedMethod = encoder.StartsWith("Software") ? "Processor · Software" : "Graphics card · AMD Radeon RX 9060 XT";
                    var methodValue = (TextBlock)progressDialog.FindName("ExportMethodValue");
                    var destinationDetails = (Grid)progressDialog.FindName("ExportDestinationDetails");
                    Require(methodValue.Parent == destinationDetails && Grid.GetRow(methodValue) == 2, "Export method shares the destination information bar");
                    Require(methodValue.Text == expectedMethod, "Export method identifies the actual encoder in plain language");
                    Require(methodValue.ActualHeight < 30, "Export method stays a compact single row");
                    var details = (Border)progressDialog.FindName("ProgressDetails");
                    Require(details.Visibility == Visibility.Visible, "Export details are always visible");
                    Require(((System.Windows.Shapes.Path)progressDialog.FindName("ProgressDial")).Data is not null,
                        "Progress dial has valid geometry at zero progress");
                    Capture(progressDialog, Path.Combine(directory, encoder.StartsWith("Software") ? "export-software.png" : "export-hardware.png"));
                }
                var detailsPanel = (Border)progressDialog.FindName("ProgressDetails");
                var collapsedHeight = progressDialog.ActualHeight;
                progressVm.UpdateProgress(new(ExportPhase.EncodingSegment, 1, 2, 0.71, 0.04, "Encoding"));
                progressDialog.UpdateLayout();
                Require(progressVm.CurrentPartHeading.Contains(plan.KeptSegments[0].PartTitle), "Current part uses its actual chapter title");
                Require(progressVm.ProgressPercentText == "4%", "Dial shows total progress rather than current part progress");
                var headline = (TextBlock)progressDialog.FindName("TimeHeadline");
                Require(((SolidColorBrush)headline.Foreground).Color == ((SolidColorBrush)progressDialog.FindResource("Text.Primary")).Color,
                    "Primary progress text remains readable on the dark background");
                Capture(progressDialog, Path.Combine(directory, "quiet-focus.png"));


                foreach (int count in new[] { 1, 12, 100 })
                {
                    progressVm.Parts.Clear();
                    for (int i = 0; i < count; i++) progressVm.Parts.Add(new ExportPartItem { Label = i % 2 == 0 ? $"Commercial {i + 1}" : "A very long chapter name that should fit without touching the source times", TimeRange = "1:23:18.55 → 1:34:42.71" });
                    progressDialog.UpdateLayout();
                    var scroll = (ScrollViewer)progressDialog.FindName("PartsScrollViewer");
                    var footer = (FrameworkElement)progressDialog.FindName("ProgressFooter");
                    var bounds = footer.TransformToAncestor(progressDialog).TransformBounds(new Rect(footer.RenderSize));
                    Require(scroll.ViewportHeight > 0 && bounds.Bottom <= progressDialog.ActualHeight && bounds.Top > 0,
                        $"Export footer stays visible with {count} parts");
                    Require(progressDialog.ActualHeight <= progressDialog.MaxHeight + 1,
                        "Progress window stays within its monitor height cap");
                    Require(Math.Abs(progressDialog.ActualHeight - collapsedHeight) < 2, "Opening details preserves the progress window height");
                    var mainPanel = (Grid)progressDialog.FindName("ProgressMainPanel");
                    Require(Math.Abs(mainPanel.ActualWidth - 420) < 2,
                        "Progress panel keeps its readable width while details expand");
                    if (count == 100)
                    {

                        Require(scroll.ScrollableHeight > 0, "Long export parts list scrolls within available height");
                        scroll.ScrollToBottom();
                        progressDialog.UpdateLayout();
                        Require(scroll.VerticalOffset > 0, "Final export parts can be reached by scrolling");
                        var title = (TextBlock)progressDialog.FindName("ProgressDetailsTitle");
                        Require(title.IsVisible, "Details title remains outside the scroll body");
                        var destination = (Grid)progressDialog.FindName("ExportDestinationDetails");
                        Require(destination.TransformToAncestor(scroll).Transform(new Point()).Y < 0,
                            "Scrolling details moves destination information along with the parts list");
                    }
                }
                ((ScrollViewer)progressDialog.FindName("PartsScrollViewer")).ScrollToTop();
                progressDialog.UpdateLayout();
                var rows = (ItemsControl)progressDialog.FindName("ExportPartsList");
                var row = (ContentPresenter)rows.ItemContainerGenerator.ContainerFromIndex(1);
                var rowName = (TextBlock)row.ContentTemplate.FindName("PartName", row);
                var rowRange = (TextBlock)row.ContentTemplate.FindName("PartRange", row);
                var nameBounds = rowName.TransformToAncestor(row).TransformBounds(new Rect(rowName.RenderSize));
                var rangeBounds = rowRange.TransformToAncestor(row).TransformBounds(new Rect(rowRange.RenderSize));
                Require(rangeBounds.Left - nameBounds.Right >= 16 && rowName.ActualWidth >= 140,
                    "Long chapter names retain readable width and a clear gap before source times");
                Capture(progressDialog, Path.Combine(directory, "quiet-focus-details.png"));
                var compactSummary = (TextBlock)progressDialog.FindName("PartsSummaryText");
                Require(compactSummary.Text.Contains("parts kept ·") && !compactSummary.Text.Contains("final video"),
                    "Compact summary shows counts without repeating the final duration");
                foreach (var size in new[] { new Size(1280, 540), new Size(1280, 900), new Size(1920, 1000) })
                {
                    progressHost.Width = size.Width;
                    progressHost.Height = size.Height;
                    progressHost.UpdateLayout();
                    var scroll = (ScrollViewer)progressDialog.FindName("PartsScrollViewer");
                    var footer = (FrameworkElement)progressDialog.FindName("ProgressFooter");
                    var footerBounds = footer.TransformToAncestor(progressDialog).TransformBounds(new Rect(footer.RenderSize));
                    Require(footerBounds.Bottom <= progressDialog.ActualHeight && scroll.ViewportHeight > 100,
                        "Export page keeps actions visible and details scrollable at " + size);
                }
                Capture(progressDialog, Path.Combine(directory, "quiet-focus-resized.png"));
                progressVm.MarkComplete();
                progressDialog.UpdateLayout();
                Require(progressVm.ProgressPercentText == "100%" && progressVm.TimeHeadline == "Your video is ready", "Completion replaces the time estimate and fills the dial");
                Capture(progressDialog, Path.Combine(directory, "quiet-focus-complete.png"));
                progressVm.MarkFailed("Synthetic export failure");
                progressDialog.UpdateLayout();
                Require(progressVm.TimeHeadline == "Export could not finish", "Failure cannot retain a misleading time estimate");
                Capture(progressDialog, Path.Combine(directory, "quiet-focus-failed.png"));
                progressVm.MarkCancelled();
                Require(progressVm.TimeHeadline == "Export cancelled", "Cancellation has its own clear terminal label");
                progressVm.UpdateProgress(new(ExportPhase.Cleanup, 0, 0, 0, 0, "Temporary files remain; cleanup will retry."));
                progressDialog.UpdateLayout();
                var cleanupNotice = (TextBlock)progressDialog.FindName("CleanupWarningText");
                Require(cleanupNotice.IsVisible && cleanupNotice.Text.Contains("retry"), "Cleanup warning remains visible after cancellation");
                progressDialog.Close();
                Require(plan.KeptSegments.Select(s => s.PartTitle).SequenceEqual(new[] { chapterName, "Final scene" }),
                    "Export plan retains names and omits excluded chapters");
                await new ExportService(runner, new AdTrim.Encoders.LibX264EncoderStrategy()).RunExportAsync(plan);
                var cleanupRegistry = Path.Combine(UserPreferences.DataDirectory, "export-cleanup");
                Require(!Directory.EnumerateFiles(cleanupRegistry, "record.json", SearchOption.AllDirectories).Any(),
                    "Successful exports remove their temporary workspace records");
                using (var cancelExport = new CancellationTokenSource())
                {
                    bool encoded = false;
                    var cancelledPlan = plan with { OutputPath = Path.Combine(directory, "cancelled.mp4") };
                    try
                    {
                        await new ExportService(runner, new AdTrim.Encoders.LibX264EncoderStrategy()).RunExportAsync(cancelledPlan,
                            new ImmediateExportProgress(p =>
                            {
                                if (p.Phase == ExportPhase.EncodingSegment && p.SegmentPercent > 0)
                                { encoded = true; cancelExport.Cancel(); }
                            }), cancelExport.Token);
                        throw new InvalidOperationException("Expected cancellation during encoding");
                    }
                    catch (OperationCanceledException) when (cancelExport.IsCancellationRequested) { }
                    Require(encoded && !File.Exists(cancelledPlan.OutputPath), "Cancelling real encoding does not publish a partial output");
                    Require(!Directory.EnumerateFiles(cleanupRegistry, "record.json", SearchOption.AllDirectories).Any(),
                        "Cancellation cleans temporary video and its recovery record");
                    Require(!Directory.EnumerateFiles(directory, ".adtrim-*.mp4").Any(), "Cancellation leaves no staging video");
                }
                var chapters = await runner.RunFfprobeAsync(new[] { "-v", "error", "-show_chapters", "-of", "json", plan.OutputPath });
                using var chapterJson = System.Text.Json.JsonDocument.Parse(chapters.Stdout);
                Require(chapters.Success && chapterJson.RootElement.GetProperty("chapters").EnumerateArray()
                    .Select(c => c.GetProperty("tags").GetProperty("title").GetString())
                    .SequenceEqual(new[] { chapterName, "Final scene" }), "Exported MP4 preserves exact chapter names");
                Require(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))), "Source unchanged");
                if (Environment.GetEnvironmentVariable("ADTRIM_TEST_HARDWARE") == "1")
                    await HardwareChecks.RunAsync(window, directory, vm, media);
                await CheckIrregularFrames(window, directory);
                Console.WriteLine("All application smoke checks passed.");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                typeof(MainWindow).GetField("_allowClose", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                window.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }));
        Dispatcher.Run();
        return result;
    }
    private static async Task CheckConcurrentExports(App app, MainWindow original, string directory)
    {
        var secondSource = Path.Combine(directory, "second.mp4");
        var second = await app.OpenRecordingAsync(secondSource);
        second.Opacity = 0;
        second.ShowInTaskbar = false;
        ((MainViewModel)second.DataContext).Segments[0].State = SegmentState.Excluded;
        Invoke(original, "OnExport", original, new RoutedEventArgs());
        Invoke(second, "OnExport", second, new RoutedEventArgs());
        var firstView = (AdTrim.Views.ExportView)((ContentControl)original.FindName("ExportHost")).Content;
        var secondView = (AdTrim.Views.ExportView)((ContentControl)second.FindName("ExportHost")).Content;
        var firstVm = (ExportDialogViewModel)firstView.DataContext;
        var secondVm = (ExportDialogViewModel)secondView.DataContext;
        for (int i = 0; i < 250 && (!firstVm.CanCheckHardware || !secondVm.CanCheckHardware); i++) await Task.Delay(20);
        firstVm.SelectedAcceleration = secondVm.SelectedAcceleration = ExportAccelerationOption.Software;
        firstVm.OutputFolder = secondVm.OutputFolder = directory;
        firstVm.OutputFilename = "second.mp4";
        Invoke(firstView, "OnExport", firstView, new RoutedEventArgs());
        Require(!firstView.IsExportInFlight && firstVm.ValidationIssues.Any(v => v.Message.Contains("another AdTrim window")),
            "An export cannot overwrite a recording owned by another window");
        firstVm.OutputFilename = secondVm.OutputFilename = "simultaneous.mp4";
        Invoke(firstView, "OnExport", firstView, new RoutedEventArgs());
        Invoke(secondView, "OnExport", secondView, new RoutedEventArgs());
        Require(!secondView.IsExportInFlight && secondVm.ValidationIssues.Any(v => v.Message.Contains("Another window is exporting")),
            "Two windows cannot publish to the same output filename");
        secondVm.OutputFilename = "simultaneous-second.mp4";
        Invoke(secondView, "OnExport", secondView, new RoutedEventArgs());
        Require(firstView.IsExportInFlight && secondView.IsExportInFlight, "Independent windows can run exports simultaneously");
        await secondView.CancelAndWaitAsync();
        Require(secondVm.Mode == ExportDialogMode.Cancelled, "Cancelling one window stops only its export");
        await firstView.ExportTask!;
        for (int i = 0; i < 100 && firstView.IsExportInFlight; i++) await Task.Delay(20);
        Require(firstVm.IsCompleted, "The other window's export still completes");
        firstView.Close();
        secondView.Close();
        second.Close();
        for (int i = 0; i < 100 && second.IsVisible; i++) await Task.Delay(20);
    }

    internal static Window ShowExportForTest(AdTrim.Views.ExportView view, Window owner)
    {
        var host = new Window { Owner = owner, Width = 1280, Height = 720, Content = view,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        view.Closed += (_, _) => host.Close();
        host.Show();
        return host;
    }

    private static async Task CheckMultipleWindows(App app, MainWindow original, string source, string directory)
    {
        var secondSource = Path.Combine(directory, "second.mp4");
        File.Copy(source, secondSource, true);
        using var pipeCancellation = new CancellationTokenSource();
        var server = (Task)typeof(App).GetMethod("RunPipeServerAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { pipeCancellation.Token })!;
        await Task.Run(() => typeof(App).GetMethod("TrySendPathToExistingInstance", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { "" }));
        for (int i = 0; i < 100 && app.Windows.OfType<MainWindow>().Count() < 2; i++) await Task.Delay(20);
        var empty = app.Windows.OfType<MainWindow>().Single(w => w != original);
        pipeCancellation.Cancel();
        await server;
        Require(app.Windows.OfType<MainWindow>().Count() == 2, "Launching AdTrim without a path requests a new window through the app handoff");
        empty.Opacity = 0;
        empty.ShowInTaskbar = false;
        await empty.Ready;
        Require(empty.CanReceiveRecording, "New window is empty and ready");
        var second = await app.OpenRecordingAsync(secondSource);
        Require(second == empty, "External file open reuses an empty window");
        var duplicate = await app.OpenRecordingAsync(secondSource.ToUpperInvariant());
        Require(duplicate == second, "Duplicate file open activates its existing window");
        await second.OpenFileAsync(source);
        Require(((MainViewModel)second.DataContext).SourcePath == secondSource, "Open inside a window cannot create a second editor for an owned file");
        var secondVm = (MainViewModel)second.DataContext;
        secondVm.CommandStack.Execute(new AddSplitCommand(secondVm, 2_000_000, SplitSource.Manual));
        Require(((MainViewModel)original.DataContext).Markers.Count == 4, "Editing the second recording leaves the first timeline unchanged");
        await (Task)Invoke(second, "SavePreferencesAsync", secondVm)!;
        await (Task)Invoke(original, "SavePreferencesAsync", (MainViewModel)original.DataContext)!;
        var preferences = await UserPreferences.LoadAsync();
        Require(preferences.Positions.ContainsKey(source) && preferences.Positions.ContainsKey(secondSource), "Window saves merge playback positions instead of overwriting them");
        second.Close();
        for (int i = 0; i < 100 && second.IsVisible; i++) await Task.Delay(20);
        Require(!second.IsVisible && original.IsVisible, "Closing a recording window leaves other windows alive");
        Require(app.FindRecordingWindow(secondSource) is null, "Closed recordings release window ownership");
        for (int cycle = 0; cycle < 5; cycle++)
        {
            var reopened = await app.OpenRecordingAsync(secondSource);
            reopened.Opacity = 0;
            reopened.ShowInTaskbar = false;
            reopened.Close();
            for (int i = 0; i < 100 && reopened.IsVisible; i++) await Task.Delay(20);
            Require(!reopened.IsVisible && original.IsVisible, "Repeated recording-window close releases playback resources: " + cycle);
        }

    }

    private static void CheckNearbyReviewButtons()
    {
        var vm = new MainViewModel { DurationUs = 120_000_000 };
        vm.Segments.Add(new Segment { StartUs = 0, EndUs = 10_000_000, Label = "Opening" });
        vm.Segments.Add(new Segment { StartUs = 10_000_000, EndUs = 20_000_000, State = SegmentState.Excluded });
        vm.Segments.Add(new Segment { StartUs = 20_000_000, EndUs = 20_500_000, Label = "Short intro" });
        vm.Segments.Add(new Segment { StartUs = 20_500_000, EndUs = 120_000_000, Label = "Episode" });
        vm.RefreshKeptTimeline();
        vm.IsCollapsed = true;
        var timeline = new TimelineView { DataContext = vm, Width = 1000, Height = 190 };
        timeline.Measure(new Size(1000, 190));
        timeline.Arrange(new Rect(0, 0, 1000, 190));
        timeline.UpdateLayout();
        var buttons = ((Canvas)timeline.FindName("MarkerLane")).Children.OfType<Button>().ToArray();
        Require(buttons.Length == 2, "Adjacent kept scenes each retain a review button despite merged playback spans");
        Require(Math.Abs(Canvas.GetLeft(buttons[1]) - Canvas.GetLeft(buttons[0])) < buttons[0].Width,
            "Nearby review buttons may overlap without being hidden");
        long target = -1;
        timeline.ReviewJoinRequested += (_, position) => target = position;
        var highlight = (Canvas)timeline.FindName("ReviewHighlightCanvas");
        foreach (var button in buttons)
        {
            button.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = UIElement.MouseEnterEvent });
            Require(button.Effect is not null && highlight.Children.Count == 1,
                "Hover highlights the review button and its own boundary");
            var line = (System.Windows.Shapes.Line)highlight.Children[0];
            Require(Math.Abs(line.X1 - Canvas.GetLeft(button) - button.Width / 2) < 0.01,
                "Review highlight aligns with the hovered split");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(target == (long)button.Tag, "Overlapping review buttons keep distinct playback targets");
            button.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0)
                { RoutedEvent = UIElement.MouseLeaveEvent });
            Require(button.Effect is null && highlight.Children.Count == 0, "Hover glow clears on leaving the button");
        }
        timeline.DataContext = null;
        vm.Segments.Clear();
        vm.Segments.Add(new Segment { StartUs = 0, EndUs = 10_000_000, State = SegmentState.Excluded });
        vm.Segments.Add(new Segment { StartUs = 10_000_000, EndUs = 110_000_000 });
        vm.Segments.Add(new Segment { StartUs = 110_000_000, EndUs = 120_000_000, State = SegmentState.Excluded });
        vm.RefreshKeptTimeline();
        timeline.DataContext = vm;
        timeline.UpdateLayout();
        buttons = ((Canvas)timeline.FindName("MarkerLane")).Children.OfType<Button>().ToArray();
        Require(buttons.Length == 2 && (long)buttons[0].Tag == 0 && (long)buttons[1].Tag == 100_000_000,
            "Trimming both ends provides opening and ending review targets");
        Require(buttons.All(b => Canvas.GetLeft(b) >= 0 && Canvas.GetLeft(b) + b.Width <= 1000),
            "Endpoint review buttons stay fully inside the timeline");
        buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(target == vm.KeptTimeline.DurationUs, "Ending review targets the end of kept footage");
        timeline.DataContext = null;
    }

    private static void CheckSegmentToolbar(MainWindow window, MainViewModel vm, string directory)
    {
        var toolbar = (FrameworkElement)window.FindName("TimelineToolbar");
        var refine = (Button)window.FindName("RefineAllButton");
        var exclude = (Button)window.FindName("ToggleExcludedButton");
        var summary = vm.RefineSummary;
        foreach (var size in new[] { new Size(1920, 1080), new Size(1280, 640) })
        foreach (var segment in vm.Segments.Take(2))
        foreach (var longName in new[] { false, true })
        {
            var label = segment.Label;
            if (longName) segment.Label = new string('W', 100);
            vm.RefineSummary = longName ? "12 high / 8 medium / 3 low / 2 unchanged" : null;
            vm.SelectSegment(segment);
            window.Width = size.Width;
            window.Height = size.Height;
            window.UpdateLayout();
            var refineBounds = refine.TransformToAncestor(toolbar).TransformBounds(new Rect(refine.RenderSize));
            var excludeBounds = exclude.TransformToAncestor(toolbar).TransformBounds(new Rect(exclude.RenderSize));
            var available = new Rect(toolbar.RenderSize);
            Require(refine.IsVisible && exclude.IsVisible && !refineBounds.IntersectsWith(excludeBounds)
                && available.Contains(refineBounds) && available.Contains(excludeBounds),
                $"Segment actions remain separate and inside the toolbar at {size}, long name: {longName}");
            if (size.Width == 1280 && !longName)
                Capture(window, Path.Combine(directory, "segment-selected.png"));
            segment.Label = label;
        }
        vm.RefineSummary = summary;
        vm.ClearSelection();
        window.Width = 1280;
        window.Height = 720;
        window.UpdateLayout();
    }

    private static async Task CheckIrregularFrames(MainWindow window, string directory)
    {
        var runner = new FfmpegRunner();
        var generated = Path.Combine(directory, "irregular.mp4");
        var result = await runner.RunFfmpegAsync(new[] { "-v", "error", "-f", "lavfi", "-i",
            "testsrc2=size=640x360:rate=60:duration=12", "-vf",
            "select='not(between(t,4,5))*if(lt(t,7),not(mod(n,2)),1)'", "-fps_mode", "vfr", "-c:v", "libx264", generated });
        Require(result.Success, "Generate variable-rate footage with a one-second missing-frame gap");
        var samples = new List<string> { generated };
        if (Environment.GetEnvironmentVariable("ADTRIM_TIMING_FIXTURE") is { Length: > 0 } sample)
            samples.Add(sample); // Only a disposable local copy may be supplied.
        foreach (var path in samples)
        {
            var hash = SHA256.HashData(File.ReadAllBytes(path));
            var probe = await runner.RunFfprobeAsync(new[] { "-v", "error", "-select_streams", "v:0",
                "-read_intervals", "0%+12", "-show_frames", "-show_entries", "frame=best_effort_timestamp_time", "-of", "json", path });
            using var json = System.Text.Json.JsonDocument.Parse(probe.Stdout);
            var frames = json.RootElement.GetProperty("frames").EnumerateArray()
                .Where(f => f.TryGetProperty("best_effort_timestamp_time", out _))
                .Select(f => (long)Math.Round(double.Parse(f.GetProperty("best_effort_timestamp_time").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture) * 1_000_000)).Distinct().Order().ToArray();
            await window.OpenFileAsync(path);
            var vm = (MainViewModel)window.DataContext;
            var before = (MpvPreviewViewModel)typeof(MainWindow).GetField("_previewBefore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var after = (MpvPreviewViewModel)typeof(MainWindow).GetField("_previewAfter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            foreach (long target in new long[] { 2_100_000, 4_500_000, 5_050_000, 6_800_000, 7_200_000, 9_000_000 })
            {
                Invoke(window, "SeekTo", vm, target, true);
                await Task.Delay(500);
                for (int i = 0; i < 200 && (before.IsSeeking || after.IsSeeking || before.IsLoading || after.IsLoading); i++) await Task.Delay(20);
                var index = Array.FindIndex(frames, f => Math.Abs(f - after.PositionUs) <= 2);
                Require(index > 0 && !before.IsSeeking && !after.IsSeeking && Math.Abs(frames[index - 1] - before.PositionUs) <= 2,
                    $"Actual predecessor at {target}: {before.PositionUs} -> {after.PositionUs} ({Path.GetFileName(path)})");
            }
            Require(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Timing fixture unchanged");
        }
    }

    private static async Task CheckFramePair(MainWindow window, MainViewModel vm)
    {
        var before = (MpvPreviewViewModel)typeof(MainWindow).GetField("_previewBefore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var after = (MpvPreviewViewModel)typeof(MainWindow).GetField("_previewAfter", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var rate = vm.FrameRate;
        vm.FrameRate = new(60, 1); // The fixture decodes at 30 fps, despite this advertised rate.
        async Task Check(string action)
        {
            await Task.Delay(300);
            for (int i = 0; i < 150 && (before.IsSeeking || after.IsSeeking || before.IsLoading || after.IsLoading); i++)
                await Task.Delay(20);
            var delta = after.PositionUs - before.PositionUs;
            Require(!before.IsSeeking && !after.IsSeeking && Math.Abs(delta - 33_333) <= 2,
                $"{action}: neighboring decoded frames despite rate mismatch ({before.PositionUs}, {after.PositionUs})");
            var position = before.PositionUs;
            await Task.Delay(350);
            Require(before.PositionUs == position, "Paused frame pair stays stable");
        }
        try
        {
            foreach (var target in new long[] { 2_000_000, 5_016_667, 7_000_000 })
            {
                Invoke(window, "SeekTo", vm, target, true);
                await Check("Seek");
            }
            foreach (var direction in new[] { 1, -1, 1, 1, -1 })
            {
                Invoke(window, "StepFrame", vm, direction);
                await Check("Frame step");
            }
            for (int i = 0; i < 8; i++) Invoke(window, "SeekTo", vm, 2_000_000L + i * 100_000, true);
            await Check("Rapid seeks");
            Invoke(window, "PlayPauseBoth");
            await Task.Delay(600);
            Invoke(window, "PlayPauseBoth");
            await Check("Pause after playback");
        }
        finally { vm.FrameRate = rate; }
    }

    private static async Task<string> PrepareFixture()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "smoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "preview.mp4");
        var subtitles = Path.Combine(directory, "preview.srt");
        await File.WriteAllTextAsync(subtitles, "1\n00:00:00,000 --> 00:00:12,000\nThis subtitle must stay hidden.\n");
        var runner = new FfmpegRunner();
        var result = await runner.RunFfmpegAsync(new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            "testsrc2=size=640x360:rate=30:duration=12", "-f", "lavfi", "-i", "sine=frequency=440:duration=12",
            "-i", subtitles, "-map", "0:v", "-map", "1:a", "-map", "2:s", "-c:v", "mpeg2video",
            "-c:a", "ac3", "-c:s", "mov_text", "-disposition:s:0", "default", "-t", "12", path });
        if (!result.Success) throw new InvalidOperationException(result.Stderr);
        var media = await new MediaProbeService(runner).ProbeAsync(path);
        new ProjectStore(Path.Combine(directory, "fallback")).Save(new AdTrimProject(AdTrimProject.CurrentSchemaVersion,
            path, ProjectStore.FingerprintOf(path, media.DurationUs), media,
            new() { new("first", 4_000_000, SplitSource.Chapter, null, null, false, "Commercial 1"),
                new("second", 8_000_000, SplitSource.Chapter, null, null, false, "Part 2") },
            new() { "segment-4000000-8000000" }, SidecarLocation.NextToSource, "Part 1"));
        Console.WriteLine("Disposable fixture: " + path);
        return path;
    }
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr mpv_get_property_string(IntPtr ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("libmpv-2.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void mpv_free(IntPtr data);

    private static void RequireSubtitlesDisabled(MainWindow window)
    {
        foreach (var field in new[] { "_previewBefore", "_previewAfter" })
        {
            var player = typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var ctx = (IntPtr)typeof(MpvPreviewViewModel).GetField("_ctx", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
            foreach (var name in new[] { "sid", "secondary-sid", "sub-auto", "sub-visibility", "secondary-sub-visibility" })
            {
                var value = mpv_get_property_string(ctx, name);
                try { Require(value != IntPtr.Zero && Marshal.PtrToStringUTF8(value) == "no", field + " disables " + name); }
                finally { if (value != IntPtr.Zero) mpv_free(value); }
            }
        }
    }
    private static void Capture(System.Windows.Controls.ContentControl window, string path)
    {
        var content = (FrameworkElement)window.Content;
        content.UpdateLayout();
        var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static object? Invoke(object instance, string name, params object[] args)
        => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); Console.WriteLine("PASS: " + message); }
}

internal sealed class SmokeUpdateHandler(byte[] feed, byte[] signature) : System.Net.Http.HttpMessageHandler
{
    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken ct)
        => Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new System.Net.Http.ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sig") ? signature : feed) });
}

internal sealed class ImmediateExportProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
{
    public void Report(ExportProgress value) => report(value);
}
