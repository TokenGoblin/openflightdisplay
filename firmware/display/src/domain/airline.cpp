#include "domain/airline.h"

#include <cstring>

namespace ofd {

namespace {

struct AirlineEntry {
  const char* code;   // 3-char ICAO prefix, as broadcast in the callsign
  const char* iata;   // 2-char IATA code, as printed on a boarding pass
  const char* name;   // human-readable name
};

// Static airline lookup table (~800 bytes). On ESP32, `const` arrays
// automatically live in flash — no PROGMEM/pgm_read_byte needed.
//
// The IATA column exists because the two codes serve different masters:
// ADS-B broadcasts the ICAO designator ("UAL1234"), while every boarding
// pass, arrivals board and text message from the person you're collecting
// says IATA ("UA1234"). Flight tracking has to accept what the user has
// in front of them, so domain/flight_tracking.h's
// normalizeFlightIdentifier() translates one to the other through this
// table. A few carriers have no IATA code assigned (or none in common
// use); "" means "don't match this row by IATA".
static const AirlineEntry kAirlines[] = {
  {"AAL", "AA", "American Airlines"},
  {"ACA", "AC", "Air Canada"},
  {"AFR", "AF", "Air France"},
  {"ANA", "NH", "All Nippon Airways"},
  {"ANZ", "NZ", "Air New Zealand"},
  {"ASA", "AS", "Alaska Airlines"},
  {"BAW", "BA", "British Airways"},
  {"DAL", "DL", "Delta Air Lines"},
  {"DLH", "LH", "Lufthansa"},
  {"ENY", "MQ", "Envoy Air"},
  {"FDX", "FX", "FedEx Express"},
  {"FFT", "F9", "Frontier Airlines"},
  {"GTI", "5Y", "Atlas Air"},
  {"JAL", "JL", "Japan Airlines"},
  {"JBU", "B6", "JetBlue Airways"},
  {"JIA", "OH", "PSA Airlines"},
  {"KLM", "KL", "KLM"},
  {"NKS", "NK", "Spirit Airlines"},
  {"QFA", "QF", "Qantas"},
  {"QTR", "QR", "Qatar Airways"},
  {"QXE", "QX", "Horizon Air"},
  {"RPA", "YX", "Republic Airways"},
  {"SIA", "SQ", "Singapore Airlines"},
  {"SKW", "OO", "SkyWest Airlines"},
  {"SWA", "WN", "Southwest Airlines"},
  {"THY", "TK", "Turkish Airlines"},
  {"UAE", "EK", "Emirates"},
  {"UAL", "UA", "United Airlines"},
  {"UPS", "5X", "UPS Airlines"},
  {"WJA", "WS", "WestJet"},
};

constexpr size_t kAirlineCount = sizeof(kAirlines) / sizeof(kAirlines[0]);

}  // namespace

void extractAirlinePrefix(const char* callsign, char* out, size_t outLen) {
  if (outLen < 4) return;
  out[0] = '\0';

  if (callsign == nullptr) return;

  size_t i = 0;
  for (; i < 3; i++) {
    const char c = callsign[i];
    if (c >= 'a' && c <= 'z') {
      out[i] = static_cast<char>(c - 'a' + 'A');
    } else if (c >= 'A' && c <= 'Z') {
      out[i] = c;
    } else {
      out[0] = '\0';
      return;
    }
  }
  out[3] = '\0';
}

const char* resolveAirlineName(const char* callsign) {
  if (callsign == nullptr || callsign[0] == '\0') return nullptr;

  char prefix[4];
  extractAirlinePrefix(callsign, prefix, sizeof(prefix));
  if (prefix[0] == '\0') return nullptr;

  for (size_t i = 0; i < kAirlineCount; i++) {
    if (std::strcmp(prefix, kAirlines[i].code) == 0) {
      return kAirlines[i].name;
    }
  }
  return nullptr;
}

const char* icaoForIataAirline(const char* iata) {
  if (iata == nullptr || iata[0] == '\0' || iata[1] == '\0' || iata[2] != '\0') return nullptr;

  char upper[3];
  for (int i = 0; i < 2; i++) {
    const char c = iata[i];
    upper[i] = (c >= 'a' && c <= 'z') ? static_cast<char>(c - 'a' + 'A') : c;
  }
  upper[2] = '\0';

  for (size_t i = 0; i < kAirlineCount; i++) {
    // Skip rows with no IATA code rather than matching them against an
    // empty needle.
    if (kAirlines[i].iata[0] == '\0') continue;
    if (std::strcmp(upper, kAirlines[i].iata) == 0) {
      return kAirlines[i].code;
    }
  }
  return nullptr;
}

}  // namespace ofd