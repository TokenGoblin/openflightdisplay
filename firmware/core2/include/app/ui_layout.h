#pragma once

#include <cstdint>

namespace ofd::app::layout {

// Every pixel region for the 320x240 landscape airport-FIDS screen.
// display.cpp must not contain a layout magic number that isn't defined
// here -- see docs/CORE2_DISPLAY.md for the annotated diagram this
// mirrors.

constexpr int kScreenW = 320;
constexpr int kScreenH = 240;

// ---- header (status masthead) ----
constexpr int kHeaderH = 30;
constexpr int kHeaderTitleX = 8;
constexpr int kHeaderTitleY = 7;
constexpr int kHeaderAccentLineY = kHeaderH;  // 1px rule, full width

// Right-hand status cluster, laid out right-to-left from the screen edge.
constexpr int kHeaderRightEdge = 312;
constexpr int kHeaderBatteryPercentZoneW = 36;   // reserves room for "100%" unconditionally
constexpr int kHeaderBatteryIconGap = 4;
constexpr int kHeaderBatteryIconW = 20;
constexpr int kHeaderBatteryIconH = 11;
constexpr int kHeaderBatteryNubW = 2;
constexpr int kHeaderBatteryVisualW = kHeaderBatteryIconW + kHeaderBatteryNubW;
constexpr int kHeaderWifiGap = 8;
constexpr int kHeaderWifiIconW = 16;
constexpr int kHeaderWifiIconH = 12;

constexpr int kHeaderBatteryPercentRightX = kHeaderRightEdge;
constexpr int kHeaderBatteryIconRightX = kHeaderBatteryPercentRightX - kHeaderBatteryPercentZoneW - kHeaderBatteryIconGap;
constexpr int kHeaderBatteryIconLeftX = kHeaderBatteryIconRightX - kHeaderBatteryVisualW;
constexpr int kHeaderWifiIconRightX = kHeaderBatteryIconLeftX - kHeaderWifiGap;
constexpr int kHeaderWifiIconLeftX = kHeaderWifiIconRightX - kHeaderWifiIconW;

// Left title zone ends with margin before the right cluster begins.
constexpr int kHeaderTitleMaxRightX = kHeaderWifiIconLeftX - 10;

// ---- identity block (callsign / airline / type / ICAO) ----
constexpr int kIdentityLeftX = 10;
constexpr int kIdentityRightMarginX = 8;

constexpr int kCallsignY = 32;
constexpr int kCallsignRowH = 46;  // 32..78

constexpr int kAirlineTypeY = kCallsignY + kCallsignRowH;      // 78
constexpr int kAirlineTypeH = 22;                              // 78..100
constexpr int kTypeBadgeW = 76;
constexpr int kTypeBadgeX = kScreenW - kIdentityRightMarginX - kTypeBadgeW;  // 236
constexpr int kAirlineMaxWidth = kTypeBadgeX - kIdentityLeftX - 8;           // gap before badge

constexpr int kIcaoY = kAirlineTypeY + kAirlineTypeH;  // 100
constexpr int kIcaoH = 14;                             // 100..114

constexpr int kIdentityDividerY = kIcaoY + kIcaoH;  // 114

// ---- bottom tab bar (BtnA/BtnB/BtnC -- FLIGHT / DETAIL / SYSTEM) ----
// Present on every operational-state screen (i.e. everything shown once
// past Boot/Provisioning/LocationRequired) so page navigation always
// works the same way regardless of what's currently on screen -- see
// docs/CORE2_DISPLAY.md's "Page navigation" section.
constexpr int kTabBarH = 18;
constexpr int kTabBarY = kScreenH - kTabBarH;  // 222..240
constexpr int kTabBarColBoundaries[4] = {0, 107, 213, kScreenW};

// ---- operational grid (2 rows x 3 columns) ----
constexpr int kGridTop = kIdentityDividerY + 4;  // 118
constexpr int kGridBottom = kTabBarY;            // 222 (was kScreenH before the tab bar)
constexpr int kGridRowH = (kGridBottom - kGridTop) / 2;  // 52

constexpr int kGridColBoundaries[4] = {0, 107, 213, kScreenW};

constexpr int kGridCellPadX = 8;
constexpr int kGridLabelOffsetY = 6;   // from cell top
constexpr int kGridValueOffsetY = 19;  // from cell top, baseline for the bold value font
constexpr int kGridCaptionOffsetY = 42; // from cell top, small unit/compass/subcaption line

// ---- generic full-screen status layout (boot/searching/error/etc.) ----
constexpr int kStatusTitleY = 74;
constexpr int kStatusBodyY = 108;
constexpr int kStatusFootnoteY = 196;  // clear of the tab bar at kTabBarY (222)
constexpr int kStatusMargin = 24;

// ---- detail / system pages: a simple label/value list ----
// Label and value both use the small bitmap font (~8px tall at this
// size), so 20px/row leaves a comfortable gap -- up to 8 rows fits well
// within the header-to-tab-bar space (40 + 8*20 = 200, vs. kTabBarY =
// 222).
constexpr int kDetailTop = 40;
constexpr int kDetailRowH = 20;
constexpr int kDetailLabelX = 12;
constexpr int kDetailValueX = 130;

}  // namespace ofd::app::layout
