#pragma once

#include <cstdint>

namespace ofd {

// Matches services/gateway/src/lib/ranking.ts's STALE_POSITION_THRESHOLD_MS.
constexpr int64_t kStalePositionThresholdMs = 60000;

bool isStalePosition(int64_t positionTimestampMs, int64_t nowMs);

// The Core2 receives updates over WS; if it hasn't received *any* message
// (heartbeat or aircraft-update) in this long, the connection is treated
// as dead and reconnected with backoff -- matches
// packages/protocol/src/wsMessages.ts's WS_DEAD_CONNECTION_TIMEOUT_MS.
constexpr int64_t kDeadConnectionTimeoutMs = 45000;

bool isConnectionDead(int64_t lastMessageAtMs, int64_t nowMs);

}  // namespace ofd
