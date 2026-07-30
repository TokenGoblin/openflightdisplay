#include "domain/time_util.h"

#include <cstring>

namespace ofd {

namespace {

bool parseDigits(const char* s, int offset, int count, int& out) {
  int value = 0;
  for (int i = 0; i < count; i++) {
    char c = s[offset + i];
    if (c < '0' || c > '9') return false;
    value = value * 10 + (c - '0');
  }
  out = value;
  return true;
}

// Howard Hinnant's civil_from_days algorithm (public domain), computing
// days since 1970-01-01 for a given proleptic-Gregorian (y, m, d).
int64_t daysFromCivil(int64_t y, int m, int d) {
  y -= (m <= 2) ? 1 : 0;
  const int64_t era = (y >= 0 ? y : y - 399) / 400;
  const int64_t yoe = y - era * 400;                                     // [0, 399]
  const int64_t doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;     // [0, 365]
  const int64_t doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;              // [0, 146096]
  return era * 146097 + doe - 719468;
}

bool isLeapYear(int y) { return (y % 4 == 0 && y % 100 != 0) || (y % 400 == 0); }

int daysInMonth(int y, int m) {
  static const int kDays[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
  if (m == 2 && isLeapYear(y)) return 29;
  return kDays[m - 1];
}

}  // namespace

bool parseIso8601ToEpochMs(const char* iso, int64_t& outEpochMs) {
  if (iso == nullptr) return false;
  const size_t len = std::strlen(iso);
  // Minimum: "YYYY-MM-DDTHH:MM:SSZ" = 20 chars. Maximum supported:
  // "YYYY-MM-DDTHH:MM:SS.sssZ" = 24 chars.
  if (len != 20 && len != 24) return false;
  if (iso[4] != '-' || iso[7] != '-' || iso[10] != 'T' || iso[13] != ':' || iso[16] != ':') return false;
  if (iso[len - 1] != 'Z') return false;
  if (len == 24 && iso[19] != '.') return false;

  int year, month, day, hour, minute, second, millis = 0;
  if (!parseDigits(iso, 0, 4, year)) return false;
  if (!parseDigits(iso, 5, 2, month)) return false;
  if (!parseDigits(iso, 8, 2, day)) return false;
  if (!parseDigits(iso, 11, 2, hour)) return false;
  if (!parseDigits(iso, 14, 2, minute)) return false;
  if (!parseDigits(iso, 17, 2, second)) return false;
  if (len == 24 && !parseDigits(iso, 20, 3, millis)) return false;

  if (month < 1 || month > 12) return false;
  if (day < 1 || day > daysInMonth(year, month)) return false;
  if (hour > 23 || minute > 59 || second > 60) return false;  // 60 tolerates a leap second

  const int64_t days = daysFromCivil(year, month, day);
  const int64_t secondsOfDay = hour * 3600LL + minute * 60LL + second;
  outEpochMs = days * 86400000LL + secondsOfDay * 1000LL + millis;
  return true;
}

}  // namespace ofd
