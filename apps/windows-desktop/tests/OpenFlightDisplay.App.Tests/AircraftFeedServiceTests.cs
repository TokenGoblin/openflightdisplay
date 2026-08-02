namespace OpenFlightDisplay.App.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using OpenFlightDisplay.App.Services;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Alerts;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Feed;
using OpenFlightDisplay.Core.Ranking;
using OpenFlightDisplay.Providers;
using Xunit;

/// <summary>
/// The data pipeline: poll, filter, rank, record, evaluate alerts, publish.
/// </summary>
/// <remarks>
/// This is the busiest code in the application and had no tests at all. Every
/// case here is a state the UI renders differently, so getting one wrong shows
/// the user something untrue rather than throwing.
/// </remarks>
public class AircraftFeedServiceTests
{
    private static readonly CircleArea Area = new(47.6062, -122.3321, RadiusKm: 100);
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_successful_poll_publishes_live_aircraft()
    {
        var provider = new FakeProvider(new ProviderResult.Success([Near("abc123")], Now));
        await using var feed = new AircraftFeedService(NullLogger<AircraftFeedService>.Instance);

        FeedState state = await PollOnceAsync(feed, provider);

        var live = Assert.IsType<FeedState.Live>(state);
        Assert.Single(live.KnownAircraft);
    }

    [Fact]
    public async Task An_empty_sky_is_a_correct_answer_not_a_failure()
    {
        // Reporting this as an outage would send a user hunting a network
        // problem that does not exist.
        var provider = new FakeProvider(new ProviderResult.Success([], Now));
        await using var feed = new AircraftFeedService(NullLogger<AircraftFeedService>.Instance);

        Assert.IsType<FeedState.NoMatchingAircraft>(await PollOnceAsync(feed, provider));
    }

    [Fact]
    public async Task Aircraft_outside_the_area_are_dropped()
    {
        // Near Sydney: well outside a 100 km circle around Seattle.
        var provider = new FakeProvider(
            new ProviderResult.Success([Aircraft("abc123", -33.86, 151.2)], Now));

        await using var feed = new AircraftFeedService(NullLogger<AircraftFeedService>.Instance);

        Assert.IsType<FeedState.NoMatchingAircraft>(await PollOnceAsync(feed, provider));
    }

    [Fact]
    public async Task A_provider_failure_is_published_as_unavailable()
    {
        var provider = new FakeProvider(
            new ProviderResult.Failure(FeedFailure.NetworkUnavailable, "no network"));

        await using var feed = new AircraftFeedService(NullLogger<AircraftFeedService>.Instance);

        var unavailable = Assert.IsType<FeedState.SourceUnavailable>(
            await PollOnceAsync(feed, provider));

        Assert.Equal(FeedFailure.NetworkUnavailable, unavailable.Failure);
    }

    [Fact]
    public async Task An_exhausted_replay_is_its_own_state()
    {
        // Distinct from an empty sky: one means the recording ended, the other
        // means nothing was flying. The UI says different things.
        var provider = new FakeProvider(new ProviderResult.Exhausted("session-1"));
        await using var feed = new AircraftFeedService(NullLogger<AircraftFeedService>.Instance);

        Assert.IsType<FeedState.ReplayComplete>(await PollOnceAsync(feed, provider));
    }

    [Fact]
    public async Task The_filter_is_applied_before_publishing()
    {
        var provider = new FakeProvider(new ProviderResult.Success(
            [Near("abc123", altitudeFt: 3000), Near("def456", altitudeFt: 30000)], Now));

        await using var feed = new AircraftFeedService(NullLogger<AircraftFeedService>.Instance)
        {
            Filter = new AircraftFilter { MinAltitudeFt = 10000 },
        };

        var live = Assert.IsType<FeedState.Live>(await PollOnceAsync(feed, provider));

        Assert.Equal("def456", Assert.Single(live.KnownAircraft).IcaoHex);
    }

    [Fact]
    public async Task Only_what_survives_filtering_is_recorded()
    {
        // History should hold what the user was shown, not everything the
        // provider happened to return.
        var provider = new FakeProvider(new ProviderResult.Success(
            [Near("abc123", altitudeFt: 3000), Near("def456", altitudeFt: 30000)], Now));

        var recorder = new CountingRecorder();
        await using var feed = new AircraftFeedService(NullLogger<AircraftFeedService>.Instance)
        {
            Filter = new AircraftFilter { MinAltitudeFt = 10000 },
            Recorder = recorder,
        };

        await PollOnceAsync(feed, provider);

        Assert.Equal(1, recorder.TotalObservations);
    }

