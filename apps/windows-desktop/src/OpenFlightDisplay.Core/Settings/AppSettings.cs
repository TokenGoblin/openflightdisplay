namespace OpenFlightDisplay.Core.Settings;

using OpenFlightDisplay.Core.Alerts;
using OpenFlightDisplay.Core.Ranking;
using OpenFlightDisplay.Core.Units;


/// <summary>Where the aircraft data comes from.</summary>
public enum DataMode
{
    /// <summary>Poll an aviation-data provider directly. No gateway needed.</summary>
    DirectProvider,

    /// <summary>Consume an existing OpenFlightDisplay gateway's feed.</summary>
    Gateway,

    /// <summary>A dump1090/readsb/tar1090 JSON feed on the local network.</summary>
    LocalReceiver,

    /// <summary>Synthetic aircraft. Works with no network at all.</summary>
    Mock,

    /// <summary>Play back a recorded session.</summary>
    Replay,
}

/// <summary>Clock display preference.</summary>
public enum ClockFormat
{
    TwentyFourHour,
    TwelveHour,
}

/// <summary>
/// All persisted application settings.
/// </summary>
/// <remarks>
/// <para>
/// An immutable record written atomically as a whole. There is no partial-write
/// path: a settings change produces a complete new document, which is what makes
/// a crash mid-save unable to leave a half-valid file behind. The firmware
/// reaches the same conclusion with its atomic LittleFS write.
/// </para>
/// <para>
/// <b>No secrets live here.</b> Provider API keys go to Windows Credential
/// Manager; this record holds only a reference to which credential to look up.
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>
    /// Bumped when a change cannot be handled by simply reading the old file.
    /// </summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>False until first-run onboarding completes.</summary>
    public bool OnboardingCompleted { get; init; }

    // ---- location ----

    /// <summary>Observer latitude, or <c>null</c> if never configured.</summary>
    /// <remarks>
    /// Nullable rather than defaulted to 0,0 — the Gulf of Guinea is a real
    /// coordinate, and defaulting there would silently show an empty sky instead
    /// of prompting for a location.
    /// </remarks>
    public double? HomeLatitude { get; init; }

    /// <inheritdoc cref="HomeLatitude"/>
    public double? HomeLongitude { get; init; }

    public double MonitoringRadiusKm { get; init; } = 80.0;

    // ---- data source ----

    public DataMode DataMode { get; init; } = DataMode.Mock;

    /// <summary>Provider id when <see cref="DataMode"/> is direct, e.g. "adsblol".</summary>
    public string ProviderId { get; init; } = "mock";

    /// <summary>Base URL of the gateway, when in gateway mode.</summary>
    public string? GatewayUrl { get; init; }

    /// <summary>Base URL of a local receiver's JSON feed, when in receiver mode.</summary>
    public string? LocalReceiverUrl { get; init; }

    /// <summary>Seconds between polls. Zero means use the provider's recommendation.</summary>
    public int RefreshIntervalSeconds { get; init; }

    // ---- presentation ----

    public UnitSystem Units { get; init; } = UnitSystem.Aviation;

    public ClockFormat ClockFormat { get; init; } = ClockFormat.TwentyFourHour;

    /// <summary>Show times in UTC rather than the local zone.</summary>
    public bool UseUtc { get; init; }

    public RankingMode RankingMode { get; init; } = RankingMode.NearestHorizontal;

    /// <summary>
    /// What reaches the board and the plot. Admits everything by default.
    /// </summary>
    public AircraftFilter Filter { get; init; } = AircraftFilter.None;

    // ---- privacy-sensitive features, all off by default ----

    /// <summary>
    /// Record observations to the local history database.
    /// </summary>
    /// <remarks>
    /// <b>Off by default and disclosed during onboarding.</b> History is the one
    /// feature that turns a live display into a record of everything that flew
    /// over someone's house, so it is opt-in rather than opt-out.
    /// </remarks>
    public bool HistoryEnabled { get; init; }

    public int HistoryRetentionDays { get; init; } = 30;

    public int HistoryMaxDatabaseMb { get; init; } = 512;

    /// <summary>Windows toast notifications for alerts. Opt-in.</summary>
    public bool NotificationsEnabled { get; init; }

    /// <summary>
    /// Configured alert rules, or <c>null</c> if the user has never set any.
    /// </summary>
    /// <remarks>
    /// <b>Nullable on purpose, and it is not the same as empty.</b> Null means
    /// "never configured", which seeds the built-in emergency rule so the alert
    /// engine is not dormant on first run. An empty list means the user
    /// deliberately deleted every rule, and re-adding one would be the
    /// application overriding a decision they made explicitly.
    /// </remarks>
    public IReadOnlyList<AlertRuleSetting>? AlertRules { get; init; }

    /// <summary>Rules to install, resolving the never-configured case.</summary>
    public IReadOnlyList<AlertRuleSetting> EffectiveAlertRules =>
        AlertRules ?? [AlertRuleSetting.BuiltInEmergency];

    // ---- tracked flight ----

    /// <summary>
    /// Flight being followed, in the callsign form ADS-B broadcasts, or
    /// <c>null</c> when nothing is tracked.
    /// </summary>
    public string? TrackedCallsign { get; init; }

    /// <summary>Destination ICAO code, or <c>null</c>. Four letters, never IATA.</summary>
    public string? TrackedDestinationIcao { get; init; }

    /// <summary>
    /// Door-to-arrivals-hall travel time in minutes.
    /// </summary>
    /// <remarks>
    /// Zero means never configured, which produces no departure advice rather
    /// than a guess — the domain treats it that way deliberately.
    /// </remarks>
    public int TrackedTravelMinutes { get; init; }

    /// <summary>
    /// Estimated touchdown-to-walk-out time: taxi, deplaning, immigration, bags.
    /// </summary>
    /// <remarks>
    /// Defaulted to something plausible rather than zero, because omitting it
    /// entirely sends people to the airport to stand around — the exact failure
    /// the feature exists to prevent. Twenty minutes suits a domestic arrival
    /// with hand baggage; long-haul with a checked bag is closer to forty-five.
    /// </remarks>
    public int TrackedPostLandingMinutes { get; init; } = 20;

    /// <summary>Keep polling when the window is minimised to the tray. Opt-in.</summary>
    public bool BackgroundMonitoringEnabled { get; init; }

    /// <summary>
    /// True when there is enough configuration to start a feed.
    /// </summary>
    /// <remarks>
    /// Mock mode needs no location — it synthesises around whatever centre it is
    /// given — so it stays usable before onboarding finishes. Every other mode
    /// needs a real observer position.
    /// </remarks>
    public bool IsUsable => DataMode == DataMode.Mock
        || (HomeLatitude is not null && HomeLongitude is not null);
}
