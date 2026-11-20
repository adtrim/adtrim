using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AdTrim.Encoders;
using AdTrim.Models;
using AdTrim.Services;
using AdTrim.ViewModels;

namespace AdTrim.Views;

/// <summary>
/// Every lasting choice in one place. The window edits a draft; Save writes only the settings
/// that were changed here, on top of the latest file, and shares them with every recording
/// window. Cancel, Esc and the close button discard the draft.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Func<Func<UserPreferences, UserPreferences>, Task<UserPreferences?>> _change;
    private readonly Func<Task<(UserPreferences Saved, string? Kept)?>> _resetAll;
    private readonly UserPreferences _opened;
    private bool _loading;
    private bool _restoreDefaults;

    /// <param name="current">The settings as the opening window knows them.</param>
    /// <param name="change">Saves a change and shares it with every window; null when it could not be saved.</param>
    /// <param name="resetAll">Resets every setting, keeping a copy of the old file; null when it could not be done.</param>
    public SettingsWindow(UserPreferences current, Func<Func<UserPreferences, UserPreferences>, Task<UserPreferences?>> change,
        Func<Task<(UserPreferences Saved, string? Kept)?>> resetAll)
    {
        InitializeComponent();
        _opened = current;
        _change = change;
        _resetAll = resetAll;
        Show(current, Application.Current is App app && app.AutomaticUpdatesEnabled);
        Loaded += async (_, _) =>
        {
            Navigation.Children.OfType<RadioButton>().First().Focus();
            await LoadAccelerationOptionsAsync();
            ShowUpdateState();
            await ShowCacheSizeAsync();
        };
    }

    /// <summary>True once Save has written the draft.</summary>
    public bool Saved { get; private set; }

    private void Show(UserPreferences p, bool automaticUpdates)
    {
        _loading = true;
        try
        {
            StartImported.IsChecked = p.NewFileMarkerSource == NewFileMarkerSource.Imported;
            StartAdTrim.IsChecked = p.NewFileMarkerSource == NewFileMarkerSource.AdTrim;
            StartNone.IsChecked = p.NewFileMarkerSource == NewFileMarkerSource.None;
            PartNameBox.Text = p.SectionNameTemplate;
            ShowPartNamePreview();
            ThumbnailsSwitch.IsChecked = p.ShowThumbnails;
            WaveformSwitch.IsChecked = p.ShowWaveform;
            RulerSwitch.IsChecked = p.ShowRulerTicks;
            StartMutedSwitch.IsChecked = p.StartMuted;
            FolderBeside.IsChecked = p.ExportFolder is null;
            FolderFixed.IsChecked = p.ExportFolder is not null;
            ExportFolderBox.Text = p.ExportFolder ?? "";
            ShowFolderState();
            FilenameBox.Text = p.ExportFilenameTemplate;
            ShowFilenamePreview();
            FormatMp4.IsChecked = p.ExportFormat == ExportFormat.Mp4;
            FormatTs.IsChecked = p.ExportFormat == ExportFormat.Ts;
            SourceCommentSwitch.IsChecked = p.AddSourceExportComment;
            DeleteSidecarSwitch.IsChecked = p.DeleteSidecarAfterExport;
            AutomaticUpdatesSwitch.IsChecked = automaticUpdates;
            SelectAcceleration(p.ExportAcceleration);
        }
        finally { _loading = false; }
    }

    /// <summary>The draft: <paramref name="basis"/> with every setting this window shows.</summary>
    internal UserPreferences Draft(UserPreferences basis) => basis with
    {
        NewFileMarkerSource = StartAdTrim.IsChecked == true ? NewFileMarkerSource.AdTrim
            : StartNone.IsChecked == true ? NewFileMarkerSource.None : NewFileMarkerSource.Imported,
        SectionNameTemplate = PartNameBox.Text.Trim(),
        ShowThumbnails = ThumbnailsSwitch.IsChecked == true,
        ShowWaveform = WaveformSwitch.IsChecked == true,
        ShowRulerTicks = RulerSwitch.IsChecked == true,
        StartMuted = StartMutedSwitch.IsChecked == true,
        ExportFolder = FolderFixed.IsChecked == true ? ExportFolderBox.Text.Trim() : null,
        ExportFilenameTemplate = FilenameBox.Text.Trim(),
        ExportFormat = FormatTs.IsChecked == true ? ExportFormat.Ts : ExportFormat.Mp4,
        ExportAcceleration = (AccelerationSelector.SelectedItem as ExportAccelerationOption)?.Id ?? basis.ExportAcceleration,
        AddSourceExportComment = SourceCommentSwitch.IsChecked == true,
        DeleteSidecarAfterExport = DeleteSidecarSwitch.IsChecked == true,
    };

    /// <summary>
    /// Applies only what this window changed (<paramref name="edited"/> against <paramref name="opened"/>) to the
    /// latest saved settings, so a setting another window saved meanwhile is not undone.
    /// </summary>
    internal static UserPreferences Merge(UserPreferences latest, UserPreferences opened, UserPreferences edited)
    {
        static T Pick<T>(T edited, T opened, T latest) => EqualityComparer<T>.Default.Equals(edited, opened) ? latest : edited;
        return latest with
        {
            NewFileMarkerSource = Pick(edited.NewFileMarkerSource, opened.NewFileMarkerSource, latest.NewFileMarkerSource),
            SectionNameTemplate = Pick(edited.SectionNameTemplate, opened.SectionNameTemplate, latest.SectionNameTemplate),
            ShowThumbnails = Pick(edited.ShowThumbnails, opened.ShowThumbnails, latest.ShowThumbnails),
            ShowWaveform = Pick(edited.ShowWaveform, opened.ShowWaveform, latest.ShowWaveform),
            ShowRulerTicks = Pick(edited.ShowRulerTicks, opened.ShowRulerTicks, latest.ShowRulerTicks),
            StartMuted = Pick(edited.StartMuted, opened.StartMuted, latest.StartMuted),
            ExportFolder = Pick(edited.ExportFolder, opened.ExportFolder, latest.ExportFolder),
            ExportFilenameTemplate = Pick(edited.ExportFilenameTemplate, opened.ExportFilenameTemplate, latest.ExportFilenameTemplate),
            ExportFormat = Pick(edited.ExportFormat, opened.ExportFormat, latest.ExportFormat),
            ExportAcceleration = Pick(edited.ExportAcceleration, opened.ExportAcceleration, latest.ExportAcceleration),
            AddSourceExportComment = Pick(edited.AddSourceExportComment, opened.AddSourceExportComment, latest.AddSourceExportComment),
            DeleteSidecarAfterExport = Pick(edited.DeleteSidecarAfterExport, opened.DeleteSidecarAfterExport, latest.DeleteSidecarAfterExport),
        };
    }

    // ---- Navigation ----
    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string pane } || EditingPane is null) return;
        foreach (var panel in new FrameworkElement[] { EditingPane, TimelinePane, ExportPane, UpdatesPane, StoragePane })
            panel.Visibility = panel.Name == pane ? Visibility.Visible : Visibility.Collapsed;
        PaneScroll.ScrollToTop();
    }

    private void ShowPane(RadioButton navigation, Control focus)
    {
        navigation.IsChecked = true;
        UpdateLayout();
        focus.Focus();
    }

    // ---- Editing ----
    private void OnStartingMarkers(object sender, RoutedEventArgs e) { }

    private void OnPartNameChanged(object sender, TextChangedEventArgs e)
    {
        if (PartNamePreview is not null) ShowPartNamePreview();
    }

    private void ShowPartNamePreview()
    {
        bool valid = SectionNames.IsValid(PartNameBox.Text);
        PartNamePreview.Text = valid ? string.Join(", ", Enumerable.Range(1, 3).Select(n => SectionNames.Format(PartNameBox.Text, n))) : "";
        PartNameError.Text = "Include {n} once, for example: Chapter {n}.";
        PartNameError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPartNameDefault(object sender, RoutedEventArgs e) => PartNameBox.Text = SectionNames.DefaultTemplate;

    // ---- Timeline and playback ----
    private void OnTimelineSwitch(object sender, RoutedEventArgs e) { }

    // ---- Export ----
    private void OnFolderChoice(object sender, RoutedEventArgs e)
    {
        if (ExportFolderBox is null) return;
        ShowFolderState();
        if (!_loading && FolderFixed.IsChecked == true && ExportFolderBox.Text.Trim().Length == 0) OnBrowseFolder(sender, e);
    }

    private void OnExportFolderEdited(object sender, RoutedEventArgs e) => ShowFolderState();

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save exports to" };
        if (Directory.Exists(ExportFolderBox.Text.Trim())) dialog.InitialDirectory = ExportFolderBox.Text.Trim();
        if (dialog.ShowDialog(this) != true)
        {
            // Choosing "This folder" without picking one goes back to the recording's folder.
            if (ExportFolderBox.Text.Trim().Length == 0) { _loading = true; FolderBeside.IsChecked = true; _loading = false; ShowFolderState(); }
            return;
        }
        ExportFolderBox.Text = dialog.FolderName;
        _loading = true; FolderFixed.IsChecked = true; _loading = false;
        ShowFolderState();
    }

    /// <summary>A newly typed folder must exist. The folder already saved may be unplugged; export setup reports that.</summary>
    private string? FolderError()
    {
        if (FolderFixed.IsChecked != true) return null;
        string folder = ExportFolderBox.Text.Trim();
        if (folder.Length == 0) return "Choose a folder, or save exports beside the recording.";
        if (Directory.Exists(folder)) return null;
        return string.Equals(folder, _opened.ExportFolder, StringComparison.OrdinalIgnoreCase)
            ? null : "This folder does not exist. Choose one that does.";
    }

    private void ShowFolderState()
    {
        bool fixedFolder = FolderFixed.IsChecked == true;
        ExportFolderBox.IsEnabled = BrowseFolderButton.IsEnabled = fixedFolder;
        string folder = ExportFolderBox.Text.Trim();
        string? error = FolderError();
        if (error is null && fixedFolder && folder.Length > 0 && !Directory.Exists(folder))
            error = "This folder is not available right now. Exports save beside the recording until it is.";
        ExportFolderError.Text = error ?? "";
        ExportFolderError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnFilenameChanged(object sender, TextChangedEventArgs e)
    {
        if (FilenamePreview is not null) ShowFilenamePreview();
    }

    private void ShowFilenamePreview()
    {
        var error = ExportNaming.TemplateError(FilenameBox.Text);
        FilenamePreview.Text = error is null
            ? ExportNaming.ApplyTemplate(FilenameBox.Text, "Sample Show - s02e05 - The Long Weekend", DateOnly.FromDateTime(DateTime.Now)) + ".mp4"
            : "";
        FilenameError.Text = error ?? "";
        FilenameError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnFilenameDefault(object sender, RoutedEventArgs e) => FilenameBox.Text = ExportNaming.DefaultTemplate;

    private void OnFormatChoice(object sender, RoutedEventArgs e) { }

    // Lists adapters without testing them, as export setup does: a benchmark belongs to Evaluate options.
    private async Task LoadAccelerationOptionsAsync()
    {
        IReadOnlyList<HardwareAdapter> adapters;
        try { adapters = await Task.Run(GraphicsAdapters.Enumerate); }
        catch (System.Runtime.InteropServices.COMException) { adapters = Array.Empty<HardwareAdapter>(); }
        var options = new List<ExportAccelerationOption> { ExportAccelerationOption.Automatic };
        options.AddRange(adapters.Where(a => a.Encoder is not null).Select(a => new ExportAccelerationOption(a.Id, a.Name, a)));
        options.Add(ExportAccelerationOption.Software);
        _loading = true;
        try { AccelerationSelector.ItemsSource = options; SelectAcceleration(_opened.ExportAcceleration); }
        finally { _loading = false; }
    }

    private void SelectAcceleration(string id)
    {
        if (AccelerationSelector.ItemsSource is not List<ExportAccelerationOption> options) return;
        var option = options.FirstOrDefault(o => o.Id == id);
        if (option is null)
        {
            option = new ExportAccelerationOption(id, "Previously selected graphics card (unavailable)");
            options.Add(option);
            AccelerationSelector.Items.Refresh();
        }
        AccelerationSelector.SelectedItem = option;
    }

    private void OnAccelerationChoice(object sender, SelectionChangedEventArgs e) { }

    private void OnExportSwitch(object sender, RoutedEventArgs e) { }

    // ---- Updates ----
    private void OnAutomaticUpdates(object sender, RoutedEventArgs e) { }

    private async void OnCheckNow(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app) return;
        CheckNowButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking…";
        try { await app.Updates.CheckAsync(); }
        finally { CheckNowButton.IsEnabled = true; ShowUpdateState(); }
    }

    private void ShowUpdateState()
    {
        InstalledVersionText.Text = "AdTrim v" + AppVersion.Numeric;
        if (Application.Current is not App app) { UpdateStatusText.Text = ""; return; }
        var updates = app.Updates;
        UpdateStatusText.Text = updates.IsChecking ? "Checking…"
            : updates.Error is { } error ? "The last check did not finish: " + error
            : updates.Notice is { } notice ? notice.Label + ". Use Help > " + notice.Label + "… to install it."
            : "No update was found.";
    }

    // ---- Storage ----
    private static readonly string[] CacheRoots =
        [ThumbnailService.DefaultCacheRoot, AudioLevelService.DefaultCacheRoot, MediaCacheCleanup.LegacyWaveformRoot];

    private async Task ShowCacheSizeAsync()
    {
        CacheSizeText.Text = "Measuring…";
        long bytes = await Task.Run(() => CacheRoots.Where(Directory.Exists).Sum(root =>
        {
            try { return new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0L; }
        }));
        CacheSizeText.Text = bytes < 1024 * 1024 ? $"{bytes / 1024.0:0} KB in use" : $"{bytes / (1024.0 * 1024):0} MB in use";
    }

    private async void OnClearCaches(object sender, RoutedEventArgs e)
    {
        ClearCachesButton.IsEnabled = false;
        try
        {
            // Everything AdTrim named as a cache, however recent. A recording that is open rebuilds what it needs.
            int removed = await Task.Run(() => MediaCacheCleanup.Run(ThumbnailService.DefaultCacheRoot, ThumbnailService.CurrentFolderSuffix,
                AudioLevelService.DefaultCacheRoot, AudioLevelService.CacheVersion, MediaCacheCleanup.LegacyWaveformRoot, DateTime.UtcNow.AddYears(100)));
            await ShowCacheSizeAsync();
            CacheSizeText.Text += removed == 1 ? " · 1 cache removed" : $" · {removed} caches removed";
        }
        finally { ClearCachesButton.IsEnabled = true; }
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(UserPreferences.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{UserPreferences.DataDirectory}\"") { UseShellExecute = false });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { MessageBox.Show(this, "The folder could not be opened. " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnResetAll(object sender, RoutedEventArgs e)
    {
        Show(_opened.WithSettingsFrom(new UserPreferences()), true);
        _restoreDefaults = true;
        FooterNote.Text = "Defaults are shown. Save to use them; a copy of the current settings file is kept.";
    }

    // ---- Window ----
    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (!SectionNames.IsValid(PartNameBox.Text)) { ShowPane(NavEditing, PartNameBox); return; }
        if (FolderError() is not null) { ShowFolderState(); ShowPane(NavExport, ExportFolderBox); return; }
        if (ExportNaming.TemplateError(FilenameBox.Text) is not null) { ShowPane(NavExport, FilenameBox); return; }
        SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        try
        {
            var opened = _opened;
            if (_restoreDefaults)
            {
                if (await _resetAll() is not { } reset) return;
                opened = reset.Saved;
            }
            var edited = Draft(opened);
            if (edited != opened && await _change(latest => Merge(latest, opened, edited)) is null) return;
            bool automaticUpdates = AutomaticUpdatesSwitch.IsChecked == true;
            if (Application.Current is App app && app.AutomaticUpdatesEnabled != automaticUpdates)
            {
                try { app.SetAutomaticUpdates(automaticUpdates); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    MessageBox.Show(this, "The update setting could not be saved. " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            Saved = true;
            Close();
        }
        finally { SaveButton.IsEnabled = CancelButton.IsEnabled = true; }
    }
}
