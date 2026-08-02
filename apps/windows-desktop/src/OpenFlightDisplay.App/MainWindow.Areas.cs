namespace OpenFlightDisplay.App;

using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenFlightDisplay.App.Dialogs;
using OpenFlightDisplay.App.Services;
using OpenFlightDisplay.App.ViewModels;
using OpenFlightDisplay.Core.Alerts;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Export;
using OpenFlightDisplay.Core.Ranking;
using OpenFlightDisplay.Core.Settings;
using OpenFlightDisplay.Core.Units;
using OpenFlightDisplay.Core.Tracking;
using OpenFlightDisplay.Infrastructure.Maps;
using OpenFlightDisplay.Infrastructure.Settings;
using OpenFlightDisplay.Infrastructure.Tracking;
using OpenFlightDisplay.Persistence;
using OpenFlightDisplay.Providers;
using OpenFlightDisplay.Providers.Replay;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

/// <summary>
/// The monitoring-area editor.
/// </summary>
/// <remarks>
/// Part of <see cref="MainWindow"/>. The window owns nine pages and had grown
/// past two thousand lines in one file, which made it the only genuinely hard
/// place to work in this codebase. Split per feature; no behaviour changed.
/// </remarks>
public sealed partial class MainWindow
{
    // ---- monitoring area ----

    private AreaShape SelectedAreaShape =>
        AreaShapeBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse(tag, out AreaShape shape)
            ? shape
            : AreaShape.Circle;

    private void PopulateAreaForm()
    {
        _suppressSelectionEvents = true;
        try
        {
            MonitoringAreaSetting area = _settings.MonitoringArea;

            foreach (object candidate in AreaShapeBox.Items)
            {
                if (candidate is ComboBoxItem { Tag: string tag }
                    && Enum.TryParse(tag, out AreaShape shape)
                    && shape == area.Shape)
                {
                    AreaShapeBox.SelectedItem = candidate;
                    break;
                }
            }

            AreaShapeBox.SelectedItem ??= AreaShapeBox.Items[0];

            AreaRadiusBox.Text = area.RadiusKm.ToString(CultureInfo.CurrentCulture);
            AreaHeadingBox.Text = area.HeadingDeg.ToString(CultureInfo.CurrentCulture);
            AreaWidthBox.Text = area.WidthDeg.ToString(CultureInfo.CurrentCulture);

            AreaUseHomeCheck.IsChecked = area.CenterLat is null;
            AreaLatBox.Text = area.CenterLat?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            AreaLonBox.Text = area.CenterLon?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

            AreaVerticesBox.Text = string.Join(
                Environment.NewLine,
                area.Vertices.Select(v => string.Create(
                    CultureInfo.CurrentCulture, $"{v.Lat}, {v.Lon}")));

            AreaMinAltBox.Text = area.MinAltitudeFt?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;
            AreaMaxAltBox.Text = area.MaxAltitudeFt?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

            AreaSummary.Text = "Currently monitoring: " + area.Summarise();
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        UpdateAreaPanels();
    }

    private void OnAreaShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        // AreaPolygonPanel is created last on this page; a null means the
        // visual tree is still being built.
        if (AreaPolygonPanel is not null)
        {
            UpdateAreaPanels();
        }
    }

    private void OnAreaCentreToggled(object sender, RoutedEventArgs e) => UpdateAreaPanels();

