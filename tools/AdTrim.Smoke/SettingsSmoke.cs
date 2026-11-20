using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdTrim;
using AdTrim.Services;
using AdTrim.ViewModels;
using AdTrim.Views;

internal static partial class Program
{
    /// <summary>Saves one settings change the way the Settings window does, shared with every open window.</summary>
    private static Task<UserPreferences?> ChangeSettings(MainWindow window, Func<UserPreferences, UserPreferences> change)
        => (Task<UserPreferences?>)Invoke(window, "ChangeSettingsAsync", change)!;

    private static UserPreferences WindowPreferences(MainWindow window)
        => (UserPreferences)typeof(MainWindow).GetField("_preferences", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static SettingsWindow OpenSettingsForCheck(MainWindow window)
    {
        var change = typeof(MainWindow).GetMethod("ChangeSettingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Func<UserPreferences, UserPreferences>, Task<UserPreferences?>>>(window);
        var reset = typeof(MainWindow).GetMethod("ResetSettingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Func<Task<(UserPreferences Saved, string? Kept)?>>>(window);
        // Shown without ShowDialog so the check can drive it; the app opens it modally.
        var settings = new SettingsWindow(WindowPreferences(window), change, reset) { Owner = window, ShowActivated = false };
        settings.Show();
        settings.UpdateLayout();
        return settings;
    }

    private static async Task CheckSettingsWindow(MainWindow window, MainViewModel vm, string directory)
    {
        // Later checks rely on the smoke settings (source comments off); put them back afterwards.
        var original = await UserPreferences.ReadAsync();
        var mainMenu = (Menu)window.FindName("MainMenu");
        var fileMenu = (MenuItem)mainMenu.Items[0];
        var settingsItem = (MenuItem)window.FindName("SettingsMenuItem");
        Require(fileMenu.Items.Contains(settingsItem) && settingsItem.InputGestureText == "Ctrl+," && settingsItem.IsEnabled,
            "File menu opens Settings with Ctrl+,");
        Require(window.FindName("SourceCommentMenu") is null && window.FindName("StartingMarkersMenu") is null
            && window.FindName("AutomaticUpdatesMenu") is null && window.FindName("SegmentNamesMenuItem") is null,
            "Settings that moved to the Settings window are no longer in the menus");

        var settings = OpenSettingsForCheck(window);
        try
        {
            var panes = new[] { "EditingPane", "TimelinePane", "ExportPane", "UpdatesPane", "StoragePane" };
            var navigation = ((StackPanel)settings.FindName("Navigation")).Children.OfType<RadioButton>().ToArray();
            Require(navigation.Length == panes.Length && navigation[0].IsChecked == true
                && ((FrameworkElement)settings.FindName("EditingPane")).Visibility == Visibility.Visible,
                "Settings opens on Editing with five categories");
            var save = (Button)settings.FindName("SaveButton");
            for (int i = 0; i < panes.Length; i++)
            {
                navigation[i].IsChecked = true;
                settings.UpdateLayout();
                var pane = (FrameworkElement)settings.FindName(panes[i]);
                Require(pane.Visibility == Visibility.Visible && panes.Where(p => p != panes[i]).All(p => ((FrameworkElement)settings.FindName(p)).Visibility == Visibility.Collapsed),
                    $"Settings shows only the {panes[i]} pane");
                // Every word must be readable on the dark background: WPF's own Label and RadioButton default to black text.
                foreach (var block in Descendants<TextBlock>(pane).Where(b => b.IsVisible && b.Text.Length > 0))
                    Require(block.Foreground is SolidColorBrush { Color: var c } && Brightness(c) > 0.3,
                        $"Settings text is readable on the dark background: \"{block.Text[..Math.Min(40, block.Text.Length)]}\"");
                var bounds = save.TransformToAncestor(settings).TransformBounds(new Rect(save.RenderSize));
                Require(bounds.Bottom <= settings.ActualHeight && bounds.Right <= settings.ActualWidth, $"Save stays visible on {panes[i]}");
                Capture(settings, Path.Combine(directory, $"settings-{panes[i]}.png"));
            }

            navigation[0].IsChecked = true;
            Require(((RadioButton)settings.FindName("StartImported")).IsChecked == true, "Settings shows imported markers as the starting choice");
            Require(((Button)settings.FindName("CancelButton")).IsCancel && !save.IsDefault, "Esc cancels Settings; Enter in a text box does not save it");

            // Nothing is saved until Save, and Cancel discards the draft.
            var before = await UserPreferences.ReadAsync();
            ((TextBox)settings.FindName("PartNameBox")).Text = "Chapter {n}";
            SetSwitch(settings, "RulerSwitch", false);
            await Task.Delay(100);
            Require(vm.SectionNameTemplate == before.SectionNameTemplate && vm.ShowRulerTicks
                && (await UserPreferences.ReadAsync()).SectionNameTemplate == before.SectionNameTemplate,
                "Settings changes wait for Save");
            Click(settings, "CancelButton");
            var cancelled = await UserPreferences.ReadAsync();
            Require(!settings.IsVisible && !settings.Saved
                && cancelled.SectionNameTemplate == before.SectionNameTemplate && cancelled.ShowRulerTicks && vm.ShowRulerTicks,
                "Cancel discards every pane's changes");
        }
        finally { if (settings.IsVisible) settings.Close(); }

        settings = OpenSettingsForCheck(window);
        try
        {
            var partName = (TextBox)settings.FindName("PartNameBox");
            partName.Text = "Chapter";
            Click(settings, "SaveButton");
            Require(settings.IsVisible && ((TextBlock)settings.FindName("PartNameError")).IsVisible
                && ((FrameworkElement)settings.FindName("EditingPane")).IsVisible
                && (await UserPreferences.ReadAsync()).SectionNameTemplate == SectionNames.DefaultTemplate,
                "An unusable part name keeps Settings open on its pane and saves nothing");
            partName.Text = "Chapter {n}";
            SetSwitch(settings, "RulerSwitch", false);
            var filename = (TextBox)settings.FindName("FilenameBox");
            filename.Text = "no title";
            Click(settings, "SaveButton");
            Require(settings.IsVisible && ((TextBlock)settings.FindName("FilenameError")).IsVisible
                && ((FrameworkElement)settings.FindName("ExportPane")).IsVisible
                && (await UserPreferences.ReadAsync()).ExportFilenameTemplate == ExportNaming.DefaultTemplate,
                "An export filename pattern without {title} keeps Settings open on its pane and saves nothing");
            filename.Text = "{title} [AdTrim {version}]";
            Require(((TextBlock)settings.FindName("FilenamePreview")).Text.EndsWith($"[AdTrim {AppVersion.Numeric}].mp4"),
                "The filename preview shows the AdTrim version");

            // Another window saves a setting this one did not touch; Save must not undo it.
            await ChangeSettings(window, p => p with { StartMuted = true });
            Click(settings, "SaveButton");
            for (int i = 0; i < 100 && settings.IsVisible; i++) await Task.Delay(20);
            var saved = await UserPreferences.ReadAsync();
            Require(!settings.IsVisible && settings.Saved && saved.SectionNameTemplate == "Chapter {n}" && !saved.ShowRulerTicks
                && saved.ExportFilenameTemplate == "{title} [AdTrim {version}]" && saved.StartMuted,
                "Save writes the changes from every pane together and keeps another window's change");
            Require(vm.SectionNameTemplate == "Chapter {n}" && !vm.ShowRulerTicks, "Saved settings show in the open window at once");
        }
        finally { if (settings.IsVisible) settings.Close(); }

        settings = OpenSettingsForCheck(window);
        try
        {
            Click(settings, "RestoreDefaultsButton");
            Require((await UserPreferences.ReadAsync()).SectionNameTemplate == "Chapter {n}", "Restore defaults waits for Save");
            Click(settings, "SaveButton");
            for (int i = 0; i < 100 && settings.IsVisible; i++) await Task.Delay(20);
            var reset = await UserPreferences.ReadAsync();
            Require(!settings.IsVisible && reset.SectionNameTemplate == SectionNames.DefaultTemplate && reset.ShowRulerTicks && !reset.StartMuted
                && reset.ExportFilenameTemplate == ExportNaming.DefaultTemplate && vm.ShowRulerTicks,
                "Restore defaults and Save returns every setting to its default");
        }
        finally { if (settings.IsVisible) settings.Close(); }
        await ChangeSettings(window, p => p.WithSettingsFrom(original));
    }

    private static void Click(Window window, string name)
        => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

    private static void SetSwitch(Window window, string name, bool on)
    {
        var toggle = (CheckBox)window.FindName(name);
        toggle.IsChecked = on;
        toggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private static double Brightness(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
