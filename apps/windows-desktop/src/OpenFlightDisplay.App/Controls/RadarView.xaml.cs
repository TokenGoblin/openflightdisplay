namespace OpenFlightDisplay.App.Controls;

using System.Collections.Specialized;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OpenFlightDisplay.App.ViewModels;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Units;
using Windows.Foundation;

/// <summary>
/// A north-up plan-position display: range rings, an observer marker, and
/// heading-oriented aircraft symbols.
/// </summary>
/// <remarks>
/// <para>
/// Drawn natively rather than in a WebView. The ADR reserves WebView2 for a real
/// slippy map; range rings and symbols need none of that, and keeping them in
/// C# keeps selection, hit-testing and theming native.
/// </para>
/// <para>
/// Track-up mode, panning, zoom and map tiles are Phase 2. This is north-up and
/// fixed to the monitoring radius.
/// </para>
/// </remarks>
public sealed partial class RadarView : UserControl
{
    /// <summary>Aircraft to plot.</summary>
    public static readonly DependencyProperty AircraftProperty =
        DependencyProperty.Register(
            nameof(Aircraft),
            typeof(IReadOnlyList<AircraftRowViewModel>),
            typeof(RadarView),
            new PropertyMetadata(null, OnAircraftChanged));

    /// <summary>Radius represented by the outermost ring, in kilometres.</summary>
    public static readonly DependencyProperty RangeKmProperty =
        DependencyProperty.Register(
            nameof(RangeKm),
            typeof(double),
            typeof(RadarView),
            new PropertyMetadata(80.0, OnRangeChanged));

    /// <summary>Units for the ring labels.</summary>
    public static readonly DependencyProperty UnitsProperty =
        DependencyProperty.Register(
            nameof(Units),
            typeof(UnitSystem),
            typeof(RadarView),
            new PropertyMetadata(UnitSystem.Aviation, OnRangeChanged));

    private INotifyCollectionChanged? _observedCollection;

    public RadarView() => InitializeComponent();

    public IReadOnlyList<AircraftRowViewModel>? Aircraft
    {
        get => (IReadOnlyList<AircraftRowViewModel>?)GetValue(AircraftProperty);
        set => SetValue(AircraftProperty, value);
    }

    public double RangeKm
    {
        get => (double)GetValue(RangeKmProperty);
        set => SetValue(RangeKmProperty, value);
    }

    public UnitSystem Units
    {
        get => (UnitSystem)GetValue(UnitsProperty);
        set => SetValue(UnitsProperty, value);
    }

    /// <summary>Raised when an aircraft symbol is clicked.</summary>
    public event EventHandler<AircraftRowViewModel>? AircraftSelected;

    // Geometry is read fresh from the host grid on every draw.
    //
    // An earlier version cached the size from SizeChanged and shared it between
    // the scale pass and the aircraft pass. The two passes then ran at different
    // times with different cached values, so the rings and the plot agreed with
    // each other but not with the control - the whole picture drew ~1.5x too
    // large and off-centre. One code path, no cached layout state.
    private double SurfaceWidth => Surface.ActualWidth;

    private double SurfaceHeight => Surface.ActualHeight;

    private double CentreX => SurfaceWidth / 2.0;

    private double CentreY => SurfaceHeight / 2.0;

    /// <summary>Pixels per kilometre at the current size and range.</summary>
    /// <remarks>
    /// Scaled to the smaller dimension so the outermost ring always fits, with a
    /// margin for the ring labels and cardinal marks that sit outside it.
    /// </remarks>
    private double PixelsPerKm
    {
        get
        {
            double usableRadius = (Math.Min(SurfaceWidth, SurfaceHeight) / 2.0) - 28.0;
            return usableRadius <= 0 || RangeKm <= 0 ? 0 : usableRadius / RangeKm;
        }
    }

    private static void OnAircraftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var radar = (RadarView)d;

        // Re-subscribe so an ObservableCollection mutating in place still
        // repaints; without this the radar would only update when the whole
        // collection instance was swapped.
        if (radar._observedCollection is not null)
        {
            radar._observedCollection.CollectionChanged -= radar.OnCollectionChanged;
        }

        radar._observedCollection = e.NewValue as INotifyCollectionChanged;
        if (radar._observedCollection is not null)
        {
            radar._observedCollection.CollectionChanged += radar.OnCollectionChanged;
        }

