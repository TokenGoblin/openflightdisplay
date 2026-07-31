namespace OpenFlightDisplay.Core.Aircraft;

/// <summary>
/// Broad aircraft class. Mirrors <c>AircraftCategorySchema</c> in
/// <c>packages/shared-models/src/aircraft.ts</c>.
/// </summary>
/// <remarks>
/// Provider completeness for this field varies wildly. <see cref="Unknown"/>
/// means "the provider said something we did not recognise"; a field the
/// provider omitted entirely is represented as <c>null</c> on the model, not as
/// <see cref="Unknown"/>. Collapsing those two would make a category filter
/// silently exclude aircraft that were never categorised at all.
/// </remarks>
public enum AircraftCategory
{
    FixedWing,
    Rotorcraft,
    Glider,
    Balloon,
    Drone,
    GroundVehicle,
    Unknown,
}

/// <summary>
/// Transponder-declared emergency. Mirrors <c>EmergencyStateSchema</c>.
/// </summary>
public enum EmergencyState
{
    None,
    General,
    Medical,
    MinimumFuel,
    NoCommunications,
    UnlawfulInterference,
    Downed,
}

/// <summary>
/// Why a record is less trustworthy than it looks. Mirrors
/// <c>DataQualityFlagSchema</c>.
/// </summary>
/// <remarks>
/// Per the product rule in <c>docs/PRODUCT_REQUIREMENTS.md</c>, missing
/// enrichment never suppresses an otherwise valid aircraft — these flags exist
/// so the UI can *show* the gap rather than hide the aircraft.
/// </remarks>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "A plural 'Flags' name is the documented convention for a [Flags] enum, and " +
                    "this type deliberately mirrors 'dataQualityFlags' in " +
                    "packages/shared-models/src/aircraft.ts. Renaming it would break that mirror " +
                    "for a naming rule that does not apply to flags enums.")]
public enum DataQualityFlags
{
    None = 0,
    NoPosition = 1 << 0,
    NoCallsign = 1 << 1,
    NoAltitude = 1 << 2,
    StalePosition = 1 << 3,
    EstimatedPosition = 1 << 4,
}

/// <summary>Vertical trend derived from vertical rate.</summary>
public enum VerticalTrend
{
    /// <summary>No vertical rate reported. Distinct from <see cref="Level"/>.</summary>
    Unknown,
    Climbing,
    Descending,
    Level,
    OnGround,
}
