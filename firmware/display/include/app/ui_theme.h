#pragma once

#include <M5Unified.h>

#include "board/board.h"
#include "domain/display_format.h"

namespace ofd::app::theme {

// Airport-signage color palette, as RGB565. Named tokens only -- nothing
// in the renderer should reference a raw hex/RGB565 literal. See
// docs/DISPLAY_UI.md for the design rationale.
//
// The palette is deliberately board-independent: the Core2 and the Tab5
// show the same product and should look like it. Only geometry
// (ui_layout_*.h) and type sizes (the font roles below) differ.
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
// A role is a (typeface, integer scale) pair rather than a bare font
// pointer, because the two supported panels are four times apart in
// linear resolution and M5GFX only bundles the FreeSans family up to
// 24pt. The Tab5 reaches its display sizes by scaling the same typefaces
// the Core2 uses, so both boards render identical shapes at proportional
// sizes and there is exactly one set of role names for the renderer to
// reason about.
//
// All faces come from the GFXFF FreeSans family M5GFX already links in
// (GNU FreeFont, licensed under the GNU GPL v3 with the font-embedding
// exception -- redistribution as embedded glyphs in compiled firmware is
// explicitly permitted; see docs/DISPLAY_UI.md), plus the built-in Font0
// bitmap face. No font asset files are added by this project.
//
// Role A -- Identifier: the callsign only. Primary size, with one smaller
//           fallback for long callsigns (see ui::drawFitText).
// Role B -- Value: metric/status values, airline name, ICAO, type badge.
//           Bold for numeric emphasis, regular for descriptive text.
// Role C -- Micro label: uppercase field labels, header captions, units,
//           and the Tab5's traffic-list rows. Font0, the built-in bitmap
//           face -- already linked, zero extra flash, and a fixed 6x8
//           cell at scale 1 that makes column widths trivially
//           predictable.
struct FontSpec {
  const lgfx::IFont* font;
  uint8_t size;
};

// Applies a role to a drawing target. Always use this rather than a bare
// setFont(): a stale setTextSize() left over from a previous role draws
// text at wildly the wrong size, and on a board where every role has
// size 1 that mistake stays invisible until the same code runs on the
// board where they don't.
inline void applyFont(lgfx::LGFXBase& gfx, const FontSpec& spec) {
  gfx.setFont(spec.font);
  gfx.setTextSize(spec.size);
}

#if defined(OFD_BOARD_CORE2)

// 320x240. Scale 1 throughout -- these are the exact sizes the
// airport-FIDS design was drawn and hardware-tested at.
inline FontSpec FONT_IDENTIFIER_PRIMARY()  { return {&fonts::FreeSansBold24pt7b, 1}; }
inline FontSpec FONT_IDENTIFIER_FALLBACK() { return {&fonts::FreeSansBold18pt7b, 1}; }
inline FontSpec FONT_VALUE_LARGE()         { return {&fonts::FreeSansBold12pt7b, 1}; }
inline FontSpec FONT_VALUE_SMALL()         { return {&fonts::FreeSansBold9pt7b, 1}; }
inline FontSpec FONT_LABEL_REGULAR()       { return {&fonts::FreeSans9pt7b, 1}; }
inline FontSpec FONT_MICRO_LABEL()         { return {&fonts::Font0, 1}; }   // 6x8 cell
inline FontSpec FONT_MICRO_HEADER()        { return {&fonts::Font0, 2}; }   // 12x16 cell

#elif defined(OFD_BOARD_TAB5)

// 1280x720 -- 4x the linear resolution of the Core2 and, unlike a
// countertop Core2, meant to be read from across a room. Sizes are
// roughly 2-3x rather than a strict 4x: at a true 4x scale the callsign
// alone would eat the whole hero column, and Font0's blocky bitmap edges
// get conspicuous past about 3x.
inline FontSpec FONT_IDENTIFIER_PRIMARY()  { return {&fonts::FreeSansBold24pt7b, 3}; }
inline FontSpec FONT_IDENTIFIER_FALLBACK() { return {&fonts::FreeSansBold24pt7b, 2}; }
inline FontSpec FONT_VALUE_LARGE()         { return {&fonts::FreeSansBold12pt7b, 3}; }
inline FontSpec FONT_VALUE_SMALL()         { return {&fonts::FreeSansBold12pt7b, 2}; }
inline FontSpec FONT_LABEL_REGULAR()       { return {&fonts::FreeSans9pt7b, 2}; }
inline FontSpec FONT_MICRO_LABEL()         { return {&fonts::Font0, 3}; }   // 18x24 cell
inline FontSpec FONT_MICRO_HEADER()        { return {&fonts::Font0, 4}; }   // 24x32 cell

#endif

}  // namespace ofd::app::theme
