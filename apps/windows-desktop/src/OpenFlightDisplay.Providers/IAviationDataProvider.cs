namespace OpenFlightDisplay.Providers;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Areas;
using OpenFlightDisplay.Core.Feed;

/// <summary>
/// A source of aircraft observations.
/// </summary>
/// <remarks>
/// Mirrors the <c>AviationDataProvider</c> contract in
/// <c>services/gateway/src/providers/provider.ts</c>, with one deliberate
/// difference: failures are <b>returned</b> as <see cref="ProviderResult"/>
/// rather than thrown.
///
/// A provider being unreachable is an expected operating condition for this
/// application, not an exceptional one — the display is specified to keep
/// running and show an explicit status. Using exceptions for it would mean
/// using exceptions for ordinary application state, which the brief forbids.
/// Genuine bugs still throw.
/// </remarks>
public interface IAviationDataProvider
{
    /// <summary>Stable identifier, e.g. "adsblol", "mock", "replay".</summary>
    string Id { get; }

    /// <summary>Human-readable name for settings and attribution.</summary>
    string DisplayName { get; }

    /// <summary>True if this provider cannot operate without a credential.</summary>
    bool RequiresApiKey { get; }

    /// <summary>
    /// Polling interval this provider's terms and rate limits allow.
    /// </summary>
    TimeSpan RecommendedPollInterval { get; }

    /// <summary>Fetches aircraft currently within the given area.</summary>
    /// <remarks>
    /// Implementations must honour <paramref name="cancellationToken"/> and must
    /// not throw for network or protocol failures.
    /// </remarks>
    Task<ProviderResult> FetchAircraftAsync(
        MonitoringArea area,
        CancellationToken cancellationToken);
}

/// <summary>Outcome of a fetch: either observations, or a categorised failure.</summary>
public abstract record ProviderResult
{
    private ProviderResult()
    {
    }

    /// <summary>
    /// The provider answered. <paramref name="Aircraft"/> may be empty — an
    /// empty sky is a successful answer, not a failure.
    /// </summary>
    public sealed record Success(
        IReadOnlyList<AircraftState> Aircraft,
        DateTimeOffset ObservedAt) : ProviderResult;

    /// <summary>The provider did not answer usefully.</summary>
    public sealed record Failure(
        FeedFailure Kind,
        string Detail,
        Exception? Cause = null) : ProviderResult;

    /// <summary>A replay source ran out of recorded frames.</summary>
    public sealed record Exhausted(string RecordingName) : ProviderResult;
}
