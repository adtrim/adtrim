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
/// Every lasting choice in one place. Each change is saved at once through
/// <see cref="ChangeAsync"/>, which also updates every open recording window, so the
/// window has nothing to apply or cancel.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Func<Func<UserPreferences, UserPreferences>, Task<UserPreferences?>> _change;
    private readonly Func<Task<(UserPreferences Saved, string? Kept)?>> _resetAll;
    private UserPreferences _current;
    private bool _loading;

    /// <param name="current">The settings as the opening window knows them.</param>
    /// <param name="change">Saves one change and shares it with every window; null when it could not be saved.</param>
    /// <param name="resetAll">Resets every setting; null when it could not be done.</param>
    public SettingsWindow(UserPreferences current, Func<Func<UserPreferences, UserPreferences>, Task<UserPreferences?>> change,
        Func<Task<(UserPreferences Saved, string? Kept)?>> resetAll)
    {
        InitializeComponent();
        _current = current;
        _change = change;
        _resetAll = resetAll;
        Show(current);
        Loaded += async (_, _) =>
        {
            Navigation.Children.OfType<RadioButton>().First().Focus();
            await LoadAccelerationOptionsAsync();
            ShowUpdateState();
            await ShowCacheSizeAsync();
        };
    }

    /// <summary>The settings the window currently shows, for checks.</summary>
    internal UserPreferences Current => _current;

    private void Show(UserPreferences p)
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
            FrameAccurateSwitch.IsChecked = p.FrameAccurateSeek;
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
            AutomaticUpdatesSwitch.IsChecked = Application.Current is App app && app.AutomaticUpdatesEnabled;
            SelectAcceleration(p.ExportAcceleration);
        }
        finally { _loading = false; }
    }

    private async Task<bool> ChangeAsync(Func<UserPreferences, UserPreferences> change)
    {
        if (_loading) return false;
        var saved = await _change(change);
        if (saved is null) { Show(_current); return false; }
        _current = saved;
        return true;
    }

    // ---- Navigation ----
    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string pane } || EditingPane is null) return;
        foreach (var panel in new FrameworkElement[] { EditingPane, TimelinePane, ExportPane, UpdatesPane, StoragePane })
            panel.Visibility = panel.Name == pane ? Visibility.Visible : Visibility.Collapsed;
        PaneScroll.ScrollToTop();
    }

    // ---- Editing ----
    private async void OnStartingMarkers(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag } && Enum.TryParse<NewFileMarkerSource>(tag, out var source))
            await ChangeAsync(p => p with { NewFileMarkerSource = source });
    }

    private async void OnPartNameChanged(object sender, TextChangedEventArgs e)
    {
        if (PartNamePreview is null) return;
        ShowPartNamePreview();
        if (SectionNames.IsValid(PartNameBox.Text))
        {
            string template = PartNameBox.Text.Trim();
            await ChangeAsync(p => p with { SectionNameTemplate = template });
        }
    }

    private void ShowPartNamePreview()
    {
        bool valid = SectionNames.IsValid(PartNameBox.Text);
        PartNamePreview.Text = valid ? string.Join(", ", Enumerable.Range(1, 3).Select(n => SectionNames.Format(PartNameBox.Text, n))) : "";
        PartNameError.Text = "Include {n} once, for example: Chapter {n}. Until then, the last valid name is kept.";
        PartNameError.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnPartNameDefault(object sender, RoutedEventArgs e) => PartNameBox.Text = SectionNames.DefaultTemplate;

    // ---- Timeline and playback ----
    private async void OnTimelineSwitch(object sender, RoutedEventArgs e)
    {
        bool thumbnails = ThumbnailsSwitch.IsChecked == true, waveform = WaveformSwitch.IsChecked == true,
            ruler = RulerSwitch.IsChecked == true, accurate = FrameAccurateSwitch.IsChecked == true, muted = StartMutedSwitch.IsChecked == true;
        await ChangeAsync(p => p with
        {
            ShowThumbnails = thumbnails, ShowWaveform = waveform, ShowRulerTicks = ruler, FrameAccurateSeek = accurate, StartMuted = muted,
        });
    }

    // ---- Export ----
    private async void OnFolderChoice(object sender, RoutedEventArgs e)
    {
        if (ExportFolderBox is null) return;
        ShowFolderState();
        if (FolderBeside.IsChecked == true) await ChangeAsync(p => p with { ExportFolder = null });
        else if (Directory.Exists(ExportFolderBox.Text.Trim()))
        {
            string folder = ExportFolderBox.Text.Trim();
            await ChangeAsync(p => p with { ExportFolder = folder });
        }
        else if (!_loading) OnBrowseFolder(sender, e);
    }

    private async void OnExportFolderEdited(object sender, RoutedEventArgs e)
    {
        string folder = ExportFolderBox.Text.Trim();
        ShowFolderState();
        if (FolderFixed.IsChecked == true && folder != (_current.ExportFolder ?? "") && Directory.Exists(folder))
            await ChangeAsync(p => p with { ExportFolder = folder });
    }

    private async void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Save exports to" };
        if (Directory.Exists(ExportFolderBox.Text.Trim())) dialog.InitialDirectory = ExportFolderBox.Text.Trim();
        if (dialog.ShowDialog(this) != true)
        {
            // Choosing "This folder" without picking one leaves the setting as it was.
            if (_current.ExportFolder is null) { _loading = true; FolderBeside.IsChecked = true; _loading = false; ShowFolderState(); }
            return;
        }
        ExportFolderBox.Text = dialog.FolderName;
        _loading = true; FolderFixed.IsChecked = true; _loading = false;
        ShowFolderState();
        string folder = dialog.FolderName;
        await ChangeAsync(p => p with { ExportFolder = folder });
    }

    private void ShowFolderState()
    {
        bool fixedFolder = FolderFixed.IsChecked == true;
        ExportFolderBox.IsEnabled = BrowseFolderButton.IsEnabled = fixedFolder;
        string folder = ExportFolderBox.Text.Trim();
        bool missing = fixedFolder && folder.Length > 0 && !Directory.Exists(folder);
        ExportFolderError.Text = missing ? "This folder is not available right now. Exports save beside the recording until it is." : "";
        ExportFolderError.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnFilenameChanged(object sender, TextChangedEventArgs e)
    {
        if (FilenamePreview is null) return;
        ShowFilenamePreview();
        if (ExportNaming.TemplateError(FilenameBox.Text) is null)
        {
            string template = FilenameBox.Text.Trim();
            await ChangeAsync(p => p with { ExportFilenameTemplate = template });
        }
    }

    private void ShowFilenamePreview()
    {
        var error = ExportNaming.TemplateError(FilenameBox.Text);
        FilenamePreview.Text = error is null
            ? ExportNaming.ApplyTemplate(FilenameBox.Text, "Sample Show - s02e05 - The Long Weekend", DateOnly.FromDateTime(DateTime.Now)) + ".mp4"
            : "";
        FilenameError.Text = error is null ? "" : error + " Until then, the last valid pattern is kept.";
        FilenameError.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnFilenameDefault(object sender, RoutedEventArgs e) => FilenameBox.Text = ExportNaming.DefaultTemplate;

    private async void OnFormatChoice(object sender, RoutedEventArgs e)
    {
        if (FormatTs is null) return;
        var format = FormatTs.IsChecked == true ? ExportFormat.Ts : ExportFormat.Mp4;
        await ChangeAsync(p => p with { ExportFormat = format });
    }

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
        try { AccelerationSelector.ItemsSource = options; SelectAcceleration(_current.ExportAcceleration); }
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

    private async void OnAccelerationChoice(object sender, SelectionChangedEventArgs e)
    {
        if (AccelerationSelector.SelectedItem is ExportAccelerationOption option)
            await ChangeAsync(p => p with { ExportAcceleration = option.Id });
    }

    private async void OnExportSwitch(object sender, RoutedEventArgs e)
    {
        bool comment = SourceCommentSwitch.IsChecked == true, deleteSidecar = DeleteSidecarSwitch.IsChecked == true;
        await ChangeAsync(p => p with { AddSourceExportComment = comment, DeleteSidecarAfterExport = deleteSidecar });
    }

    // ---- Updates ----
    private void OnAutomaticUpdates(object sender, RoutedEventArgs e)
    {
        if (Application.Current is not App app) return;
        try { app.SetAutomaticUpdates(AutomaticUpdatesSwitch.IsChecked == true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AutomaticUpdatesSwitch.IsChecked = app.AutomaticUpdatesEnabled;
            MessageBox.Show(this, "The update setting could not be saved. " + ex.Message, "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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

    private async void OnResetAll(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Return every setting in this window to its default? A copy of the current settings file is kept.",
                "Reset all settings", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        var result = await _resetAll();
        if (result is not { } reset) return;
        _current = reset.Saved;
        if (Application.Current is App app)
        {
            try { app.SetAutomaticUpdates(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        Show(_current);
        FooterNote.Text = reset.Kept is null ? "Settings were reset." : $"Settings were reset. The old file was kept as {Path.GetFileName(reset.Kept)}.";
    }

    // ---- Window ----
    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }

    private void OnDone(object sender, RoutedEventArgs e) => Close();
}
