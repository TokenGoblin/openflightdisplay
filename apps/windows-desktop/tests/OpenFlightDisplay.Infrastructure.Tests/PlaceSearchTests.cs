namespace OpenFlightDisplay.Infrastructure.Tests;

using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using OpenFlightDisplay.Infrastructure.Maps;
using Xunit;

/// <summary>
/// Postcode and place lookup. The cases that matter are the ones that would put
/// somebody's radar in the wrong place without saying so.
/// </summary>
public class PlaceSearchTests
{
    [Fact]
    public async Task A_postcode_resolves_to_coordinates()
    {
        // Shape of a real Nominatim jsonv2 response.
        var search = Search(HttpStatusCode.OK, """
            [
              {
                "lat": "47.6062100",
                "lon": "-122.3320700",
                "display_name": "98101, Seattle, King County, Washington, United States"
              }
            ]
            """);

        var found = Assert.IsType<PlaceSearchResult.Found>(
            await search.SearchAsync("98101", CancellationToken.None));

        Place place = Assert.Single(found.Places);
        Assert.Equal(47.60621, place.Latitude, 5);
        Assert.Equal(-122.33207, place.Longitude, 5);
    }

    [Fact]
    public async Task Coordinates_are_parsed_invariantly_not_by_locale()
    {
        // Nominatim always uses a dot. Parsing with a culture that reads it as a
        // thousands separator turns 47.6 into 476 and puts the radar in orbit.
        var original = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture =
                new System.Globalization.CultureInfo("de-DE");

            var search = Search(HttpStatusCode.OK, """
                [ { "lat": "47.6062100", "lon": "-122.3320700", "display_name": "Seattle" } ]
                """);

            var found = Assert.IsType<PlaceSearchResult.Found>(
                await search.SearchAsync("Seattle", CancellationToken.None));

            Assert.Equal(47.60621, found.Places[0].Latitude, 5);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task An_empty_result_is_no_matches_not_a_failure()
    {
        // A typo is the user's to fix and reads differently from an outage.
        var search = Search(HttpStatusCode.OK, "[]");

        var none = Assert.IsType<PlaceSearchResult.NoMatches>(
            await search.SearchAsync("zzzzzzzz", CancellationToken.None));

        Assert.Equal("zzzzzzzz", none.Query);
    }

    [Fact]
    public async Task A_service_problem_is_a_failure_not_a_missing_place()
    {
        var search = Search(HttpStatusCode.ServiceUnavailable, "");

        var failure = Assert.IsType<PlaceSearchResult.Failure>(
            await search.SearchAsync("98101", CancellationToken.None));

        Assert.Contains("503", failure.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rows_without_usable_coordinates_are_skipped_not_defaulted()
    {
        // A row defaulted to 0,0 would silently point the radar at the Gulf of
        // Guinea, which is the one failure this codebase never tolerates.
        var search = Search(HttpStatusCode.OK, """
            [
              { "display_name": "No coordinates here" },
              { "lat": "not-a-number", "lon": "0", "display_name": "Nonsense" },
              { "lat": "51.4775", "lon": "-0.4614", "display_name": "London Heathrow" }
            ]
            """);

        var found = Assert.IsType<PlaceSearchResult.Found>(
            await search.SearchAsync("heathrow", CancellationToken.None));

        Place place = Assert.Single(found.Places);
        Assert.Equal(51.4775, place.Latitude, 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a")]
    [InlineData(null)]
    public async Task A_query_too_short_to_mean_anything_is_refused_without_a_request(string? query)
    {
        var handler = new CountingHandler(HttpStatusCode.OK, "[]");
        var search = new PlaceSearch(
            new HttpClient(handler) { BaseAddress = new Uri("https://nominatim.openstreetmap.org") },
            NullLogger<PlaceSearch>.Instance);

        Assert.IsType<PlaceSearchResult.Failure>(
            await search.SearchAsync(query, CancellationToken.None));

        // The service is rate limited and donated; a pointless query must not
        // reach it.
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task Repeating_a_search_is_served_from_cache()
    {
        // Comparing two results means re-running the same search, which must
        // not cost a second request against a one-per-second limit.
        var handler = new CountingHandler(HttpStatusCode.OK, """
            [ { "lat": "47.6", "lon": "-122.3", "display_name": "Seattle" } ]
            """);

        var search = new PlaceSearch(
            new HttpClient(handler) { BaseAddress = new Uri("https://nominatim.openstreetmap.org") },
            NullLogger<PlaceSearch>.Instance);

        await search.SearchAsync("Seattle", CancellationToken.None);
        await search.SearchAsync("seattle", CancellationToken.None);
        await search.SearchAsync("  Seattle  ", CancellationToken.None);

        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void A_long_administrative_chain_is_shortened_for_the_list()
    {
        var place = new Place(
            "98101, Seattle, King County, Washington, 98101, United States",
            47.6, -122.3);

        Assert.Equal("98101, Seattle, King County", place.ShortName);
    }

    [Fact]
    public void A_short_name_is_left_alone()
        => Assert.Equal("Seattle", new Place("Seattle", 47.6, -122.3).ShortName);

    [Fact]
    public void A_response_that_is_not_an_array_yields_nothing_rather_than_throwing()
        => Assert.Empty(PlaceSearch.Parse("""{ "error": "whatever" }"""));

    private static PlaceSearch Search(HttpStatusCode status, string body)
        => new(
            new HttpClient(new CountingHandler(status, body))
            {
                BaseAddress = new Uri("https://nominatim.openstreetmap.org"),
            },
            NullLogger<PlaceSearch>.Instance);

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public CountingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body),
            });
        }
    }
}
