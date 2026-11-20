using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AdTrim;
using AdTrim.Models;
using AdTrim.ViewModels;
using AdTrim.Views;

internal static partial class Program
{
    private static void CheckExportSetupLayout(MainWindow window, ExportView view, string directory)
    {
        var vm = (ExportDialogViewModel)view.DataContext;
        var scroll = (ScrollViewer)view.FindName("ConfigScroll");
        var notice = (TextBlock)view.FindName("TsChapterNotice");
        var banner = (Border)view.FindName("TsChapterBanner");
        var footer = (FrameworkElement)view.FindName("ConfigFooter");
        var cards = new[] { "FormatCard", "DestinationCard", "AccelerationCard", "AfterExportCard" }
            .Select(name => (FrameworkElement)view.FindName(name)).ToArray();
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(view).TransformBounds(new Rect(element.RenderSize));

        foreach (var size in new[] { new Size(2560, 1390), new Size(1280, 720), new Size(1280, 640) })
        {
            window.Width = size.Width;
            window.Height = size.Height;
            scroll.ScrollToTop();
            window.UpdateLayout();
            var visible = Bounds(scroll);
            for (int i = 1; i < cards.Length; i++)
                Require(Math.Abs(Bounds(cards[i]).Left - Bounds(cards[0]).Left) < 1
                    && Math.Abs(cards[i].ActualWidth - cards[0].ActualWidth) < 1
                    && Bounds(cards[i]).Top >= Bounds(cards[i - 1]).Bottom - 1,
                    $"Export settings stay in one column in format, destination, acceleration, after-export order at {size}: {i}");
            // The job pane sits beside the settings, never on top of them or pushing them down.
            var job = (FrameworkElement)view.FindName("ProgressDetails");
            Require(job.IsVisible && Bounds(job).Left >= Bounds(cards[0]).Right && Bounds(job).Top <= Bounds(cards[0]).Top,
                $"Parts list sits beside the settings at {size}");
            Require(visible.Contains(Bounds(banner)) && Bounds(banner).Contains(Bounds(notice)),
                $"TS chapter limitation is fully visible without scrolling at {size}");
            Require(banner.BorderThickness.Left >= 3 && banner.Background is SolidColorBrush { Color.A: > 0 }
                && notice.Foreground != view.FindResource("Text.Secondary") && notice.FontSize >= 13,
                "TS chapter limitation has a highlighted panel and readable primary text");
            Require(Bounds(footer).Top >= visible.Bottom && Bounds(footer).Bottom <= view.ActualHeight + 1,
                $"Export actions remain visible outside the scrolling settings at {size}");
            scroll.ScrollToBottom();
            window.UpdateLayout();
            Require(Bounds(scroll).Contains(Bounds((FrameworkElement)view.FindName("DeleteSidecarCheckBox"))),
                $"Post-export option can be reached at {size}");
            if (size.Width > 1280)
                Require(Bounds(footer).Top - Bounds(cards[^1]).Bottom < 40,
                    "Export actions follow the last settings card on a tall window");
            scroll.ScrollToTop();
            window.UpdateLayout();
            Capture(window, Path.Combine(directory, $"export-setup-ts-{size.Width}-{size.Height}.png"));
            if (size.Width > 1280)
            {
                var panel = (FrameworkElement)view.FindName("ConfigPanel");
                var visual = new DrawingVisual();
                using (var drawing = visual.RenderOpen())
                {
                    drawing.DrawRectangle(view.Background, null, new Rect(panel.RenderSize));
                    drawing.DrawRectangle(new VisualBrush(panel), null, new Rect(panel.RenderSize));
                }
                var bitmap = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var png = new PngBitmapEncoder();
                png.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, "export-setup-panel.png"));
                png.Save(file);
            }
        }
        vm.SelectedFormat = ExportFormat.Mp4;
        window.UpdateLayout();
        Require(banner.Visibility == Visibility.Collapsed && notice.Visibility == Visibility.Collapsed,
            "Switching to MP4 removes the TS warning panel");
        Capture(window, Path.Combine(directory, "export-setup-mp4.png"));
        vm.SelectedFormat = ExportFormat.Ts;
        window.UpdateLayout();
    }
}
