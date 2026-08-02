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
/// The compact, always-on-top window.
/// </summary>
/// <remarks>
/// Part of <see cref="MainWindow"/>. The window owns nine pages and had grown
/// past two thousand lines in one file, which made it the only genuinely hard
/// place to work in this codebase. Split per feature; no behaviour changed.
/// </remarks>
public sealed partial class MainWindow
{
    // ---- compact mode ----

    /// <summary>Compact window size, in device-independent pixels.</summary>
    private const int CompactWidthDip = 360;

    /// <inheritdoc cref="CompactWidthDip"/>
    private const int CompactHeightDip = 150;

    private bool _isCompact;
    private Windows.Graphics.RectInt32 _restoreBounds;

    private void OnToggleCompact(object sender, RoutedEventArgs e)
    {
        if (_isCompact)
        {
            ExitCompactMode();
        }
        else
        {
            EnterCompactMode();
        }
    }

    /// <summary>
    /// Shrinks to a small always-on-top window showing only the nearest aircraft.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="Microsoft.UI.Windowing.AppWindow.MoveAndResize"/> takes
    /// physical pixels, not DIPs.</b> Passing a DIP size straight in produces a
    /// window a third too small on a 150% display and two-thirds too small at
    /// 200%, so the size is scaled by the XAML root's rasterisation scale. This
    /// is the same units confusion that produced a phantom DPI defect in this
    /// project; see the DPI section of docs/WINDOWS_DESKTOP.md.
    /// </para>
    /// <para>
    /// The previous bounds are captured so leaving compact mode restores the
    /// window rather than guessing a size.
    /// </para>
    /// </remarks>
    private void EnterCompactMode()
    {
        Microsoft.UI.Windowing.AppWindow appWindow = AppWindow;
        _restoreBounds = new Windows.Graphics.RectInt32(
            appWindow.Position.X, appWindow.Position.Y,
            appWindow.Size.Width, appWindow.Size.Height);

        double scale = Content.XamlRoot?.RasterizationScale ?? 1.0;

        // The banner and attribution bar are hidden so the panel gets the whole
        // window. Leaving them took roughly half of a 150 DIP window and clipped
        // the content it exists to show. The compact panel carries the status
        // and the provider name itself, so nothing is lost.
        Nav.IsPaneVisible = false;
        StatusBanner.Visibility = Visibility.Collapsed;
        AttributionBar.Visibility = Visibility.Collapsed;
        CompactPage.Visibility = Visibility.Visible;
        HideAllPagesExceptCompact();

        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
        }

        appWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(CompactWidthDip * scale),
            (int)(CompactHeightDip * scale)));

        _isCompact = true;
        CompactButton.Content = "Expand";
        RefreshCompact();
    }

    private void ExitCompactMode()
    {
        Microsoft.UI.Windowing.AppWindow appWindow = AppWindow;

        if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = false;
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
        }

        // Restores the exact bounds captured on the way in, including position:
        // a compact window is usually dragged to a corner, and expanding in
        // place would leave most of the window off-screen.
        if (_restoreBounds.Width > 0 && _restoreBounds.Height > 0)
        {
            appWindow.MoveAndResize(_restoreBounds);
        }

        Nav.IsPaneVisible = true;
        StatusBanner.Visibility = Visibility.Visible;
        AttributionBar.Visibility = Visibility.Visible;
        CompactPage.Visibility = Visibility.Collapsed;
        _isCompact = false;
        CompactButton.Content = "Compact";

        // Re-runs the normal navigation logic so the previously selected page
        // comes back, rather than leaving every page collapsed.
        OnNavSelectionChanged(Nav, null!);
    }

    private void HideAllPagesExceptCompact()
    {
        RadarPage.Visibility = Visibility.Collapsed;
        BoardPage.Visibility = Visibility.Collapsed;
        SourcesPage.Visibility = Visibility.Collapsed;
        AlertsPage.Visibility = Visibility.Collapsed;
        TrackPage.Visibility = Visibility.Collapsed;
        HistoryPage.Visibility = Visibility.Collapsed;
        DiagnosticsPage.Visibility = Visibility.Collapsed;
        AreasPage.Visibility = Visibility.Collapsed;
        SettingsPage.Visibility = Visibility.Collapsed;
        NotBuiltPage.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Updates the compact readout.
    /// </summary>
    /// <remarks>
    /// Shows the nearest aircraft, and the tracked flight's departure advice
    /// when there is one — the two things worth a window that sits on top of
    /// everything else.
    /// </remarks>
    private void RefreshCompact()
    {
        if (!_isCompact)
        {
            return;
        }

        AircraftRowViewModel? nearest = ViewModel.Aircraft.FirstOrDefault();

        if (nearest is null)
        {
            CompactHeadline.Text = ViewModel.StatusHeadline;
            CompactDetail.Text = ViewModel.StatusDetail;
        }
        else
        {
            CompactHeadline.Text = nearest.Callsign;
            CompactDetail.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"{nearest.Distance} Â· {nearest.Altitude} Â· {ViewModel.Aircraft.Count:N0} in range");
        }

        if (_tracking.CurrentState is { } tracked
            && tracked.Departure.Advice != DepartureAdvice.Unknown)
        {
            CompactTracked.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"{tracked.Callsign}: {FlightTracking.AdviceWord(tracked.Departure.Advice)} " +
                $"(ETA {FlightTracking.FormatMinutesRemaining(tracked.Progress.MinutesRemaining)} min)");
            CompactTracked.Visibility = Visibility.Visible;
        }
        else
        {
            CompactTracked.Visibility = Visibility.Collapsed;
        }
    }

}
