#pragma once

namespace ofd {

// How well the aircraft data source is currently working.
//
//   Ok          - a recent poll succeeded and returned usable data.
//   Degraded    - the source answered, but not usefully (unparseable or
//                 oversized response, HTTP error status).
//   Unavailable - the source could not be reached at all.
//
// The distinction is load-bearing: docs/PRODUCT_REQUIREMENTS.md forbids
// an indefinite spinner, so every one of these maps to an explicit
// on-screen state rather than to "keep waiting".
//
// This used to live in domain/protocol.h alongside a parser for the
// gateway's WebSocket frames. That parser -- and its 4KB static
// JSON document -- became unreachable when the firmware started polling
// adsb.lol directly, and was removed; this enum was the only part still
// in use. See the git history for the WebSocket message contract if a
// gateway-mediated mode returns.
enum class ProviderHealth {
  Ok,
  Degraded,
  Unavailable,
};

}  // namespace ofd
