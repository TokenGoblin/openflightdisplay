namespace OpenFlightDisplay.App.Dialogs;

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenFlightDisplay.App.Services;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Settings;
using OpenFlightDisplay.Core.Units;
using OpenFlightDisplay.Providers;

/// <summary>
/// First-run setup: privacy, data source, location, units, optional features,
/// and a real connection test.
/// </summary>
/// <remarks>
/// <para>
/// The connection test actually polls the chosen provider rather than
/// pretending to. A setup wizard that reports success without checking is worse
/// than no wizard, because it converts a configuration problem into a
/// mysterious empty screen later.
/// </para>
/// <para>
/// Skipping is allowed and lands on working mock data. First run must not be a
/// locked door.
/// </para>
/// </remarks>
public sealed partial class OnboardingDialog : ContentDialog
{
    private readonly ProviderRegistry _providers;
    private readonly AppSettings _initial;

    private int _step;

    public OnboardingDialog(ProviderRegistry providers, AppSettings initial)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(initial);

        InitializeComponent();

        _providers = providers;
        _initial = initial;

        RadiusBox.Text = initial.MonitoringRadiusKm.ToString(CultureInfo.CurrentCulture);
        ShowStep(0);
    }

    /// <summary>Settings chosen during setup. Only valid once the dialog completes.</summary>
    public AppSettings Result { get; private set; } = new();

    /// <summary>True if the user finished setup rather than skipping it.</summary>
    public bool Completed { get; private set; }

    private StackPanel[] Steps =>
        [StepPrivacy, StepSource, StepLocation, StepUnits, StepFeatures, StepTest];

    private bool IsLastStep => _step == Steps.Length - 1;

    private void ShowStep(int index)
    {
        _step = Math.Clamp(index, 0, Steps.Length - 1);

        for (int i = 0; i < Steps.Length; i++)
        {
            Steps[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;
        }

        IsSecondaryButtonEnabled = _step > 0;
        PrimaryButtonText = IsLastStep ? "Finish" : "Next";
    }

    private void OnBack(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Cancel the default close so the dialog stays open and just moves back.
        args.Cancel = true;
        ShowStep(_step - 1);
    }

    private async void OnNext(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (IsLastStep)
        {
            Result = BuildSettings() with { OnboardingCompleted = true };
            Completed = true;
            return;
        }

        args.Cancel = true;

        // Validate the location before leaving that step, so an error is shown
        // next to the field that caused it rather than at the end.
        if (Steps[_step] == StepLocation && !ValidateLocation())
        {
            return;
        }

        ShowStep(_step + 1);

        if (Steps[_step] == StepTest)
        {
            await RunConnectionTestAsync().ConfigureAwait(true);
        }
    }

    private async void OnTestAgain(object sender, RoutedEventArgs e)
        => await RunConnectionTestAsync().ConfigureAwait(true);

    /// <summary>
    /// Polls the selected provider once and reports what actually happened.
    /// </summary>
    private async Task RunConnectionTestAsync()
    {
        TestButton.IsEnabled = false;
        TestRing.IsActive = true;
        TestResult.Text = "Contacting the data source…";
        TestDetail.Text = string.Empty;

        try
        {
            AppSettings candidate = BuildSettings();
            IAviationDataProvider provider = _providers.Resolve(candidate.DataMode);

            double lat = candidate.HomeLatitude ?? 0;
            double lon = candidate.HomeLongitude ?? 0;
            var area = new CircleArea(lat, lon, candidate.MonitoringRadiusKm);

            // Bounded so a hung provider cannot leave setup spinning forever.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            ProviderResult result = await provider
                .FetchAircraftAsync(area, cts.Token)
                .ConfigureAwait(true);

            switch (result)
            {
                case ProviderResult.Success success when success.Aircraft.Count > 0:
                    TestResult.Text = $"Working — {success.Aircraft.Count} aircraft returned.";
                    TestDetail.Text = $"Source: {provider.DisplayName}.";
                    break;

                case ProviderResult.Success:
                    // An empty sky is a successful answer, not a failure.
                    TestResult.Text = "Working — the source answered, with no aircraft in range.";
                    TestDetail.Text =
                        "That is a normal answer. Try a larger radius if you expected traffic.";
                    break;

                case ProviderResult.Exhausted:
                    TestResult.Text = "No recording loaded.";
                    TestDetail.Text = "Replay has nothing to play yet.";
                    break;

                case ProviderResult.Failure failure:
                    TestResult.Text = $"Could not reach the data source ({failure.Kind}).";
                    TestDetail.Text =
                        $"{failure.Detail}. You can finish setup anyway and fix this in Settings.";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            TestResult.Text = "The data source did not respond in time.";
            TestDetail.Text = "You can finish setup anyway and try again later.";
        }
        catch (NotSupportedException ex)
        {
            TestResult.Text = "That data source is not implemented yet.";
            TestDetail.Text = ex.Message;
        }
        finally
        {
            TestRing.IsActive = false;
            TestButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Validates the location fields, rejecting rather than clamping.
    /// </summary>
    /// <remarks>
    /// A blank location is allowed and leaves the app on mock data — but if the
    /// user picked live data, a location is required, because a live provider
    /// with no coordinates has nothing to ask about.
    /// </remarks>
    private bool ValidateLocation()
    {
        LocationError.Visibility = Visibility.Collapsed;

        bool latBlank = string.IsNullOrWhiteSpace(LatBox.Text);
        bool lonBlank = string.IsNullOrWhiteSpace(LonBox.Text);

        if (!double.TryParse(RadiusBox.Text, CultureInfo.CurrentCulture, out double radius)
            || radius is < 0.5 or > 500)
        {
            return Fail("Monitoring radius must be between 0.5 and 500 km.");
        }

        if (latBlank && lonBlank)
        {
            return !ModeLive.IsChecked!.Value
                || Fail("Live data needs a location. Enter one, or go back and choose mock data.");
        }

        if (latBlank || lonBlank)
        {
            return Fail("Enter both a latitude and a longitude, or leave both blank.");
        }

        if (!double.TryParse(LatBox.Text, CultureInfo.CurrentCulture, out double lat)
            || lat is < -90 or > 90)
        {
            return Fail("Latitude must be a number between -90 and 90.");
        }

        if (!double.TryParse(LonBox.Text, CultureInfo.CurrentCulture, out double lon)
            || lon is < -180 or > 180)
        {
            return Fail("Longitude must be a number between -180 and 180.");
        }

        return true;

        bool Fail(string message)
        {
            LocationError.Text = message;
            LocationError.Visibility = Visibility.Visible;
            return false;
        }
    }

    private AppSettings BuildSettings()
    {
        double? lat = double.TryParse(LatBox.Text, CultureInfo.CurrentCulture, out double parsedLat)
            ? parsedLat
            : null;

        double? lon = double.TryParse(LonBox.Text, CultureInfo.CurrentCulture, out double parsedLon)
            ? parsedLon
            : null;

        double radius = double.TryParse(RadiusBox.Text, CultureInfo.CurrentCulture, out double parsedRadius)
            ? parsedRadius
            : _initial.MonitoringRadiusKm;

        UnitSystem units = true switch
        {
            _ when UnitsMetric.IsChecked is true => UnitSystem.Metric,
            _ when UnitsImperial.IsChecked is true => UnitSystem.Imperial,
            _ => UnitSystem.Aviation,
        };

        DataMode mode = ModeLive.IsChecked is true ? DataMode.DirectProvider : DataMode.Mock;

        return _initial with
        {
            DataMode = mode,
            ProviderId = mode == DataMode.DirectProvider ? "adsblol" : "mock",
            HomeLatitude = lat,
            HomeLongitude = lon,
            MonitoringRadiusKm = radius,
            Units = units,
            HistoryEnabled = HistoryCheck.IsChecked is true,
            NotificationsEnabled = NotificationsCheck.IsChecked is true,
        };
    }
}
