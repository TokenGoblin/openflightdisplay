namespace OpenFlightDisplay.App.Controls;

using System.Collections.Specialized;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using OpenFlightDisplay.App.ViewModels;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Geo;
using OpenFlightDisplay.Core.Units;
using OpenFlightDisplay.Infrastructure.Maps;
using OpenFlightDisplay.Persistence;
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

    /// <summary>
    /// Recorded track of the selected aircraft, oldest first.
    /// </summary>
    /// <remarks>
    /// Positions are absolute lat/lon rather than screen points, so the trail
    /// stays correct when the range or window size changes.
    /// </remarks>
    public static readonly DependencyProperty TrailProperty =
        DependencyProperty.Register(
            nameof(Trail),
            typeof(IReadOnlyList<TrailPoint>),
            typeof(RadarView),
            new PropertyMetadata(null, OnTrailChanged));

    /// <summary>Observer latitude, the origin the plot is drawn around.</summary>
    public static readonly DependencyProperty ObserverLatitudeProperty =
        DependencyProperty.Register(
            nameof(ObserverLatitude),
            typeof(double),
            typeof(RadarView),
            new PropertyMetadata(0.0, OnTrailChanged));

    /// <summary>Observer longitude.</summary>
    public static readonly DependencyProperty ObserverLongitudeProperty =
        DependencyProperty.Register(
            nameof(ObserverLongitude),
            typeof(double),
            typeof(RadarView),
            new PropertyMetadata(0.0, OnTrailChanged));

    /// <summary>Units for the ring labels.</summary>
    public static readonly DependencyProperty UnitsProperty =
        DependencyProperty.Register(
            nameof(Units),
            typeof(UnitSystem),
            typeof(RadarView),
            new PropertyMetadata(UnitSystem.Aviation, OnRangeChanged));

    /// <summary>
    /// Most aircraft symbols drawn in one pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plot rebuilds its visual tree on every poll, so this is a hard cost
    /// ceiling, and it was the dominant one. Measured with a 1,000-aircraft
    /// mock: an uncapped plot creates roughly two thousand XAML elements every
    /// two seconds and pins a full core, with working set climbing several
    /// hundred megabytes a minute as they are discarded.
    /// </para>
    /// <para>
    /// The list arrives ranked, so the cap keeps the nearest aircraft — the ones
    /// the display exists to show. A denser plot than this is also unreadable,
    /// so the limit costs nothing a user would want.
    /// </para>
    /// </remarks>
    public const int MaxSymbols = 200;

    /// <summary>
    /// Most callsign labels drawn in one pass.
    /// </summary>
    /// <remarks>
    /// Far lower than <see cref="MaxSymbols"/> because labels overlap long
    /// before symbols do — at around a hundred aircraft the text becomes an
    /// unreadable mass. Nearest aircraft keep their labels; the rest are drawn
    /// as bare symbols and remain selectable, with their identity in the tooltip
    /// and on the flight board.
    /// </remarks>
    public const int MaxLabels = 40;

    /// <summary>
    /// Supplies map tiles, or <c>null</c> when the backdrop is off.
    /// </summary>
    /// <remarks>
    /// A property rather than a dependency property: it is set once from the
    /// window and never bound.
    /// </remarks>
    public MapTileCache? Tiles { get; set; }

    /// <summary>Identifies the current map draw so superseded ones can stop.</summary>
    private int _mapGeneration;

    /// <summary>
    /// The geometry the current backdrop was drawn for.
    /// </summary>
    /// <remarks>
    /// The map only needs redrawing when the centre or the scale changes, which
    /// is rare — not on every poll. Without this the tiles would be rebuilt
    /// every two seconds for a picture that had not moved.
    /// </remarks>
    private (double Lat, double Lon, double MetresPerPixel, double Width, double Height) _mapDrawnFor;

    private INotifyCollectionChanged? _observedCollection;

    /// <summary>True while a coalesced redraw is already queued.</summary>
    private bool _redrawPending;

    public RadarView() => InitializeComponent();

    /// <summary>Forces an immediate redraw, for changes the plot cannot observe.</summary>
    /// <remarks>
    /// Switching the map backdrop on or off changes nothing the dependency
    /// properties watch, so the window asks for the redraw explicitly.
    /// </remarks>
    public void RedrawNow() => RequestRedraw();

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

    public IReadOnlyList<TrailPoint>? Trail
    {
        get => (IReadOnlyList<TrailPoint>?)GetValue(TrailProperty);
        set => SetValue(TrailProperty, value);
    }

    public double ObserverLatitude
    {
        get => (double)GetValue(ObserverLatitudeProperty);
        set => SetValue(ObserverLatitudeProperty, value);
    }

    public double ObserverLongitude
    {
        get => (double)GetValue(ObserverLongitudeProperty);
        set => SetValue(ObserverLongitudeProperty, value);
    }

    /// <summary>Raised when an aircraft symbol is clicked.</summary>
    public event EventHandler<AircraftRowViewModel>? AircraftSelected;

    /// <summary>
    /// Ranges the zoom control steps through, in kilometres.
    /// </summary>
    /// <remarks>
    /// A fixed ladder rather than a continuous zoom. Range rings are only
    /// readable at round numbers, and a radar whose rings read "37 km" is worse
    /// than one that steps.
    /// </remarks>
    public static readonly IReadOnlyList<double> ZoomSteps =
        [2, 5, 10, 20, 40, 80, 150, 250];

    /// <summary>
    /// Furthest the user may zoom out, in kilometres.
    /// </summary>
    /// <remarks>
    /// The monitoring radius. Beyond it there is nothing to show — the provider
    /// was never asked — and an empty band outside the last ring would read as
    /// "no traffic there" rather than "never looked".
    /// </remarks>
    public double MaxRangeKm { get; set; } = 250;

    /// <summary>Raised when the user zooms. The window persists the new range.</summary>
    public event EventHandler<double>? RangeChangeRequested;

    private void OnZoomIn(object sender, RoutedEventArgs e) => Zoom(-1);

    private void OnZoomOut(object sender, RoutedEventArgs e) => Zoom(1);

    /// <summary>
    /// Zooms with the scroll wheel.
    /// </summary>
    /// <remarks>
    /// Marked handled so the wheel does not also scroll whatever container the
    /// radar sits in — zooming the plot and scrolling the page at once is the
    /// usual way this feels broken.
    /// </remarks>
    private void OnPointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(Surface).Properties.MouseWheelDelta;
        if (delta != 0)
        {
            Zoom(delta > 0 ? -1 : 1);
            e.Handled = true;
        }
    }

    /// <summary>Steps the range along the ladder.</summary>
    /// <param name="direction">Negative zooms in, positive zooms out.</param>
    private void Zoom(int direction)
    {
        // Nearest ladder step to the current range, so a range typed into
        // Settings still zooms sensibly rather than jumping.
        int index = 0;
        double best = double.MaxValue;

        for (int i = 0; i < ZoomSteps.Count; i++)
        {
            double distance = Math.Abs(ZoomSteps[i] - RangeKm);
            if (distance < best)
            {
                best = distance;
                index = i;
            }
        }

        int target = Math.Clamp(index + direction, 0, ZoomSteps.Count - 1);
        double range = ZoomSteps[target];

        // Never past the monitoring radius: there is no data out there.
        if (range > MaxRangeKm)
        {
            range = MaxRangeKm;
        }

        if (Math.Abs(range - RangeKm) < 0.001)
        {
            return;
        }

        RangeChangeRequested?.Invoke(this, range);
    }

    /// <summary>Updates the range readout beside the zoom buttons.</summary>
    private void UpdateRangeLabel()
    {
        double outer = UnitConverter.DistanceFromKm(RangeKm, Units);

        RangeLabel.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"{outer:N0} {UnitConverter.DistanceUnitLabel(Units)}");

        ZoomOutButton.IsEnabled = RangeKm < MaxRangeKm - 0.001;
        ZoomInButton.IsEnabled = RangeKm > ZoomSteps[0] + 0.001;
    }

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

        radar.RequestRedraw();
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RadarView)d).RequestRedraw();

    private static void OnTrailChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RadarView)d).RequestRedraw();

    /// <summary>
    /// Converts a geographic position to a point on the plot.
    /// </summary>
    /// <remarks>
    /// Uses the same haversine distance and initial bearing the ranker does, so
    /// a trail point and the live symbol for the same coordinates land in
    /// exactly the same place. Computing the trail any other way would leave a
    /// visible gap between the track and the aircraft drawing it.
    /// </remarks>
    private bool TryProject(double lat, double lon, out double x, out double y)
    {
        double distanceKm = GeoMath.HaversineDistanceKm(
            ObserverLatitude, ObserverLongitude, lat, lon);

        if (distanceKm > RangeKm)
        {
            x = 0;
            y = 0;
            return false;
        }

        double bearingRad = GeoMath.InitialBearingDeg(
            ObserverLatitude, ObserverLongitude, lat, lon) * Math.PI / 180.0;

        x = CentreX + (distanceKm * PixelsPerKm * Math.Sin(bearingRad));
        y = CentreY - (distanceKm * PixelsPerKm * Math.Cos(bearingRad));
        return true;
    }

    private void DrawTrail()
    {
        IReadOnlyList<TrailPoint> trail = Trail ?? [];

        // Two points minimum: a single recorded position is not a track, and
        // drawing a zero-length line just puts a dot under the aircraft.
        if (trail.Count < 2 || PixelsPerKm <= 0)
        {
            return;
        }

        var brush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        var line = new Polyline
        {
            Stroke = brush,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
        };

        foreach (TrailPoint point in trail)
        {
            // Points outside the current range are skipped rather than clamped
            // to the edge, which would draw a track along the rim that the
            // aircraft never flew.
            if (TryProject(point.Latitude, point.Longitude, out double x, out double y))
            {
                line.Points.Add(new Point(x, y));
            }
        }

        if (line.Points.Count >= 2)
        {
            AircraftCanvas.Children.Add(line);
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RequestRedraw();

    /// <summary>
    /// Schedules one redraw for the current batch of collection changes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Redrawing directly from the handler was the single largest performance
    /// bug in this control. A poll updating 1,000 aircraft raises up to 1,000
    /// collection-changed events, and the plot was rebuilding its entire visual
    /// tree for every one of them — a thousand full redraws per update, pinning
    /// a core and churning hundreds of megabytes a minute.
    /// </para>
    /// <para>
    /// Coalescing onto the dispatcher collapses that batch into a single redraw
    /// once the collection has settled. Measured before and after with a
    /// 1,000-aircraft mock rather than assumed.
    /// </para>
    /// </remarks>
    private void RequestRedraw()
    {
        if (_redrawPending)
        {
            return;
        }

        _redrawPending = true;

        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _redrawPending = false;
            DrawMapAsync();
            DrawScale();
            DrawAircraft();
        }))
        {
            // The queue refuses work while shutting down; drawing now would
            // fail anyway, so just clear the flag.
            _redrawPending = false;
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // The canvases are pinned to the top-left and have no size of their own,
        // so the backdrop is clipped here rather than by layout; otherwise tiles
        // overhanging the plot would spill across the rest of the window.
        Surface.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, SurfaceWidth, SurfaceHeight),
        };

        DrawMapAsync();
        DrawScale();
        DrawAircraft();
    }

    /// <summary>
    /// Draws the map backdrop, if one is switched on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Async and fire-and-forget by design: tiles arrive from disk or network,
    /// and the rings and aircraft must never wait on them. If the map never
    /// draws, the radar is exactly what it was before this feature existed.
    /// </para>
    /// <para>
    /// Tiles are scaled to the radar's own metres-per-pixel rather than the
    /// radar being rebuilt around the map, so a symbol lands over the road it is
    /// genuinely above and the range rings keep meaning what they meant.
    /// </para>
    /// </remarks>
    private async void DrawMapAsync()
    {
        if (Tiles is null || PixelsPerKm <= 0)
        {
            MapCanvas.Children.Clear();
            _mapDrawnFor = default;
            return;
        }

        double metresPerPixel = 1000.0 / PixelsPerKm;
        var wanted = (ObserverLatitude, ObserverLongitude, metresPerPixel, SurfaceWidth, SurfaceHeight);

        // Nothing moved, so the tiles on screen are still correct.
        if (_mapDrawnFor == wanted && MapCanvas.Children.Count > 0)
        {
            return;
        }

        _mapDrawnFor = wanted;

        // A generation counter, deliberately NOT a CancellationTokenSource.
        //
        // This method is async void and re-entrant: every poll can start another
        // one. The first version cancelled and disposed the previous draw's
        // token source, and when that older draw resumed from its await it
        // touched a disposed token and threw ObjectDisposedException. An
        // exception escaping an async void method on the UI thread takes the
        // whole process down, which is exactly what it did - intermittently,
        // because it needed a redraw to land while a fetch was in flight.
        //
        // A superseded draw now simply stops. In-flight fetches are left to
        // finish, which costs nothing: their result lands in the tile cache and
        // the next draw picks it up from disk.
        int generation = ++_mapGeneration;

        IReadOnlyList<TilePlacement> placements = SlippyMap.Cover(
            ObserverLatitude, ObserverLongitude, SurfaceWidth, SurfaceHeight, metresPerPixel);

        MapCanvas.Children.Clear();

        try
        {
            foreach (TilePlacement placement in placements)
            {
                string? path = await Tiles
                    .GetTileAsync(placement.Tile, CancellationToken.None)
                    .ConfigureAwait(true);

                // Superseded while we were awaiting: the canvas now belongs to a
                // newer draw and must not be touched.
                if (generation != _mapGeneration)
                {
                    return;
                }

                if (path is null)
                {
                    // A missing tile leaves a gap rather than a placeholder. An
                    // invented tile would misrepresent the ground.
                    continue;
                }

                var image = new Image
                {
                    Source = new BitmapImage(new Uri(path)),
                    Width = placement.Size,
                    Height = placement.Size,
                    IsHitTestVisible = false,
                };

                Canvas.SetLeft(image, placement.Left);
                Canvas.SetTop(image, placement.Top);
                MapCanvas.Children.Add(image);
            }
        }
#pragma warning disable CA1031 // Nothing here is worth killing the application for.
        catch (Exception)
#pragma warning restore CA1031
        {
            // A corrupt cached tile, a vanished file, a decode failure. The
            // backdrop is decoration; the rings and symbols are the instrument
            // and are already drawn. Swallowing here is what stops an async void
            // method turning a cosmetic problem into a dead process.
            _mapDrawnFor = default;
        }
    }

    private void DrawScale()
    {
        UpdateRangeLabel();
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

        // Trail first so aircraft symbols draw on top of it.
        DrawTrail();

        var symbolBrush = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var mutedBrush = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

        int drawn = 0;

        foreach (AircraftRowViewModel row in aircraft)
        {
            if (drawn >= MaxSymbols)
            {
                break;
            }

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

            if (state.Callsign is not null && drawn < MaxLabels)
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

            drawn++;
        }

        // Say when the plot is not showing everything. Silently omitting
        // aircraft would make the radar disagree with the flight board with no
        // explanation, which is exactly the kind of quiet inconsistency the
        // project's no-silent-failure rule exists to prevent.
        //
        // Both causes are named, because they need different actions: aircraft
        // outside the current zoom come back by zooming out, aircraft past the
        // symbol cap do not.
        int outsideRange = aircraft.Count(a =>
            a.Aircraft.DistanceFromObserverKm is { } d && d > RangeKm);

        if (aircraft.Count > drawn)
        {
            string reason = outsideRange > 0
                ? string.Create(
                    CultureInfo.CurrentCulture,
                    $"Showing {drawn} of {aircraft.Count} aircraft — {outsideRange} are beyond " +
                    $"this zoom. All are on the flight board.")
                : string.Create(
                    CultureInfo.CurrentCulture,
                    $"Showing the nearest {drawn} of {aircraft.Count} aircraft. All are on the flight board.");

            var note = new TextBlock
            {
                Text = reason,
                FontSize = 11,
                Foreground = mutedBrush,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(note, 8);
            Canvas.SetTop(note, 8);
            AircraftCanvas.Children.Add(note);
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
