#include "app/config_store.h"

#include <ArduinoJson.h>
#include <LittleFS.h>

#include <cstring>

namespace ofd::app {

namespace {

bool atomicWrite(const char* path, const char* data, size_t len) {
  char tmpPath[64];
  std::snprintf(tmpPath, sizeof(tmpPath), "%s.tmp", path);

  File f = LittleFS.open(tmpPath, "w");
  if (!f) return false;
  const size_t written = f.write(reinterpret_cast<const uint8_t*>(data), len);
  f.close();
  if (written != len) {
    LittleFS.remove(tmpPath);
    return false;
  }

  // LittleFS.rename() overwrites the destination if it already exists,
  // giving us an atomic swap: readers only ever see the fully-old or
  // fully-new file, never a partial one.
  if (LittleFS.exists(path)) LittleFS.remove(path);
  return LittleFS.rename(tmpPath, path);
}

bool readWholeFile(const char* path, char* buf, size_t bufLen, size_t& outLen) {
  if (!LittleFS.exists(path)) return false;
  File f = LittleFS.open(path, "r");
  if (!f) return false;
  const size_t size = f.size();
  if (size == 0 || size >= bufLen) {
    f.close();
    return false;
  }
  const size_t read = f.readBytes(buf, size);
  f.close();
  buf[read] = '\0';
  outLen = read;
  return read == size;
}

}  // namespace

bool ConfigStore::loadConfig(DeviceConfig& out) {
  char buf[512];
  size_t len = 0;
  if (!readWholeFile(kConfigPath, buf, sizeof(buf), len)) return false;
  char error[64] = {0};
  return parseAndValidateDeviceConfig(buf, len, out, error, sizeof(error));
}

bool ConfigStore::saveConfig(const DeviceConfig& config) {
  char buf[512];
  const size_t written = serializeDeviceConfig(config, buf, sizeof(buf));
  if (written == 0) return false;
  return atomicWrite(kConfigPath, buf, written);
}

bool ConfigStore::loadPairingToken(char* outBuf, size_t outBufLen) {
  char buf[160];
  size_t len = 0;
  if (!readWholeFile(kPairingPath, buf, sizeof(buf), len)) return false;

  StaticJsonDocument<160> doc;
  if (deserializeJson(doc, buf, len)) return false;
  const char* token = doc["pairingToken"] | "";
  if (std::strlen(token) == 0 || std::strlen(token) >= outBufLen) return false;
  std::strcpy(outBuf, token);
  return true;
}

bool ConfigStore::savePairingToken(const char* token) {
  StaticJsonDocument<160> doc;
  doc["pairingToken"] = token;
  char buf[160];
  const size_t written = serializeJson(doc, buf, sizeof(buf));
  if (written == 0) return false;
  return atomicWrite(kPairingPath, buf, written);
}

}  // namespace ofd::app
