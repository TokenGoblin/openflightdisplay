namespace OpenFlightDisplay.Infrastructure.Maps;

using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

/// <summary>A place that a search matched.</summary>
/// <param name="DisplayName">Full human-readable name, for disambiguating results.</param>
public sealed record Place(string DisplayName, double Latitude, double Longitude)
{
    /// <summary>
    /// A shortened label for a list.
    /// </summary>
    /// <remarks>
    /// Nominatim returns the whole administrative chain — postcode, town,
    /// county, state, country — which is far too long to read at a glance. The
    /// first three parts identify a place; the rest is context the full name
    /// still carries.
    /// </remarks>
    public string ShortName
    {
        get
        {
            string[] parts = DisplayName.Split(',', StringSplitOptions.TrimEntries);
            return parts.Length <= 3 ? DisplayName : string.Join(", ", parts.Take(3));
        }
    }
}

/// <summary>Outcome of a place search.</summary>
public abstract record PlaceSearchResult
{
    private PlaceSearchResult()
    {
    }

    public sealed record Found(IReadOnlyList<Place> Places) : PlaceSearchResult;

    /// <summary>The service answered and knows nothing by that name.</summary>
    /// <remarks>Distinct from a failure: a typo is the user's to fix.</remarks>
    public sealed record NoMatches(string Query) : PlaceSearchResult;

    public sealed record Failure(string Detail) : PlaceSearchResult;
}

/// <summary>
/// Turns a postcode or place name into coordinates, using OpenStreetMap's
/// Nominatim service.
/// </summary>
/// <remarks>
/// <para>
/// Exists so nobody has to find their own latitude and longitude to use this
/// application. Works for postcodes and ZIP codes, town names, and airports.
/// </para>
/// <para>
/// <b>Nominatim's usage policy is stricter than the tile policy</b> and is
/// enforced here rather than trusted to callers: an absolute maximum of one
/// request per second, an identifying User-Agent, and no
/// autocomplete-as-you-type. The rate limit lives in this class because a UI
/// that forgot it would be indistinguishable from abuse from the far end.
/// </para>
/// <para>
/// Results are cached for the session, so re-running the same search — which is
/// exactly what someone comparing two results does — costs nothing.
/// </para>
/// </remarks>
public sealed partial class PlaceSearch : IDisposable
{
    /// <summary>Most results offered for one query.</summary>
    /// <remarks>
    /// Enough to disambiguate a duplicated town name without turning the choice
    /// into a research task.
    /// </remarks>
    public const int MaxResults = 5;

    /// <summary>Nominatim's hard rate limit. Not a target — a ceiling.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    private readonly HttpClient _httpClient;
    private readonly ILogger<PlaceSearch> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IReadOnlyList<Place>> _cache = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;

    public PlaceSearch(HttpClient httpClient, ILogger<PlaceSearch> logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Searches for a postcode, place name or airport.</summary>
    public async Task<PlaceSearchResult> SearchAsync(string? query, CancellationToken cancellationToken)
    {
        string trimmed = (query ?? string.Empty).Trim();

        if (trimmed.Length < 2)
        {
            return new PlaceSearchResult.Failure(
                "Enter a postcode, ZIP code or place name — at least two characters.");
        }

        if (_cache.TryGetValue(trimmed, out IReadOnlyList<Place>? cached))
        {
            return cached.Count == 0
                ? new PlaceSearchResult.NoMatches(trimmed)
                : new PlaceSearchResult.Found(cached);
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // One request per second, serialised. Held across the request so
            // concurrent callers queue rather than burst.
            TimeSpan since = _timeProvider.GetUtcNow() - _lastRequest;
            if (since < MinimumInterval)
            {
                await Task.Delay(MinimumInterval - since, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }

            _lastRequest = _timeProvider.GetUtcNow();

            return await QueryAsync(trimmed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _gate.Dispose();

    private async Task<PlaceSearchResult> QueryAsync(string query, CancellationToken cancellationToken)
    {
        string url = string.Create(
            CultureInfo.InvariantCulture,
            $"/search?q={Uri.EscapeDataString(query)}&format=jsonv2&limit={MaxResults}");

        try
        {
            using HttpResponseMessage response =
                await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // 403 here means the User-Agent was rejected, which is a bug in
                // this application rather than anything the user did.
                return new PlaceSearchResult.Failure(
                    $"The place-lookup service returned HTTP {(int)response.StatusCode}.");
            }

            string body = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            var places = Parse(body);
            _cache[query] = places;

            LogSearched(_logger, query, places.Count);

            return places.Count == 0
                ? new PlaceSearchResult.NoMatches(query)
                : new PlaceSearchResult.Found(places);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new PlaceSearchResult.Failure("The place-lookup service did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            return new PlaceSearchResult.Failure(
                $"The place could not be looked up: {ex.Message}. Coordinates can still be typed in.");
        }
        catch (JsonException)
        {
            return new PlaceSearchResult.Failure(
                "The place-lookup service returned something unreadable.");
        }
    }

    /// <summary>Reads Nominatim's result array.</summary>
    /// <remarks>
    /// Latitude and longitude arrive as <b>strings</b>, and always with a dot
    /// separator regardless of locale, so they are parsed invariantly. Parsing
    /// them with the current culture puts a German user's search in the wrong
    /// hemisphere.
    /// </remarks>
    internal static IReadOnlyList<Place> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var places = new List<Place>();

        foreach (JsonElement element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("lat", out JsonElement latElement)
                || !element.TryGetProperty("lon", out JsonElement lonElement)
                || !double.TryParse(
                    latElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                || !double.TryParse(
                    lonElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                continue;
            }

            string name = element.TryGetProperty("display_name", out JsonElement nameElement)
                ? nameElement.GetString() ?? "Unnamed place"
                : "Unnamed place";

            places.Add(new Place(name, lat, lon));
        }

        return places;
    }

    [LoggerMessage(
        EventId = 6100,
        Level = LogLevel.Information,
        Message = "Place search for {Query} returned {Count} results")]
    private static partial void LogSearched(ILogger logger, string query, int count);
}
