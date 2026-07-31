namespace OpenFlightDisplay.Infrastructure.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using OpenFlightDisplay.Core.Settings;
using OpenFlightDisplay.Core.Units;
using OpenFlightDisplay.Infrastructure.Settings;
using Xunit;

/// <summary>
/// Settings persistence. The load path must never throw — corrupt settings must
/// not be able to stop the application starting.
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public SettingsStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"ofd-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Test cleanup only.
        }
    }

    [Fact]
    public async Task Loading_a_missing_file_yields_defaults_rather_than_throwing()
    {
        AppSettings settings = await Store().LoadAsync(CancellationToken.None);

        Assert.False(settings.OnboardingCompleted);
        Assert.Equal(DataMode.Mock, settings.DataMode);
    }

    [Fact]
    public async Task Settings_round_trip_through_disk()
    {
        var original = new AppSettings
        {
            OnboardingCompleted = true,
            HomeLatitude = 47.6062,
            HomeLongitude = -122.3321,
            MonitoringRadiusKm = 120.0,
            DataMode = DataMode.DirectProvider,
            ProviderId = "adsblol",
            Units = UnitSystem.Metric,
            HistoryEnabled = true,
        };

        SettingsStore store = Store();
        Assert.True(await store.SaveAsync(original, CancellationToken.None));

        AppSettings loaded = await store.LoadAsync(CancellationToken.None);

        Assert.Equal(original, loaded);
    }

    [Fact]
    public async Task A_corrupt_file_falls_back_to_defaults_instead_of_throwing()
    {
        // Corrupt settings must not stop the app starting - the user can always
        // reconfigure, but only if they can get in.
        await File.WriteAllTextAsync(_path, "{ this is not json", TestToken);

        AppSettings settings = await Store().LoadAsync(CancellationToken.None);

        Assert.Equal(new AppSettings(), settings);
    }

    [Fact]
    public async Task A_file_containing_json_null_falls_back_to_defaults()
    {
        await File.WriteAllTextAsync(_path, "null", TestToken);

        AppSettings settings = await Store().LoadAsync(CancellationToken.None);

        Assert.Equal(new AppSettings(), settings);
    }

    [Fact]
    public async Task Settings_from_a_newer_schema_are_refused_rather_than_guessed_at()
    {
        // Same rule the wire protocol applies to an unrecognised schemaVersion:
        // reject, do not best-effort parse.
        await File.WriteAllTextAsync(_path, """{ "SchemaVersion": 99, "MonitoringRadiusKm": 5 }""", TestToken);

        AppSettings settings = await Store().LoadAsync(CancellationToken.None);

        Assert.Equal(80.0, settings.MonitoringRadiusKm);
    }

    [Fact]
    public async Task Saving_creates_the_directory_when_it_does_not_exist()
    {
        string nested = Path.Combine(_directory, "a", "b", "settings.json");
        var store = new SettingsStore(nested, NullLogger<SettingsStore>.Instance);

        Assert.True(await store.SaveAsync(new AppSettings(), CancellationToken.None));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public async Task A_failed_save_leaves_the_previous_settings_intact()
    {
        SettingsStore store = Store();
        var good = new AppSettings { MonitoringRadiusKm = 42.0, OnboardingCompleted = true };
        Assert.True(await store.SaveAsync(good, CancellationToken.None));

        // Hold the destination open exclusively so the atomic move cannot land.
        bool saved;
        using (var _ = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            saved = await store.SaveAsync(
                new AppSettings { MonitoringRadiusKm = 999.0 }, CancellationToken.None);
        }

        Assert.False(saved);

        // The point of the atomic write: the old file is still the old file.
        AppSettings reloaded = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(42.0, reloaded.MonitoringRadiusKm);
        Assert.True(reloaded.OnboardingCompleted);
    }

    [Fact]
    public async Task A_failed_save_does_not_leave_temp_files_behind()
    {
        SettingsStore store = Store();
        Assert.True(await store.SaveAsync(new AppSettings(), CancellationToken.None));

        using (var _ = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await store.SaveAsync(new AppSettings(), CancellationToken.None);
        }

        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Concurrent_saves_all_complete_and_leave_valid_settings()
    {
        SettingsStore store = Store();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            store.SaveAsync(new AppSettings { MonitoringRadiusKm = 10.0 + i }, CancellationToken.None)));

        // Whichever won, the file must be readable and one of the written values.
        AppSettings loaded = await store.LoadAsync(CancellationToken.None);
        Assert.InRange(loaded.MonitoringRadiusKm, 10.0, 29.0);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Mock_mode_is_usable_before_a_location_is_configured()
    {
        // Otherwise first run would be a locked door: no location means no feed,
        // and no feed means nothing to look at while configuring.
        Assert.True(new AppSettings { DataMode = DataMode.Mock }.IsUsable);
    }

    [Fact]
    public void Direct_provider_mode_is_not_usable_without_a_location()
        => Assert.False(new AppSettings { DataMode = DataMode.DirectProvider }.IsUsable);

    [Fact]
    public void Direct_provider_mode_is_usable_once_a_location_exists()
    {
        var settings = new AppSettings
        {
            DataMode = DataMode.DirectProvider,
            HomeLatitude = 47.6,
            HomeLongitude = -122.3,
        };

        Assert.True(settings.IsUsable);
    }

    [Fact]
    public void History_and_notifications_are_off_by_default()
    {
        // Both are opt-in. History in particular turns a live display into a
        // record of everything that flew over someone's house.
        var defaults = new AppSettings();

        Assert.False(defaults.HistoryEnabled);
        Assert.False(defaults.NotificationsEnabled);
        Assert.False(defaults.BackgroundMonitoringEnabled);
    }

    [Fact]
    public void A_missing_home_location_is_null_rather_than_zero()
    {
        // 0,0 is the Gulf of Guinea, a real coordinate. Defaulting there would
        // show an empty sky instead of prompting for a location.
        var defaults = new AppSettings();

        Assert.Null(defaults.HomeLatitude);
        Assert.Null(defaults.HomeLongitude);
    }

    private static CancellationToken TestToken => CancellationToken.None;

    private SettingsStore Store() => new(_path, NullLogger<SettingsStore>.Instance);
}
