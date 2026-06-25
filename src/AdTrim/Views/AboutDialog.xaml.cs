using System.Windows;
using System.Windows.Input;

namespace AdTrim.Views;

public partial class AboutDialog : Window
{
    public AboutDialog() => InitializeComponent();

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
