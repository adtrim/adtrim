using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdTrim.Models;

namespace AdTrim.Controls;

public partial class SplitMarker : UserControl
{
    public SplitMarker()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyVisuals();
        DataContextChanged += (_, _) => ApplyVisuals();
    }

    public static readonly DependencyProperty ConfidenceProperty = DependencyProperty.Register(
        nameof(Confidence), typeof(Confidence), typeof(SplitMarker),
        new PropertyMetadata(Confidence.Neutral, OnVisualPropertyChanged));

    public Confidence Confidence
    {
        get => (Confidence)GetValue(ConfidenceProperty);
        set => SetValue(ConfidenceProperty, value);
    }

    public static readonly DependencyProperty ConfirmedProperty = DependencyProperty.Register(
        nameof(Confirmed), typeof(bool), typeof(SplitMarker),
        new PropertyMetadata(false, OnVisualPropertyChanged));

    public bool Confirmed
    {
        get => (bool)GetValue(ConfirmedProperty);
        set => SetValue(ConfirmedProperty, value);
    }

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(SplitMarker),
        new PropertyMetadata(false, OnVisualPropertyChanged));

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public static readonly DependencyProperty ShowAuditionProperty = DependencyProperty.Register(
        nameof(ShowAudition), typeof(bool), typeof(SplitMarker),
        new PropertyMetadata(false));

    public bool ShowAudition
    {
        get => (bool)GetValue(ShowAuditionProperty);
        set => SetValue(ShowAuditionProperty, value);
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SplitMarker),
        new PropertyMetadata(null));

    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SplitMarker m) m.ApplyVisuals();
    }

    private void ApplyVisuals()
    {
        // Confirmed markers always paint green regardless of refinement
        // confidence - confirmation is the user's explicit "this is right",
        // so it should look the same whether the underlying refine result was
        // high, medium, low, or unchanged.
        var color = Confirmed
            ? "Marker.High"
            : Confidence switch
            {
                Confidence.High      => "Marker.High",
                Confidence.Medium    => "Marker.Medium",
                Confidence.Low       => "Marker.Low",
                Confidence.Unchanged => "Marker.Unchanged",
                _                    => "Marker.Neutral",
            };
        var stroke = (Brush)(Application.Current.TryFindResource(color) ?? Brushes.Gray);
        var fill = Confirmed
            ? stroke
            : (Brush)(Application.Current.TryFindResource("Bg.Surface2") ?? Brushes.Black);

        Triangle.Stroke = stroke;
        Triangle.Fill = fill;

        // Stem: solid when confirmed, dashed when not. Color = confidence color.
        StemSolid.Stroke = stroke;
        StemDashed.Stroke = stroke;
        StemSolid.Visibility = Confirmed ? Visibility.Visible : Visibility.Collapsed;
        StemDashed.Visibility = Confirmed ? Visibility.Collapsed : Visibility.Visible;

        // Selected markers glow regardless of confirmation
        if (IsSelected)
        {
            Triangle.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 0,
                Color = ((SolidColorBrush)(Application.Current.TryFindResource("Marker.SelectedHalo") ?? Brushes.MediumPurple)).Color,
                Opacity = 0.65,
            };
        }
        else
        {
            Triangle.Effect = null;
        }
    }
}