        radar.DrawAircraft();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var radar = (RadarView)d;
        radar.DrawScale();
        radar.DrawAircraft();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The scale is redrawn alongside the plot rather than only on resize.
        // Keeping them on separate triggers is what let them drift apart.
        DrawScale();
        DrawAircraft();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawScale();
        DrawAircraft();
    }

    private void DrawScale()
    {
        ScaleCanvas.Children.Clear();

        if (PixelsPerKm <= 0)
        {
            return;
        }

        var ringBrush = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        var textBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        // Four evenly spaced rings. More than that turns into moiré at small
        // sizes and stops being readable.
        for (int i = 1; i <= 4; i++)
        {
            double ringKm = RangeKm * i / 4.0;
            double radiusPx = ringKm * PixelsPerKm;

            var ring = new Ellipse
            {
                Width = radiusPx * 2,
                Height = radiusPx * 2,
                Stroke = ringBrush,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(ring, CentreX - radiusPx);
            Canvas.SetTop(ring, CentreY - radiusPx);
            ScaleCanvas.Children.Add(ring);

            double labelValue = UnitConverter.DistanceFromKm(ringKm, Units);
            var label = new TextBlock
            {
                Text = string.Create(
                    CultureInfo.CurrentCulture,
                    $"{labelValue:N0} {UnitConverter.DistanceUnitLabel(Units)}"),
                FontSize = 10,
                Foreground = textBrush,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(label, CentreX + 4);
            Canvas.SetTop(label, CentreY - radiusPx - 6);
            ScaleCanvas.Children.Add(label);
        }

        // Cardinal ticks. North-up is stated in text, not implied by an
        // unlabelled needle.
        string[] cardinals = ["N", "E", "S", "W"];
        for (int i = 0; i < cardinals.Length; i++)
        {
            double angleRad = i * Math.PI / 2.0;
            double outer = RangeKm * PixelsPerKm;

            var mark = new TextBlock
            {
                Text = cardinals[i],
                FontSize = 11,
                Foreground = textBrush,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(mark, CentreX + (outer + 6) * Math.Sin(angleRad) - 5);
            Canvas.SetTop(mark, CentreY - (outer + 6) * Math.Cos(angleRad) - 8);
            ScaleCanvas.Children.Add(mark);
        }

        // Observer marker.
        var observer = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"],
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(observer, CentreX - 4);
        Canvas.SetTop(observer, CentreY - 4);
        ScaleCanvas.Children.Add(observer);
    }

    private void DrawAircraft()
    {
        AircraftCanvas.Children.Clear();

        IReadOnlyList<AircraftRowViewModel> aircraft = Aircraft ?? [];
        EmptyLabel.Visibility = aircraft.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (PixelsPerKm <= 0)
        {
            return;
        }

        var symbolBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var mutedBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

        foreach (AircraftRowViewModel row in aircraft)
        {
            AircraftState state = row.Aircraft;

            if (state.DistanceFromObserverKm is not { } distanceKm
                || state.BearingFromObserverDeg is not { } bearingDeg)
            {
                continue;
            }

            // Beyond the outer ring: not drawn rather than clamped to the edge,
            // which would misrepresent its position.
            if (distanceKm > RangeKm)
            {
                continue;
            }

            double bearingRad = bearingDeg * Math.PI / 180.0;
            double x = CentreX + distanceKm * PixelsPerKm * Math.Sin(bearingRad);
            double y = CentreY - distanceKm * PixelsPerKm * Math.Cos(bearingRad);

            // Stale positions are drawn dimmer AND the row carries the word
            // "stale"; the board is where that is stated in text.
            Brush brush = row.IsStale ? mutedBrush : symbolBrush;

            var symbol = BuildSymbol(state, brush, row.HasEmergency);
            symbol.Tag = row;

            // A generous invisible hit area: the symbol itself is ~14px and
            // clicking it precisely with a mouse is fiddly.
            var hit = new Grid
            {
                Width = 28,
                Height = 28,
                Background = new SolidColorBrush(Colors.Transparent),
                Tag = row,
            };

            hit.Children.Add(symbol);
            hit.Tapped += OnSymbolTapped;
            ToolTipService.SetToolTip(hit, row.AccessibleDescription);
            AutomationPropertiesHelper.SetName(hit, row.AccessibleDescription);

            Canvas.SetLeft(hit, x - 14);
            Canvas.SetTop(hit, y - 14);
            AircraftCanvas.Children.Add(hit);

            if (state.Callsign is not null)
            {
                var label = new TextBlock
                {
                    Text = row.Callsign,
                    FontSize = 10,
                    Foreground = brush,
                    IsHitTestVisible = false,
                };

                Canvas.SetLeft(label, x + 12);
                Canvas.SetTop(label, y - 6);
                AircraftCanvas.Children.Add(label);
            }
        }
    }

    /// <summary>
    /// Builds the aircraft glyph, rotated to its track.
    /// </summary>
    /// <remarks>
    /// A triangle pointing along the track when a heading is known, a circle
    /// when it is not. The shape difference is deliberate: an unrotated triangle
    /// would read as "heading north" rather than "heading unknown".
    /// </remarks>
    private static FrameworkElement BuildSymbol(AircraftState state, Brush brush, bool emergency)
    {
        if (state.TrackHeadingDeg is not { } heading)
        {
            return new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = brush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var triangle = new Polygon
        {
            Fill = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Points =
            [
                new Point(7, 0),
                new Point(13, 14),
                new Point(7, 10),
                new Point(1, 14),
            ],
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = heading },
        };

        if (!emergency)
        {
            return triangle;
        }

        // Emergency gets a ring around the symbol as well as the word
        // "EMERGENCY" on the board row - shape and text, never colour alone.
        var grid = new Grid { Width = 22, Height = 22 };
        grid.Children.Add(new Ellipse
        {
            Width = 22,
            Height = 22,
            Stroke = brush,
            StrokeThickness = 2,
        });

        grid.Children.Add(triangle);
        return grid;
    }

    private void OnSymbolTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AircraftRowViewModel row })
        {
            AircraftSelected?.Invoke(this, row);
        }
    }
}

/// <summary>
/// Small shim so the automation name can be set from code without pulling the
/// full automation namespace into every call site.
/// </summary>
internal static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject element, string name)
        => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, name);
}
