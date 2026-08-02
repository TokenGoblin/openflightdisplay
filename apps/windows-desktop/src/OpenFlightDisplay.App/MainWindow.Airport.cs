namespace OpenFlightDisplay.App;

using System.Globalization;
using Microsoft.UI.Xaml;
using OpenFlightDisplay.App.Services;
using OpenFlightDisplay.App.ViewModels;
using OpenFlightDisplay.Core.Tracking;
using OpenFlightDisplay.Infrastructure.Tracking;

/// <summary>
/// The airport movements board.
/// </summary>
/// <remarks>
/// Part of <see cref="MainWindow"/>. Shows what is observed to be arriving at
/// and departing from a chosen airport — <b>not</b> a schedule, because ADS-B
/// carries none. The page says so at the top rather than leaving a user to
/// wonder why an airport board has no gate numbers.
/// </remarks>
public sealed partial class MainWindow
{
    private void OnWatchAirport(object sender, RoutedEventArgs e) => Safe(WatchAirportAsync);

    private async Task WatchAirportAsync()
    {
        AirportError.IsOpen = false;
        AirportStatus.Text = "Looking up the airport…";
        AirportList.ItemsSource = null;

        string? problem = await _airportBoard
            .StartAsync(AirportIcaoBox.Text, CancellationToken.None)
            .ConfigureAwait(true);

        if (problem is not null)
        {
            // Reported rather than left as an empty board, which would look
            // exactly like an airport with no traffic.
            AirportError.Message = problem;
            AirportError.IsOpen = true;
            AirportStatus.Text = string.Empty;
            return;
        }

        _settings = _settings with { AirportBoardIcao = _airportBoard.Airport?.Icao };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        AirportStatus.Text = "Watching. The first movements appear within a few seconds.";
    }

    private void OnStopAirport(object sender, RoutedEventArgs e) => Safe(StopAirportAsync);

    private async Task StopAirportAsync()
    {
        await _airportBoard.StopAsync().ConfigureAwait(true);

        _settings = _settings with { AirportBoardIcao = null };
        await _settingsStore.SaveAsync(_settings).ConfigureAwait(true);

        AirportList.ItemsSource = null;
        AirportError.IsOpen = false;
        AirportStatus.Text = "Not watching an airport.";
    }

    /// <summary>Resumes the airport that was being watched when the app closed.</summary>
    private async Task ResumeAirportBoardAsync()
    {
        if (_settings.AirportBoardIcao is not { } icao)
        {
            AirportStatus.Text = "Not watching an airport.";
            return;
        }

        AirportIcaoBox.Text = icao;

        // A failure here is shown but must not stop startup — the rest of the
        // application does not depend on this board.
        string? problem = await _airportBoard
            .StartAsync(icao, CancellationToken.None)
            .ConfigureAwait(true);

        if (problem is not null)
        {
            AirportError.Message = problem;
            AirportError.IsOpen = true;
        }
    }

    /// <summary>Marshals a board update onto the UI thread.</summary>
    private void OnAirportBoardChanged(object? sender, AirportBoardState state)
        => SafeHandler.Post(DispatcherQueue, () => RenderAirportBoard(state), ReportHandlerFailure);

    private void RenderAirportBoard(AirportBoardState state)
    {
        AirportList.ItemsSource = state.Movements
            .Select(m => new AirportMovementViewModel(m, _settings.Units))
            .ToList();

        int arriving = state.Movements.Count(m => m.Kind == MovementKind.Arriving);
        int departing = state.Movements.Count(m => m.Kind == MovementKind.Departing);

        string name = state.Airport.Name ?? state.Airport.Icao;

        AirportStatus.Text = state.Movements.Count == 0
            ? string.Create(
                CultureInfo.CurrentCulture,
                $"{name}: nothing within {AirportMovements.RadiusKm:N0} km is transmitting. " +
                $"Field elevation {state.Airport.ElevationFt:N0} ft.")
            : string.Create(
                CultureInfo.CurrentCulture,
                $"{name}: {arriving} arriving, {departing} departing, " +
                $"{state.Movements.Count} aircraft within {AirportMovements.RadiusKm:N0} km. " +
                $"Field elevation {state.Airport.ElevationFt:N0} ft — heights are above the field.");

        if (state.Issue is { } issue)
        {
            AirportError.Message = $"{issue} The movements shown are the last ones received.";
            AirportError.IsOpen = true;
        }
        else
        {
            AirportError.IsOpen = false;
        }
    }
}
