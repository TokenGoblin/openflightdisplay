#include "domain/staleness.h"

namespace ofd {

bool isStalePosition(int64_t positionTimestampMs, int64_t nowMs) {
  return (nowMs - positionTimestampMs) > kStalePositionThresholdMs;
}

bool isConnectionDead(int64_t lastMessageAtMs, int64_t nowMs) {
  return (nowMs - lastMessageAtMs) > kDeadConnectionTimeoutMs;
}

}  // namespace ofd
