#include "domain/airline.h"

#include <cstring>

namespace ofd {

namespace {

struct AirlineEntry {
  const char* code;   // 3-char ICAO prefix
  const char* name;   // human-readable name
};

// Static airline lookup table (~700 bytes). On ESP32, `const` arrays
// automatically live in flash — no PROGMEM/pgm_read_byte needed.
static const AirlineEntry kAirlines[] = {
  {"AAL", "American Airlines"},
  {"ACA", "Air Canada"},
  {"AFR", "Air France"},
  {"ANA", "All Nippon Airways"},
  {"ANZ", "Air New Zealand"},
  {"ASA", "Alaska Airlines"},
  {"BAW", "British Airways"},
  {"DAL", "Delta Air Lines"},
  {"DLH", "Lufthansa"},
  {"ENY", "Envoy Air"},
  {"FDX", "FedEx Express"},
  {"FFT", "Frontier Airlines"},
  {"GTI", "Atlas Air"},
  {"JAL", "Japan Airlines"},
  {"JBU", "JetBlue Airways"},
  {"JIA", "PSA Airlines"},
  {"KLM", "KLM"},
  {"NKS", "Spirit Airlines"},
  {"QFA", "Qantas"},
  {"QTR", "Qatar Airways"},
  {"QXE", "Horizon Air"},
  {"RPA", "Republic Airways"},
  {"SIA", "Singapore Airlines"},
  {"SKW", "SkyWest Airlines"},
  {"SWA", "Southwest Airlines"},
  {"THY", "Turkish Airlines"},
  {"UAE", "Emirates"},
  {"UAL", "United Airlines"},
  {"UPS", "UPS Airlines"},
  {"WJA", "WestJet"},
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

}  // namespace ofd