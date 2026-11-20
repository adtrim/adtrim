using System.Windows;
using System.Windows.Controls;
using AdTrim.Services;

namespace AdTrim.Views;

public partial class SegmentNamesDialog : Window
{
    public SegmentNamesDialog(string template)
    {
        InitializeComponent();
        TemplateBox.Text = SectionNames.Normalize(template);
        Loaded += (_, _) => { TemplateBox.Focus(); TemplateBox.SelectAll(); };
    }

    public string NameTemplate => TemplateBox.Text.Trim();

    private void OnTitleBarMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
    }
    private void OnTitleClose(object sender, RoutedEventArgs e) => Close();

    private void OnTemplateChanged(object sender, TextChangedEventArgs e)
    {
        if (SaveButton is null) return;
        bool valid = SectionNames.IsValid(TemplateBox.Text);
        SaveButton.IsEnabled = valid;
        PreviewText.Text = valid
            ? string.Join(", ", Enumerable.Range(1, 3).Select(n => SectionNames.Format(TemplateBox.Text, n)))
            : "Include {n} once, for example: Chapter {n}";
    }

    private void OnUseDefault(object sender, RoutedEventArgs e)
    {
        TemplateBox.Text = SectionNames.DefaultTemplate;
        TemplateBox.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (SectionNames.IsValid(TemplateBox.Text)) DialogResult = true;
    }
}