    /// <summary>Shows only the fields the selected shape actually uses.</summary>
    private void UpdateAreaPanels()
    {
        AreaShape shape = SelectedAreaShape;

        AreaCentrePanel.Visibility = shape == AreaShape.Polygon
            ? Visibility.Collapsed
            : Visibility.Visible;

        AreaConePanel.Visibility = shape == AreaShape.Cone
            ? Visibility.Visible
            : Visibility.Collapsed;

        AreaPolygonPanel.Visibility = shape == AreaShape.Polygon
            ? Visibility.Visible
            : Visibility.Collapsed;

        AreaCentreCoords.Visibility = AreaUseHomeCheck.IsChecked is true
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnSaveArea(object sender, RoutedEventArgs e) => Safe(SaveAreaAsync);

    private async Task SaveAreaAsync()
    {
        if (!TryReadArea(out MonitoringAreaSetting area, out string? error))
        {
            AreaError.Message = error;
            AreaError.IsOpen = true;
            return;
        }

        // Refuses to save an area that cannot be built. Saving one would show an
        // empty sky with no explanation on the next poll.
        if (area.Build(_settings.HomeLatitude, _settings.HomeLongitude) is null)
        {
            AreaError.Message =
                "This area cannot be used yet. A circle or cone centred on home needs a home "
                + "location, which is set on the Settings page.";
            AreaError.IsOpen = true;
            return;
        }

        AreaError.IsOpen = false;

        _settings = _settings with { MonitoringArea = area };

        if (!await _settingsStore.SaveAsync(_settings).ConfigureAwait(true))
        {
            AreaError.Message = "The area could not be saved. It applies to this session only.";
            AreaError.IsOpen = true;
        }

        AreaSummary.Text = "Currently monitoring: " + area.Summarise();
        await RestartFeedAsync().ConfigureAwait(true);
    }

    private bool TryReadArea(out MonitoringAreaSetting area, out string? error)
    {
        area = new MonitoringAreaSetting();
        error = null;

        AreaShape shape = SelectedAreaShape;

        if (!double.TryParse(AreaRadiusBox.Text, CultureInfo.CurrentCulture, out double radiusKm))
        {
            radiusKm = _settings.MonitoringRadiusKm;
        }

        double heading = 0;
        double width = 90;

        if (shape == AreaShape.Cone)
        {
            if (!double.TryParse(AreaHeadingBox.Text, CultureInfo.CurrentCulture, out heading))
            {
                error = "The facing must be a number of degrees.";
                return false;
            }

            if (!double.TryParse(AreaWidthBox.Text, CultureInfo.CurrentCulture, out width))
            {
                error = "The width must be a number of degrees.";
                return false;
            }
        }

        double? centreLat = null;
        double? centreLon = null;

        if (shape != AreaShape.Polygon && AreaUseHomeCheck.IsChecked is not true)
        {
            if (!double.TryParse(AreaLatBox.Text, CultureInfo.CurrentCulture, out double lat)
                || !double.TryParse(AreaLonBox.Text, CultureInfo.CurrentCulture, out double lon))
            {
                error = "Enter a centre latitude and longitude, or tick 'centre on my home location'.";
                return false;
            }

            centreLat = lat;
            centreLon = lon;
        }

        IReadOnlyList<GeoPoint> vertices = [];
        if (shape == AreaShape.Polygon && !TryReadVertices(AreaVerticesBox.Text, out vertices, out error))
        {
            return false;
        }

        if (!TryReadOptionalAltitude(AreaMinAltBox.Text, out double? minAlt))
        {
            error = "The 'above' altitude must be a number in feet, or blank.";
            return false;
        }

        if (!TryReadOptionalAltitude(AreaMaxAltBox.Text, out double? maxAlt))
        {
            error = "The 'below' altitude must be a number in feet, or blank.";
            return false;
        }

        var candidate = new MonitoringAreaSetting
        {
            Shape = shape,
            CenterLat = centreLat,
            CenterLon = centreLon,
            RadiusKm = radiusKm,
            HeadingDeg = heading,
            WidthDeg = width,
            Vertices = vertices,
            MinAltitudeFt = minAlt,
            MaxAltitudeFt = maxAlt,
        };

        if (candidate.Validate() is { } problem)
        {
            error = problem;
            return false;
        }

        area = candidate;
        return true;
    }

    /// <summary>
    /// Parses the polygon outline.
    /// </summary>
    /// <remarks>
    /// Names the offending line rather than reporting a general failure — a
    /// sixty-point outline with one typo is otherwise miserable to correct.
    /// </remarks>
    private static bool TryReadVertices(
        string? text,
        out IReadOnlyList<GeoPoint> vertices,
        out string? error)
    {
        vertices = [];
        error = null;

        var points = new List<GeoPoint>();
        string[] lines = (text ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            string[] parts = lines[i].Split(',', StringSplitOptions.TrimEntries);

            if (parts.Length != 2
                || !double.TryParse(parts[0], CultureInfo.CurrentCulture, out double lat)
                || !double.TryParse(parts[1], CultureInfo.CurrentCulture, out double lon))
            {
                error = $"Line {i + 1} is not a latitude and longitude: \"{lines[i]}\".";
                return false;
            }

            if (lat is < -90 or > 90 || lon is < -180 or > 180)
            {
                error = $"Line {i + 1} is out of range. Latitude is -90 to 90, longitude -180 to 180.";
                return false;
            }

            points.Add(new GeoPoint(lat, lon));
        }

        vertices = points;
        return true;
    }

}