    [Fact]
    public async Task Ranking_mode_reaches_the_published_order()
    {
        var provider = new FakeProvider(new ProviderResult.Success(
            [Near("high", altitudeFt: 30000), Near("low", altitudeFt: 3000)], Now));

        await using var feed = new AircraftFeedService(NullLogger<AircraftFeedService>.Instance);

        FeedState state = await PollOnceAsync(
            feed, provider, RankingMode.LowestAltitude);

        var live = Assert.IsType<FeedState.Live>(state);
        Assert.Equal("low", live.KnownAircraft[0].IcaoHex);
    }

    [Fact]
    public async Task An_alert_rule_fires_and_is_delivered()
    {
        var provider = new FakeProvider(new ProviderResult.Success(
            [Near("abc123") with { EmergencyState = EmergencyState.General }], Now));

        var notifier = new CountingNotifier();
        await using var feed = new AircraftFeedService(NullLogger<AircraftFeedService>.Instance)
        {
            Notifier = notifier,
            AlertRules =
            [
                new AlertRule
                {
                    Id = "r1",
                    Name = "Emergency",
                    Trigger = AlertTrigger.EmergencySquawk,
                    Channels = AlertChannels.InApp | AlertChannels.Toast,
                },
            ],
        };

        await PollOnceAsync(feed, provider);

        Assert.Equal(1, notifier.Count);
        Assert.Single(feed.Alerts.History);
    }

    [Fact]
    public async Task A_recorder_that_drops_does_not_stop_the_feed()
    {
        // History is best-effort by design; the display is not.
        var provider = new FakeProvider(new ProviderResult.Success([Near("abc123")], Now));

        await using var feed = new AircraftFeedService(NullLogger<AircraftFeedService>.Instance)
        {
            Recorder = new RefusingRecorder(),
        };

        Assert.IsType<FeedState.Live>(await PollOnceAsync(feed, provider));
    }

    [Fact]
    public async Task Starting_twice_does_not_leave_two_loops_running()
    {
        // A second loop would double the request rate against a free service.
        var provider = new FakeProvider(new ProviderResult.Success([Near("abc123")], Now));
        await using var feed = new AircraftFeedService(NullLogger<AircraftFeedService>.Instance);

        await feed.StartAsync(provider, Area, 47.6062, -122.3321);
        await feed.StartAsync(provider, Area, 47.6062, -122.3321);
        await WaitForStateAsync(feed);
        await feed.StopAsync();

        int afterStop = provider.Calls;
        await Task.Delay(300);

        Assert.Equal(afterStop, provider.Calls);
    }

    /// <summary>Runs the feed until it publishes a terminal state, then stops it.</summary>
    private static async Task<FeedState> PollOnceAsync(
        AircraftFeedService feed,
        IAviationDataProvider provider,
        RankingMode ranking = RankingMode.NearestHorizontal)
    {
        await feed.StartAsync(provider, Area, 47.6062, -122.3321, ranking);
        FeedState state = await WaitForStateAsync(feed);
        await feed.StopAsync();
        return state;
    }

    private static async Task<FeedState> WaitForStateAsync(AircraftFeedService feed)
    {
        // The loop publishes Connecting first; wait for what comes after it.
        for (int i = 0; i < 100; i++)
        {
            if (feed.CurrentState is not FeedState.Connecting and not FeedState.NeedsConfiguration)
            {
                return feed.CurrentState;
            }

            await Task.Delay(20);
        }

        return feed.CurrentState;
    }

    private static AircraftState Near(string hex, double? altitudeFt = 30000)
        => Aircraft(hex, 47.61, -122.33, altitudeFt);

    private static AircraftState Aircraft(
        string hex, double lat, double lon, double? altitudeFt = 30000) => new()
        {
            Provider = "test",
            IcaoHex = hex,
            Callsign = "TST" + hex[..3].ToUpperInvariant(),
            Latitude = lat,
            Longitude = lon,
            GeometricAltitudeFt = altitudeFt,
            GroundSpeedKt = 420,
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
            PositionTimestamp = DateTimeOffset.UtcNow,
        };

    private sealed class FakeProvider : IAviationDataProvider
    {
        private readonly ProviderResult _result;

        public FakeProvider(ProviderResult result) => _result = result;

        public int Calls { get; private set; }

        public string Id => "fake";

        public string DisplayName => "Fake";

        public bool RequiresApiKey => false;

        public TimeSpan RecommendedPollInterval => TimeSpan.FromMilliseconds(50);

        public Task<ProviderResult> FetchAircraftAsync(
            MonitoringArea area, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class CountingRecorder : IObservationRecorder
    {
        public int TotalObservations { get; private set; }

        public bool Record(IReadOnlyList<AircraftState> aircraft)
        {
            TotalObservations += aircraft.Count;
            return true;
        }
    }

    private sealed class RefusingRecorder : IObservationRecorder
    {
        public bool Record(IReadOnlyList<AircraftState> aircraft) => false;
    }

    private sealed class CountingNotifier : IAlertNotifier
    {
        public int Count { get; private set; }

        public void Notify(AlertEvent alertEvent) => Count++;
    }
}
