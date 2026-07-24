import { useEffect, useRef, useState } from "react";
import type { DeviceConfiguration } from "@openflightdisplay/shared-models";
import { useGeolocation } from "../hooks/useGeolocation";
import { useQrScanner } from "../hooks/useQrScanner";
import { pairWithCore2, putCore2Config, claimDeviceWithGateway, putGatewayConfig, ApiError } from "../lib/api";
import { normalizeHttpUrl, toWebSocketBaseUrl, isValidAddress } from "../lib/url";
import { StatusPill } from "./StatusPill";
import {
  saveStoredConnection,
  loadWizardProgress,
  saveWizardProgress,
  clearWizardProgress,
  type StoredConnection,
} from "../lib/storage";

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
  const resumed = loadWizardProgress();
  const [step, setStep] = useState<Step>(resumed?.step ?? "pair");
  const [draft, setDraft] = useState<WizardDraft>(resumed?.draft ?? INITIAL_DRAFT);
  const [error, setError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  // Persist after every step change so a mobile browser reloading this
  // tab (e.g. after switching away to look something up) doesn't throw
  // away progress -- see lib/storage.ts's WizardProgress doc comment.
  useEffect(() => {
    saveWizardProgress({ step, draft });
  }, [step, draft]);

  return (
    <div style={{ maxWidth: 480, margin: "2rem auto", padding: "0 1rem" }}>
      {step !== "pair" ? (
        <div style={{ marginBottom: "1rem" }}>
          <StatusPill label="Paired" />
        </div>
      ) : null}
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
                gatewayUrl: `${toWebSocketBaseUrl(draft.gatewayBaseUrl)}/ws/v1/aircraft`,
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
              clearWizardProgress();
              onComplete(connection);
            } catch (err) {
              // Surface the real error message whenever we have one
              // (ApiError or otherwise, e.g. a response that failed
              // schema validation) rather than a generic "unexpectedly"
              // that gives the user nothing to act on or report.
              setError(err instanceof Error ? err.message : "Setup failed unexpectedly");
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
  // Defaults to manual entry: camera scanning requires a secure (HTTPS)
  // context, which this LAN-over-plain-HTTP system doesn't have (see
  // useQrScanner.ts) -- verified on real hardware that "scan" as the
  // default led straight into that dead end.
  const [mode, setMode] = useState<"scan" | "manual">("manual");
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
      onNext({
        core2BaseUrl,
        code,
        pairingToken: result.pairingToken,
        deviceId: result.deviceId,
        gatewayBaseUrl: normalizeHttpUrl(gatewayBaseUrl),
      });
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
            if (!isValidAddress(manualIp) || !isValidAddress(gatewayBaseUrl)) return;
            void completePairing(normalizeHttpUrl(manualIp), manualCode);
          }}
        >
          <label>
            Display IP address
            <input value={manualIp} onChange={(e) => setManualIp(e.target.value)} placeholder="192.168.1.42" required />
          </label>
          {manualIp.trim() !== "" && !isValidAddress(manualIp) ? (
            <p role="alert">That doesn't look like a valid IP address.</p>
          ) : null}
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
          {gatewayBaseUrl.trim() !== "" && !isValidAddress(gatewayBaseUrl) ? (
            <p role="alert">That doesn't look like a valid gateway address.</p>
          ) : null}
          <button
            type="submit"
            disabled={isPairing || !isValidAddress(manualIp) || !isValidAddress(gatewayBaseUrl)}
          >
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
  // Verified needed on real hardware/mobile: `type="number"` combined
  // with `value={number}` + `onChange={... Number(e.target.value) ...}`
  // fights the user mid-edit -- clearing the field or typing a lone "-"
  // to start a negative number produces an empty string, Number("") is
  // 0 (not NaN), so the input immediately snaps back to "0" before a
  // second character can be typed. Keeping the raw text in state (only
  // parsing to a number on submit) avoids that entirely, and plain
  // text+inputMode="decimal" sidesteps mobile browsers' inconsistent
  // native number-input behavior for negatives/decimals.
  const [latitudeText, setLatitudeText] = useState(String(draft.latitude));
  const [longitudeText, setLongitudeText] = useState(String(draft.longitude));
  const geo = useGeolocation();

  useEffect(() => {
    if (geo.result) {
      setLatitudeText(String(geo.result.latitude));
      setLongitudeText(String(geo.result.longitude));
    }
  }, [geo.result]);

  const latitude = Number(latitudeText);
  const longitude = Number(longitudeText);
  const isValid =
    latitudeText.trim() !== "" &&
    longitudeText.trim() !== "" &&
    Number.isFinite(latitude) &&
    Number.isFinite(longitude) &&
    latitude >= -90 &&
    latitude <= 90 &&
    longitude >= -180 &&
    longitude <= 180;

  return (
    <div>
      <h2>Where should we monitor?</h2>
      <button type="button" onClick={geo.request} disabled={geo.isLoading}>
        {geo.isLoading ? "Locating…" : "Use my location"}
      </button>
      {geo.error ? <p role="alert">{geo.error}</p> : null}

      <form
        onSubmit={(e) => {
          e.preventDefault();
          if (!isValid) return;
          onNext({ latitude, longitude });
        }}
      >
        <label>
          Latitude
          <input
            type="text"
            inputMode="decimal"
            value={latitudeText}
            onChange={(e) => setLatitudeText(e.target.value)}
            placeholder="e.g. 47.6062"
            required
          />
        </label>
        <label>
          Longitude
          <input
            type="text"
            inputMode="decimal"
            value={longitudeText}
            onChange={(e) => setLongitudeText(e.target.value)}
            placeholder="e.g. -122.3321"
            required
          />
        </label>
        {!isValid && (latitudeText.trim() !== "" || longitudeText.trim() !== "") ? (
          <p role="alert">Enter a valid latitude (-90 to 90) and longitude (-180 to 180).</p>
        ) : null}
        <button type="button" onClick={onBack}>
          Back
        </button>
        <button type="submit" disabled={!isValid}>
          Next
        </button>
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
  // Same fix as LocationStep: keep the raw text in state and only parse
  // to a number on submit, rather than fighting the user's edit with a
  // number-typed controlled value on every keystroke.
  const [radiusText, setRadiusText] = useState(String(draft.radiusKm));
  const radiusKm = Number(radiusText);
  const isValid = radiusText.trim() !== "" && Number.isFinite(radiusKm) && radiusKm >= 0.5 && radiusKm <= 500;

  return (
    <form
      onSubmit={(e) => {
        e.preventDefault();
        if (!isValid) return;
        onNext({ radiusKm });
      }}
    >
      <h2>Monitoring radius</h2>
      <label>
        Radius (km)
        <input
          type="text"
          inputMode="decimal"
          value={radiusText}
          onChange={(e) => setRadiusText(e.target.value)}
          placeholder="e.g. 15"
          required
        />
      </label>
      {!isValid && radiusText.trim() !== "" ? <p role="alert">Enter a radius between 0.5 and 500 km.</p> : null}
      <button type="button" onClick={onBack}>
        Back
      </button>
      <button type="submit" disabled={!isValid}>
        Next
      </button>
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
