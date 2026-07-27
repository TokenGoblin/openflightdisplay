#include "app/wifi_provisioning.h"

#include <ArduinoJson.h>
#include <DNSServer.h>
#include <LittleFS.h>
#include <WiFi.h>

#include <cstring>

namespace ofd::app {

namespace {
constexpr const char* kWifiCredsPath = "/wifi.json";
DNSServer g_dnsServer;
constexpr uint8_t kDnsPort = 53;
IPAddress g_apIp(192, 168, 4, 1);
bool g_credentialsJustSaved = false;

// Raw scanNetworks() results, deduplicated (mesh/multi-AP networks
// broadcast the same SSID from several BSSIDs -- only the strongest is
// kept) and capped at a small, fixed size -- no heap allocation.
constexpr size_t kMaxScanResults = 40;
constexpr size_t kMaxReportedNetworks = 15;

struct ScanEntry {
  char ssid[33] = {0};
  int32_t rssi = 0;
  bool secure = false;
};

const char* kSetupPageHtml = R"HTML(
<!DOCTYPE html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>OpenFlightDisplay setup</title></head>
<body style="font-family: sans-serif; max-width: 420px; margin: 2rem auto; padding: 0 1rem;">
<h2>Connect OpenFlightDisplay to Wi-Fi</h2>
<form method="POST" action="/wifi-setup">
<label>Wi-Fi network<br>
<select id="ssidPicker" style="width:100%" onchange="if(this.value) document.getElementById('ssid').value=this.value">
<option value="">Tap "Scan" below, or type your network's name directly</option>
</select></label>
<div style="margin:0.35rem 0 0.75rem;">
<button type="button" id="scanBtn" onclick="scanNetworks()">Scan for networks</button>
<div style="font-size:0.8em;color:#666;margin-top:0.25rem;">
May briefly interrupt this connection for a second or two while it scans -- it should
reconnect on its own. If it doesn't, just rejoin this Wi-Fi network and reload the page.
</div>
</div>
<label>Network name (SSID)<br><input id="ssid" name="ssid" maxlength="32" required style="width:100%"></label><br><br>
<label>Password<br><input name="password" type="password" maxlength="64" style="width:100%"></label><br><br>
<button type="submit">Connect</button>
</form>
<script>
function signalBars(rssi) {
  if (rssi >= -60) return '████';
  if (rssi >= -70) return '███░';
  if (rssi >= -80) return '██░░';
  return '█░░░';
}
function scanNetworks() {
  var picker = document.getElementById('ssidPicker');
  var btn = document.getElementById('scanBtn');
  btn.disabled = true;
  btn.textContent = 'Scanning…';
  picker.innerHTML = '<option value="">Scanning for networks&hellip;</option>';
  fetch('/wifi-scan').then(function(r) { return r.json(); }).then(function(data) {
    var networks = data.networks || [];
    picker.innerHTML = '';
    if (networks.length === 0) {
      var msg = data.scanFailed ? 'Scan failed — type SSID below' : 'No networks found — type SSID below';
      picker.innerHTML = '<option value="">' + msg + '</option>';
    } else {
      picker.innerHTML = '<option value="">Select a network…</option>';
      networks.forEach(function(n) {
        var opt = document.createElement('option');
        opt.value = n.ssid;
        opt.textContent = signalBars(n.rssi) + '  ' + n.ssid + (n.secure ? '  🔒' : '');
        picker.appendChild(opt);
      });
    }
  }).catch(function() {
    picker.innerHTML = '<option value="">Scan failed — type SSID below</option>';
  }).finally(function() {
    btn.disabled = false;
    btn.textContent = 'Rescan';
  });
}
</script>
</body></html>
)HTML";
}  // namespace

bool loadWifiCredentials(WifiCredentials& out) {
  if (!LittleFS.exists(kWifiCredsPath)) return false;
  File f = LittleFS.open(kWifiCredsPath, "r");
  if (!f) return false;
  char buf[160];
  const size_t size = f.size();
  if (size == 0 || size >= sizeof(buf)) {
    f.close();
    return false;
  }
  const size_t read = f.readBytes(buf, size);
  f.close();
  buf[read] = '\0';

  StaticJsonDocument<160> doc;
  if (deserializeJson(doc, buf, read)) return false;
  const char* ssid = doc["ssid"] | "";
  const char* password = doc["password"] | "";
  if (std::strlen(ssid) == 0 || std::strlen(ssid) >= sizeof(out.ssid)) return false;
  std::strcpy(out.ssid, ssid);
  std::strncpy(out.password, password, sizeof(out.password) - 1);
  return true;
}

