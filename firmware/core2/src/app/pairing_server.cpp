#include "app/pairing_server.h"
#include "app/wifi_provisioning.h"

#include <ArduinoJson.h>
#include <LittleFS.h>
#include <WiFi.h>
#include <esp_system.h>

#include <cstdio>
#include <cstring>

namespace ofd::app {

namespace {

constexpr size_t kBodyBufferCapacity = 1024;
char g_bodyBuffer[kBodyBufferCapacity];

StaticJsonDocument<128> g_errorDoc;
StaticJsonDocument<192> g_pairRequestDoc;
StaticJsonDocument<192> g_pairResponseDoc;
StaticJsonDocument<256> g_statusDoc;
StaticJsonDocument<kBodyBufferCapacity> g_configWrapperDoc;

void generatePairingToken(char* out, size_t outLen) {
  static const char kHex[] = "0123456789abcdef";
  size_t i = 0;
  while (i + 8 <= outLen - 1) {
    const uint32_t r = esp_random();
    for (int b = 0; b < 8 && i < outLen - 1; b++, i++) {
      out[i] = kHex[(r >> (b * 4)) & 0xF];
    }
  }
  out[i] = '\0';
}

bool checkBearerToken(AsyncWebServerRequest* request, const AppContext& ctx) {
  if (!ctx.hasPairingToken) return false;
  if (!request->hasHeader("Authorization")) return false;
  const String header = request->getHeader("Authorization")->value();
  if (!header.startsWith("Bearer ")) return false;
  const String token = header.substring(7);
  return token.equals(ctx.pairingToken);
}

void sendJsonError(AsyncWebServerRequest* request, int code, const char* error) {
  g_errorDoc.clear();
  g_errorDoc["schemaVersion"] = 1;
  g_errorDoc["error"] = error;
  char buf[128];
  const size_t len = serializeJson(g_errorDoc, buf, sizeof(buf));
  request->send(code, "application/json", String(buf, len));
}

const char* wifiStateToString(WifiState s) {
  switch (s) {
    case WifiState::Connected:    return "connected";
    case WifiState::Provisioning: return "provisioning";
    default:                      return "disconnected";
  }
}

const char* providerStateToString(ProviderHealth h) {
  switch (h) {
    case ProviderHealth::Ok:          return "ok";
    case ProviderHealth::Degraded:    return "degraded";
    case ProviderHealth::Unavailable: return "unavailable";
    default:                          return "unknown";
  }
}

}  // namespace

// ---- iOS-inspired dashboard builder (all strings in RAM, no PROGMEM) ----

static void appendHead(String& out, const char* title, bool includeRefresh) {
  out += "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"UTF-8\">";
  out += "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1,viewport-fit=cover\">";
  out += "<title>";
  out += title;
  out += "</title>";
  if (includeRefresh) out += "<meta http-equiv=\"refresh\" content=\"15\">";
  out += "<style>";
  out += ":root{";
  out += "--bg:#000;--card-bg:#1C1C1E;--card-border:rgba(255,255,255,0.08);";
  out += "--text:#FFF;--text2:#8E8E93;--blue:#0A84FF;--green:#30D158;";
  out += "--red:#FF453A;--orange:#FF9F0A;--gray:#636366;--separator:rgba(84,84,88,0.65);";
  out += "}*{margin:0;padding:0;box-sizing:border-box}";
  out += "body{font-family:-apple-system,BlinkMacSystemFont,'SF Pro Display','SF Pro Text','Helvetica Neue',sans-serif;";
  out += "background:var(--bg);color:var(--text);-webkit-font-smoothing:antialiased;";
  out += "min-height:100vh;padding:0 16px 40px}";
  out += ".nav{position:sticky;top:0;z-index:10;background:rgba(0,0,0,0.85);";
  out += "backdrop-filter:blur(20px);-webkit-backdrop-filter:blur(20px);";
  out += "margin:0 -16px;padding:12px 16px;border-bottom:0.5px solid var(--separator);";
  out += "display:flex;align-items:center;justify-content:space-between}";
  out += ".nav-title{font-size:20px;font-weight:700;letter-spacing:-0.2px}";
  out += ".nav-tabs{display:flex;gap:4px}";
  out += ".nav-tab{font-size:14px;color:var(--blue);text-decoration:none;padding:6px 14px;border-radius:20px;font-weight:500;transition:background 0.2s}";
  out += ".nav-tab.active{background:var(--blue);color:#FFF;font-weight:600}";
  out += ".container{max-width:520px;margin:0 auto;padding-top:8px}";
  out += ".card{background:var(--card-bg);border-radius:14px;border:0.5px solid var(--card-border);margin:12px 0;overflow:hidden}";
  out += ".card-header{font-size:13px;font-weight:600;color:var(--text2);text-transform:uppercase;letter-spacing:0.5px;padding:14px 16px 6px}";
  out += ".card-row{display:flex;align-items:center;justify-content:space-between;padding:11px 16px;min-height:44px}";
  out += ".card-row + .card-row{border-top:0.5px solid var(--separator)}";
  out += ".row-label{font-size:16px;color:var(--text)}";
  out += ".row-value{font-size:16px;color:var(--text2);text-align:right;max-width:55%;word-break:break-all}";
  out += ".row-value.mono{font-variant-numeric:tabular-nums;font-family:SF Mono,Menlo,monospace;font-size:14px}";
  out += ".pill{display:inline-flex;align-items:center;gap:5px;font-size:13px;font-weight:600;padding:3px 10px;border-radius:20px}";
  out += ".pill::before{content:'';display:inline-block;width:8px;height:8px;border-radius:50%;background:currentColor}";
  out += ".pill-ok{color:var(--green);background:rgba(48,209,88,0.12)}";
  out += ".pill-warn{color:var(--orange);background:rgba(255,159,10,0.12)}";
  out += ".pill-err{color:var(--red);background:rgba(255,69,58,0.12)}";
  out += ".pill-neutral{color:var(--gray);background:rgba(99,99,102,0.12)}";
  out += "input{font:inherit;width:100%;padding:12px;margin:4px 0;border-radius:10px;border:0.5px solid var(--separator);background:rgba(118,118,128,0.12);color:var(--text);font-size:16px;outline:none;-webkit-appearance:none}";
  out += "input:focus{border-color:var(--blue);background:rgba(10,132,255,0.08)}";
  out += ".btn{font:inherit;font-size:16px;font-weight:600;padding:12px 20px;border-radius:10px;border:none;cursor:pointer;width:100%;text-align:center;transition:opacity 0.2s}";
  out += ".btn:active{opacity:0.7}";
  out += ".btn-primary{background:var(--blue);color:#FFF}";
  out += ".btn-ghost{background:transparent;color:var(--blue)}";
  out += ".toast{font-size:14px;padding:12px 16px;border-radius:10px;margin:12px 0;display:none;text-align:center}";
  out += ".toast-ok{background:rgba(48,209,88,0.15);color:var(--green);display:block}";
  out += ".toast-err{background:rgba(255,69,58,0.15);color:var(--red);display:block}";
  out += ".footer{text-align:center;color:var(--text2);font-size:12px;margin-top:20px;padding-bottom:20px}";
  out += ".label{margin-top:14px;font-size:14px;color:var(--text2)}</style></head><body>";
}

// ---- /api/flight JSON response builder ----

static const char* compassDir(double deg) {
  if (deg < 0.0 || deg >= 360.0) return "—";
  const char* dirs[] = {"N","NNE","NE","ENE","E","ESE","SE","SSE",
                         "S","SSW","SW","WSW","W","WNW","NW","NNW"};
  return dirs[static_cast<int>((deg + 11.25) / 22.5) % 16];
}

static void buildFlightJson(const AppContext& ctx, char* buf, size_t bufLen) {
  StaticJsonDocument<512> doc;
  doc["schemaVersion"] = 1;

  if (!ctx.hasLatestAircraft || ctx.latestAircraft.count == 0) {
    doc["hasData"] = false;
    doc["providerHealthy"] = (ctx.providerHealth == ProviderHealth::Ok);
  } else {
    const auto& ac = ctx.latestAircraft.items[0];
    doc["hasData"] = true;
    doc["callsign"] = ac.hasCallsign ? ac.callsign : ac.icaoHex;
    doc["icaoHex"] = ac.icaoHex;
    if (ac.hasAirlineName) doc["airline"] = ac.airlineName;
    if (ac.hasAircraftType) doc["type"] = ac.aircraftTypeCode;
    if (ac.hasDistanceFromObserverKm) doc["distanceKm"] = ac.distanceFromObserverKm;
    if (ac.hasAltitudeFt) doc["altitudeFt"] = ac.altitudeFt;
    if (ac.hasGroundSpeedKt) {
      doc["groundSpeedKt"] = ac.groundSpeedKt;
      doc["groundSpeedMph"] = ac.groundSpeedMph;
    }
    if (ac.hasTrackHeadingDeg) {
      doc["trackHeadingDeg"] = ac.trackHeadingDeg;
      const char* comp = compassDir(ac.trackHeadingDeg);
      if (comp) doc["compassDirection"] = comp;
    }
    if (ac.hasVerticalRateFtPerMin) doc["verticalRateFpm"] = ac.verticalRateFtPerMin;
    if (ac.hasSquawk) doc["squawk"] = ac.squawk;
    doc["onGround"] = ac.onGround;
    const char* emState = "none";
    switch (ac.emergencyState) {
      case ofd::EmergencyState::General:              emState = "general"; break;
      case ofd::EmergencyState::Medical:              emState = "medical"; break;
      case ofd::EmergencyState::MinimumFuel:          emState = "minfuel"; break;
      case ofd::EmergencyState::NoCommunications:     emState = "nocomms"; break;
      case ofd::EmergencyState::UnlawfulInterference: emState = "unlawful"; break;
      case ofd::EmergencyState::Downed:               emState = "downed"; break;
      default: break;
    }
    doc["emergency"] = emState;
    doc["latitude"] = ac.latitude;
    doc["longitude"] = ac.longitude;
    if (ctx.lastAircraftUpdateAtMs > 0) {
      doc["ageSeconds"] = (millis() - ctx.lastAircraftUpdateAtMs) / 1000;
    }
    doc["providerHealthy"] = (ctx.providerHealth == ProviderHealth::Ok);
  }
  serializeJson(doc, buf, bufLen);
}

// ---- /flight web page builder ----

static String buildFlightPage(const AppContext& ctx) {
  String html;
  html.reserve(4000);
  appendHead(html, "Flight — OpenFlightDisplay", false);

  // Nav
  html += "<div class=\"nav\"><span class=\"nav-title\">OpenFlightDisplay</span>";
  html += "<div class=\"nav-tabs\"><a href=\"/\" class=\"nav-tab\">Status</a>";
  html += "<a href=\"/setup\" class=\"nav-tab\">Setup</a>";
  html += "<a href=\"/flight\" class=\"nav-tab active\">Flight</a></div></div>";

  html += "<div class=\"container\">";
  html += "<div id=\"content\"><div class=\"card\" style=\"padding:16px;text-align:center\"><p style=\"color:var(--text2)\">Loading...</p></div></div>";
  html += "<div class=\"footer\">Updates every 3s</div>";
  html += "</div>";

  // Script — polls /api/flight every 3 seconds
  html += "<script>\n";
  html += "var t=document.getElementById(\"content\");\n";
  html += "function esc(v){return String(v).replace(/&/g,\"&\").replace(/</g,\"<\").replace(/>/g,\">\");}\n";
  html += "function r(l,v,m){return \"<div class=card-row><span class=row-label>\"+esc(l)+\"</span><span class='row-value\"+(m?\" mono\":\"\")+\"'>\"+esc(v)+\"</span></div>\";}\n";
  html += "function flat(v){return v!=null?v:\"—\";}\n";
  html += "var nf=0;\n";
  html += "function load(){\n";
  html += "fetch(\"/api/flight\").then(function(r){return r.json()}).then(function(d){nf=0;\n";
  html += "if(!d.hasData){t.innerHTML=\"<div class=card style=padding:16px;text-align:center><p style=color:var(--text2)>\"+(d.providerHealthy?\"No aircraft in range\":\"Data source unavailable\")+\"</p></div>\";return;}\n";
  html += "var a=d,h=\"\";\n";
  html += "h+=\"<div class=card>\";\n";
  html += "h+=\"<div class=card-row><span class=row-label style=font-size:18px;color:var(--text)>\"+esc(a.callsign||\"\")+\"</span>\";\n";
  html += "h+=\"<span class='pill \"+(a.providerHealthy&&a.ageSeconds<30?\"pill-ok\":(a.ageSeconds<60?\"pill-warn\":\"pill-err\"))+\"'>\"+esc(a.ageSeconds||0)+\"s</span></div>\";\n";
  html += "if(a.airline)h+=r(\"Airline\",a.airline);\n";
  html += "if(a.type)h+=r(\"Aircraft\",a.type);\n";
  html += "h+=r(\"ICAO\",flat(a.icaoHex),1);\n";
  html += "h+=\"</div>\";\n";
  html += "h+=\"<div class=card><div class=card-header>Flight Metrics</div>\";\n";
  html += "h+=r(\"Distance\",flat(a.distanceKm)!=null?a.distanceKm.toFixed(1)+\" km\":\"—\");\n";
  html += "h+=r(\"Altitude\",flat(a.altitudeFt)!=null?Math.round(a.altitudeFt)+\" ft\":\"—\");\n";
  html += "h+=r(\"Speed\",flat(a.groundSpeedKt)!=null?Math.round(a.groundSpeedKt)+\" kt / \"+Math.round(a.groundSpeedMph)+\" mph\":\"—\");\n";
  html += "h+=r(\"Heading\",flat(a.trackHeadingDeg)!=null?Math.round(a.trackHeadingDeg)+\"&deg; \"+esc(flat(a.compassDirection||\"\")):\"—\");\n";
  html += "h+=\"</div>\";\n";
  html += "h+=\"<div class=card><div class=card-header>Status</div>\";\n";
  html += "if(a.verticalRateFpm!=null)h+=r(\"Vertical Rate\",Math.round(a.verticalRateFpm)+\" fpm\");\n";
  html += "if(a.squawk)h+=r(\"Squawk\",a.squawk,1);\n";
  html += "h+=r(\"On Ground\",a.onGround?\"Yes\":\"No\");\n";
  html += "h+=r(\"Emergency\",a.emergency===\"none\"?\"None\":\"<span style=color:var(--red);font-weight:700>\"+esc(a.emergency.toUpperCase())+\"</span>\");\n";
  html += "h+=r(\"Position\",(a.latitude?a.latitude.toFixed(4):\"—\")+\", \"+(a.longitude?a.longitude.toFixed(4):\"—\"));\n";
  html += "h+=\"</div>\";\n";
  html += "t.innerHTML=h;\n";
  html += "}).catch(function(){nf++;if(nf>3)t.innerHTML=\"<div class=card style=padding:16px;text-align:center><p style=color:var(--red)>Connection lost</p></div>\";});\n";
  html += "}\n";
  html += "load();setInterval(load,3000);\n";
  html += "</script></body></html>";

  return html;
}

static String buildDashboard(const AppContext& ctx, const char* ip, const char* ssid) {
  String html;
  html.reserve(3000);

  appendHead(html, "OpenFlightDisplay", true);

  // Nav bar
  html += "<div class=\"nav\"><span class=\"nav-title\">OpenFlightDisplay</span>";
  html += "<div class=\"nav-tabs\"><a href=\"/\" class=\"nav-tab active\">Status</a>";
  html += "<a href=\"/setup\" class=\"nav-tab\">Setup</a>";
  html += "<a href=\"/flight\" class=\"nav-tab\">Flight</a></div></div>";

  html += "<div class=\"container\">";

  // Network card
  html += "<div class=\"card\"><div class=\"card-header\">Network</div>";
  html += "<div class=\"card-row\"><span class=\"row-label\">Wi-Fi</span>";
  const char* wLabel, *wClass;
  if (ctx.wifiState == WifiState::Connected) { wLabel = "Connected"; wClass = "pill-ok"; }
  else if (ctx.wifiState == WifiState::Provisioning) { wLabel = "Provisioning"; wClass = "pill-warn"; }
  else { wLabel = "Disconnected"; wClass = "pill-err"; }
  html += "<span class=\"pill ";
  html += wClass;
  html += "\">";
  html += wLabel;
  html += "</span></div>";

  html += "<div class=\"card-row\"><span class=\"row-label\">SSID</span>";
  html += "<span class=\"row-value\">";
  html += (ssid && ssid[0]) ? ssid : "—";
  html += "</span></div>";

  html += "<div class=\"card-row\"><span class=\"row-label\">IP Address</span>";
  html += "<span class=\"row-value mono\">";
  html += (ip && ip[0]) ? ip : "—";
  html += "</span></div>";

  html += "<div class=\"card-row\"><span class=\"row-label\">Device</span><span class=\"row-value mono\">";
  html += ctx.deviceId;
  html += "</span></div></div>";

  // Data Source card
  html += "<div class=\"card\"><div class=\"card-header\">Data Source</div>";
  html += "<div class=\"card-row\"><span class=\"row-label\">adsb.lol</span>";
  const char* pLabel, *pClass;
  if (!ctx.providerStarted) { pLabel = "Idle"; pClass = "pill-neutral"; }
  else if (ctx.providerHealth == ProviderHealth::Ok) { pLabel = "Healthy"; pClass = "pill-ok"; }
  else if (ctx.providerHealth == ProviderHealth::Degraded) { pLabel = "Degraded"; pClass = "pill-warn"; }
  else { pLabel = "Unavailable"; pClass = "pill-err"; }
  html += "<span class=\"pill ";
  html += pClass;
  html += "\">";
  html += pLabel;
  html += "</span></div>";

  html += "<div class=\"card-row\"><span class=\"row-label\">Aircraft</span><span class=\"row-value\">";
  if (ctx.hasLatestAircraft) {
    char buf[16];
    std::snprintf(buf, sizeof(buf), "%u in range", static_cast<unsigned>(ctx.latestAircraft.count));
    html += buf;
  } else {
    html += "—";
  }
  html += "</span></div>";

  html += "<div class=\"card-row\"><span class=\"row-label\">Last Update</span><span class=\"row-value\">";
  if (ctx.hasLatestAircraft && ctx.lastAircraftUpdateAtMs > 0) {
    const unsigned age = static_cast<unsigned>((millis() - ctx.lastAircraftUpdateAtMs) / 1000);
    char buf[24];
    if (age < 60) std::snprintf(buf, sizeof(buf), "%us ago", age);
    else std::snprintf(buf, sizeof(buf), "%um %us ago", age / 60, age % 60);
    html += buf;
  } else {
    html += "—";
  }
  html += "</span></div></div>";

  // System card
  html += "<div class=\"card\"><div class=\"card-header\">System</div>";
  html += "<div class=\"card-row\"><span class=\"row-label\">Firmware</span><span class=\"row-value mono\">";
  html += ctx.firmwareVersion;
  html += "</span></div>";

  html += "<div class=\"card-row\"><span class=\"row-label\">Memory</span><span class=\"row-value\">";
  char heapBuf[24];
  std::snprintf(heapBuf, sizeof(heapBuf), "%.0f kB free", ESP.getFreeHeap() / 1024.0f);
  html += heapBuf;
  html += "</span></div>";

  // Battery (cached from AXP192 via pollBattery)
  {
    html += "<div class=\"card-row\"><span class=\"row-label\">Battery</span>";
    const auto& batt = ctx.battery;
    if (batt.valid) {
      html += "<span class=\"pill ";
      if (batt.percent >= 20) html += "pill-ok";
      else if (batt.percent >= 10) html += "pill-warn";
      else html += "pill-err";
      html += "\">";
      char bbuf[16];
      if (batt.charging) std::snprintf(bbuf, sizeof(bbuf), "%u%% %s %.2fV", batt.percent, "\xE2\x9A\xA1", batt.voltage);
      else std::snprintf(bbuf, sizeof(bbuf), "%u%% %.2fV", batt.percent, batt.voltage);
      html += bbuf;
      html += "</span>";
    } else {
      html += "<span class=\"pill pill-neutral\">--%</span>";
    }
    html += "</div>";
  }

  html += "<div class=\"card-row\"><span class=\"row-label\">Location</span><span class=\"row-value\" style=\"font-size:13px\">";
  if (ctx.hasConfig && ctx.config.hasMonitoringArea) {
    const auto& a = ctx.config.monitoringArea;
    char locBuf[80];
    std::snprintf(locBuf, sizeof(locBuf), "%.4f, %.4f<br>%.0f km radius", a.centerLat, a.centerLon, a.radiusKm);
    html += locBuf;
  } else {
    html += "Not configured";
  }
  html += "</span></div></div>";

  // Footer
  html += "<div class=\"footer\">Auto-refresh &middot; ";
  const time_t now = time(nullptr);
  if (now > 1700000000) {
    const struct tm* t = localtime(&now);
    char tb[16];
    std::snprintf(tb, sizeof(tb), "%02d:%02d:%02d", t->tm_hour, t->tm_min, t->tm_sec);
    html += tb;
  } else {
    html += "—";
  }
  html += "</div></div></body></html>";

  return html;
}

static String buildSetupPage(const AppContext& ctx) {
  String html;
  html.reserve(3500);

  appendHead(html, "Setup — OpenFlightDisplay", false);

  // Nav
  html += "<div class=\"nav\"><span class=\"nav-title\">OpenFlightDisplay</span>";
  html += "<div class=\"nav-tabs\"><a href=\"/\" class=\"nav-tab\">Status</a>";
  html += "<a href=\"/setup\" class=\"nav-tab active\">Setup</a>";
  html += "<a href=\"/flight\" class=\"nav-tab\">Flight</a></div></div>";

  html += "<div class=\"container\">";

  // ---- Wi‑Fi card ----
  html += "<div class=\"card\" style=\"padding:16px\">";
  html += "<div class=\"card-header\">Wi‑Fi Network</div>";

  // Show current connection if any
  String currentSsid = "—";
  if (ctx.wifiState == WifiState::Connected) {
    currentSsid = WiFi.SSID();
  }
  html += "<div class=\"card-row\"><span class=\"row-label\">Connected to</span><span class=\"row-value\">";
  html += currentSsid;
  html += "</span></div>";

  html += "<span class=\"row-label\">New SSID</span>";
  html += "<input type=\"text\" id=\"wssid\" placeholder=\"e.g. MyHomeWiFi\" maxlength=\"32\" autocomplete=\"off\">";
  html += "<span class=\"row-label\">Password</span>";
  html += "<input type=\"password\" id=\"wpwd\" placeholder=\"e.g. correct‑horse‑battery‑staple\" maxlength=\"64\" autocomplete=\"off\">";
  html += "<button class=\"btn btn-primary\" onclick=\"saveWifi()\">Save Wi‑Fi Credentials</button>";
  html += "</div>";

  // ---- Location card ----
  html += "<div class=\"card\" style=\"padding:16px\">";
  html += "<div class=\"card-header\">Monitoring Area</div>";
  html += "<span class=\"row-label\">Latitude</span>";
  html += "<input type=\"text\" inputmode=\"decimal\" id=\"lat\" placeholder=\"e.g. 47.6062\" autocomplete=\"off\">";
  html += "<span class=\"row-label\">Longitude</span>";
  html += "<input type=\"text\" inputmode=\"decimal\" id=\"lon\" placeholder=\"e.g. -122.3321\" autocomplete=\"off\">";
  html += "<span class=\"row-label\">Radius (km)</span>";
  html += "<input type=\"text\" inputmode=\"decimal\" id=\"radius\" placeholder=\"e.g. 15\" autocomplete=\"off\">";
  html += "<div style=\"display:flex;gap:8px;margin-top:12px\">";
  html += "<button class=\"btn btn-ghost\" onclick=\"navigator.geolocation&&navigator.geolocation.getCurrentPosition(function(p){document.getElementById('lat').value=p.coords.latitude.toFixed(6);document.getElementById('lon').value=p.coords.longitude.toFixed(6)})\">Use Current Location</button>";
  html += "<button class=\"btn btn-primary\" onclick=\"saveLocation()\">Save Location</button>";
  html += "</div></div>";

  html += "<div id=\"s\" class=\"toast\"></div>";
  html += "</div>";

  // Script
  html += "<script>";
  html += "function show(t,ok){var s=document.getElementById('s');s.className=ok?'toast toast-ok':'toast toast-err';s.textContent=t}";
  html += "function saveWifi(){";
  html += "var ssid=document.getElementById('wssid').value.trim(),pwd=document.getElementById('wpwd').value;";
  html += "if(!ssid){show('Enter an SSID',false);return}";
  html += "fetch('/api/v1/wifi',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({ssid:ssid,password:pwd})})";
  html += ".then(function(r){if(!r.ok)return r.json().then(function(e){throw new Error(e.error||'Failed')});show('Wi‑Fi saved. Reboot to connect.',true)})";
  html += ".catch(function(e){show(e.message,false)})}";
  html += "function saveLocation(){";
  html += "var lat=parseFloat(document.getElementById('lat').value),lon=parseFloat(document.getElementById('lon').value),r=parseFloat(document.getElementById('radius').value);";
  html += "if(isNaN(lat)||isNaN(lon)||isNaN(r)||lat<-90||lat>90||lon<-180||lon>180||r<0.5||r>500){show('Invalid values. Check ranges.',false);return}";
  html += "fetch('/api/v1/config',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({schemaVersion:1,config:{monitoringArea:{kind:'circle',centerLat:lat,centerLon:lon,radiusKm:r}}})})";
  html += ".then(function(resp){if(!resp.ok)return resp.json().then(function(e){throw new Error(e.error||'Failed')});show('Location saved. Redirecting…',true);setTimeout(function(){location.href='/'},1500)})";
  html += ".catch(function(err){show(err.message,false)})}";
  html += "</script></body></html>";

  return html;
}

// ---- route registration ----

void registerPairingRoutes(AsyncWebServer& server, AppContext& ctx) {
  DefaultHeaders::Instance().addHeader("Access-Control-Allow-Origin", "*");
  DefaultHeaders::Instance().addHeader("Access-Control-Allow-Methods", "GET, POST, PUT, OPTIONS");
  DefaultHeaders::Instance().addHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");

  server.on("/pair", HTTP_OPTIONS, [](AsyncWebServerRequest* r) { r->send(200); });
  server.on("/api/v1/config", HTTP_OPTIONS, [](AsyncWebServerRequest* r) { r->send(200); });
  server.on("/api/v1/factory-reset", HTTP_OPTIONS, [](AsyncWebServerRequest* r) { r->send(200); });

  server.on("/pair", HTTP_GET, [](AsyncWebServerRequest* r) { r->redirect("/"); });

  server.on("/setup", HTTP_GET, [&ctx](AsyncWebServerRequest* r) {
    r->send(200, "text/html", buildSetupPage(ctx));
  });

  // Flight detail page
  server.on("/flight", HTTP_GET, [&ctx](AsyncWebServerRequest* r) {
    r->send(200, "text/html", buildFlightPage(ctx));
  });

  // Flight JSON API (for the /flight page polling)
  server.on("/api/v1/flight", HTTP_OPTIONS, [](AsyncWebServerRequest* r) { r->send(200); });
  server.on("/api/flight", HTTP_GET, [&ctx](AsyncWebServerRequest* r) {
    char buf[512];
    buildFlightJson(ctx, buf, sizeof(buf));
    r->send(200, "application/json", String(buf));
  });

  // Wi‑Fi credential save endpoint
  server.on("/api/v1/config", HTTP_OPTIONS, [](AsyncWebServerRequest* r) { r->send(200); });
  server.on("/api/v1/wifi", HTTP_OPTIONS, [](AsyncWebServerRequest* r) { r->send(200); });

  server.on("/api/v1/wifi", HTTP_PUT,
    [](AsyncWebServerRequest*) {},
    nullptr,
    [&ctx](AsyncWebServerRequest* r, uint8_t* data, size_t len, size_t index, size_t total) {
      if (index == 0 && total >= kBodyBufferCapacity) { sendJsonError(r, 400, "invalid_request"); return; }
      std::memcpy(g_bodyBuffer + index, data, len);
      if (index + len < total) return;
      g_bodyBuffer[total] = '\0';

      StaticJsonDocument<256> doc;
      if (deserializeJson(doc, g_bodyBuffer, total)) { sendJsonError(r, 400, "invalid_json"); return; }

      const char* ssid = doc["ssid"] | "";
      const char* pwd  = doc["password"] | "";
      if (std::strlen(ssid) == 0 || std::strlen(ssid) >= 33) { sendJsonError(r, 400, "invalid_ssid"); return; }

      WifiCredentials creds;
      std::strcpy(creds.ssid, ssid);
      std::strncpy(creds.password, pwd, sizeof(creds.password) - 1);
      if (!saveWifiCredentials(creds)) { sendJsonError(r, 500, "save_failed"); return; }

      g_statusDoc.clear();
      g_statusDoc["schemaVersion"] = 1;
      g_statusDoc["message"] = "saved";
      char buf[64];
      const size_t respLen = serializeJson(g_statusDoc, buf, sizeof(buf));
      r->send(200, "application/json", String(buf, respLen));
      // Reboot so the new credentials take effect
      delay(500);
      ESP.restart();
    });

  server.on("/", HTTP_GET, [&ctx](AsyncWebServerRequest* r) {
    String ipStr = "—";
    String ssidStr = "—";
    if (WiFi.status() == WL_CONNECTED) {
      ipStr = WiFi.localIP().toString();
      ssidStr = WiFi.SSID();
    }
    r->send(200, "text/html", buildDashboard(ctx, ipStr.c_str(), ssidStr.c_str()));
  });

  server.on("/pair", HTTP_POST,
    [](AsyncWebServerRequest*) {},
    nullptr,
    [&ctx](AsyncWebServerRequest* r, uint8_t* data, size_t len, size_t index, size_t total) {
      if (index == 0 && total >= kBodyBufferCapacity) { sendJsonError(r, 400, "invalid_request"); return; }
      std::memcpy(g_bodyBuffer + index, data, len);
      if (index + len < total) return;
      g_bodyBuffer[total] = '\0';

      if (deserializeJson(g_pairRequestDoc, g_bodyBuffer, total) || (g_pairRequestDoc["schemaVersion"] | -1) != 1) {
        sendJsonError(r, 400, "invalid_request"); return;
      }
      const char* code = g_pairRequestDoc["code"] | "";
      if (!ctx.pairingCodeManager.tryClaim(code, millis())) {
        sendJsonError(r, 401, "invalid_or_expired_code"); return;
      }

      char token[40];
      generatePairingToken(token, sizeof(token));
      std::strcpy(ctx.pairingToken, token);
      ctx.hasPairingToken = true;
      ctx.configStore.savePairingToken(token);

      g_pairResponseDoc.clear();
      g_pairResponseDoc["schemaVersion"] = 1;
      g_pairResponseDoc["pairingToken"] = token;
      g_pairResponseDoc["deviceId"] = ctx.deviceId;
      char buf[192];
      const size_t respLen = serializeJson(g_pairResponseDoc, buf, sizeof(buf));
      r->send(200, "application/json", String(buf, respLen));
    });

  server.on("/api/v1/status", HTTP_GET, [&ctx](AsyncWebServerRequest* r) {
    g_statusDoc.clear();
    g_statusDoc["schemaVersion"] = 1;
    g_statusDoc["deviceId"] = ctx.deviceId;
    g_statusDoc["firmwareVersion"] = ctx.firmwareVersion;
    g_statusDoc["wifiState"] = wifiStateToString(ctx.wifiState);
    g_statusDoc["providerState"] = providerStateToString(ctx.providerHealth);
    if (ctx.hasLatestAircraft) {
      g_statusDoc["lastAircraftUpdateAgeSeconds"] = (millis() - ctx.lastAircraftUpdateAtMs) / 1000;
    }
    g_statusDoc["freeHeapBytes"] = ESP.getFreeHeap();
    char buf[256];
    const size_t len = serializeJson(g_statusDoc, buf, sizeof(buf));
    r->send(200, "application/json", String(buf, len));
  });

  server.on("/api/v1/config", HTTP_GET, [&ctx](AsyncWebServerRequest* r) {
    if (!checkBearerToken(r, ctx)) { sendJsonError(r, 401, "invalid_or_missing_pairing_token"); return; }
    if (!ctx.hasConfig) { sendJsonError(r, 404, "no_config"); return; }
    char buf[512];
    const size_t len = serializeDeviceConfig(ctx.config, buf, sizeof(buf));
    r->send(200, "application/json", String(buf, len));
  });

  // Device status (battery + system health)
  server.on("/api/v1/device-status", HTTP_GET, [&ctx](AsyncWebServerRequest* r) {
    g_statusDoc.clear();
    g_statusDoc["schemaVersion"] = 1;
    JsonObject batt = g_statusDoc.createNestedObject("battery");
    const auto& b = ctx.battery;
    batt["valid"] = b.valid;
    batt["percent"] = b.percent;
    batt["voltage"] = b.voltage;
    batt["charging"] = b.charging;
    batt["externalPower"] = b.externalPower;
    batt["readAgeMs"] = b.lastReadMs > 0 ? millis() - b.lastReadMs : 0;
    char buf[128];
    const size_t len = serializeJson(g_statusDoc, buf, sizeof(buf));
    r->send(200, "application/json", String(buf, len));
  });

  server.on("/api/v1/device-status", HTTP_OPTIONS, [](AsyncWebServerRequest* r) { r->send(200); });

  server.on("/api/v1/factory-reset", HTTP_POST, [&ctx](AsyncWebServerRequest* r) {
    LittleFS.remove("/config.json");
    LittleFS.remove("/pairing.json");
    ctx.hasConfig = false;
    ctx.hasPairingToken = false;
    ctx.pairingToken[0] = '\0';
    ctx.config = DeviceConfig{};
    ctx.providerStarted = false;
    ctx.pairingCodeManager.regenerate(millis());
    g_statusDoc.clear();
    g_statusDoc["schemaVersion"] = 1;
    g_statusDoc["message"] = "reset";
    char buf[64];
    const size_t len = serializeJson(g_statusDoc, buf, sizeof(buf));
    r->send(200, "application/json", String(buf, len));
  });

  server.on("/api/v1/config", HTTP_PUT,
    [](AsyncWebServerRequest*) {},
    nullptr,
    [&ctx](AsyncWebServerRequest* r, uint8_t* data, size_t len, size_t index, size_t total) {
      if (index == 0 && total >= kBodyBufferCapacity) { sendJsonError(r, 400, "invalid_config"); return; }
      std::memcpy(g_bodyBuffer + index, data, len);
      if (index + len < total) return;
      g_bodyBuffer[total] = '\0';

      // Seeded with the device's own hardware identity so a PUT that
      // omits "deviceId" (e.g. one that only updates monitoringArea or
      // brightness) still validates -- parseAndValidateDeviceConfig
      // keeps whatever `parsed.deviceId` already holds when the payload
      // doesn't specify one, and now requires the *result* to be
      // non-empty rather than silently accepting no deviceId at all.
      DeviceConfig parsed;
      std::strncpy(parsed.deviceId, ctx.deviceId, sizeof(parsed.deviceId) - 1);
      char error[64] = {0};
      bool ok = false;

      if (deserializeJson(g_configWrapperDoc, g_bodyBuffer, total) == DeserializationError::Ok &&
          g_configWrapperDoc.containsKey("config")) {
        static char configJson[kBodyBufferCapacity];
        const size_t configLen = serializeJson(g_configWrapperDoc["config"], configJson, sizeof(configJson));
        ok = (configLen > 0 && parseAndValidateDeviceConfig(configJson, configLen, parsed, error, sizeof(error)));
      } else {
        ok = parseAndValidateDeviceConfig(g_bodyBuffer, total, parsed, error, sizeof(error));
      }

      if (!ok) { sendJsonError(r, 400, error[0] ? error : "invalid_config"); return; }

      ctx.config = parsed;
      ctx.hasConfig = true;
      ctx.configStore.saveConfig(parsed);

      char buf[512];
      const size_t respLen = serializeDeviceConfig(parsed, buf, sizeof(buf));
      r->send(200, "application/json", String(buf, respLen));
    });
}

}  // namespace ofd::app