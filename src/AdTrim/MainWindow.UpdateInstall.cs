using System.Runtime.InteropServices;
using System.Windows.Interop;
using AdTrim.ViewModels;

namespace AdTrim;

public partial class MainWindow
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);

    /// <summary>
    /// A Windows message box or file dialog disables its owner window natively; WPF does not list it
    /// among OwnedWindows. Restarting underneath it would close the window inside the dialog's loop.
    /// </summary>
    public bool IsBlockedByDialog => new WindowInteropHelper(this).Handle is var handle && handle != IntPtr.Zero && !IsWindowEnabled(handle);

    public bool CanRestartForUpdate => _ready.Task.IsCompletedSuccessfully && !_switching && !_hydrating && !_allowClose
        && _openingPath is null && _activeExportDialog is null
        && OwnedWindows.Cast<System.Windows.Window>().All(w => w is Views.UpdateDialog)
        && DataContext is MainViewModel { IsBusy: false };

    public async Task<bool> SaveForUpdateAsync()
    {
        if (!CanRestartForUpdate || DataContext is not MainViewModel vm) return false;
        _playback.ResetShuttle();
        _playback.After.Pause();
        _playback.Before.Pause();
        _saves.StopAutosave();
        _switching = true;
        vm.IsBusy = true;
        vm.BusyOperation = "Preparing update";
        await _saves.WaitForIdleAsync();
        if (!await SavePreferencesAsync(vm)) return false;
        return await _saves.SaveAsync(vm) && !vm.IsDirty;
    }

    public void CancelUpdatePreparation()
    {
        _switching = false;
        _saves.DropPendingClose();
        if (DataContext is MainViewModel vm)
        {
            vm.IsBusy = false;
            vm.BusyOperation = null;
            if (vm.IsDirty) _saves.StartAutosave();
        }
        IsEnabled = true;
    }

    public void CloseForUpdate()
    {
        _refines.Cancel();
        _playback.CancelFrameRecovery();
        if (DataContext is MainViewModel vm)
        {
            _timelineMedia.ReleaseAudioPass(vm);
            _timelineMedia.CancelThumbnailLoad(vm, false);
        }
        _allowClose = true;
        Close();
    }
}