bool saveWifiCredentials(const WifiCredentials& creds) {
  StaticJsonDocument<160> doc;
  doc["ssid"] = creds.ssid;
  doc["password"] = creds.password;
  char buf[160];
  const size_t written = serializeJson(doc, buf, sizeof(buf));
  if (written == 0) return false;

  const char* tmpPath = "/wifi.json.tmp";
  File f = LittleFS.open(tmpPath, "w");
  if (!f) return false;
  const size_t actuallyWritten = f.write(reinterpret_cast<const uint8_t*>(buf), written);
  f.close();
  if (actuallyWritten != written) {
    LittleFS.remove(tmpPath);
    return false;
  }
  if (LittleFS.exists(kWifiCredsPath)) LittleFS.remove(kWifiCredsPath);
  return LittleFS.rename(tmpPath, kWifiCredsPath);
}

// Blocking (WiFi.scanNetworks() default) is fine here -- this only runs
// when the setup page's "Scan" button is explicitly tapped (not
// automatically on page load -- see kSetupPageHtml), so it never fires
// during the vulnerable window right as a phone is still associating
// with the softAP. It's still a real tradeoff worth knowing about: the
// ESP32 has one radio shared between hosting the softAP and scanning,
// so channel-hopping to scan necessarily knocks it off the AP's channel
// for the scan's duration. A phone already fully joined before the scan
// starts normally reconnects on its own once it's done; capping the
// per-channel dwell time (vs. the ~300ms default) keeps that window
// short. Found by testing on real hardware: an earlier version of this
// feature auto-scanned on page load and caused "unable to join" on the
// phone because the scan started before its connection had settled.
void handleWifiScan(AsyncWebServerRequest* request) {
  constexpr uint16_t kMsPerChannel = 120;
  Serial.printf("[wifi-scan] starting, current mode=%d, softAP stations=%u\n", WiFi.getMode(),
                WiFi.softAPgetStationNum());
  const int16_t found = WiFi.scanNetworks(/*async=*/false, /*show_hidden=*/false, /*passive=*/false,
                                           kMsPerChannel);
  Serial.printf("[wifi-scan] scanNetworks() returned %d\n", found);

  static ScanEntry entries[kMaxScanResults];
  size_t count = 0;

  for (int16_t i = 0; i < found && count < kMaxScanResults; i++) {
    const String ssid = WiFi.SSID(i);
    if (ssid.length() == 0) continue;  // hidden network -- nothing to pick

    const int32_t rssi = WiFi.RSSI(i);
    const bool secure = WiFi.encryptionType(i) != WIFI_AUTH_OPEN;

    bool merged = false;
    for (size_t j = 0; j < count; j++) {
      if (ssid.equals(entries[j].ssid)) {
        if (rssi > entries[j].rssi) entries[j].rssi = rssi;
        merged = true;
        break;
      }
    }
    if (!merged) {
      std::strncpy(entries[count].ssid, ssid.c_str(), sizeof(entries[count].ssid) - 1);
      entries[count].ssid[sizeof(entries[count].ssid) - 1] = '\0';
      entries[count].rssi = rssi;
      entries[count].secure = secure;
      count++;
    }
  }
  WiFi.scanDelete();

  // Strongest signal first -- insertion sort, count is always small.
  for (size_t i = 1; i < count; i++) {
    ScanEntry key = entries[i];
    size_t j = i;
    while (j > 0 && entries[j - 1].rssi < key.rssi) {
      entries[j] = entries[j - 1];
      j--;
    }
    entries[j] = key;
  }

  StaticJsonDocument<2048> doc;
  doc["schemaVersion"] = 1;
  doc["scanFailed"] = found < 0;
  JsonArray networks = doc.createNestedArray("networks");
  const size_t reportCount = count < kMaxReportedNetworks ? count : kMaxReportedNetworks;
  for (size_t i = 0; i < reportCount; i++) {
    JsonObject net = networks.createNestedObject();
    net["ssid"] = entries[i].ssid;
    net["rssi"] = entries[i].rssi;
    net["secure"] = entries[i].secure;
  }

  char buf[2048];
  const size_t len = serializeJson(doc, buf, sizeof(buf));
  request->send(200, "application/json", String(buf, len));
}

