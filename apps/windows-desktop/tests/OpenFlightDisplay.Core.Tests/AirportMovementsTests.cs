namespace OpenFlightDisplay.Core.Tests;

using OpenFlightDisplay.Core.Aircraft;
using OpenFlightDisplay.Core.Tracking;
using Xunit;

/// <summary>
/// Classifying observed traffic around an airport.
/// </summary>
/// <remarks>
/// The board this feeds is deliberately not a flight-information display. These
/// tests pin the cases where an honest "we cannot tell" must not quietly become
/// a confident claim.
/// </remarks>
public class AirportMovementsTests
{
    /// <summary>Salt Lake City: a field at 4,227 ft, which is why elevation matters.</summary>
    private static readonly Airport Slc = new("KSLC", 40.7884, -111.9778, 4227, "Salt Lake City");

    private static readonly DateTimeOffset Now = new(2026, 8, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_descending_aircraft_near_the_field_is_arriving()
    {
        var movement = Classify(At(40.85, -111.98, altitudeFt: 8000, verticalRate: -900));

        Assert.Equal(MovementKind.Arriving, movement.Kind);
    }

    [Fact]
    public void A_climbing_aircraft_near_the_field_is_departing()
    {
        var movement = Classify(At(40.85, -111.98, altitudeFt: 8000, verticalRate: 2000));

        Assert.Equal(MovementKind.Departing, movement.Kind);
    }

    [Fact]
    public void Height_is_measured_against_the_field_not_sea_level()
    {
        // The reason the airport lookup fetches elevation. At Salt Lake City an
        // aircraft at 5,000 ft is 773 ft above the field, not 5,000.
        var movement = Classify(At(40.80, -111.98, altitudeFt: 5000, verticalRate: 1500));

        Assert.Equal(773, movement.HeightAboveFieldFt!.Value, 0);
    }

    [Fact]
    public void A_climb_out_of_a_high_field_is_not_mistaken_for_an_overflight()
    {
        // Judged against sea level, an aircraft 2,000 ft above Salt Lake City is
        // at 6,227 ft and would look like traffic passing over a sea-level
        // airport. Against the field it is plainly a departure.
        var movement = Classify(At(40.80, -111.98, altitudeFt: 6227, verticalRate: 2500));

        Assert.Equal(MovementKind.Departing, movement.Kind);
        Assert.Equal(2000, movement.HeightAboveFieldFt!.Value, 0);
    }

    [Fact]
    public void An_aircraft_with_no_vertical_rate_is_unknown_not_assumed_level()
    {
        // "We cannot tell" and "passing through" are different facts. Collapsing
        // them would put a confident label on a guess.
        var movement = Classify(At(40.85, -111.98, altitudeFt: 8000, verticalRate: null));

        Assert.Equal(MovementKind.Unknown, movement.Kind);
    }

    [Fact]
    public void High_traffic_descending_across_the_area_is_an_overflight()
    {
        // Without a ceiling every airliner starting its descent over the state
        // would appear on this board as an arrival.
        var movement = Classify(At(40.85, -111.98, altitudeFt: 33000, verticalRate: -1800));

        Assert.Equal(MovementKind.Overflight, movement.Kind);
    }

    [Fact]
    public void Ground_traffic_uses_the_reported_flag_not_a_low_altitude()
    {
        // An aircraft on short final is not on the ground. Inferring it from
        // altitude would hide the arrival everyone is waiting for.
        var onGround = Classify(At(40.789, -111.978, altitudeFt: 4230, verticalRate: 0, onGround: true));
        var shortFinal = Classify(At(40.80, -111.978, altitudeFt: 4400, verticalRate: -700));

        Assert.Equal(MovementKind.OnGround, onGround.Kind);
        Assert.Equal(MovementKind.Arriving, shortFinal.Kind);
    }

    [Fact]
    public void Aircraft_beyond_the_radius_are_not_on_the_board_at_all()
    {
        // Denver, ~600 km away.
        Assert.Null(AirportMovements.Classify(
            At(39.86, -104.67, altitudeFt: 8000, verticalRate: -900), Slc));
    }

    [Fact]
    public void The_board_puts_movements_before_overflights()
    {
        // What somebody watching an airport actually cares about.
        var board = AirportMovements.Build(
            [
                At(40.85, -111.98, altitudeFt: 33000, verticalRate: -1800),  // overflight
                At(40.86, -111.98, altitudeFt: 7000, verticalRate: 2000),    // departing
                At(40.84, -111.98, altitudeFt: 6000, verticalRate: -900),    // arriving
            ],
            Slc);

        Assert.Equal(MovementKind.Arriving, board[0].Kind);
        Assert.Equal(MovementKind.Departing, board[1].Kind);
        Assert.Equal(MovementKind.Overflight, board[2].Kind);
    }

    [Fact]
    public void Nothing_is_hidden_from_the_board()
    {
        // Omitting overflights would make the board disagree with the radar with
        // no explanation, which is the quiet inconsistency the project forbids.
        var board = AirportMovements.Build(
            [
                At(40.85, -111.98, altitudeFt: 33000, verticalRate: -1800),
                At(40.84, -111.98, altitudeFt: 6000, verticalRate: null),
            ],
            Slc);

        Assert.Equal(2, board.Count);
    }

    [Fact]
    public void A_departure_shows_no_minutes_away()
    {
        // "12 min" beside a departing aircraft reads as an arrival time. It is
        // the distance it has already covered, which is not what anyone would
        // assume.
        Assert.Equal("—", AirportMovements.FormatMinutesAway(MovementKind.Departing, 12));
        Assert.Contains("12", AirportMovements.FormatMinutesAway(MovementKind.Arriving, 12), StringComparison.Ordinal);
    }

    [Fact]
    public void An_arrival_estimate_is_marked_as_approximate()
    {
        // It ignores routing, approach paths and holding - exactly what makes a
        // real arrival time differ - so it must not read as an ETA.
        Assert.StartsWith("~", AirportMovements.FormatMinutesAway(MovementKind.Arriving, 8), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unreported_altitude_renders_as_a_dash_not_zero()
        => Assert.Equal("—", AirportMovements.FormatHeight(null));

    [Fact]
    public void Every_movement_kind_has_a_word()
    {
        foreach (MovementKind kind in Enum.GetValues<MovementKind>())
        {
            Assert.False(string.IsNullOrWhiteSpace(AirportMovements.KindWord(kind)));
        }
    }

    private static AirportMovement Classify(AircraftState aircraft)
        => AirportMovements.Classify(aircraft, Slc)!.Value;

    private static AircraftState At(
        double lat,
        double lon,
        double? altitudeFt,
        double? verticalRate,
        bool onGround = false) => new()
        {
            Provider = "test",
            IcaoHex = "abc123",
            Callsign = "TST123",
            Latitude = lat,
            Longitude = lon,
            GeometricAltitudeFt = altitudeFt,
            GroundSpeedKt = 250,
            VerticalRateFtPerMin = verticalRate,
            OnGround = onGround,
            FirstSeen = Now,
            LastSeen = Now,
            PositionTimestamp = Now,
        };
}
