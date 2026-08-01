namespace OpenFlightDisplay.Infrastructure.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Feed;
using OpenFlightDisplay.Core.Tracking;
using OpenFlightDisplay.Infrastructure.Tracking;
using OpenFlightDisplay.Providers;
using OpenFlightDisplay.Providers.AdsbLol;
using Xunit;

/// <summary>
/// The tracking loop around <see cref="FlightTracking"/>. The domain judgement
/// is tested in Core; what matters here is what the loop remembers between
/// polls, and that the states a traveller could act on wrongly stay distinct.
/// </summary>
public class FlightTrackingServiceTests
{
    private static readonly Airport Seatac =
        new("KSEA", 47.4502, -122.3088, ElevationFt: 433, Name: "Seattle-Tacoma");

    [Fact]
    public async Task Before_first_contact_the_flight_is_awaiting_not_lost()
    {
        // Normal before pushback, and also what a wrong flight number looks
        // like. Reporting it as lost would suggest something went wrong.
        var gateway = new FakeGateway { Airport = new AirportLookupResult.Resolved(Seatac) };
        await using var service = Service(gateway, out _);

        service.Configure(Request());
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(FlightPhase.AwaitingContact, service.CurrentState!.Progress.Phase);
        Assert.Null(service.CurrentState.Aircraft);
    }

    [Fact]
    public async Task A_seen_flight_becomes_enroute_with_an_eta()
    {
        var gateway = new FakeGateway { Airport = new AirportLookupResult.Resolved(Seatac) };

        // 200 km out at 400 kt: about 16 minutes.
        gateway.Aircraft = Aircraft(49.25, -122.3088, altitudeFt: 30000, groundSpeedKt: 400);

        await using var service = Service(gateway, out _);
        service.Configure(Request());
        await service.PollOnceAsync(CancellationToken.None);

        TrackedFlightState state = service.CurrentState!;
        Assert.Equal(FlightPhase.Enroute, state.Progress.Phase);
        Assert.NotNull(state.Progress.MinutesRemaining);
        Assert.NotNull(state.Aircraft);
    }

    [Fact]
    public async Task An_empty_answer_keeps_the_last_known_position()
    {
        // A coverage gap is not a reason to blank the display. The staleness
        // clock is what escalates, not the absence of one response.
        var gateway = new FakeGateway { Airport = new AirportLookupResult.Resolved(Seatac) };
        gateway.Aircraft = Aircraft(49.25, -122.3088, altitudeFt: 30000, groundSpeedKt: 400);

        await using var service = Service(gateway, out _);
        service.Configure(Request());
        await service.PollOnceAsync(CancellationToken.None);

        gateway.Aircraft = null;
        await service.PollOnceAsync(CancellationToken.None);

        Assert.NotNull(service.CurrentState!.Aircraft);
        Assert.True(service.CurrentState.Progress.SecondsSinceContact >= 0);
    }