void startProvisioningAccessPoint(AsyncWebServer& server, const char* apName) {
  // AP_STA, not just AP -- the station radio is what scanNetworks()
  // needs, and the softAP keeps serving the captive portal throughout.
  WiFi.mode(WIFI_AP_STA);
  WiFi.softAPConfig(g_apIp, g_apIp, IPAddress(255, 255, 255, 0));
  WiFi.softAP(apName);

  g_dnsServer.start(kDnsPort, "*", g_apIp);

  server.on("/", HTTP_GET, [](AsyncWebServerRequest* request) {
    request->send(200, "text/html; charset=utf-8", kSetupPageHtml);
  });

  server.on("/wifi-scan", HTTP_GET, handleWifiScan);

  server.on("/wifi-setup", HTTP_POST, [](AsyncWebServerRequest* request) {
    if (!request->hasParam("ssid", true)) {
      request->send(400, "text/plain", "Missing ssid");
      return;
    }
    WifiCredentials creds;
    const String ssid = request->getParam("ssid", true)->value();
    const String password = request->hasParam("password", true) ? request->getParam("password", true)->value() : "";
    if (ssid.length() == 0 || ssid.length() >= sizeof(creds.ssid)) {
      request->send(400, "text/plain", "Invalid SSID");
      return;
    }
    std::strcpy(creds.ssid, ssid.c_str());
    std::strncpy(creds.password, password.c_str(), sizeof(creds.password) - 1);

    if (!saveWifiCredentials(creds)) {
      request->send(500, "text/plain", "Failed to save credentials");
      return;
    }
    g_credentialsJustSaved = true;
    request->send(200, "text/html; charset=utf-8",
                   "<html><body><p>Saved. The display will now try to connect and show a QR "
                   "code to finish pairing.</p></body></html>");
    // main.cpp's loop() notices the new credentials are saved and
    // transitions out of provisioning mode on its next iteration --
    // deliberately not rebooting from inside a request handler.
  });

  // Common captive-portal probe endpoints across platforms all just get
  // the same setup page; auto-popup behavior varies by OS/browser, and
  // manually browsing to 192.168.4.1 is the documented fallback (see
  // docs/PROVISIONING.md).
  server.onNotFound([](AsyncWebServerRequest* request) {
    request->send(200, "text/html; charset=utf-8", kSetupPageHtml);
  });

  server.begin();
}

void processProvisioningDns() { g_dnsServer.processNextRequest(); }

bool consumeWifiCredentialsJustSaved() {
  if (!g_credentialsJustSaved) return false;
  g_credentialsJustSaved = false;
  return true;
}

bool connectToWifi(const WifiCredentials& creds, uint32_t timeoutMs) {
  WiFi.mode(WIFI_STA);
  WiFi.begin(creds.ssid, creds.password);

  const uint32_t start = millis();
  while (WiFi.status() != WL_CONNECTED) {
    if (millis() - start > timeoutMs) {
      // WiFi.begin() leaves the ESP32's WiFi driver auto-reconnecting to
      // this (evidently unreachable) SSID in the background forever --
      // confirmed on real hardware by serial logs still showing
      // NO_AP_FOUND reconnect attempts *minutes* after this function had
      // already given up and the caller had moved on to provisioning
      // mode. Left alone, that ongoing STA reconnect attempt keeps the
      // radio busy enough that a later WiFi.scanNetworks() call (the
      // setup page's network picker) reliably fails. Disconnecting with
      // reconnect=false stops it cleanly so the radio is actually idle
      // once this function reports failure.
      WiFi.disconnect(true);
      return false;
    }
    delay(250);
  }
  return true;
}

}  // namespace ofd::app
