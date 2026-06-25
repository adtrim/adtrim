using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdTrim.Models;

namespace AdTrim.Controls;

public partial class SegmentBand : UserControl
{
    public SegmentBand()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyVisuals();
        Unloaded += (_, _) =>
        {
            if (Source is { } seg) seg.PropertyChanged -= OnSegmentPropertyChanged;
        };
    }

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(SegmentState), typeof(SegmentBand),
        new PropertyMetadata(SegmentState.Default, OnVisualPropertyChanged));

    public SegmentState State
    {
        get => (SegmentState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(SegmentBand),
        new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

    public string? Label
    {
        get => (string?)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>
    /// Backing segment. When set, the band mirrors its <see cref="Segment.State"/>
    /// and <see cref="Segment.Label"/> live - without this, toggling exclusion on
    /// a selected segment doesn't repaint until the next full timeline Redraw.
    /// </summary>
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(Segment), typeof(SegmentBand),
        new PropertyMetadata(null, OnSourceChanged));

    public Segment? Source
    {
        get => (Segment?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SegmentBand band) return;
        if (e.OldValue is Segment oldSeg) oldSeg.PropertyChanged -= band.OnSegmentPropertyChanged;
        if (e.NewValue is Segment newSeg)
        {
            newSeg.PropertyChanged += band.OnSegmentPropertyChanged;
            band.State = newSeg.State;
            band.Label = newSeg.Label;
        }
    }

    private void OnSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Segment seg) return;
        if (e.PropertyName == nameof(Segment.State)) State = seg.State;
        else if (e.PropertyName == nameof(Segment.Label)) Label = seg.Label;
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SegmentBand b) b.ApplyVisuals();
    }

    private void ApplyVisuals()
    {
        var isSel = State is SegmentState.Selected or SegmentState.SelectedExcluded;
        var isExc = State is SegmentState.Excluded or SegmentState.SelectedExcluded;

        // Excluded:          stripe overlay + strong border + tinted bg
        // Selected:          accent-soft fill + 1.5px accent border + accent inset glow
        // SelectedExcluded:  stripe overlay + accent border + accent inset glow
        //                    (lattice tells you it's excluded, blue tells you it's selected)
        if (isExc)
        {
            Stripe.Visibility = Visibility.Visible;
            if (isSel)
            {
                // Soft accent fill underneath, accent-stroked lattice on top -
                // matches the selected-included look but keeps the lattice cue.
                Bd.Background = (Brush)(Application.Current.TryFindResource("Accent.SelectionFill") ?? Brushes.Transparent);
                Stripe.Fill = (Brush)(Application.Current.TryFindResource("Stripe.Crosshatch.Accent") ?? Stripe.Fill);
                Bd.BorderBrush = (Brush)(Application.Current.TryFindResource("Accent.Base") ?? Brushes.DodgerBlue);
                Bd.BorderThickness = new Thickness(1.5);
            }
            else
            {
                Bd.Background = (Brush)(Application.Current.TryFindResource("Segment.Excluded.Bg") ?? Brushes.Black);
                Stripe.Fill = (Brush)(Application.Current.TryFindResource("Stripe.Crosshatch") ?? Stripe.Fill);
                Bd.BorderBrush = (Brush)(Application.Current.TryFindResource("Border.Strong") ?? Brushes.Gray);
                Bd.BorderThickness = new Thickness(1);
            }
        }
        else if (isSel)
        {
            Bd.Background = (Brush)(Application.Current.TryFindResource("Accent.SelectionFill") ?? Brushes.Transparent);
            Bd.BorderBrush = (Brush)(Application.Current.TryFindResource("Accent.Base") ?? Brushes.DodgerBlue);
            Bd.BorderThickness = new Thickness(1.5);
            Stripe.Visibility = Visibility.Collapsed;
        }
        else
        {
            Bd.Background = Brushes.Transparent;
            Bd.BorderBrush = (Brush)(Application.Current.TryFindResource("Border.Subtle") ?? Brushes.Gray);
            Bd.BorderThickness = new Thickness(1);
            Stripe.Visibility = Visibility.Collapsed;
        }

        if (isSel)
        {
            Bd.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 6,
                ShadowDepth = 0,
                Color = ((SolidColorBrush)(Application.Current.TryFindResource("Accent.Base") ?? Brushes.DodgerBlue)).Color,
                Opacity = 0.35,
            };
        }
        else
        {
            Bd.Effect = null;
        }

        // Label chip
        LabelTb.Text = Label ?? string.Empty;
        if (isExc)
        {
            LabelTb.Foreground = (Brush)(Application.Current.TryFindResource("Text.Secondary") ?? Brushes.LightGray);
            LabelChip.Background = new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0));
            LabelDot.Visibility = Visibility.Visible;
            LabelTb.Text = (Label ?? "").ToUpperInvariant();
        }
        else
        {
            LabelTb.Foreground = (Brush)(Application.Current.TryFindResource("Text.Primary") ?? Brushes.White);
            LabelChip.Background = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0));
            LabelDot.Visibility = Visibility.Collapsed;
        }
    }
}
