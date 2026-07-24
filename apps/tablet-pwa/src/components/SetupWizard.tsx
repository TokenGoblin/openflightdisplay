import { useEffect, useRef, useState } from "react";
import type { DeviceConfiguration } from "@openflightdisplay/shared-models";
import { useGeolocation } from "../hooks/useGeolocation";
import { useQrScanner } from "../hooks/useQrScanner";
import { pairWithCore2, putCore2Config, claimDeviceWithGateway, putGatewayConfig, ApiError } from "../lib/api";
import { saveStoredConnection, type StoredConnection } from "../lib/storage";

type Step = "pair" | "location" | "radius" | "confirm";

interface WizardDraft {
  core2BaseUrl: string;
  code: string;
  gatewayBaseUrl: string;
  deviceId: string;
  deviceName: string;
  pairingToken: string;
  latitude: number;
  longitude: number;
  radiusKm: number;
}

const INITIAL_DRAFT: WizardDraft = {
  core2BaseUrl: "",
  code: "",
  gatewayBaseUrl: "",
  deviceId: "",
  deviceName: "OpenFlightDisplay",
  pairingToken: "",
  latitude: 0,
  longitude: 0,
  radiusKm: 15,
};

/** Parses "http://<ip>/pair?code=<code>" from the Core2's QR code. */
function parsePairingQrPayload(text: string): { core2BaseUrl: string; code: string } | null {
  try {
    const url = new URL(text);
    const code = url.searchParams.get("code");
    if (!code) return null;
    return { core2BaseUrl: `${url.protocol}//${url.host}`, code };
  } catch {
    return null;
  }
}

export function SetupWizard({ onComplete }: { onComplete: (connection: StoredConnection) => void }) {
  const [step, setStep] = useState<Step>("pair");
  const [draft, setDraft] = useState<WizardDraft>(INITIAL_DRAFT);
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  return (
    <div style={{ maxWidth: 480, margin: "2rem auto", padding: "0 1rem" }}>
      {error ? (
        <p role="alert" style={{ color: "#e5484d" }}>
          {error}
        </p>
      ) : null}
      {step === "pair" ? (
        <PairStep
          draft={draft}
          onNext={(update) => {
            setDraft((d) => ({ ...d, ...update }));
            setStep("location");
          }}
          onError={setError}
        />
      ) : null}
      {step === "location" ? (
        <LocationStep
          draft={draft}
          onBack={() => setStep("pair")}
          onNext={(update) => {
            setDraft((d) => ({ ...d, ...update }));
            setStep("radius");
          }}
        />
      ) : null}
      {step === "radius" ? (
        <RadiusStep
          draft={draft}
          onBack={() => setStep("location")}
          onNext={(update) => {
            setDraft((d) => ({ ...d, ...update }));
            setStep("confirm");
          }}
        />
      ) : null}
      {step === "confirm" ? (
        <ConfirmStep
          draft={draft}
          isSubmitting={isSubmitting}
          onBack={() => setStep("radius")}
          onSubmit={async () => {
            setIsSubmitting(true);
            setError(null);
            try {
              const config: DeviceConfiguration = {
                deviceId: draft.deviceId,
                deviceName: draft.deviceName,
                gatewayUrl: draft.gatewayBaseUrl.replace(/^http/, "ws") + "/ws/v1/aircraft",
                monitoringArea: {
                  kind: "circle",
                  centerLat: draft.latitude,
                  centerLon: draft.longitude,
                  radiusKm: draft.radiusKm,
                },
                displayProfile: { mode: "single-aircraft", brightness: 200, units: "metric", use24HourClock: true },
              };
              await putCore2Config(draft.core2BaseUrl, draft.pairingToken, config);
              await claimDeviceWithGateway(draft.gatewayBaseUrl, draft.deviceId, draft.deviceName, draft.pairingToken);
              await putGatewayConfig(draft.gatewayBaseUrl, draft.deviceId, draft.pairingToken, config);

              const connection: StoredConnection = {
                deviceId: draft.deviceId,
                deviceName: draft.deviceName,
                core2BaseUrl: draft.core2BaseUrl,
                gatewayBaseUrl: draft.gatewayBaseUrl,
                pairingToken: draft.pairingToken,
              };
              saveStoredConnection(connection);
              onComplete(connection);
            } catch (err) {
              setError(err instanceof ApiError ? err.message : "Setup failed unexpectedly");
            } finally {
              setIsSubmitting(false);
            }
          }}
        />
      ) : null}
    </div>
  );
}

