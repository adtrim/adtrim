using System.Windows;
using System.Windows.Input;

namespace AdTrim.Views;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var node = e.OriginalSource as DependencyObject; node is not null;)
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Documents.Hyperlink) return;
            node = node is FrameworkContentElement content ? content.Parent
                : System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void OnGitHubLink(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
