#pragma once

#include <cstddef>
#include <cstdint>

#include "domain/monitoring_area.h"

namespace ofd {

// Firmware-side mirror of packages/shared-models's DeviceConfiguration,
// scoped to what the Core2 itself needs (see docs/PROTOCOL.md's
// GET/PUT /api/v1/config). Fixed-size buffers only -- no heap string
// growth in the config path.
struct DeviceConfig {
  char deviceId[32] = {0};
  char deviceName[64] = "OpenFlightDisplay";

  bool hasGatewayUrl = false;
  char gatewayUrl[128] = {0};

  bool hasMonitoringArea = false;
  CircleMonitoringArea monitoringArea;

  uint8_t brightness = 200;
};

// Parses and validates a UTF-8 JSON config payload (the "config" object
// from PUT /api/v1/config -- see docs/PROTOCOL.md) into `out`. Returns
// false and writes a short reason into errorOut on any invalid, corrupt,
// or unsupported-shape input (e.g. a non-circle monitoringArea.kind) --
// callers must leave any previously-stored config untouched and surface
// the error rather than partially applying the new one (fail closed, per
// docs/ARCHITECTURE.md's atomic-config-write requirement).
bool parseAndValidateDeviceConfig(const char* json, size_t len, DeviceConfig& out, char* errorOut,
                                   size_t errorOutLen);

// Serializes `config` back to JSON (used both for persisting to LittleFS
// and for responding to GET /api/v1/config). Returns the number of bytes
// written (excluding NUL), or 0 if `bufLen` was too small.
size_t serializeDeviceConfig(const DeviceConfig& config, char* buf, size_t bufLen);

}  // namespace ofd
