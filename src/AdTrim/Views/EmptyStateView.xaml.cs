using System.Windows;
using System.Windows.Controls;

namespace AdTrim.Views;

public partial class EmptyStateView : UserControl
{
    public EmptyStateView()
    {
        InitializeComponent();
    }

    private void OnOpenClicked(object sender, RoutedEventArgs e)
    {
        // Forward to the host MainWindow's File ▸ Open… flow. Walking the
        // visual tree keeps the EmptyStateView host-agnostic - works whether
        // it's embedded as a child or used standalone.
        var window = Window.GetWindow(this) as MainWindow;
        window?.OpenFileFromEmptyState();
    }
}