    [Fact]
    public async Task A_poll_failure_is_reported_without_discarding_the_position()
    {
        var gateway = new FakeGateway { Airport = new AirportLookupResult.Resolved(Seatac) };
        gateway.Aircraft = Aircraft(49.25, -122.3088, altitudeFt: 30000, groundSpeedKt: 400);

        await using var service = Service(gateway, out _);
        service.Configure(Request());
        await service.PollOnceAsync(CancellationToken.None);

        gateway.Result = new ProviderResult.Failure(
            FeedFailure.NetworkUnavailable, "the network went away");

        await service.PollOnceAsync(CancellationToken.None);

        Assert.NotNull(service.CurrentState!.Aircraft);
        Assert.Contains("network", service.CurrentState.FeedIssue!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_destination_that_is_not_recognised_is_reported_and_not_retried()
    {
        // A code the service does not know will not start being known, so
        // retrying it every ten seconds only wastes a free service's capacity.
        var gateway = new FakeGateway { Airport = new AirportLookupResult.NotFound("ZZZZ") };

        await using var service = Service(gateway, out _);
        service.Configure(Request(destination: "ZZZZ"));

        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(1, gateway.AirportCalls);
        Assert.Contains("ZZZZ", service.CurrentState!.DestinationIssue!, StringComparison.Ordinal);
        Assert.Null(service.CurrentState.Destination);
    }

    [Fact]
    public async Task A_destination_lookup_failure_is_retried_and_can_recover()
    {
        // The opposite case: a network failure may clear, and a flight
        // configured while offline should still get its ETA once it comes back.
        var gateway = new FakeGateway
        {
            Airport = new AirportLookupResult.Failure(
                FeedFailure.NetworkUnavailable, "offline"),
        };

        await using var service = Service(gateway, out _);
        service.Configure(Request(destination: "KSEA"));

        await service.PollOnceAsync(CancellationToken.None);
        Assert.Null(service.CurrentState!.Destination);
        Assert.NotNull(service.CurrentState.DestinationIssue);

        gateway.Airport = new AirportLookupResult.Resolved(Seatac);
        await service.PollOnceAsync(CancellationToken.None);

        Assert.NotNull(service.CurrentState!.Destination);
        Assert.Null(service.CurrentState.DestinationIssue);
        Assert.Equal(2, gateway.AirportCalls);
    }

    [Fact]
    public async Task No_destination_means_no_lookup_and_no_advice()
    {
        var gateway = new FakeGateway();
        gateway.Aircraft = Aircraft(49.25, -122.3088, altitudeFt: 30000, groundSpeedKt: 400);

        await using var service = Service(gateway, out _);
        service.Configure(Request(destination: null));
        await service.PollOnceAsync(CancellationToken.None);

        Assert.Equal(0, gateway.AirportCalls);
        Assert.Null(service.CurrentState!.Progress.MinutesRemaining);
        Assert.Equal(DepartureAdvice.Unknown, service.CurrentState.Departure.Advice);

        // Still useful: the aircraft is being followed, there is just no ETA.
        Assert.NotNull(service.CurrentState.Aircraft);
    }

    [Fact]
    public async Task Departure_advice_is_raised_on_change_not_on_every_poll()
    {
        // A toast per poll through the whole approach would train the user to
        // dismiss the one that matters.
        var gateway = new FakeGateway { Airport = new AirportLookupResult.Resolved(Seatac) };
        gateway.Aircraft = Aircraft(48.0, -122.3088, altitudeFt: 8000, groundSpeedKt: 300);

        await using var service = Service(gateway, out _);

        var raised = new List<DepartureAdvice>();
        service.DepartureAdviceChanged += (_, s) => raised.Add(s.Departure.Advice);

        service.Configure(Request(travelMinutes: 30));

        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);

        // Unknown at start, then one transition once an ETA exists. The same
        // advice repeating must not raise again.
        Assert.Equal(raised.Distinct().Count(), raised.Count);
    }

    [Fact]
    public async Task Starting_a_new_flight_discards_the_previous_ones_state()
    {
        // Carrying a position across would show one flight's last known
        // position under another's callsign.
        var gateway = new FakeGateway { Airport = new AirportLookupResult.Resolved(Seatac) };
        gateway.Aircraft = Aircraft(49.25, -122.3088, altitudeFt: 30000, groundSpeedKt: 400);

        await using var service = Service(gateway, out _);
        service.Configure(Request());
        await service.PollOnceAsync(CancellationToken.None);
        Assert.NotNull(service.CurrentState!.Aircraft);

        gateway.Aircraft = null;
        service.Configure(Request(callsign: "DAL5678"));

        Assert.Equal("DAL5678", service.CurrentState!.Callsign);
        Assert.Null(service.CurrentState.Aircraft);
        Assert.Equal(FlightPhase.AwaitingContact, service.CurrentState.Progress.Phase);
    }

    [Fact]
    public async Task The_poll_interval_tightens_as_the_flight_approaches()
    {
        // The efficiency argument for the whole feature, end to end.
        var gateway = new FakeGateway { Airport = new AirportLookupResult.Resolved(Seatac) };

        await using var service = Service(gateway, out _);
        service.Configure(Request());

        // Far out and fast: hours away.
        gateway.Aircraft = Aircraft(52.0, -122.3088, altitudeFt: 35000, groundSpeedKt: 300);
        TimeSpan farOut = await service.PollOnceAsync(CancellationToken.None);

        // Inside the approach radius.
        gateway.Aircraft = Aircraft(47.7, -122.3088, altitudeFt: 4000, groundSpeedKt: 200);
        TimeSpan close = await service.PollOnceAsync(CancellationToken.None);

        Assert.True(
            close < farOut,
            $"expected a tighter cadence on approach, got {close} against {farOut}");
        Assert.Equal(FlightTracking.MinPollInterval, close);
    }

    [Fact]
    public async Task Stopping_clears_the_tracked_flight()
    {
        var gateway = new FakeGateway { Airport = new AirportLookupResult.Resolved(Seatac) };
        await using var service = Service(gateway, out _);

        await service.StartAsync(Request());
        Assert.NotNull(service.Tracked);

        await service.StopAsync();

        Assert.Null(service.Tracked);
        Assert.Null(service.CurrentState);
    }

    private static FlightTrackingService Service(FakeGateway gateway, out FakeGateway captured)
    {
        captured = gateway;
        return new FlightTrackingService(gateway, NullLogger<FlightTrackingService>.Instance);
    }

    private static TrackedFlightRequest Request(
        string callsign = "UAL1234",
        string? destination = "KSEA",
        int travelMinutes = 45,
        int postLandingMinutes = 20)
        => new(callsign, destination, travelMinutes, postLandingMinutes);

    private static AircraftState Aircraft(
        double lat,
        double lon,
        double? altitudeFt,
        double? groundSpeedKt,
        bool onGround = false) => new()
        {
            Provider = "test",
            IcaoHex = "abc123",
            Callsign = "UAL1234",
            Latitude = lat,
            Longitude = lon,
            GeometricAltitudeFt = altitudeFt,
            GroundSpeedKt = groundSpeedKt,
            OnGround = onGround,
            FirstSeen = DateTimeOffset.UnixEpoch,
            LastSeen = DateTimeOffset.UnixEpoch,
            PositionTimestamp = DateTimeOffset.UnixEpoch,
        };

    /// <summary>Scripted gateway: no network, and awkward cases on demand.</summary>
    private sealed class FakeGateway : ITrackedFlightGateway
    {
        /// <summary>Returned by the next callsign poll, or <c>null</c> for none.</summary>
        public AircraftState? Aircraft { get; set; }

        /// <summary>Overrides <see cref="Aircraft"/> when set, for failure cases.</summary>
        public ProviderResult? Result { get; set; }

        public AirportLookupResult Airport { get; set; } =
            new AirportLookupResult.NotFound("ZZZZ");

        public int AirportCalls { get; private set; }

        public int CallsignCalls { get; private set; }

        public Task<ProviderResult> FetchByCallsignAsync(
            string callsign,
            CancellationToken cancellationToken)
        {
            CallsignCalls++;

            if (Result is { } scripted)
            {
                return Task.FromResult(scripted);
            }

            IReadOnlyList<AircraftState> aircraft = Aircraft is { } a ? [a] : [];

            return Task.FromResult<ProviderResult>(
                new ProviderResult.Success(aircraft, DateTimeOffset.UtcNow));
        }

        public Task<AirportLookupResult> ResolveAirportAsync(
            string? icao,
            CancellationToken cancellationToken)
        {
            AirportCalls++;
            return Task.FromResult(Airport);
        }
    }
}