function PairStep({
  draft,
  onNext,
  onError,
}: {
  draft: WizardDraft;
  onNext: (update: Partial<WizardDraft>) => void;
  onError: (message: string) => void;
}) {
  const [mode, setMode] = useState<"scan" | "manual">("scan");
  const [manualIp, setManualIp] = useState("");
  const [manualCode, setManualCode] = useState("");
  const [gatewayBaseUrl, setGatewayBaseUrl] = useState(draft.gatewayBaseUrl);
  const [isPairing, setIsPairing] = useState(false);
  const videoRef = useRef<HTMLVideoElement>(null);
  const { decodedText, error: scanError } = useQrScanner(videoRef, mode === "scan");

  async function completePairing(core2BaseUrl: string, code: string) {
    setIsPairing(true);
    try {
      const result = await pairWithCore2(core2BaseUrl, code);
      onNext({ core2BaseUrl, code, pairingToken: result.pairingToken, deviceId: result.deviceId, gatewayBaseUrl });
    } catch (err) {
      onError(err instanceof ApiError ? err.message : "Could not pair with the display");
    } finally {
      setIsPairing(false);
    }
  }

  // Runs as an effect (not inline in the render body) so it fires exactly
  // once per newly-decoded QR payload -- calling completePairing (which
  // calls setState) directly during render is a React anti-pattern that
  // can double-fire under StrictMode's dev-mode double-render.
  useEffect(() => {
    if (!decodedText) return;
    const parsed = parsePairingQrPayload(decodedText);
    if (parsed) void completePairing(parsed.core2BaseUrl, parsed.code);
    // eslint-disable-next-line react-hooks/exhaustive-deps -- completePairing is stable enough for Phase 1's scope
  }, [decodedText]);

  return (
    <div>
      <h2>Add your display</h2>
      <div role="tablist" aria-label="Pairing method" style={{ display: "flex", gap: "0.5rem", marginBottom: "1rem" }}>
        <button type="button" aria-pressed={mode === "scan"} onClick={() => setMode("scan")}>
          Scan QR code
        </button>
        <button type="button" aria-pressed={mode === "manual"} onClick={() => setMode("manual")}>
          Enter manually
        </button>
      </div>

      {mode === "scan" ? (
        <div>
          <video ref={videoRef} aria-label="Camera preview" style={{ width: "100%", borderRadius: 8 }} muted playsInline />
          {scanError ? <p role="alert">{scanError} — try manual entry instead.</p> : null}
        </div>
      ) : (
        <form
          onSubmit={(e) => {
            e.preventDefault();
            void completePairing(`http://${manualIp}`, manualCode);
          }}
        >
          <label>
            Display IP address
            <input value={manualIp} onChange={(e) => setManualIp(e.target.value)} placeholder="192.168.1.42" required />
          </label>
          <label>
            Pairing code
            <input value={manualCode} onChange={(e) => setManualCode(e.target.value)} placeholder="482913" required />
          </label>
          <label>
            Gateway address
            <input
              value={gatewayBaseUrl}
              onChange={(e) => setGatewayBaseUrl(e.target.value)}
              placeholder="http://192.168.1.50:8787"
              required
            />
          </label>
          <button type="submit" disabled={isPairing}>
            {isPairing ? "Pairing…" : "Pair"}
          </button>
        </form>
      )}
    </div>
  );
}

function LocationStep({
  draft,
  onBack,
  onNext,
}: {
  draft: WizardDraft;
  onBack: () => void;
  onNext: (update: Partial<WizardDraft>) => void;
}) {
  const [latitude, setLatitude] = useState(draft.latitude || 0);
  const [longitude, setLongitude] = useState(draft.longitude || 0);
  const geo = useGeolocation();

  return (
    <div>
      <h2>Where should we monitor?</h2>
      <button type="button" onClick={geo.request} disabled={geo.isLoading}>
        {geo.isLoading ? "Locating…" : "Use my location"}
      </button>
      {geo.error ? <p role="alert">{geo.error}</p> : null}
      {geo.result ? (
        <p>
          Detected: {geo.result.latitude.toFixed(4)}, {geo.result.longitude.toFixed(4)}
        </p>
      ) : null}

      <form
        onSubmit={(e) => {
          e.preventDefault();
          onNext({
            latitude: geo.result?.latitude ?? latitude,
            longitude: geo.result?.longitude ?? longitude,
          });
        }}
      >
        <label>
          Latitude
          <input
            type="number"
            step="any"
            value={geo.result?.latitude ?? latitude}
            onChange={(e) => setLatitude(Number(e.target.value))}
            required
          />
        </label>
        <label>
          Longitude
          <input
            type="number"
            step="any"
            value={geo.result?.longitude ?? longitude}
            onChange={(e) => setLongitude(Number(e.target.value))}
            required
          />
        </label>
        <button type="button" onClick={onBack}>
          Back
        </button>
        <button type="submit">Next</button>
      </form>
    </div>
  );
}

function RadiusStep({
  draft,
  onBack,
  onNext,
}: {
  draft: WizardDraft;
  onBack: () => void;
  onNext: (update: Partial<WizardDraft>) => void;
}) {
  const [radiusKm, setRadiusKm] = useState(draft.radiusKm);

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        onNext({ radiusKm });
      }}
    >
      <h2>Monitoring radius</h2>
      <label>
        Radius (km)
        <input
          type="number"
          min={0.5}
          max={500}
          step="0.5"
          value={radiusKm}
          onChange={(e) => setRadiusKm(Number(e.target.value))}
          required
        />
      </label>
      <button type="button" onClick={onBack}>
        Back
      </button>
      <button type="submit">Next</button>
    </form>
  );
}

function ConfirmStep({
  draft,
  isSubmitting,
  onBack,
  onSubmit,
}: {
  draft: WizardDraft;
  isSubmitting: boolean;
  onBack: () => void;
  onSubmit: () => void;
}) {
  return (
    <div>
      <h2>Confirm setup</h2>
      <ul>
        <li>Display: {draft.deviceId}</li>
        <li>Gateway: {draft.gatewayBaseUrl}</li>
        <li>
          Location: {draft.latitude.toFixed(4)}, {draft.longitude.toFixed(4)}
        </li>
        <li>Radius: {draft.radiusKm} km</li>
      </ul>
      <button type="button" onClick={onBack} disabled={isSubmitting}>
        Back
      </button>
      <button type="button" onClick={onSubmit} disabled={isSubmitting}>
        {isSubmitting ? "Saving…" : "Finish setup"}
      </button>
    </div>
  );
}
