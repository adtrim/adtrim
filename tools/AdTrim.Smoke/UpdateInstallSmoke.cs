using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AdTrim;
using AdTrim.Commands;
using AdTrim.Services;
using AdTrim.ViewModels;
using AdTrim.Views;

internal static partial class Program
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowEnabled(IntPtr window);
    private static string UpdateTestVersion { get { var v = UpdateFeedReader.ParseVersion(AppVersion.Numeric); return new Version(v.Major, v.Minor, v.Build + 1).ToString(); } }
    private static int RunUpdateInstallSmoke()
    {
        var source = PrepareFixture().GetAwaiter().GetResult();
        var directory = Path.Combine(Path.GetDirectoryName(source)!, "update-state");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("ADTRIM_DATA_DIR", directory);
        File.WriteAllText(Path.Combine(directory, "preferences.json"), "{\"AddSourceExportComment\":false}");
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup -= (StartupEventHandler)Delegate.CreateDelegate(typeof(StartupEventHandler), app,
            typeof(App).GetMethod("OnStartup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!);
        int result = 1;
        app.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var windows = new List<MainWindow>();
            try
            {
                app.SetAutomaticUpdates(false);
                var next = Path.Combine(Path.GetDirectoryName(source)!, "other", Path.GetFileName(source));
                Directory.CreateDirectory(Path.GetDirectoryName(next)!);
                File.Copy(source, next);
                var hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
                foreach (var path in new[] { source, next })
                {
                    var window = new MainWindow(path) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
                    windows.Add(window);
                    window.Show();
                    await window.Ready.WaitAsync(TimeSpan.FromSeconds(20));
                    window.UpdateLayout();
                    Require(window.Title == "AdTrim - " + path && ((TextBlock)window.FindName("TitleRecordingPath")).Text == path,
                        "Title bars distinguish recordings with identical filenames in different folders");
                    Require(((Border)window.FindName("TitlePathEllipsis")).Visibility == Visibility.Collapsed,
                        "Paths that fit in the title bar have no ellipsis");
                    Require(window.CanRestartForUpdate, "An idle recording can prepare for an update");
                    var vm = (MainViewModel)window.DataContext;
                    vm.PlayheadUs = 5_000_000;
                    vm.CommandStack.Execute(new AddSplitCommand(vm, 5_000_000, AdTrim.Models.SplitSource.Manual));
                    Require(vm.IsDirty, "The updater fixture includes unsaved edits");
                    Require(await window.SaveForUpdateAsync(), "Restart preparation flushes open recording edits");
                    Require(!vm.IsDirty && File.Exists(path + ".adt.json"), "Restart preparation only accepts a completed project save");
                    window.CancelUpdatePreparation();
                    Require(window.CanRestartForUpdate && window.IsEnabled && !vm.IsBusy, "Aborted preparation restores an editable window");
                }
                var first = windows[0];
                var empty = app.NewWindow();
                await empty.Ready;
                Require(empty.Title == "AdTrim", "An empty recording window retains the AdTrim title");
                empty.CloseForUpdate();
                var firstVm = (MainViewModel)first.DataContext;
                var ownerField = typeof(App).GetField("_updateOwner", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var preparingField = typeof(App).GetField("_updatePreparing", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var deferredField = typeof(App).GetField("_updateDeferredPaths", BindingFlags.Instance | BindingFlags.NonPublic)!;
                ownerField.SetValue(app, first);
                preparingField.SetValue(app, true);
                try
                {
                    int count = app.Windows.Count;
                    Require(app.NewWindow() == first && app.Windows.Count == count, "External empty-window requests cannot create an unsaved window during update preparation");
                    Require(await app.OpenRecordingAsync(next) == first && ((List<string>)deferredField.GetValue(app)!).SequenceEqual([next]),
                        "External recording opens during preparation are deferred for restart");
                }
                finally { ownerField.SetValue(app, null); preparingField.SetValue(app, false); ((List<string>)deferredField.GetValue(app)!).Clear(); }
                await CheckNativeDialogBlocksRestartAsync(first);
                first.Test.Export();
                await WaitForExportSetupAsync(first);
                Require(!first.CanRestartForUpdate, "Export setup blocks restart instead of losing its choices");
                ((ExportView)((ContentControl)first.FindName("ExportHost")).Content).Close();
                firstVm.IsBusy = true;
                Require(!first.CanRestartForUpdate && !await first.SaveForUpdateAsync(), "Analysis blocks update preparation");
                firstVm.IsBusy = false;
                string preferences = Path.Combine(directory, "preferences.json");
                using (var locked = new FileStream(preferences, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Require(!await first.SaveForUpdateAsync(), "A settings save failure prevents update shutdown");
                first.CancelUpdatePreparation();
                firstVm.PlayheadUs = 7_000_000;
                firstVm.CommandStack.Execute(new AddSplitCommand(firstVm, 7_000_000, AdTrim.Models.SplitSource.Manual));
                var store = first.Test.Store!;
                var fallbackPath = store.AppDataFallbackPathFor(source);
                Directory.CreateDirectory(Path.GetDirectoryName(fallbackPath)!);
                using (var fallbackLock = new FileStream(fallbackPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                using (var locked = new FileStream(source + ".adt.json", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    Require(!await first.SaveForUpdateAsync() && firstVm.IsDirty, "A project save failure preserves unsaved edits and prevents shutdown");
                first.CancelUpdatePreparation();
                Require(await first.SaveForUpdateAsync(), "A failed save can be retried without closing the recording");
                first.CancelUpdatePreparation();
                Require(Enumerable.SequenceEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(source))), "Saving for an update leaves the source recording unchanged");

                using var key = RSA.Create(3072);
                var fixtures = await CreateUpdateExecutablesAsync(directory);
                var bytes = await File.ReadAllBytesAsync(fixtures.Installer);
                var manifest = new UpdatePackageManifest(1, UpdateTestVersion, $"AdTrim-Setup-v{UpdateTestVersion}.exe", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
                var json = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var signature = key.SignData(json, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
                var announcement = JsonSerializer.SerializeToUtf8Bytes(new UpdateFeed(1, 1, DateTimeOffset.UtcNow.AddMinutes(-1), null,
                    UpdateTestVersion, "Synthetic update fixture.", []), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                using var announcements = new HttpClient(new SmokeUpdateHandler(announcement, key.SignData(announcement, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
                var updates = new UpdateService(directory, key.ExportSubjectPublicKeyInfoPem(), announcements, installed: AppVersion.Numeric);
                typeof(App).GetField("_updates", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, updates);
                await updates.CheckAsync();
                var opener = app.NewWindow();
                await opener.Ready;
                var showing = opener.ShowUpdateAsync();
                await WaitUntilAsync(() => app.Windows.OfType<UpdateDialog>().Any(w => w.IsVisible), "The update announcement did not open");
                var updateWindow = app.Windows.OfType<UpdateDialog>().Single();
                Require(windows.All(w => IsWindowEnabled(new System.Windows.Interop.WindowInteropHelper(w).Handle)),
                    "The production update announcement keeps every recording window usable during download");
                Require(updateWindow.Owner is null && updateWindow.ShowInTaskbar, "The update window belongs to no recording window and can be found in the taskbar");
                opener.CloseForUpdate();
                await Task.Delay(200);
                Require(updateWindow.IsVisible && app.UpdateDialogOpen, "Closing the recording window it opened from keeps the update window and its download");
                await first.ShowUpdateAsync();
                Require(app.Windows.OfType<UpdateDialog>().Count() == 1 && updateWindow.IsVisible,
                    "Asking for updates from another window brings the existing update window forward instead of doing nothing");
                updateWindow.Close();
                await showing;
                Require(!app.UpdateDialogOpen, "Closing the modeless update dialog clears the shared open-dialog guard");
                using var client = new HttpClient(new UpdateInstallHandler(json, signature, bytes));
                var packages = new UpdatePackageService(directory, key.ExportSubjectPublicKeyInfoPem(), client);
                var notice = new UpdateNotice(UpdateTestVersion, "regular", "Synthetic update fixture.", []);
                var dialog = new UpdateDialog(notice, packages: packages) { Owner = first, ShowActivated = false, ShowInTaskbar = false };
                dialog.Show();
                dialog.UpdateLayout();
                var button = (Button)dialog.FindName("UpdateButton");
                var bounds = button.TransformToAncestor(dialog).TransformBounds(new Rect(button.RenderSize));
                Require(bounds.Left >= 0 && bounds.Right <= dialog.ActualWidth && bounds.Bottom <= dialog.ActualHeight, "Download and restart action fits inside the update dialog");
                var statuses = new List<string>();
                var status = (TextBlock)dialog.FindName("DownloadStatus");
                System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
                    .AddValueChanged(status, (_, _) => { if (statuses.LastOrDefault() != status.Text) statuses.Add(status.Text); });
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => button.IsEnabled, "Update download did not finish");
                Require(button.Content.ToString() == "Restart and update", "A signed, verified download offers restart in the app");
                Require(statuses.Any(s => s.StartsWith("Verifying the update's signature")) && statuses.Any(s => s.StartsWith("Checking the downloaded installer"))
                    && statuses.Any(s => s.StartsWith("Checking the installer's version")) && statuses[^1].StartsWith("Update downloaded and verified")
                    && !statuses.Any(s => s.Contains("99%")), "Download progress names each verification step instead of sitting at 99%: " + string.Join(" | ", statuses));
                var downloaded = (DownloadedUpdate)typeof(UpdateDialog).GetField("_download", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                Require(File.Exists(downloaded.InstallerPath), "Download is held in the isolated update folder");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                Require(((TextBlock)dialog.FindName("DownloadStatus")).Text.Contains("installed copy")
                    && windows.All(w => w.IsVisible && w.CanRestartForUpdate), "A test-folder build refuses to overwrite the installed app and keeps all windows open");
                Capture(dialog, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(directory))!, "update-ready.png"));
                dialog.Close();
                await WaitUntilAsync(() => !Directory.Exists(downloaded.Directory), "Closing the update dialog did not remove its downloaded installer");

                using var unavailable = new HttpClient(new UpdateInstallHandler([], [], [], missing: true));
                var fallback = new UpdateDialog(notice, packages: new UpdatePackageService(directory, key.ExportSubjectPublicKeyInfoPem(), unavailable))
                    { Owner = first, ShowActivated = false, ShowInTaskbar = false };
                fallback.Show();
                ((Button)fallback.FindName("UpdateButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => ((Button)fallback.FindName("UpdateButton")).Visibility == Visibility.Collapsed, "Manual fallback did not appear");
                Require(((Button)fallback.FindName("ReleaseButton")).IsEnabled, "Older releases without signed package information keep the manual download available");
                fallback.Close();

                var corruptBytes = bytes.ToArray();
                corruptBytes[^1] ^= 1;
                using var corruptClient = new HttpClient(new UpdateInstallHandler(json, signature, corruptBytes));
                var corrupt = new UpdateDialog(notice, packages: new UpdatePackageService(directory, key.ExportSubjectPublicKeyInfoPem(), corruptClient))
                    { Owner = first, ShowActivated = false, ShowInTaskbar = false };
                corrupt.Show();
                var retry = (Button)corrupt.FindName("UpdateButton");
                retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => retry.IsEnabled, "Corrupt download did not finish");
                Require(retry.Content.ToString() == "Download update" && ((TextBlock)corrupt.FindName("DownloadStatus")).Text.Contains("could not be verified"),
                    "A corrupt download shows a retryable error instead of crashing or offering installation");
                corrupt.Close();

                await CheckUpdateWindowAsync(app, directory);
                await CheckUpdateWorkerAsync(directory, fixtures, key, manifest, source);
                Require(Enumerable.SequenceEqual(hash, SHA256.HashData(await File.ReadAllBytesAsync(source))), "Installer and restart checks leave the original recording unchanged");
                result = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                foreach (var dialog in app.Windows.OfType<UpdateDialog>().ToArray()) dialog.Close();
                foreach (var window in windows) { window.CancelUpdatePreparation(); window.CloseForUpdate(); }
                app.Shutdown();
            }
        }));
        app.Run();
        return result;
    }

    private sealed record UpdateExecutables(string Parent, string OldApp, string Installer, string FailedInstaller,
        string FailOnceInstaller, string CrashInstaller, string HangInstaller);
    private static async Task<UpdateExecutables> CreateUpdateExecutablesAsync(string directory)
    {
        var compiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NSIS", "makensis.exe");
        if (!File.Exists(compiler)) throw new FileNotFoundException("Update smoke checks require NSIS, the installer compiler.", compiler);
        async Task<string> Compile(string name, string section, string? version = null)
        {
            version ??= UpdateTestVersion;
            var exe = Path.Combine(directory, name + ".exe");
            string script = Path.Combine(directory, name + ".nsi");
            await File.WriteAllTextAsync(script, $"""
                Unicode true
                RequestExecutionLevel user
                SilentInstall silent
                AutoCloseWindow true
                OutFile "{exe}"
                InstallDir "{directory}"
                VIProductVersion "{version}.0"
                VIAddVersionKey /LANG=1033 "FileVersion" "{version}.0"
                VIAddVersionKey /LANG=1033 "FileDescription" "AdTrim update test fixture"
                VIAddVersionKey /LANG=1033 "LegalCopyright" "Test fixture; not for distribution"
                Section
                {section}
                SectionEnd
                """);
            var start = new ProcessStartInfo(compiler) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add("/V2");
            start.ArgumentList.Add(script);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new IOException(await stdout + await stderr);
            return exe;
        }
        var parent = await Compile("parent", "Sleep 60000");
        // A started app records its command line and, like AdTrim, writes its own process ID as the started
        // marker, then stays running briefly: the update window only trusts a running installed copy.
        const string startedApp = """
            FileOpen $0 "$EXEDIR\restarted.txt" w
            FileWrite $0 $CMDLINE
            FileClose $0
            ReadEnvStr $1 ADTRIM_UPDATE_STARTED
            StrCmp $1 "" started
            System::Call 'kernel32::GetCurrentProcessId() i .r2'
            FileOpen $0 $1 w
            FileWrite $0 $2
            FileClose $0
            Sleep 1500
            started:
            """;
        var app = await Compile("app", startedApp);
        var oldApp = await Compile("oldapp", startedApp, AppVersion.Numeric);
        var crashApp = await Compile("crashapp", "SetErrorLevel 3");
        // Each installer writes the payload, an added file and the installation's own file list, as Setup does.
        string Install(string exe) => $"""
            SetOutPath "$INSTDIR"
            ClearErrors
            File /oname=AdTrim.exe "{exe}"
            CreateDirectory "$INSTDIR\binaries"
            FileOpen $0 "$INSTDIR\binaries\added.dll" w
            FileWrite $0 "added by the update"
            FileClose $0
            FileOpen $0 "$INSTDIR\install-files.txt" w
            FileWrite $0 "AdTrim.exe$\r$\nbinaries\added.dll$\r$\n"
            FileClose $0
            IfErrors 0 payloadCopied
            SetErrorLevel 1
            Abort "AdTrim files could not be updated."
            payloadCopied:
            """;
        var installer = await Compile("installer", Install(app));
        var failed = await Compile("failed", Install(app) + "\nSetErrorLevel 7");
        var failOnce = await Compile("failonce", """
            IfFileExists "$INSTDIR\fail-once.txt" 0 proceed
            Delete "$INSTDIR\fail-once.txt"
            SetErrorLevel 3
            Abort
            proceed:
            """ + "\n" + Install(app));
        var crash = await Compile("crash", Install(crashApp));
        // A new version that keeps running without ever becoming ready.
        var hangApp = await Compile("hangapp", "Sleep 120000");
        var hang = await Compile("hang", Install(hangApp));
        return new(parent, oldApp, installer, failed, failOnce, crash, hang);
    }

    // A Windows message box disables its owner natively, outside WPF's OwnedWindows; restart must still see it.
    private static async Task CheckNativeDialogBlocksRestartAsync(MainWindow window)
    {
        Require(!window.IsBlockedByDialog, "An idle window is not blocked by a dialog");
        bool? blockedDuring = null;
        var probe = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        probe.Tick += (_, _) => RecoveryEnumWindows((handle, _) =>
        {
            RecoveryGetWindowThreadProcessId(handle, out var process);
            if (process != Environment.ProcessId) return true;
            var title = new System.Text.StringBuilder(256);
            RecoveryGetWindowText(handle, title, 256);
            if (title.ToString() != "Blocking dialog check") return true;
            blockedDuring = window.IsBlockedByDialog;
            RecoveryEndDialog(handle, new IntPtr(1));
            return true;
        }, IntPtr.Zero);
        probe.Start();
        MessageBox.Show(window, "Restart must wait for this dialog.", "Blocking dialog check");
        probe.Stop();
        await Task.Delay(50);
        Require(blockedDuring == true && !window.IsBlockedByDialog, "A Windows message box on a recording window blocks restarting for an update");
    }

    // The update helper's window: steps, error choices, issue details and the installer warning.
    private static async Task CheckUpdateWindowAsync(App app, string directory)
    {
        var window = new UpdateProgressWindow(AppVersion.Numeric) { ShowActivated = false };
        window.Show();
        window.SetTarget(UpdateTestVersion);
        window.ShowStep(UpdateStep.Installing);
        window.UpdateLayout();
        var rows = (System.Collections.IEnumerable)((ItemsControl)window.FindName("StepList")).ItemsSource;
        var texts = rows.Cast<UpdateProgressWindow.StepRow>().Select(r => r.Glyph + " " + r.Text).ToArray();
        Require(texts.Length == 6 && texts[0].StartsWith("✓") && texts[3].StartsWith("▶ Installing") && texts[5].StartsWith("○"),
            "The update window shows finished, current and remaining steps: " + string.Join(" | ", texts));
        Require(((TextBlock)window.FindName("Versions")).Text.Contains(UpdateTestVersion), "The update window shows both versions");
        Require(((Button)window.FindName("RetryButton")).Visibility != Visibility.Visible, "No choices are offered while a step is running");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var error = new IOException($@"The installer stopped. See {profile}\AppData\Local\AdTrim\updates\job\log.txt");
        var choice = window.ShowErrorAsync(error.Message, "Cancel update",
            UpdateProgressWindow.FormatDetails(AppVersion.Numeric, UpdateTestVersion, UpdateStep.Installing, error));
        window.UpdateLayout();
        foreach (var name in new[] { "RetryButton", "CancelButton", "CopyButton", "ReportButton", "ErrorPanel" })
            Require(((FrameworkElement)window.FindName(name)).Visibility == Visibility.Visible, "A failed step offers " + name);
        Require(!window.Details.Contains(profile, StringComparison.OrdinalIgnoreCase) && window.Details.Contains(@"%LOCALAPPDATA%\AdTrim\updates")
            && window.Details.Contains("Installing the update") && window.Details.Contains(UpdateTestVersion),
            "Copied details name the step and versions without the user's folder names");
        Require(UpdateProgressWindow.IssuesUri.AbsoluteUri == "https://github.com/adtrim/adtrim/issues/new", "Report a problem opens a new GitHub issue");
        ((Button)window.FindName("CopyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(Clipboard.GetText() == window.Details, "Copy details places the issue text on the clipboard");
        Capture(window, Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(directory))!, "update-error.png"));
        ((Button)window.FindName("RetryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(await choice.WaitAsync(TimeSpan.FromSeconds(5)) == UpdateChoice.Retry, "Retry runs the failed step again");
        // Closing the window from the taskbar or with Alt+F4 means Cancel update, but recovery keeps its window.
        choice = window.ShowErrorAsync("Installing failed again.", "Cancel update", window.Details);
        window.Close();
        Require(await choice.WaitAsync(TimeSpan.FromSeconds(5)) == UpdateChoice.Cancel && window.IsVisible,
            "Closing a failed update's window cancels the update but keeps the window for recovery");
        window.ShowStep(UpdateStep.Restoring);
        var restoreChoice = window.ShowErrorAsync("Restoring failed.", "Close", window.Details);
        await Task.Delay(100);
        Require(window.IsVisible && !restoreChoice.IsCompleted, "A later recovery error is shown in the same visible window and waits for an answer");
        ((Button)window.FindName("RetryButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await restoreChoice;
        var previousWarning = UpdateProgressWindow.InstallerWarningAfter;
        UpdateProgressWindow.InstallerWarningAfter = TimeSpan.FromMilliseconds(500);
        bool stopped = false;
        window.StopRequested += (_, _) => stopped = true;
        window.ShowStep(UpdateStep.Installing);
        var stop = (Button)window.FindName("StopButton");
        await WaitUntilAsync(() => stop.Visibility == Visibility.Visible, "A long-running installer did not offer to stop");
        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(stopped, "Stopping a long-running installer is passed to the update helper");
        window.ShowStep(UpdateStep.Installing);
        await WaitUntilAsync(() => stop.Visibility == Visibility.Visible, "A retried installer did not offer to stop");
        Require(stop.IsEnabled, "Stop installer can be used again after Retry");
        UpdateProgressWindow.InstallerWarningAfter = previousWarning;
        window.ShowStep(UpdateStep.Restoring);
        Require(((TextBlock)window.FindName("Heading")).Text == "Restoring AdTrim", "Cancelling switches the window to restoring the previous version");
        window.CloseWhenFinished();

        // A launch that forwarded to a running copy asks that copy to confirm, which waits until every window is ready.
        string confirm = Path.Combine(directory, UpdateInstallation.StartedMarkerName);
        var starting = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        var confirmation = (Task)typeof(App).GetMethod("ConfirmStartedWhenReadyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app, [confirm])!;
        await Task.Delay(500);
        Require(!File.Exists(confirm), "A copy still starting does not confirm an update");
        starting.Show();
        await confirmation.WaitAsync(TimeSpan.FromSeconds(30));
        Require(await File.ReadAllTextAsync(confirm) == Environment.ProcessId.ToString(), "Once its windows are ready, the running copy confirms with its own process");
        File.Delete(confirm);
        starting.CloseForUpdate();

        await CheckUpdateStartupProtectionAsync(app, directory);

        // Launches during an update hand their recording to the update window and exit.
        var stage = UpdateStaging.CreateDirectory(directory);
        var worker = new UpdateWorker(directory, stage, "", AppVersion.Numeric, directory);
        var helperWindow = new UpdateProgressWindow(AppVersion.Numeric) { ShowActivated = false };
        helperWindow.Show();
        using var stopPipe = new CancellationTokenSource();
        var pipe = (Task)typeof(App).GetMethod("RunUpdatePipeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app, [worker, helperWindow, stopPipe.Token])!;
        string requested = Path.Combine(directory, "opened during update.mp4");
        bool handed = await Task.Run(() => UpdateInstallation.TrySendToHelper(directory, requested));
        Require(handed && worker.TakeDeferred().SequenceEqual([requested]), "A launch during installation hands its recording to the update window");
        stopPipe.Cancel();
        await pipe;
        helperWindow.CloseWhenFinished();
        Require(!UpdateInstallation.TrySendToHelper(directory, requested), "Without an update window, the hand-off reports failure instead of waiting");
        await UpdateStaging.RemoveDirectoryAsync(directory, stage);
    }

    private static async Task CheckUpdateStartupProtectionAsync(App app, string directory)
    {
        var editors = app.Windows.OfType<MainWindow>().ToArray();
        Require(editors.Length >= 2 && editors.All(w => w.Ready.IsCompletedSuccessfully),
            "Startup protection covers multiple already-ready recording windows");
        string marker = Path.Combine(directory, UpdateInstallation.StartedMarkerName);
        var opening = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        MainWindow? arriving = null;
        var gate = new Mutex(false, UpdateInstallation.GateName(directory), out var created);
        Require(created, "The startup protection fixture owns an independent update gate");
        var protection = (Task)typeof(App).GetMethod("ProtectUpdateStartupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app, [marker, CancellationToken.None])!;
        try
        {
            Require(editors.All(w => !w.IsEnabled && !((Button)w.FindName("MarkersButton")).IsEnabled),
                "Ready editors reject input while another recording is still opening");
            var confirmation = (Task)typeof(App).GetMethod("ConfirmStartedWhenReadyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(app, [marker])!;
            arriving = app.NewWindow();
            await arriving.Ready.WaitAsync(TimeSpan.FromSeconds(20));
            Require(!arriving.IsEnabled && !((Button)arriving.FindName("PlayPauseButton")).IsEnabled,
                "Recording windows opened during startup are protected before they become usable");
            await Task.Delay(200);
            Require(!File.Exists(marker) && !confirmation.IsCompleted, "An unfinished recording still withholds startup confirmation");
            opening.Show();
            await confirmation.WaitAsync(TimeSpan.FromSeconds(20));
            Require(File.Exists(marker) && app.Windows.OfType<MainWindow>().All(w => !w.IsEnabled) && !protection.IsCompleted,
                "Even after confirmation, editors stay protected while the helper can offer rollback");
            gate.Dispose();
            await protection.WaitAsync(TimeSpan.FromSeconds(5));
            Require(app.Windows.OfType<MainWindow>().All(w => w.IsEnabled),
                "Helper completion or exit releases every restored recording window");
            var normal = app.NewWindow();
            try
            {
                await normal.Ready.WaitAsync(TimeSpan.FromSeconds(20));
                Require(normal.IsEnabled, "Windows opened after the helper exits work normally");
            }
            finally { normal.CloseForUpdate(); }
        }
        finally
        {
            gate.Dispose();
            await protection.WaitAsync(TimeSpan.FromSeconds(5));
            opening.CloseForUpdate();
            arriving?.CloseForUpdate();
            if (File.Exists(marker)) File.Delete(marker);
        }

        var interrupted = new MainWindow { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        using var exitedHelper = new Mutex(false, UpdateInstallation.GateName(directory));
        var interruptedProtection = (Task)typeof(App).GetMethod("ProtectUpdateStartupAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(app, [marker, CancellationToken.None])!;
        try
        {
            Require(editors.All(w => !w.IsEnabled) && !interrupted.Ready.IsCompleted,
                "A helper can exit while another recording is still unready");
            exitedHelper.Dispose();
            await interruptedProtection.WaitAsync(TimeSpan.FromSeconds(5));
            Require(editors.All(w => w.IsEnabled) && !interrupted.Ready.IsCompleted,
                "Helper failure releases ready editors without waiting for the unfinished recording");
        }
        finally
        {
            exitedHelper.Dispose();
            await interruptedProtection.WaitAsync(TimeSpan.FromSeconds(5));
            interrupted.CloseForUpdate();
        }
    }

    private static async Task CheckUpdateWorkerAsync(string directory, UpdateExecutables fixture, RSA key,
        UpdatePackageManifest manifest, string source)
    {
        string publicKey = key.ExportSubjectPublicKeyInfoPem();
        var target = Path.Combine(directory, "installed app with spaces");
        var settings = await File.ReadAllBytesAsync(Path.Combine(directory, "preferences.json"));
        var oldApp = await File.ReadAllBytesAsync(fixture.OldApp);
        string installedExe = Path.Combine(target, "AdTrim.exe");
        // An installation as Setup leaves it: the app, its uninstaller, its file list, and user data beside them.
        async Task ResetInstallation()
        {
            // Started fixtures stay running for a moment after confirming; wait for them to release their files.
            for (int attempt = 1; Directory.Exists(target); attempt++)
            {
                try { Directory.Delete(target, true); }
                catch (Exception ex) when (attempt < 40 && ex is IOException or UnauthorizedAccessException) { await Task.Delay(250); }
            }
            Directory.CreateDirectory(target);
            await File.WriteAllBytesAsync(installedExe, oldApp);
            await File.WriteAllTextAsync(Path.Combine(target, "Uninstall.exe"), "old uninstaller");
            await File.WriteAllTextAsync(Path.Combine(target, UpdateBackup.ListName), "AdTrim.exe\r\n");
            await File.WriteAllTextAsync(Path.Combine(target, "project.adt.json"), "user-owned sentinel");
        }
        bool IsPrevious() => Enumerable.SequenceEqual(oldApp, File.ReadAllBytes(installedExe))
            && File.ReadAllText(Path.Combine(target, "Uninstall.exe")) == "old uninstaller"
            && File.ReadAllText(Path.Combine(target, UpdateBackup.ListName)) == "AdTrim.exe\r\n"
            && !File.Exists(Path.Combine(target, "binaries", "added.dll"))
            && File.ReadAllText(Path.Combine(target, "project.adt.json")) == "user-owned sentinel";
        async Task<(string Stage, UpdatePackageManifest Manifest)> Stage(string installer)
        {
            var bytes = await File.ReadAllBytesAsync(installer);
            var m = manifest with { SizeBytes = bytes.Length, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
            var json = JsonSerializer.SerializeToUtf8Bytes(m, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            string stage = UpdateStaging.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(stage, "update-package.json"), json);
            await File.WriteAllBytesAsync(Path.Combine(stage, "update-package.json.sig"), key.SignData(json, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
            File.Copy(installer, Path.Combine(stage, m.InstallerName));
            return (stage, m);
        }
        // Runs one helper session up to the installation; the parent stands in for the closing AdTrim.
        async Task<(UpdateWorker Worker, Process Parent, Mutex Gate, EventWaitHandle Ready)> Prepare(string stage, UpdatePackageManifest m, long ticksOffset = 0, string? registered = null)
        {
            var parent = Process.Start(new ProcessStartInfo(fixture.Parent) { UseShellExecute = false })!;
            UpdateInstallation.Write(stage, "job.json", new UpdateInstallJob(m.Version, target, parent.Id, parent.StartTime.ToUniversalTime().Ticks + ticksOffset));
            UpdateInstallation.WriteResume(new(stage, m), [source]);
            var gate = new Mutex(false, UpdateInstallation.GateName(directory), out _);
            var ready = new EventWaitHandle(false, EventResetMode.ManualReset, UpdateInstallation.ReadyName(stage));
            var worker = new UpdateWorker(directory, stage, publicKey, AppVersion.Numeric, registered ?? target) { InstallerAttempts = 1 };
            return (worker, parent, gate, ready);
        }
        async Task Release(UpdateWorker worker, Process parent, Mutex gate, EventWaitHandle ready, string stage)
        {
            worker.Dispose();
            if (!parent.HasExited) { parent.Kill(); await parent.WaitForExitAsync(); }
            parent.Dispose();
            gate.Dispose();
            ready.Dispose();
            await UpdateStaging.RemoveDirectoryAsync(directory, stage);
        }
        string Restarted() => File.ReadAllText(Path.Combine(target, "restarted.txt"));

        // 1. Success: wait for the exact parent, back up, install, start, then remove the backup.
        await ResetInstallation();
        var (stage, m) = await Stage(fixture.Installer);
        var (worker, parent, gate, ready) = await Prepare(stage, m);
        try
        {
            var steps = new List<UpdateStep>();
            worker.StepChanged += steps.Add;
            Require(UpdateInstallation.IsUpdating(directory), "A named gate prevents app startup during installation");
            var waiting = worker.WaitForParentAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await WaitUntilAsync(() => ready.WaitOne(0) || waiting.IsCompleted, "Update worker did not acknowledge readiness");
            if (waiting.IsFaulted) await waiting;
            Require(!waiting.IsCompleted && IsPrevious(), "The installer waits for the exact original process to exit before replacing files");
            gate.Dispose();
            Require(UpdateInstallation.IsUpdating(directory), "The worker retains the startup gate after the parent releases it");
            parent.Kill();
            await waiting.WaitAsync(TimeSpan.FromSeconds(30));
            await worker.InstallAsync(CancellationToken.None);
            Require(Directory.Exists(worker.BackupDirectory) && File.ReadAllText(Path.Combine(worker.BackupDirectory, "files", "Uninstall.exe")) == "old uninstaller",
                "The previous version is backed up before installing");
            Require(FileVersionInfo.GetVersionInfo(installedExe).FileVersion == UpdateTestVersion + ".0" && File.Exists(Path.Combine(target, "binaries", "added.dll")),
                "Silent NSIS installation handles a destination containing spaces and installs the expected version");
            await worker.StartAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Require(Restarted().Contains("--resume-update") && UpdateInstallation.ReadResume(directory, stage, UpdateTestVersion).Recordings.SequenceEqual([source]),
                "The new version starts with the original recording paths and confirms it is running");
            worker.Finish();
            Require(!Directory.Exists(worker.BackupDirectory) && !UpdateInstallation.IsUpdating(directory),
                "A confirmed start removes the backup and releases the startup gate");
            Require(steps.SequenceEqual([UpdateStep.WaitingForClose, UpdateStep.BackingUp, UpdateStep.Verifying, UpdateStep.Installing,
                UpdateStep.CheckingVersion, UpdateStep.Starting, UpdateStep.Done]), "Every step is reported in order: " + string.Join(", ", steps));
        }
        finally { await Release(worker, parent, gate, ready, stage); }

        // 2. A file briefly in use: the installer fails once and the helper retries it automatically.
        await ResetInstallation();
        await File.WriteAllTextAsync(Path.Combine(target, "fail-once.txt"), "in use");
        (stage, m) = await Stage(fixture.FailOnceInstaller);
        (worker, parent, gate, ready) = await Prepare(stage, m);
        try
        {
            worker = new UpdateWorker(directory, stage, publicKey, AppVersion.Numeric, target) { InstallerAttempts = 2 };
            parent.Kill();
            await worker.WaitForParentAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await worker.InstallAsync(CancellationToken.None);
            Require(FileVersionInfo.GetVersionInfo(installedExe).FileVersion == UpdateTestVersion + ".0", "A failed installer run is retried once automatically");
        }
        finally { await Release(worker, parent, gate, ready, stage); }

        // 3. The installer fails after writing part of the update; Cancel update restores and restarts the previous version.
        foreach (var (installer, failure) in new[] { (fixture.FailedInstaller, "installer-failed"), (fixture.CrashInstaller, "new-version-crashed") })
        {
            await ResetInstallation();
            (stage, m) = await Stage(installer);
            (worker, parent, gate, ready) = await Prepare(stage, m);
            try
            {
                parent.Kill();
                await worker.WaitForParentAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                bool failed = false;
                try
                {
                    await worker.InstallAsync(CancellationToken.None);
                    await worker.StartAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                }
                catch (IOException) { failed = true; }
                Require(failed && worker.NeedsRestore && !IsPrevious(), $"{failure}: the failure is reported after the installation changed files");
                await worker.RestoreAsync();
                Require(IsPrevious(), $"{failure}: Cancel update restores exactly the previous installation and keeps user files");
                await worker.StartAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                Require(worker.Step == UpdateStep.StartingPrevious && Restarted().Contains("--resume-update")
                    && UpdateInstallation.ReadResume(directory, stage, AppVersion.Numeric).Recordings.SequenceEqual([source]),
                    $"{failure}: the previous version reopens the same recordings");
            }
            finally { await Release(worker, parent, gate, ready, stage); }
        }

        // 3b. A new version that hangs: Retry waits for the same copy instead of starting another, a marker
        // naming another process does not count, and Cancel stops the hung copy before restoring its files.
        await ResetInstallation();
        (stage, m) = await Stage(fixture.HangInstaller);
        (worker, parent, gate, ready) = await Prepare(stage, m);
        try
        {
            int Running() => Process.GetProcessesByName("AdTrim").Count(p =>
            {
                try { return string.Equals(p.MainModule?.FileName, installedExe, StringComparison.OrdinalIgnoreCase); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
                finally { p.Dispose(); }
            });
            parent.Kill();
            await worker.WaitForParentAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            await worker.InstallAsync(CancellationToken.None);
            bool timedOut = false;
            try { await worker.StartAsync(TimeSpan.FromSeconds(3), CancellationToken.None); }
            catch (TimeoutException) { timedOut = true; }
            Require(timedOut && Running() == 1, "A new version that never becomes ready is reported, and stays the only copy");
            await File.WriteAllTextAsync(Path.Combine(stage, UpdateInstallation.StartedMarkerName), Environment.ProcessId.ToString());
            timedOut = false;
            try { await worker.StartAsync(TimeSpan.FromSeconds(3), CancellationToken.None); }
            catch (TimeoutException) { timedOut = true; }
            Require(timedOut && Running() == 1, "Retry waits for the same starting copy, and a marker from any other process does not confirm it");
            await worker.RestoreAsync();
            Require(Running() == 0 && IsPrevious(), "Cancel update stops the hung new version and restores the previous installation");
            await worker.StartAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Require(worker.Step == UpdateStep.StartingPrevious, "The previous version then starts and confirms it is running");
        }
        finally { await Release(worker, parent, gate, ready, stage); }

        // 4. Refusals before anything is changed.
        foreach (var failure in new[] { "tampered", "wrong-directory", "stale-parent", "tampered-after-ready", "cancelled" })
        {
            await ResetInstallation();
            (stage, m) = await Stage(fixture.Installer);
            (worker, parent, gate, ready) = await Prepare(stage, m, failure == "stale-parent" ? 1 : 0, failure == "wrong-directory" ? directory : null);
            using var cancel = new CancellationTokenSource();
            try
            {
                if (failure == "tampered") await File.AppendAllTextAsync(Path.Combine(stage, m.InstallerName), "changed after download");
                bool rejected = false;
                try
                {
                    var waiting = worker.WaitForParentAsync(TimeSpan.FromSeconds(30), cancel.Token);
                    if (failure is "tampered-after-ready" or "cancelled")
                    {
                        await WaitUntilAsync(() => ready.WaitOne(0) || waiting.IsCompleted, "Failure fixture did not become ready");
                        if (failure == "cancelled") cancel.Cancel();
                        else { await File.AppendAllTextAsync(Path.Combine(stage, m.InstallerName), "changed during shutdown"); parent.Kill(); }
                    }
                    await waiting.WaitAsync(TimeSpan.FromSeconds(30));
                    await worker.InstallAsync(cancel.Token);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or OperationCanceledException) { rejected = true; }
                Require(rejected && !File.Exists(Path.Combine(stage, "completed.json")) && !worker.NeedsRestore && IsPrevious(),
                    "Worker rejects " + failure + " without reporting installation success or replacing the app");
                if (failure == "tampered")
                {
                    // AdTrim has already closed; Cancel update must still bring it back with the same recordings.
                    await worker.StartAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                    Require(worker.Step == UpdateStep.StartingPrevious && IsPrevious() && Restarted().Contains("--resume-update")
                        && UpdateInstallation.ReadResume(directory, stage, AppVersion.Numeric).Recordings.SequenceEqual([source]),
                        "Cancelling a rejected download reopens the installed version with the same recordings");
                }
            }
            finally { await Release(worker, parent, gate, ready, stage); }
        }
        Require(Enumerable.SequenceEqual(settings, await File.ReadAllBytesAsync(Path.Combine(directory, "preferences.json"))),
            "Installation preserves existing settings");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException(failure);
            await Task.Delay(20);
        }
    }

    private sealed class UpdateInstallHandler(byte[] json, byte[] signature, byte[] installer, bool missing = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(missing ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".json") ? json : request.RequestUri.AbsolutePath.EndsWith(".sig") ? signature : installer) });
    }
}
