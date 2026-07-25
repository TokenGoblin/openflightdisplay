import { STATUS_LABEL, type StatusKind } from "../lib/status";

/** CSS class mapping for each status kind (defined in global.css). */
const CLASS_BY_STATUS: Record<StatusKind, string> = {
  "configuration-required": "status-banner status-banner--configuration-required",
  connecting: "status-banner status-banner--connecting",
  "data-source-unavailable": "status-banner status-banner--data-source-unavailable",
  "waiting-for-first-data": "status-banner status-banner--waiting-for-first-data",
  "no-matching-aircraft": "status-banner status-banner--no-matching-aircraft",
  stale: "status-banner status-banner--stale",
  "showing-aircraft": "",
};

// `detail?: string | undefined` (not just `detail?: string`) is deliberate:
// under exactOptionalPropertyTypes, callers commonly pass a possibly-
// undefined value via JSX (e.g. `detail={feed.providerStatus?.message}`),
// which is exactly the "present with value undefined" case that a bare
// `detail?: string` would reject.
export function StatusBanner({ status, detail }: { status: StatusKind; detail?: string | undefined }) {
  if (status === "showing-aircraft") return null;

  return (
    <div role="status" data-status={status} className={CLASS_BY_STATUS[status]}>
      {STATUS_LABEL[status]}
      {detail ? ` — ${detail}` : ""}
    </div>
  );
}