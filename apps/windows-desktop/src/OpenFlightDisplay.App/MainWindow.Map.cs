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
/// The OpenStreetMap backdrop and its tile cache.
/// </summary>
/// <remarks>
/// Part of <see cref="MainWindow"/>. The window owns nine pages and had grown
/// past two thousand lines in one file, which made it the only genuinely hard
/// place to work in this codebase. Split per feature; no behaviour changed.
/// </remarks>
public sealed partial class MainWindow
{
    // ---- map backdrop ----

    /// <summary>
    /// Turns the backdrop on or off and applies it immediately.
    /// </summary>
    /// <remarks>
    /// Attribution is switched with it. OpenStreetMap's licence requires credit
    /// wherever its imagery appears, so the two are set together rather than the
    /// notice being a static piece of layout somebody could later "tidy" away
    /// while leaving the map.
    /// </remarks>
    private async void OnMapToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSelectionEvents)
        {
            return;
        }

        _settings = _settings with { MapOverlayEnabled = MapCheck.IsChecked is true };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        ApplyMapSetting();
        UpdateMapCacheStatus();
    }

    private void ApplyMapSetting()
    {
        Radar.Tiles = _settings.MapOverlayEnabled
            ? _services.GetRequiredService<MapTileCache>()
            : null;

        ViewModel.MapAttributionVisible = _settings.MapOverlayEnabled;

        // Forces the plot to rebuild so the backdrop appears or disappears
        // without waiting for the next poll.
        Radar.RedrawNow();
    }

    private void UpdateMapCacheStatus()
    {
        if (!_settings.MapOverlayEnabled)
        {
            MapCacheStatus.Text = "The map is off. No tiles are being requested.";
            return;
        }

        var cache = _services.GetRequiredService<MapTileCache>();

        MapCacheStatus.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"Tiles are cached in {MapTileCache.DefaultCacheDirectory} " +
            $"({cache.CacheBytes() / 1024.0 / 1024.0:N1} MB) and reused for " +
            $"{MapTileCache.MaxCacheAge.TotalDays:N0} days.");
    }

    private void OnClearMapCache(object sender, RoutedEventArgs e)
    {
        long bytes = _services.GetRequiredService<MapTileCache>().Clear();

        MapCacheStatus.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"Cleared {bytes / 1024.0 / 1024.0:N1} MB of cached tiles.");

        Radar.RedrawNow();
    }

}
