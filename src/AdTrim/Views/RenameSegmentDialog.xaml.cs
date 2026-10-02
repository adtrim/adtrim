using System.Windows;
using System.Windows.Controls;

namespace AdTrim.Views;

public partial class RenameSegmentDialog : Window
{
    public RenameSegmentDialog(string name)
    {
        InitializeComponent();
        NameBox.Text = name;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public string SegmentName => NameBox.Text.Trim();
    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }
    private void OnTitleClose(object sender, RoutedEventArgs e) => Close();

    private void OnNameChanged(object sender, TextChangedEventArgs e)
    {
        if (RenameButton is not null) RenameButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text);
    }

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(SegmentName)) DialogResult = true;
    }
}
