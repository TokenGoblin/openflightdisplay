#pragma once

#include <cstdint>

namespace ofd::app::layout {

// Every pixel region for the M5Stack Tab5's 1280x720 landscape
// airport-FIDS screen. Same name-for-name contract as
// ui_layout_core2.h -- the shared renderer compiles against whichever
// profile ui_layout.h selects, so a name present in one profile and
// missing from the other is a build error, not a silently wrong screen.
//
// This is not a scaled copy of the Core2 profile. The Core2 shows one
// aircraft because one aircraft is all that fits; this panel has twelve
// times the area, so the FLIGHT page splits into a hero column (the
// nearest aircraft, same information architecture as the Core2) and a
// traffic column (a departures-board list of the other aircraft the
// provider already ranked and returned). The remaining screen states are
// the same states in the same order -- only re-proportioned.
//
// UNVERIFIED ON HARDWARE: every number below was derived from the panel
// geometry and the font metrics, not from looking at a real Tab5. See
// docs/TAB5_HARDWARE.md.

constexpr int kScreenW = 1280;
constexpr int kScreenH = 720;

// ---- header (status masthead) ----
constexpr int kHeaderH = 64;
constexpr int kHeaderTitleX = 28;
constexpr int kHeaderTitleY = 16;
constexpr int kHeaderAccentLineY = kHeaderH;

constexpr int kHeaderRightEdge = 1248;
constexpr int kHeaderIconY = 17;
constexpr int kHeaderBatteryPercentZoneW = 80;   // "100%" at the micro role's 18px cell
constexpr int kHeaderBatteryPercentY = 20;
constexpr int kHeaderBatteryIconGap = 12;
constexpr int kHeaderBatteryIconW = 52;
constexpr int kHeaderBatteryIconH = 28;
constexpr int kHeaderBatteryNubW = 5;
constexpr int kHeaderBatteryNubInsetY = 8;
constexpr int kHeaderBatteryFillInset = 4;
constexpr int kHeaderBatteryBoltThickness = 3;
constexpr int kHeaderBatteryVisualW = kHeaderBatteryIconW + kHeaderBatteryNubW;
constexpr int kHeaderWifiGap = 20;
constexpr int kHeaderWifiIconW = 40;
constexpr int kHeaderWifiIconH = 30;
constexpr int kHeaderWifiBarW = 8;
constexpr int kHeaderWifiBarStep = 13;
constexpr int kHeaderWifiBarH1 = 10;
constexpr int kHeaderWifiBarH2 = 18;
constexpr int kHeaderWifiBarH3 = 28;

constexpr int kHeaderBatteryPercentRightX = kHeaderRightEdge;
constexpr int kHeaderBatteryIconRightX = kHeaderBatteryPercentRightX - kHeaderBatteryPercentZoneW - kHeaderBatteryIconGap;
constexpr int kHeaderBatteryIconLeftX = kHeaderBatteryIconRightX - kHeaderBatteryVisualW;
constexpr int kHeaderWifiIconRightX = kHeaderBatteryIconLeftX - kHeaderWifiGap;
constexpr int kHeaderWifiIconLeftX = kHeaderWifiIconRightX - kHeaderWifiIconW;

constexpr int kHeaderTitleMaxRightX = kHeaderWifiIconLeftX - 24;

// ---- two-column split (FLIGHT page) ----
// The hero column carries exactly what the Core2 shows; the traffic
// column is additive. Everything that isn't the FLIGHT page ignores the
// split and uses the full width.
constexpr int kHeroColW = 780;
constexpr int kTrafficColX = kHeroColW;
constexpr int kTrafficColW = kScreenW - kTrafficColX;  // 500

// ---- identity block (callsign / airline / type / ICAO) ----
constexpr int kIdentityBlockW = kHeroColW;
constexpr int kIdentityLeftX = 28;
constexpr int kIdentityRightMarginX = 24;

constexpr int kCallsignY = 80;
constexpr int kCallsignRowH = 168;  // 80..248, sized for FreeSansBold24pt at scale 3

constexpr int kAirlineTypeY = kCallsignY + kCallsignRowH;  // 248
constexpr int kAirlineTypeH = 56;                          // 248..304
constexpr int kAirlineTextOffsetY = 10;
constexpr int kTypeBadgeW = 200;
constexpr int kTypeBadgeX = kIdentityBlockW - kIdentityRightMarginX - kTypeBadgeW;  // 556
constexpr int kAirlineMaxWidth = kTypeBadgeX - kIdentityLeftX - 24;

constexpr int kIcaoY = kAirlineTypeY + kAirlineTypeH;  // 304
constexpr int kIcaoH = 36;                             // 304..340
constexpr int kIcaoTextOffsetY = 6;

constexpr int kIdentityDividerY = kIcaoY + kIcaoH;  // 340

// ---- bottom tab bar (FLIGHT / DETAIL / SYSTEM) ----
// This board has no buttons under the panel, so the bar is the actual
// touch target rather than a label for one. 56px tall gives a comfortable
// finger-sized row at this panel's pixel density -- see
// board::pollPageRequest() in src/board/tab5.cpp for the hit test.
constexpr int kTabBarH = 56;
constexpr int kTabBarY = kScreenH - kTabBarH;  // 664..720
constexpr int kTabBarColBoundaries[4] = {0, 427, 854, kScreenW};
constexpr int kTabBarActiveIndicatorH = 5;

// ---- operational grid (2 rows x 3 columns, inside the hero column) ----
constexpr int kGridTop = kIdentityDividerY + 4;  // 344
constexpr int kGridBottom = kTabBarY;            // 664
constexpr int kGridRowH = (kGridBottom - kGridTop) / 2;  // 160

constexpr int kGridColBoundaries[4] = {0, 260, 520, kHeroColW};

constexpr int kGridCellPadX = 24;
constexpr int kGridLabelOffsetY = 8;     // 8..32 at the micro role's 24px cell
constexpr int kGridValueOffsetY = 38;    // 38..125 at the value role's ~87px line
constexpr int kGridCaptionOffsetY = 128; // 128..152, clear of the value above it
constexpr int kGridValueUnitGap = 12;
constexpr int kGridUnitBaselineOffsetY = 32;  // centers the unit against the tall value

constexpr int kStatusAccentBarW = 8;
constexpr int kStatusAccentBarBottomInset = 24;
constexpr int kStatusAccentTextGap = 12;

constexpr int kDegreeMarkGap = 8;
constexpr int kDegreeMarkOffsetX = 10;
constexpr int kDegreeMarkOffsetY = 12;
constexpr int kDegreeMarkRadius = 7;

// ---- nearby-traffic board (FLIGHT page, right column) ----
// The provider already fetches and ranks up to kMaxAircraftPerUpdate
// aircraft; the Core2 can only ever show items[0]. Rows are the micro
// bitmap role so the three columns line up on a fixed 18px cell without
// per-row measurement.
constexpr int kTrafficPadX = 24;
constexpr int kTrafficHeaderY = 80;
constexpr int kTrafficDividerY = 124;
constexpr int kTrafficCaptionY = 132;
constexpr int kTrafficRowTop = 170;
constexpr int kTrafficRowH = 60;
constexpr int kTrafficMaxRows = (kTabBarY - kTrafficRowTop) / kTrafficRowH;  // 8
constexpr int kTrafficCallsignX = kTrafficColX + kTrafficPadX;
constexpr int kTrafficDistRightX = kTrafficColX + 340;
constexpr int kTrafficAltRightX = kScreenW - kTrafficPadX;
constexpr int kTrafficEmptyY = kTrafficRowTop + 40;
constexpr int kTrafficRowTextOffsetY = 16;  // text top edge within a row

// ---- generic full-screen status layout (boot/searching/error/etc.) ----
constexpr int kStatusTitleY = 240;
constexpr int kStatusBodyY = 360;
constexpr int kStatusFootnoteY = 580;  // clear of the tab bar at kTabBarY (664)
constexpr int kStatusMargin = 96;
constexpr int kNoTrafficClockY = kStatusFootnoteY - 40;

// ---- detail / system pages: a simple label/value list ----
// 8 rows at 64px from kDetailTop lands at 608, clear of the tab bar.
// The value column is 836px wide against an 18px fixed cell, i.e. ~46
// characters -- enough for a 32-char SSID or a full lat/lon pair without
// ellipsizing.
constexpr int kDetailTop = 96;
constexpr int kDetailRowH = 64;
constexpr int kDetailLabelX = 40;
constexpr int kDetailValueX = 420;

// ---- Wi-Fi provisioning screen ----
constexpr int kProvisionPromptY = 200;
constexpr int kProvisionBoxY = 260;
constexpr int kProvisionBoxH = 140;
constexpr int kProvisionBoxRadius = 12;
constexpr int kProvisionBoxTextInset = 48;
constexpr int kProvisionApNameY = 300;

// ---- setup-required (pairing) screen ----
// A version-6 QR is 41 modules; at 12px each that's a 492px block, which
// is genuinely scannable across a room rather than the "hold your phone
// right up to it" affair the Core2's 164px block is.
constexpr int kQrModuleSize = 12;
constexpr int kQrX = 80;
constexpr int kQrQuietZone = 16;
constexpr int kSetupTextGapX = 80;
constexpr int kSetupLabelY = 180;
constexpr int kSetupIpY = 230;
constexpr int kSetupPathY = 340;
constexpr int kSetupHintY = 460;

// ---- OTA progress screen ----
constexpr int kOtaTitleY = 200;
constexpr int kOtaStatusY = 310;
constexpr int kOtaBarY = 400;
constexpr int kOtaBarH = 64;
constexpr int kOtaBarRadius = 12;
constexpr int kOtaBarInset = 6;
constexpr int kOtaPercentGapY = 32;

}  // namespace ofd::app::layout
