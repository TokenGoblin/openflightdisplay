#pragma once

#include <M5Unified.h>

#include "domain/display_format.h"

namespace ofd::app::theme {

// Airport-signage color palette, as RGB565. Named tokens only -- nothing
// in display.cpp should reference a raw hex/RGB565 literal. See
// docs/CORE2_DISPLAY.md for the design rationale.
//
// M5GFX's color565() is a constexpr-friendly helper on M5Canvas/LGFXBase,
// but building the palette needs it before any canvas exists, so this
// uses lgfx's free-standing packer directly.
constexpr uint16_t rgb(uint8_t r, uint8_t g, uint8_t b) {
  return static_cast<uint16_t>(((r & 0xF8) << 8) | ((g & 0xFC) << 3) | (b >> 3));
}

constexpr uint16_t COLOR_BACKGROUND     = rgb(6, 10, 20);     // near-black navy
constexpr uint16_t COLOR_HEADER_BG      = rgb(15, 23, 38);    // slightly lighter navy
constexpr uint16_t COLOR_TEXT_PRIMARY   = rgb(245, 247, 250); // warm white
constexpr uint16_t COLOR_TEXT_SECONDARY = rgb(148, 163, 184); // cool blue-gray
constexpr uint16_t COLOR_TEXT_DIM       = rgb(90, 101, 120);  // dimmer secondary
constexpr uint16_t COLOR_GRID           = rgb(40, 50, 68);    // separators/borders
constexpr uint16_t COLOR_ACCENT         = rgb(56, 142, 255);  // aviation blue
constexpr uint16_t COLOR_GOOD           = rgb(52, 199, 89);   // live / normal
constexpr uint16_t COLOR_CAUTION        = rgb(255, 176, 32);  // stale / low battery
constexpr uint16_t COLOR_CRITICAL       = rgb(255, 69, 58);   // emergency / critical

inline uint16_t colorForRole(ofd::StatusColorRole role) {
  switch (role) {
    case ofd::StatusColorRole::Good: return COLOR_GOOD;
    case ofd::StatusColorRole::Caution: return COLOR_CAUTION;
    case ofd::StatusColorRole::Critical: return COLOR_CRITICAL;
    case ofd::StatusColorRole::Neutral: default: return COLOR_TEXT_SECONDARY;
  }
}

// ---- font roles ----
//
// Three roles, five concrete sizes -- all from the GFXFF FreeSans family
// M5GFX already links in (GNU FreeFont, licensed under the GNU GPL v3
// with the font-embedding exception -- redistribution as embedded glyphs
// in compiled firmware is explicitly permitted; see docs/CORE2_DISPLAY.md).
// No new font asset files were added for this UI.
//
// Role A -- Identifier: the callsign only. Primary size, with one smaller
//           fallback for long callsigns (see ui::fitTextToWidth).
// Role B -- Value: metric/status values, airline name, ICAO, type badge.
//           Bold for numeric emphasis, regular for descriptive text.
// Role C -- Micro label: uppercase field labels, header captions, units.
//           The built-in bitmap font (already linked, zero extra flash).
inline const lgfx::IFont* FONT_IDENTIFIER_PRIMARY()  { return &fonts::FreeSansBold24pt7b; }
inline const lgfx::IFont* FONT_IDENTIFIER_FALLBACK() { return &fonts::FreeSansBold18pt7b; }
inline const lgfx::IFont* FONT_VALUE_LARGE()         { return &fonts::FreeSansBold12pt7b; }
inline const lgfx::IFont* FONT_VALUE_SMALL()         { return &fonts::FreeSansBold9pt7b; }
inline const lgfx::IFont* FONT_LABEL_REGULAR()       { return &fonts::FreeSans9pt7b; }
// constexpr, not `inline const` -- the project's build_flags request
// -std=gnu++17, but the ESP32 Arduino core's own build script appends an
// unconditional -std=gnu++11 afterwards that wins (last flag on the
// command line), so inline variables (a C++17 feature) aren't reliably
// available here. constexpr integral constants have always been
// single-definition-safe in a header, in any standard version.
constexpr uint8_t FONT_MICRO_GLCD_SIZE_LABEL = 1;   // used with the default bitmap font
constexpr uint8_t FONT_MICRO_GLCD_SIZE_HEADER = 2;

}  // namespace ofd::app::theme
