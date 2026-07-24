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

const char* kSetupPageHtml = R"HTML(
<!DOCTYPE html><html><head><meta name="viewport" content="width=device-width, initial-scale=1">
<title>OpenFlightDisplay setup</title></head>
<body style="font-family: sans-serif; max-width: 420px; margin: 2rem auto; padding: 0 1rem;">
<h2>Connect OpenFlightDisplay to Wi-Fi</h2>
<form method="POST" action="/wifi-setup">
<label>Network name (SSID)<br><input name="ssid" maxlength="32" required style="width:100%"></label><br><br>
<label>Password<br><input name="password" type="password" maxlength="64" style="width:100%"></label><br><br>
<button type="submit">Connect</button>
</form>
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

void startProvisioningAccessPoint(AsyncWebServer& server, const char* apName) {
  WiFi.mode(WIFI_AP);
  WiFi.softAPConfig(g_apIp, g_apIp, IPAddress(255, 255, 255, 0));
  WiFi.softAP(apName);

  g_dnsServer.start(kDnsPort, "*", g_apIp);

  server.on("/", HTTP_GET, [](AsyncWebServerRequest* request) {
    request->send(200, "text/html", kSetupPageHtml);
  });

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
    request->send(200, "text/html",
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
    request->send(200, "text/html", kSetupPageHtml);
  });

  server.begin();
}

void processProvisioningDns() { g_dnsServer.processNextRequest(); }

bool connectToWifi(const WifiCredentials& creds, uint32_t timeoutMs) {
  WiFi.mode(WIFI_STA);
  WiFi.begin(creds.ssid, creds.password);

  const uint32_t start = millis();
  while (WiFi.status() != WL_CONNECTED) {
    if (millis() - start > timeoutMs) return false;
    delay(250);
  }
  return true;
}

}  // namespace ofd::app
