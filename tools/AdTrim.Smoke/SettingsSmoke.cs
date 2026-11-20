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
            var done = (Button)settings.FindName("DoneButton");
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
                var bounds = done.TransformToAncestor(settings).TransformBounds(new Rect(done.RenderSize));
                Require(bounds.Bottom <= settings.ActualHeight && bounds.Right <= settings.ActualWidth, $"Done stays visible on {panes[i]}");
                Capture(settings, Path.Combine(directory, $"settings-{panes[i]}.png"));
            }

            navigation[0].IsChecked = true;
            Require(((RadioButton)settings.FindName("StartImported")).IsChecked == true, "Settings shows imported markers as the starting choice");
            var partName = (TextBox)settings.FindName("PartNameBox");
            partName.Text = "Chapter";
            await Task.Delay(100);
            Require(((TextBlock)settings.FindName("PartNameError")).IsVisible && WindowPreferences(window).SectionNameTemplate == SectionNames.DefaultTemplate,
                "An unusable part name is explained and not saved");
            partName.Text = "Chapter {n}";
            for (int i = 0; i < 100 && vm.SectionNameTemplate != "Chapter {n}"; i++) await Task.Delay(20);
            Require(vm.SectionNameTemplate == "Chapter {n}" && (await UserPreferences.ReadAsync()).SectionNameTemplate == "Chapter {n}",
                "A part name change is saved and shown in the open window at once");
            partName.Text = SectionNames.DefaultTemplate;

            navigation[1].IsChecked = true;
            var ruler = (CheckBox)settings.FindName("RulerSwitch");
            ruler.IsChecked = false;
            ruler.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            for (int i = 0; i < 100 && vm.ShowRulerTicks; i++) await Task.Delay(20);
            Require(!vm.ShowRulerTicks && !(await UserPreferences.ReadAsync()).ShowRulerTicks, "Ruler ticks are saved, not forgotten at the next start");
            ruler.IsChecked = true;
            ruler.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            for (int i = 0; i < 100 && !vm.ShowRulerTicks; i++) await Task.Delay(20);

            navigation[2].IsChecked = true;
            var filename = (TextBox)settings.FindName("FilenameBox");
            filename.Text = "no title";
            await Task.Delay(100);
            Require(((TextBlock)settings.FindName("FilenameError")).IsVisible
                && (await UserPreferences.ReadAsync()).ExportFilenameTemplate == ExportNaming.DefaultTemplate,
                "An export filename pattern without {title} is explained and not saved");
            filename.Text = "{title} [AdTrim {version}]";
            for (int i = 0; i < 100 && (await UserPreferences.ReadAsync()).ExportFilenameTemplate != "{title} [AdTrim {version}]"; i++) await Task.Delay(20);
            Require(((TextBlock)settings.FindName("FilenamePreview")).Text.EndsWith($"[AdTrim {AppVersion.Numeric}].mp4"),
                "The filename preview shows the AdTrim version");
            filename.Text = ExportNaming.DefaultTemplate;
            for (int i = 0; i < 100 && (await UserPreferences.ReadAsync()).ExportFilenameTemplate != ExportNaming.DefaultTemplate; i++) await Task.Delay(20);

            done.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Require(!settings.IsVisible, "Done closes Settings");
        }
        finally { if (settings.IsVisible) settings.Close(); }
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
