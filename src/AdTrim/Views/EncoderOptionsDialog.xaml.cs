using System.Windows;
using System.Windows.Controls;
using AdTrim.Services;
using AdTrim.ViewModels;

namespace AdTrim.Views;

public partial class EncoderOptionsDialog : Window
{
    public sealed record ResultRow(ExportAccelerationOption Option, bool Available, bool Fastest, double? Milliseconds, string Detail)
    {
        public string Name => Option.Name;
        public string Result => !Available ? "Unavailable: " + Detail
            : $"{Milliseconds / 1000:0.00}s" + (Fastest ? "  |  Fastest in this test" : "");
    }

    public EncoderOptionsDialog(EncoderDetection detection, string? failure = null)
    {
        InitializeComponent();
        bool recovery = failure is not null;
        Explanation.Text = recovery ? failure + "\nChoose an option to restart the entire export, or cancel."
            : detection.Error ?? "Evaluation results. Your export selection has not changed.";
        var fastest = detection.Preferred?.Adapter.Id ?? (detection.SoftwareMilliseconds is not null ? "software" : null);
        var rows = detection.Results.Select(r => new ResultRow(new(r.Adapter.Id, r.Adapter.Name, r.Adapter),
            r.Available, r.Available && r.Adapter.Id == fastest, r.Available ? r.Milliseconds : null, r.Detail)).ToList();
        rows.Add(new(ExportAccelerationOption.Software, detection.SoftwareMilliseconds is not null, fastest == "software",
            detection.SoftwareMilliseconds, detection.SoftwareDetail ?? "Software evaluation did not complete successfully."));
        OptionsList.ItemsSource = rows;
        OptionsList.SelectedItem = rows.FirstOrDefault(r => r.Fastest);
        RestartButton.Visibility = recovery ? Visibility.Visible : Visibility.Collapsed;
        DismissButton.Content = recovery ? "Cancel export" : "Close";
    }

    public ExportAccelerationOption? SelectedOption => (OptionsList.SelectedItem as ResultRow) is { Available: true } row ? row.Option : null;
    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }
    private void OnTitleClose(object sender, RoutedEventArgs e) => Close();
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RestartButton is not null) RestartButton.IsEnabled = SelectedOption is not null;
    }
    private void OnRestart(object sender, RoutedEventArgs e)
    {
        if (SelectedOption is not null) DialogResult = true;
    }
}
