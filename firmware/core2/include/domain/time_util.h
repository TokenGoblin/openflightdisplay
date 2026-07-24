#pragma once

#include <cstdint>

namespace ofd {

// Minimal, dependency-free UTC-only ISO 8601 parser
// ("YYYY-MM-DDTHH:MM:SS.sssZ" or "YYYY-MM-DDTHH:MM:SSZ") -> epoch
// milliseconds. Deliberately hand-rolled instead of using time.h's
// mktime/timegm, whose timezone behavior differs between the native test
// environment and the ESP32 Arduino core -- this way the same code is
// used and tested identically on both.
//
// Returns false (and leaves outEpochMs untouched) on any malformed input,
// rather than guessing -- callers must treat that as "reject the
// message," per docs/PROTOCOL.md's bounded/fail-closed parsing rules.
bool parseIso8601ToEpochMs(const char* iso, int64_t& outEpochMs);

}  // namespace ofd
