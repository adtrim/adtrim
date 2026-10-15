using System.Windows;
using System.Windows.Controls;

namespace AdTrim.Views;

/// <summary>Lists every keyboard shortcut (F1).</summary>
public partial class ShortcutsDialog : Window
{
    public ShortcutsDialog()
    {
        InitializeComponent();
        var groups = AdTrim.Models.KeyboardShortcuts.Groups;
        for (int g = 0; g < groups.Length; g++)
            (g % 2 == 0 ? LeftColumn : RightColumn).Children.Add(BuildGroup(groups[g].Group, groups[g].Items));
    }

    private static UIElement BuildGroup(string title, (string Keys, string Action)[] items)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < items.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = items[i].Keys, Margin = new Thickness(0, 2, 8, 2), FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("Font.Mono"), FontSize = 12 };
            var action = new TextBlock { Text = items[i].Action, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap,
                Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("Text.Secondary") };
            Grid.SetRow(keys, i); Grid.SetRow(action, i); Grid.SetColumn(action, 1);
            grid.Children.Add(keys); grid.Children.Add(action);
        }
        panel.Children.Add(grid);
        return panel;
    }

    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
