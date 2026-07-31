#pragma once

#include <cstddef>

namespace ofd {

// Resolves an ICAO airline designator (the first 3 letters of a callsign)
// to a human-readable airline name. Returns the airline name, or nullptr
// if the prefix is not recognised.
//
// The lookup table lives in PROGMEM to keep flash usage ~700 bytes.
// Prefixes are stored uppercase; callsigns are normalised before lookup.
//
// A nullptr return means "not a recognised airline prefix" — callers
// should display the raw callsign in that case, not a placeholder.
const char* resolveAirlineName(const char* callsign);

// Extracts the 3-letter ICAO airline prefix from a callsign (e.g.
// "UAL1234" → "UAL"). Returns an empty string if the callsign doesn't
// start with 3 alphabetic characters.
void extractAirlinePrefix(const char* callsign, char* out, size_t outLen);

// Maps a 2-character IATA airline code to its 3-letter ICAO designator
// ("UA" → "UAL", case-insensitive). Returns nullptr if the code isn't in
// the table.
//
// Needed because the two codes address different audiences: ADS-B
// broadcasts ICAO, while boarding passes and arrivals boards print IATA.
// Flight tracking accepts what the user actually has to hand and
// translates — see domain/flight_tracking.h's normalizeFlightIdentifier().
const char* icaoForIataAirline(const char* iata);

}  // namespace ofd