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
/// Postcode and place-name lookup for the home location.
/// </summary>
/// <remarks>
/// Part of <see cref="MainWindow"/>. The window owns nine pages and had grown
/// past two thousand lines in one file, which made it the only genuinely hard
/// place to work in this codebase. Split per feature; no behaviour changed.
/// </remarks>
public sealed partial class MainWindow
{
    // ---- place search ----

    /// <summary>Results currently offered, so a choice can be mapped back to coordinates.</summary>
    private IReadOnlyList<Place> _placeResults = [];

    private void OnPlaceSearchKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        // Enter searches, because a search box that ignores Enter is annoying.
        // Deliberately NOT search-as-you-type: Nominatim's usage policy forbids
        // autocomplete, and every keystroke would be a request.
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            OnSearchPlace(sender, new RoutedEventArgs());
        }
    }

    private async void OnSearchPlace(object sender, RoutedEventArgs e)
    {
        // Disabled while running so a second press cannot queue behind the
        // one-per-second limit and make the app look frozen.
        PlaceSearchButton.IsEnabled = false;
        PlaceSearchStatus.Text = "Searching…";
        PlaceResults.Visibility = Visibility.Collapsed;

        try
        {
            PlaceSearchResult result = await _services
                .GetRequiredService<PlaceSearch>()
                .SearchAsync(PlaceSearchBox.Text, CancellationToken.None)
                .ConfigureAwait(true);

            switch (result)
            {
                case PlaceSearchResult.Found found:
                    _placeResults = found.Places;

                    PlaceResults.ItemsSource = found.Places
                        .Select(p => p.ShortName)
                        .ToList();

                    PlaceResults.Visibility = Visibility.Visible;

                    PlaceSearchStatus.Text = found.Places.Count == 1
                        ? "One match. Select it to fill in the coordinates."
                        : string.Create(
                            CultureInfo.CurrentCulture,
                            $"{found.Places.Count} matches. Select the right one.");
                    break;

                case PlaceSearchResult.NoMatches noMatches:
                    _placeResults = [];
                    PlaceSearchStatus.Text =
                        $"Nothing found for \"{noMatches.Query}\". Check the spelling, or "
                        + "type the coordinates directly.";
                    break;

                case PlaceSearchResult.Failure failure:
                    _placeResults = [];
                    PlaceSearchStatus.Text = failure.Detail;
                    break;

                default:
                    break;
            }
        }
#pragma warning disable CA1031 // A failed search must not take the app down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            PlaceSearchStatus.Text = $"The search failed: {ex.Message}";
        }
        finally
        {
            PlaceSearchButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Fills the coordinate boxes from the chosen result.
    /// </summary>
    /// <remarks>
    /// Deliberately does <b>not</b> save or restart the feed. The coordinates
    /// land in the form and the user still presses Save, so a mis-picked result
    /// is corrected before it takes effect rather than after.
    /// </remarks>
    private void OnPlaceChosen(object sender, SelectionChangedEventArgs e)
    {
        if (PlaceResults.SelectedIndex < 0 || PlaceResults.SelectedIndex >= _placeResults.Count)
        {
            return;
        }

        Place place = _placeResults[PlaceResults.SelectedIndex];

        LatBox.Text = place.Latitude.ToString("F5", CultureInfo.CurrentCulture);
        LonBox.Text = place.Longitude.ToString("F5", CultureInfo.CurrentCulture);

        PlaceSearchStatus.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"Filled in {place.ShortName} ({place.Latitude:F4}, {place.Longitude:F4}). " +
            $"Press Save and restart feed to apply it.");
    }

}
