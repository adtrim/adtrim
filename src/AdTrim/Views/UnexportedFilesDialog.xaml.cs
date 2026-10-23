using System.Collections;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AdTrim.Services;

namespace AdTrim.Views;

public partial class UnexportedFilesDialog : Window
{
    public sealed record FileRow(string FullPath, string RelativePath, string Detail, bool CanOpen)
    {
        public string FileName => Path.GetFileName(FullPath);
    }
    private readonly ObservableCollection<FileRow> _files = new();
    private CancellationTokenSource _scan = new();
    private readonly Dictionary<string, TreeViewItem> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ListCollectionView _view;
    private readonly string _root;
    private string _selectedFolder;
    private bool _scanComplete;
    private bool _refreshing;
    private bool _closed;
    private Task? _scanTask;
    public string? SelectedPath { get; private set; }

    public UnexportedFilesDialog(string folder)
    {
        InitializeComponent();
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        _selectedFolder = _root;
        FolderLabel.Text = _root;
        FilesList.ItemsSource = _files;
        _view = (ListCollectionView)CollectionViewSource.GetDefaultView(_files);
        _view.CustomSort = new FolderOrder();
        _view.Filter = row => IsWithinFolder(((FileRow)row).FullPath, _selectedFolder);
        EnsureFolder(_root).IsSelected = true;
        Loaded += (_, _) =>
        {
            var area = MonitorBounds.WorkArea(this);
            MinWidth = Math.Min(MinWidth, area.Width);
            MinHeight = Math.Min(MinHeight, area.Height);
            MaxWidth = area.Width;
            MaxHeight = area.Height;
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
            Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
            _scanTask = ScanAsync(_root, _scan.Token);
        };
        Closing += (_, _) => _scan.Cancel();
        Closed += async (_, _) => { _closed = true; if (_scanTask is not null) await _scanTask; _scan.Dispose(); };
    }

    private static bool IsWithinFolder(string path, string folder)
    {
        string prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return path.Equals(folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_refreshing || !_scanComplete) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        FolderTree.IsEnabled = false;
        string folder = _selectedFolder;
        try
        {
            _scan.Cancel();
            if (_scanTask is not null) await _scanTask;
            if (_closed) return;
            _scan.Dispose();
            _scan = new();
            var expanded = _folders.Where(p => p.Value.IsExpanded).Select(p => p.Key).ToArray();
            foreach (var row in _files.Where(r => IsWithinFolder(r.FullPath, folder)).ToArray()) _files.Remove(row);
            _folders[folder].Items.Clear();
            foreach (var path in _folders.Keys.Where(p => p != folder && IsWithinFolder(p, folder)).ToArray()) _folders.Remove(path);
            _scanComplete = false;
            StatusLabel.Text = "Scanning...";
            UpdateEmptyState();
            _scanTask = ScanAsync(folder, _scan.Token);
            await _scanTask;
            foreach (string path in expanded)
                if (_folders.TryGetValue(path, out var node)) node.IsExpanded = true;
        }
        catch (Exception ex) { StatusLabel.Text = "Refresh stopped: " + ex.Message; }
        finally
        {
            _refreshing = false;
            if (!_closed) { RefreshButton.IsEnabled = true; FolderTree.IsEnabled = true; }
        }
    }

    private TreeViewItem EnsureFolder(string path)
    {
        if (_folders.TryGetValue(path, out var existing)) return existing;
        var node = new TreeViewItem { Header = Path.GetFileName(path) is { Length: > 0 } name ? name : path,
            Tag = path, IsExpanded = path == _root, Foreground = Foreground, Padding = new Thickness(4),
            Visibility = path == _root ? Visibility.Visible : Visibility.Collapsed };
        _folders.Add(path, node);
        var siblings = path == _root ? FolderTree.Items : EnsureFolder(Path.GetDirectoryName(path)!).Items;
        int at = 0;
        while (at < siblings.Count && FolderOrder.CompareNames((string)((TreeViewItem)siblings[at]).Header, (string)node.Header) < 0) at++;
        siblings.Insert(at, node);
        return node;
    }

