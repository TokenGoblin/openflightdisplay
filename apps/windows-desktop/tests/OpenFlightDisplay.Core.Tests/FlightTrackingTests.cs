namespace OpenFlightDisplay.Core.Tests;

using System.Text.RegularExpressions;
using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Tracking;
using Xunit;

/// <summary>
/// Parity tests for the flight-tracking port.
///
/// The firmware's own suite is
/// <c>firmware/display/test/native/test_flight_tracking</c>; these assert the
/// same behaviour against the same thresholds so the two implementations cannot
/// drift silently.
/// </summary>
public class FlightTrackingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Seattle-Tacoma, with a realistic field elevation.</summary>
    private static readonly Airport Seatac = new("KSEA", 47.4502, -122.3088, 433.0);

    // ---- thresholds match the firmware ----

    [Fact]
    public void Thresholds_match_the_firmware_header()
    {
        Assert.Equal(55.0, FlightTracking.ApproachRadiusKm);
        Assert.Equal(8.0, FlightTracking.LandedRadiusKm);
        Assert.Equal(500.0, FlightTracking.LandedMaxHeightFt);
        Assert.Equal(120.0, FlightTracking.LandedMaxGroundSpeedKt);
        Assert.Equal(-300.0, FlightTracking.DescentRateFtPerMin);
        Assert.Equal(TimeSpan.FromSeconds(300), FlightTracking.LostContactAfter);
        Assert.Equal(TimeSpan.FromSeconds(10), FlightTracking.MinPollInterval);
        Assert.Equal(TimeSpan.FromSeconds(300), FlightTracking.MaxPollInterval);
        Assert.Equal(15, FlightTracking.LeaveSoonWindowMinutes);
        Assert.Equal(10, FlightTracking.LateThresholdMinutes);
    }

    // ---- phases ----

    [Fact]
    public void A_flight_never_seen_is_awaiting_contact()
    {
        FlightProgress progress = FlightTracking.ComputeProgress(
            aircraft: null, Seatac, everSeen: false, secondsSinceContact: 0);

        Assert.Equal(FlightPhase.AwaitingContact, progress.Phase);
        Assert.Null(progress.MinutesRemaining);
        Assert.Null(progress.DistanceToDestinationKm);
    }

    [Fact]
    public void A_flight_never_seen_ignores_any_aircraft_passed_in()
    {
        // everSeen false means the aircraft argument is meaningless; using it
        // would put a fabricated ETA on screen for a flight that has not
        // switched its transponder on.
        FlightProgress progress = FlightTracking.ComputeProgress(
            Aircraft(47.5, -122.3, altitudeFt: 3000, groundSpeedKt: 200),
            Seatac,
            everSeen: false,
            secondsSinceContact: 0);

        Assert.Equal(FlightPhase.AwaitingContact, progress.Phase);
    }

    [Fact]
    public void A_distant_cruising_flight_is_enroute()
    {
        FlightProgress progress = Progress(
            Aircraft(45.0, -122.3, altitudeFt: 35000, groundSpeedKt: 450));

        Assert.Equal(FlightPhase.Enroute, progress.Phase);
    }

    [Fact]
    public void A_distant_flight_with_a_descent_rate_is_descending()
    {
        FlightProgress progress = Progress(
            Aircraft(45.0, -122.3, altitudeFt: 20000, groundSpeedKt: 400, verticalRate: -1800));

        Assert.Equal(FlightPhase.Descending, progress.Phase);
    }

    [Fact]
    public void A_gentle_descent_below_the_threshold_is_still_enroute()
    {
        // -300 ft/min is the boundary; -200 is not a descent for this purpose.
        FlightProgress progress = Progress(
            Aircraft(45.0, -122.3, altitudeFt: 34000, groundSpeedKt: 450, verticalRate: -200));

        Assert.Equal(FlightPhase.Enroute, progress.Phase);
    }

    [Fact]
    public void A_flight_inside_the_approach_radius_is_approaching()
    {
        // Approaching wins over descending: proximity is the more useful fact.
        FlightProgress progress = Progress(
            Aircraft(47.7, -122.31, altitudeFt: 9000, groundSpeedKt: 300, verticalRate: -1500));

        Assert.Equal(FlightPhase.Approaching, progress.Phase);
    }

    // ---- landing, judged conservatively ----

    [Fact]
    public void Near_the_field_slow_and_low_is_landed()
    {
        FlightProgress progress = Progress(
            Aircraft(47.4502, -122.3088, altitudeFt: 600, groundSpeedKt: 40));

        Assert.Equal(FlightPhase.Landed, progress.Phase);
    }

    [Fact]
    public void On_ground_is_landed_regardless_of_reported_altitude_and_speed()
    {
        FlightProgress progress = Progress(
            Aircraft(47.4502, -122.3088, altitudeFt: null, groundSpeedKt: null, onGround: true));

        Assert.Equal(FlightPhase.Landed, progress.Phase);
    }

    [Fact]
    public void A_fast_overflight_near_the_field_is_not_landed()
    {
        // A go-around or a transit. Speed alone rules it out.
        FlightProgress progress = Progress(
            Aircraft(47.4502, -122.3088, altitudeFt: 600, groundSpeedKt: 250));

        Assert.NotEqual(FlightPhase.Landed, progress.Phase);
    }

    [Fact]
    public void A_slow_but_high_aircraft_near_the_field_is_not_landed()
    {
        // 3,000 ft over the threshold is not a landing, however slow.
        FlightProgress progress = Progress(
            Aircraft(47.4502, -122.3088, altitudeFt: 3400, groundSpeedKt: 100));

        Assert.NotEqual(FlightPhase.Landed, progress.Phase);
    }

    [Fact]
    public void Height_is_judged_against_field_elevation_not_sea_level()
    {
        // Denver's ramp is at 5,433 ft. An aircraft at 5,600 ft there is on the
        // ground; measuring against sea level would call it airborne forever.
        var denver = new Airport("KDEN", 39.8617, -104.6731, 5433.0);

        FlightProgress progress = FlightTracking.ComputeProgress(
            Aircraft(39.8617, -104.6731, altitudeFt: 5600, groundSpeedKt: 20),
            denver,
            everSeen: true,
            secondsSinceContact: 5);

        Assert.Equal(FlightPhase.Landed, progress.Phase);
    }

    // ---- lost contact is not landing ----

    [Fact]
    public void Prolonged_silence_away_from_the_destination_is_lost_contact()
    {
        FlightProgress progress = FlightTracking.ComputeProgress(
            Aircraft(45.0, -122.3, altitudeFt: 35000, groundSpeedKt: 450),
            Seatac,
            everSeen: true,
            secondsSinceContact: 400);

        // Never Landed. Conflating them sends someone to the airport an hour early.
        Assert.Equal(FlightPhase.LostContact, progress.Phase);
    }

    [Fact]
    public void Silence_exactly_at_the_threshold_is_lost_contact()
    {
        FlightProgress progress = FlightTracking.ComputeProgress(
            Aircraft(45.0, -122.3, altitudeFt: 35000, groundSpeedKt: 450),
            Seatac,
            everSeen: true,
            secondsSinceContact: 300);

        Assert.Equal(FlightPhase.LostContact, progress.Phase);
    }

    [Fact]
    public void A_landed_aircraft_stays_landed_even_after_going_silent()
    {
        // Landing is checked before silence, so an aircraft that touched down
        // and stopped transmitting does not regress to "no contact".
        FlightProgress progress = FlightTracking.ComputeProgress(
            Aircraft(47.4502, -122.3088, altitudeFt: 500, groundSpeedKt: 15),
            Seatac,
            everSeen: true,
            secondsSinceContact: 900);

        Assert.Equal(FlightPhase.Landed, progress.Phase);
    }

    // ---- ETA ----

    [Fact]
    public void Eta_is_distance_over_groundspeed()
    {
        // ~185 km at 300 kt (555.6 km/h) is about 20 minutes.
        FlightProgress progress = Progress(
            Aircraft(49.115, -122.3088, altitudeFt: 30000, groundSpeedKt: 300));

        Assert.NotNull(progress.MinutesRemaining);
        Assert.InRange(progress.MinutesRemaining!.Value, 18, 22);
    }

    [Fact]
    public void An_aircraft_stopped_at_the_gate_has_a_distance_but_no_eta()
    {
        // The speed floor, rather than a division producing infinity.
        FlightProgress progress = Progress(
            Aircraft(45.0, -122.3, altitudeFt: null, groundSpeedKt: 0));

        Assert.NotNull(progress.DistanceToDestinationKm);
        Assert.Null(progress.MinutesRemaining);
    }

    [Fact]
    public void An_aircraft_with_no_reported_groundspeed_has_no_eta()
    {
        FlightProgress progress = Progress(
            Aircraft(45.0, -122.3, altitudeFt: 30000, groundSpeedKt: null));

        Assert.Null(progress.MinutesRemaining);
    }

    [Fact]
    public void No_destination_means_no_distance_and_no_eta()
    {
        FlightProgress progress = FlightTracking.ComputeProgress(
            Aircraft(45.0, -122.3, altitudeFt: 30000, groundSpeedKt: 450),
            destination: null,
            everSeen: true,
            secondsSinceContact: 5);

        Assert.Null(progress.DistanceToDestinationKm);
        Assert.Null(progress.MinutesRemaining);
        Assert.Equal(FlightPhase.Enroute, progress.Phase);
    }

    [Fact]
    public void An_absurdly_distant_eta_is_dropped_rather_than_shown()
    {
        // Beyond a day out is a data problem, not a flight worth counting to.
        FlightProgress progress = Progress(
            Aircraft(-33.9, 151.2, altitudeFt: 35000, groundSpeedKt: 2));

        Assert.Null(progress.MinutesRemaining);
    }

    // ---- departure advice ----

    [Fact]
    public void No_travel_time_configured_means_no_advice()
    {
        // Answering would require inventing a number.
        DeparturePlan plan = FlightTracking.ComputeDeparturePlan(
            new FlightProgress { Phase = FlightPhase.Enroute, MinutesRemaining = 60 },
            travelMinutes: 0,
            postLandingMinutes: 30);

        Assert.Equal(DepartureAdvice.Unknown, plan.Advice);
        Assert.Null(plan.MinutesUntilDeparture);
    }

    [Fact]
    public void No_eta_means_no_advice()
    {
        DeparturePlan plan = FlightTracking.ComputeDeparturePlan(
            new FlightProgress { Phase = FlightPhase.AwaitingContact },
            travelMinutes: 35,
            postLandingMinutes: 30);

        Assert.Equal(DepartureAdvice.Unknown, plan.Advice);
    }

    [Theory]
    // flightMinutes + postLanding - travel = minutesUntilDeparture
    [InlineData(120, 30, 35, DepartureAdvice.Wait)]       // +115
    [InlineData(20, 30, 35, DepartureAdvice.LeaveSoon)]   // +15, on the boundary
    [InlineData(5, 30, 35, DepartureAdvice.LeaveNow)]     // 0
    [InlineData(0, 30, 35, DepartureAdvice.LeaveNow)]     // -5
    [InlineData(0, 20, 35, DepartureAdvice.Late)]         // -15
    public void Departure_advice_follows_the_subtraction(
        int minutesRemaining,
        int postLandingMinutes,
        int travelMinutes,
        DepartureAdvice expected)
    {
        DeparturePlan plan = FlightTracking.ComputeDeparturePlan(
            new FlightProgress { Phase = FlightPhase.Enroute, MinutesRemaining = minutesRemaining },
            travelMinutes,
            postLandingMinutes);

        Assert.Equal(expected, plan.Advice);
    }

    [Fact]
    public void Post_landing_time_is_included_not_ignored()
    {
        // The entire point of the feature. Without the walk-out allowance this
        // would say "leave now" and send someone to stand around for 45 minutes.
        var progress = new FlightProgress { Phase = FlightPhase.Enroute, MinutesRemaining = 30 };

        DeparturePlan withAllowance =
            FlightTracking.ComputeDeparturePlan(progress, travelMinutes: 30, postLandingMinutes: 45);

        DeparturePlan without =
            FlightTracking.ComputeDeparturePlan(progress, travelMinutes: 30, postLandingMinutes: 0);

        Assert.Equal(45, withAllowance.MinutesUntilDeparture);
        Assert.Equal(0, without.MinutesUntilDeparture);
        Assert.Equal(DepartureAdvice.Wait, withAllowance.Advice);
        Assert.Equal(DepartureAdvice.LeaveNow, without.Advice);
    }

    [Fact]
    public void A_landed_flight_still_produces_advice_from_the_walk_out_time()
    {
        // The countdown to touchdown is over, but the walk-out is still running
        // and that is the part someone is actually driving to meet.
        DeparturePlan plan = FlightTracking.ComputeDeparturePlan(
            new FlightProgress { Phase = FlightPhase.Landed },
            travelMinutes: 20,
            postLandingMinutes: 40);

        Assert.Equal(20, plan.MinutesUntilDeparture);
        Assert.Equal(DepartureAdvice.Wait, plan.Advice);
    }

    [Fact]
    public void Minutes_until_departure_goes_negative_rather_than_clamping()
    {
        // Clamping would make "leave now" and "you are twenty minutes late"
        // indistinguishable.
        DeparturePlan plan = FlightTracking.ComputeDeparturePlan(
            new FlightProgress { Phase = FlightPhase.Enroute, MinutesRemaining = 0 },
            travelMinutes: 60,
            postLandingMinutes: 20);

        Assert.Equal(-40, plan.MinutesUntilDeparture);
        Assert.Equal(DepartureAdvice.Late, plan.Advice);
    }

    // ---- polling cadence ----

    [Theory]
    [InlineData(FlightPhase.Landed, 300)]
    [InlineData(FlightPhase.AwaitingContact, 120)]
    [InlineData(FlightPhase.LostContact, 60)]
    [InlineData(FlightPhase.Approaching, 10)]
    public void Poll_interval_is_driven_by_phase_first(FlightPhase phase, int expectedSeconds)
    {
        TimeSpan interval = FlightTracking.PollIntervalFor(new FlightProgress { Phase = phase });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), interval);
    }

    [Theory]
    [InlineData(120, 300)]
    [InlineData(60, 120)]
    [InlineData(20, 60)]
    [InlineData(5, 20)]
    [InlineData(2, 10)]
    public void Poll_interval_tightens_as_arrival_nears(int minutesRemaining, int expectedSeconds)
    {
        TimeSpan interval = FlightTracking.PollIntervalFor(new FlightProgress
        {
            Phase = FlightPhase.Enroute,
            MinutesRemaining = minutesRemaining,
        });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), interval);
    }

    [Fact]
    public void Poll_interval_is_never_faster_than_the_courtesy_floor()
    {
        foreach (FlightPhase phase in Enum.GetValues<FlightPhase>())
        {
            for (int minutes = 0; minutes < 200; minutes += 7)
            {
                TimeSpan interval = FlightTracking.PollIntervalFor(new FlightProgress
                {
                    Phase = phase,
                    MinutesRemaining = minutes,
                });

                Assert.InRange(interval, FlightTracking.MinPollInterval, FlightTracking.MaxPollInterval);
            }
        }
    }

    // ---- identifier normalization ----

    [Theory]
    [InlineData("UA1234", "UAL1234")]
    [InlineData("ua 1234", "UAL1234")]
    [InlineData("UA-1234", "UAL1234")]
    [InlineData("BA249", "BAW249")]
    [InlineData("dl99", "DAL99")]
    public void An_iata_flight_number_expands_to_the_icao_callsign(string input, string expected)
        => Assert.Equal(expected, FlightTracking.NormalizeFlightIdentifier(input));

    [Theory]
    [InlineData("UAL1234", "UAL1234")]
    [InlineData("ual1234", "UAL1234")]
    public void An_icao_callsign_passes_through(string input, string expected)
        => Assert.Equal(expected, FlightTracking.NormalizeFlightIdentifier(input));

    [Fact]
    public void An_unrecognised_two_letter_prefix_passes_through_rather_than_being_mangled()
    {
        // Somebody tracking a carrier missing from the table should still get a
        // literal match attempt.
        Assert.Equal("ZZ999", FlightTracking.NormalizeFlightIdentifier("zz999"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UNITED")]
    [InlineData("AB")]
    [InlineData("1234")]
    [InlineData(null)]
    public void Input_that_is_not_a_flight_identifier_is_rejected(string? input)
        => Assert.Null(FlightTracking.NormalizeFlightIdentifier(input));

    // ---- formatting ----

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0, "0")]
    [InlineData(8, "8")]
    [InlineData(47, "47")]
    [InlineData(60, "1H00")]
    [InlineData(125, "2H05")]
    public void Minutes_remaining_formats_for_glanceability(int? minutes, string expected)
        => Assert.Equal(expected, FlightTracking.FormatMinutesRemaining(minutes));

    [Fact]
    public void No_eta_formats_as_an_em_dash_not_a_zero()
    {
        // A bare "0" reads as "landing now".
        Assert.Equal("—", FlightTracking.FormatMinutesRemaining(null));
    }

    // ---- cross-language parity ----

    [Fact]
    public void The_airline_table_matches_the_firmware()
    {
        // The C# table duplicates firmware/display/src/domain/airline.cpp,
        // which docs/PROTOCOL.md warns about. This test reads the firmware's
        // table directly so the duplication cannot drift: adding a carrier in
        // one place and not the other breaks the build rather than quietly
        // making a flight untrackable on one client.
        string? firmware = FindFirmwareAirlineTable();

        // Asserted rather than skipped. This is a monorepo, the firmware is
        // always present, and a silently skipped parity test is worse than no
        // parity test — it reports green while checking nothing.
        Assert.True(
            firmware is not null,
            "firmware/display/src/domain/airline.cpp was not found. If it moved, update this " +
            "test rather than deleting it: it is what stops the C# and C++ airline tables drifting.");

        var expected = Regex
            .Matches(File.ReadAllText(firmware!), """\{"(?<icao>[A-Z]{3})",\s*"(?<iata>[A-Z0-9]*)",""")
            .Select(m => (Icao: m.Groups["icao"].Value, Iata: m.Groups["iata"].Value))
            .ToList();

        Assert.NotEmpty(expected);

        var actual = AirlineTable.All.Select(a => (a.Icao, a.Iata)).ToList();

        Assert.Equal(expected, actual);
    }

    private static string? FindFirmwareAirlineTable()
    {
        // Walk up from the test binary to the repository root.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName, "firmware", "display", "src", "domain", "airline.cpp");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    [Theory]
    [InlineData("KSEA", "KSEA")]
    [InlineData("ksea", "KSEA")]
    [InlineData("  EGLL  ", "EGLL")]
    [InlineData("yssy", "YSSY")]
    public void An_airport_code_of_four_letters_is_accepted_and_uppercased(
        string input,
        string expected)
        => Assert.Equal(expected, FlightTracking.NormalizeAirportIcao(input));

    [Theory]
    [InlineData("SEA")]      // IATA, and there is no safe expansion to KSEA
    [InlineData("LHR")]
    [InlineData("KSEAX")]
    [InlineData("K1SE")]     // digits are not ICAO airport codes
    [InlineData("KSE-")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_that_is_not_four_letters_is_rejected(string? input)
    {
        // IATA in particular is rejected rather than guessed at: the K prefix is
        // North America only, so expanding SEA to KSEA works exactly until
        // somebody tracks a flight to LHR. Failing loudly beats tracking a
        // flight to nowhere.
        Assert.Null(FlightTracking.NormalizeAirportIcao(input));
    }

    private static FlightProgress Progress(AircraftState aircraft)
        => FlightTracking.ComputeProgress(aircraft, Seatac, everSeen: true, secondsSinceContact: 5);

    private static AircraftState Aircraft(
        double lat,
        double lon,
        double? altitudeFt,
        double? groundSpeedKt,
        double? verticalRate = null,
        bool onGround = false) => new()
        {
            Provider = "test",
            IcaoHex = "abc123",
            Callsign = "UAL1234",
            Latitude = lat,
            Longitude = lon,
            GeometricAltitudeFt = altitudeFt,
            GroundSpeedKt = groundSpeedKt,
            VerticalRateFtPerMin = verticalRate,
            OnGround = onGround,
            FirstSeen = Now,
            LastSeen = Now,
            PositionTimestamp = Now,
        };
}
