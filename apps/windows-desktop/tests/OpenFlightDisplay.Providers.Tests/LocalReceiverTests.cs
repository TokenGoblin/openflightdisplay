namespace OpenFlightDisplay.Providers.Tests;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Feed;
using OpenFlightDisplay.Core.Quality;
using OpenFlightDisplay.Providers.LocalReceiver;
using Xunit;

/// <summary>
/// Local receiver support, tested against fixture bodies in the shape
/// dump1090, readsb and tar1090 actually serve.
/// </summary>
public class LocalReceiverTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly CircleArea Area = new(47.6062, -122.3321, 100.0);

    /// <summary>A realistic aircraft.json, with the receiver clock at <see cref="Now"/>.</summary>
    private static string Body(string aircraftJson, double? nowUnix = null)
    {
        double receiverNow = nowUnix ?? Now.ToUnixTimeMilliseconds() / 1000.0;
        return $$"""
            {
              "now": {{receiverNow.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}},
              "messages": 987654,
              "aircraft": [ {{aircraftJson}} ]
            }
            """;
    }

    // ---- envelope ----

    [Fact]
    public void Reads_aircraft_from_the_aircraft_key_not_ac()
    {
        // The one envelope difference from adsb.lol that would silently return
        // nothing if it were wrong.
        var snapshot = LocalReceiverNormalizer.Parse(
            Body("""{ "hex": "a1b2c3", "lat": 47.61, "lon": -122.33 }"""), "local", Now);

        Assert.Single(snapshot.Aircraft);
    }

    [Fact]
    public void Reads_the_receiver_clock()
    {
        var snapshot = LocalReceiverNormalizer.Parse(Body("""{ "hex": "a1b2c3" }"""), "local", Now);

        Assert.Equal(Now, snapshot.ReceiverTime);
    }

    [Fact]
    public void Reads_the_message_count()
    {
        var snapshot = LocalReceiverNormalizer.Parse(Body(""), "local", Now);

        Assert.Equal(987654, snapshot.TotalMessages);
    }

    [Fact]
    public void A_body_with_no_aircraft_key_yields_an_empty_snapshot()
    {
        var snapshot = LocalReceiverNormalizer.Parse("""{ "now": 1, "messages": 5 }""", "local", Now);

        Assert.Empty(snapshot.Aircraft);
    }

    [Fact]
    public void An_empty_aircraft_array_is_a_successful_empty_snapshot()
    {
        var snapshot = LocalReceiverNormalizer.Parse(Body(""), "local", Now);

        Assert.Empty(snapshot.Aircraft);
        Assert.NotNull(snapshot.ReceiverTime);
    }

    [Theory]
    [InlineData("""{ "now": "not a number", "aircraft": [] }""")]
    [InlineData("""{ "now": 0, "aircraft": [] }""")]
    [InlineData("""{ "now": -5, "aircraft": [] }""")]
    [InlineData("""{ "aircraft": [] }""")]
    public void A_missing_or_nonsensical_receiver_clock_is_null_not_1970(string json)
    {
        // Turning a bad value into a 1970 timestamp would mark every aircraft
        // stale and report a healthy receiver as broken.
        var snapshot = LocalReceiverNormalizer.Parse(json, "local", Now);

        Assert.Null(snapshot.ReceiverTime);
    }

    // ---- ages are relative to the receiver's clock ----

    [Fact]
    public void Position_age_is_measured_against_the_receiver_clock()
    {
        // The receiver wrote this file an hour ago; the aircraft was 10s old at
        // that point. Its position is therefore an hour and ten seconds old, not
        // ten seconds old.
        double anHourAgo = Now.AddHours(-1).ToUnixTimeMilliseconds() / 1000.0;

        var snapshot = LocalReceiverNormalizer.Parse(
            Body("""{ "hex": "a1b2c3", "lat": 47.61, "lon": -122.33, "seen_pos": 10.0 }""", anHourAgo),
            "local",
            Now);

        AircraftState aircraft = Assert.Single(snapshot.Aircraft);

        Assert.Equal(Now.AddHours(-1).AddSeconds(-10), aircraft.PositionTimestamp);
        Assert.True(Staleness.IsStale(aircraft, Now));
    }

    [Fact]
    public void Position_age_falls_back_to_our_clock_when_the_receiver_gave_none()
    {
        var snapshot = LocalReceiverNormalizer.Parse(
            """{ "aircraft": [ { "hex": "a1b2c3", "lat": 47.61, "lon": -122.33, "seen_pos": 5.0 } ] }""",
            "local",
            Now);

        Assert.Equal(Now.AddSeconds(-5), Assert.Single(snapshot.Aircraft).PositionTimestamp);
    }

    // ---- the shared tar1090 quirks still apply ----

    [Fact]
    public void Applies_the_shared_tar1090_mapping()
    {
        var snapshot = LocalReceiverNormalizer.Parse(
            Body("""
                { "hex": "a1b2c3", "flight": "UAL1234 ", "lat": 47.61, "lon": -122.33,
                  "alt_baro": "ground", "squawk": "7700", "emergency": "general" }
                """),
            "local",
            Now);

        AircraftState aircraft = Assert.Single(snapshot.Aircraft);

        Assert.Equal("UAL1234", aircraft.Callsign);
        Assert.True(aircraft.OnGround);
        Assert.Equal("7700", aircraft.Squawk);
        Assert.Equal(EmergencyState.General, aircraft.EmergencyState);
    }

    [Fact]
    public void Malformed_json_throws_so_the_provider_can_report_it()
        => Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => LocalReceiverNormalizer.Parse("<html>not json</html>", "local", Now));

    // ---- provider behaviour ----

    [Fact]
    public async Task A_working_receiver_yields_aircraft()
    {
        var provider = Provider(new StubHandler(HttpStatusCode.OK,
            Body("""{ "hex": "a1b2c3", "lat": 47.61, "lon": -122.33 }""")));

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        Assert.Single(Assert.IsType<ProviderResult.Success>(result).Aircraft);
    }

    [Fact]
    public async Task A_receiver_whose_decoder_has_stopped_is_refused_not_displayed()
    {
        // The characteristic local-receiver failure: the web server keeps
        // serving the last file the decoder wrote, so the JSON is perfect and
        // every aircraft looks live.
        double longAgo = Now.AddHours(-3).ToUnixTimeMilliseconds() / 1000.0;

        var provider = Provider(new StubHandler(HttpStatusCode.OK,
            Body("""{ "hex": "a1b2c3", "lat": 47.61, "lon": -122.33 }""", longAgo)));

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        var failure = Assert.IsType<ProviderResult.Failure>(result);
        Assert.Equal(FeedFailure.LocalReceiverUnavailable, failure.Kind);
        Assert.Contains("decoder", failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_receiver_slightly_behind_our_clock_is_still_accepted()
    {
        // Small clock differences are normal and must not trip the check.
        double slightlyBehind = Now.AddSeconds(-30).ToUnixTimeMilliseconds() / 1000.0;

        var provider = Provider(new StubHandler(HttpStatusCode.OK,
            Body("""{ "hex": "a1b2c3", "lat": 47.61, "lon": -122.33 }""", slightlyBehind)));

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        Assert.IsType<ProviderResult.Success>(result);
    }

    [Fact]
    public async Task Probes_the_well_known_paths_until_one_answers()
    {
        // A user should be able to paste a bare host without knowing whether
        // their install serves under /data/ or /dump1090/data/.
        var handler = new StubHandler(HttpStatusCode.NotFound, "")
        {
            SucceedOnPath = "/dump1090/data/aircraft.json",
            SuccessBody = Body("""{ "hex": "a1b2c3", "lat": 47.61, "lon": -122.33 }"""),
        };

        var provider = Provider(handler);

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        Assert.IsType<ProviderResult.Success>(result);
        Assert.Contains("/dump1090/data/aircraft.json", handler.RequestedPaths, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Remembers_the_path_that_worked()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound, "")
        {
            SucceedOnPath = "/dump1090/data/aircraft.json",
            SuccessBody = Body("""{ "hex": "a1b2c3", "lat": 47.61, "lon": -122.33 }"""),
        };

        var provider = Provider(handler);

        await provider.FetchAircraftAsync(Area, CancellationToken.None);
        handler.RequestedPaths.Clear();
        await provider.FetchAircraftAsync(Area, CancellationToken.None);

        // Second poll goes straight to the known path rather than probing again.
        Assert.Equal("/dump1090/data/aircraft.json", Assert.Single(handler.RequestedPaths));
    }

    [Fact]
    public async Task A_receiver_that_is_not_there_reports_local_receiver_unavailable()
    {
        var provider = Provider(new StubHandler(
            new HttpRequestException("connection refused")));

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        Assert.Equal(
            FeedFailure.LocalReceiverUnavailable,
            Assert.IsType<ProviderResult.Failure>(result).Kind);
    }

    [Fact]
    public async Task A_url_pointing_at_a_web_page_says_so_rather_than_failing_obscurely()
    {
        var provider = Provider(new StubHandler(HttpStatusCode.OK, "<html><body>tar1090</body></html>"));

        var result = await provider.FetchAircraftAsync(Area, CancellationToken.None);

        var failure = Assert.IsType<ProviderResult.Failure>(result);
        Assert.Equal(FeedFailure.InvalidResponse, failure.Kind);
        Assert.Contains("aircraft.json", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_requested_cancellation_propagates()
    {
        var provider = Provider(new StubHandler(new OperationCanceledException()));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.FetchAircraftAsync(Area, cts.Token));
    }

    [Fact]
    public void Polls_faster_than_an_internet_provider()
    {
        // No rate limit and no upstream to be polite to.
        var provider = Provider(new StubHandler(HttpStatusCode.OK, Body("")));

        Assert.True(provider.RecommendedPollInterval <= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Requires_no_api_key()
        => Assert.False(Provider(new StubHandler(HttpStatusCode.OK, Body(""))).RequiresApiKey);

    private static LocalReceiverProvider Provider(StubHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://192.168.1.10") };
        return new LocalReceiverProvider(
            client, NullLogger<LocalReceiverProvider>.Instance, new FixedTime(Now));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throw;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public StubHandler(Exception toThrow)
        {
            _throw = toThrow;
            _body = string.Empty;
        }

        /// <summary>When set, only this path returns <see cref="SuccessBody"/>.</summary>
        public string? SucceedOnPath { get; init; }

        public string SuccessBody { get; init; } = string.Empty;

        public List<string> RequestedPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            RequestedPaths.Add(path);

            if (_throw is not null)
            {
                throw _throw;
            }

            // When SucceedOnPath is set, that path answers 200 with SuccessBody
            // and everything else 404s — which is how a real install looks when
            // it serves under one of several possible paths. Without the
            // explicit OK here the "matching" path returned the handler's base
            // status, which in these tests is itself 404.
            if (SucceedOnPath is not null)
            {
                return Task.FromResult(
                    string.Equals(path, SucceedOnPath, StringComparison.Ordinal)
                        ? new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(SuccessBody),
                        }
                        : new HttpResponseMessage(HttpStatusCode.NotFound)
                        {
                            Content = new StringContent(string.Empty),
                        });
            }

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }
}