    private void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: string path }) return;
        _selectedFolder = path;
        if (_view is not null) ApplyView();
    }

    private void ApplyView()
    {
        SelectionLabel.Text = _selectedFolder == _root ? Path.GetFileName(_root) : Path.GetRelativePath(_root, _selectedFolder);
        SelectionLabel.ToolTip = _selectedFolder;
        _view.Refresh();
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        EmptyLabel.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        EmptyLabel.Text = !_scanComplete ? "Scanning..." : "No unexported files in this folder or its subfolders.";
    }

    private void PruneEmptyFolders()
    {
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _root };
        foreach (var row in _files)
        {
            string? folder = _folders.ContainsKey(row.FullPath) ? row.FullPath : Path.GetDirectoryName(row.FullPath);
            while (folder is not null && IsWithinFolder(folder, _root) && keep.Add(folder))
                folder = Path.GetDirectoryName(folder);
        }
        if (!keep.Contains(_selectedFolder))
        {
            string folder = _selectedFolder;
            do { folder = Path.GetDirectoryName(folder)!; } while (!keep.Contains(folder));
            _folders[folder].IsSelected = true;
        }
        foreach (var path in _folders.Keys.Where(path => !keep.Contains(path)).ToArray())
        {
            var node = _folders[path];
            if (node.Parent is ItemsControl parent) parent.Items.Remove(node);
            _folders.Remove(path);
        }
    }

    private sealed class FolderOrder : IComparer
    {
        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int StrCmpLogicalW(string left, string right);
        public static int CompareNames(string left, string right) => StrCmpLogicalW(left, right);

        public int Compare(object? x, object? y)
        {
            var left = ((FileRow)x!).RelativePath.Split(Path.DirectorySeparatorChar);
            var right = ((FileRow)y!).RelativePath.Split(Path.DirectorySeparatorChar);
            for (int i = 0; i < Math.Min(left.Length, right.Length); i++)
            {
                int order = StrCmpLogicalW(left[i], right[i]);
                if (order != 0) return order;
            }
            return left.Length.CompareTo(right.Length);
        }
    }

    private async Task ScanAsync(string folder, CancellationToken ct)
    {
        int exported = 0, unknown = 0, unexported = 0;
        try
        {
            await foreach (var file in UnexportedFiles.ScanAsync(folder, ct, includeFolders: true))
            {
                if (file.IsFolder) { EnsureFolder(file.Path); continue; }
                if (file.Exported) exported++;
                else
                {
                    if (file.Error is null) unexported++; else unknown++;
                    bool supported = AdTrim.Models.ExportFormats.IsSupportedSource(file.Path);
                    _files.Add(new(file.Path, Path.GetRelativePath(_root, file.Path),
                        file.Error is not null ? "Could not check: " + file.Error : supported ? "" : "Unsupported recording format.",
                        file.Error is null && supported));
                    string parent = _folders.ContainsKey(file.Path) ? file.Path : Path.GetDirectoryName(file.Path)!;
                    for (TreeViewItem? node = EnsureFolder(parent); node is not null; node = node.Parent as TreeViewItem)
                        node.Visibility = Visibility.Visible;
                    UpdateEmptyState();
                }
                StatusLabel.Text = $"Scanning... {unexported} unexported, {exported} exported, {unknown} could not be checked.";
            }
            PruneEmptyFolders();
            StatusLabel.Text = $"Scan complete{(folder == _root ? "" : " for selected folder")}. {unexported} unexported, {exported} exported, {unknown} could not be checked.";
            _scanComplete = true;
            UpdateEmptyState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _scanComplete = true;
            StatusLabel.Text = "Scan stopped: " + ex.Message;
            UpdateEmptyState();
            EmptyLabel.Text = "This folder could not be fully checked. Try Refresh.";
        }
        finally { if (!_closed && !_refreshing) RefreshButton.IsEnabled = true; }
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is FileRow row) OpenRow(row);
    }

    private void OnFileDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || (sender as ListBoxItem)?.DataContext is not FileRow row) return;
        OpenRow(row);
        e.Handled = true;
    }

    private void OpenRow(FileRow row)
    {
        if (!row.CanOpen) return;
        if (!File.Exists(row.FullPath)) { StatusLabel.Text = "That file is no longer available. Select another file."; return; }
        SelectedPath = row.FullPath;
        DialogResult = true;
    }
}
