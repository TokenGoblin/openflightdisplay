namespace OpenFlightDisplay.Core.Feed;

using OpenFlightDisplay.Core.Aircraft;

/// <summary>
/// Every state the aircraft feed can be in, as a closed set.
/// </summary>
/// <remarks>
/// <para>
/// The project's binding reliability rule is that every failure mode is
/// explicit and the user is <b>never</b> left on an indefinite spinner
/// (<c>docs/PRODUCT_REQUIREMENTS.md</c>). Modelling this as a sealed hierarchy
/// rather than as a status enum beside a nullable aircraft list is what enforces
/// it: there is no representable state that means "loading, indefinitely, with
/// nothing to say about why".
/// </para>
/// <para>
/// The hierarchy is sealed and the private constructor prevents outside
/// subclassing, so a <c>switch</c> over these cases is genuinely exhaustive and
/// the compiler can say so.
/// </para>
/// </remarks>
public abstract record FeedState
{
    private FeedState()
    {
    }

    /// <summary>
    /// Nothing has been configured yet — no location, no data source.
    /// The UI shows onboarding, not an error.
    /// </summary>
    public sealed record NeedsConfiguration : FeedState;

    /// <summary>
    /// A first fetch is in flight. This is the <i>only</i> indefinite-looking
    /// state and it is bounded: the pipeline transitions out of it on success,
    /// failure or timeout.
    /// </summary>
    public sealed record Connecting(string ProviderId) : FeedState;

    /// <summary>Live data, and at least one aircraft matched.</summary>
    public sealed record Live(
        IReadOnlyList<AircraftState> Aircraft,
        string ProviderId,
        DateTimeOffset ObservedAt) : FeedState;

    /// <summary>
    /// The provider answered successfully and there was simply nothing in range.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from every failure case. An empty sky is a normal,
    /// correct answer — the original firmware shows a clock rather than an error
    /// here, and conflating it with "provider unavailable" would train users to
    /// ignore real outages.
    /// </remarks>
    public sealed record NoMatchingAircraft(
        string ProviderId,
        DateTimeOffset ObservedAt) : FeedState;

    /// <summary>
    /// The last successful data is older than the staleness threshold.
    /// </summary>
    /// <remarks>
    /// Carries the aircraft anyway. Per <c>docs/PROTOCOL.md</c>, a provider
    /// problem must <b>not</b> clear the last-known list — it is rendered
    /// alongside a visible data-age indicator instead of blanking the screen.
    /// </remarks>
    public sealed record Stale(
        IReadOnlyList<AircraftState> Aircraft,
        string ProviderId,
        DateTimeOffset ObservedAt) : FeedState;

    /// <summary>
    /// The data source failed. <paramref name="LastKnownAircraft"/> is retained
    /// and may be empty, never null.
    /// </summary>
    public sealed record SourceUnavailable(
        string ProviderId,
        FeedFailure Failure,
        string Detail,
        IReadOnlyList<AircraftState> LastKnownAircraft,
        DateTimeOffset? LastSuccessAt) : FeedState;

    /// <summary>A replay session reached the end of its recording.</summary>
    public sealed record ReplayComplete(string RecordingName) : FeedState;

    /// <summary>Aircraft currently known, whatever the state. Never null.</summary>
    public IReadOnlyList<AircraftState> KnownAircraft => this switch
    {
        Live l => l.Aircraft,
        Stale s => s.Aircraft,
        SourceUnavailable u => u.LastKnownAircraft,
        _ => [],
    };

    /// <summary>
    /// True when the user should act to move things forward, rather than wait.
    /// </summary>
    public bool RequiresUserAction => this switch
    {
        NeedsConfiguration => true,
        SourceUnavailable u => u.Failure
            is FeedFailure.AuthenticationFailed
            or FeedFailure.LocationUnavailable
            or FeedFailure.InvalidConfiguration,
        _ => false,
    };
}

/// <summary>
/// Why a data source failed, at the granularity the UI actually needs to say
/// something useful and distinct.
/// </summary>
/// <remarks>
/// These are separated by <i>what the user can do about it</i>, not by HTTP
/// status. "Rate limited" and "authentication failed" both arrive as 4xx but
/// demand completely different advice — wait, versus go fix your key.
/// </remarks>
public enum FeedFailure
{
    /// <summary>No route to the internet at all.</summary>
    NetworkUnavailable,

    /// <summary>The provider is reachable but erroring or refusing.</summary>
    ProviderUnavailable,

    /// <summary>The configured gateway could not be reached.</summary>
    GatewayUnavailable,

    /// <summary>A local dump1090/readsb/tar1090 feed could not be reached.</summary>
    LocalReceiverUnavailable,

    /// <summary>A response arrived but could not be parsed as expected.</summary>
    InvalidResponse,

    /// <summary>The provider asked us to slow down.</summary>
    RateLimited,

    /// <summary>Credentials were rejected or are missing.</summary>
    AuthenticationFailed,

    /// <summary>The request exceeded its timeout.</summary>
    Timeout,

    /// <summary>No usable observer location is configured or obtainable.</summary>
    LocationUnavailable,

    /// <summary>Settings are present but not usable as configured.</summary>
    InvalidConfiguration,

    /// <summary>The local history database could not be opened or written.</summary>
    DatabaseFailure,
}
