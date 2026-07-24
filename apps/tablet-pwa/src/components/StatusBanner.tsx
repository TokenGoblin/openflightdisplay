import { STATUS_LABEL, type StatusKind } from "../lib/status";

const COLOR_BY_STATUS: Record<StatusKind, string> = {
  "configuration-required": "#8a93a6",
  connecting: "#f5a623",
  "data-source-unavailable": "#e5484d",
  "waiting-for-first-data": "#f5a623",
  "no-matching-aircraft": "#8a93a6",
  stale: "#f5a623",
  "showing-aircraft": "#3ecf7f",
};

// `detail?: string | undefined` (not just `detail?: string`) is deliberate:
// under exactOptionalPropertyTypes, callers commonly pass a possibly-
// undefined value via JSX (e.g. `detail={feed.providerStatus?.message}`),
// which is exactly the "present with value undefined" case that a bare
// `detail?: string` would reject.
export function StatusBanner({ status, detail }: { status: StatusKind; detail?: string | undefined }) {
  if (status === "showing-aircraft") return null;

  return (
    <div
      role="status"
      data-status={status}
      style={{
        background: COLOR_BY_STATUS[status],
        color: "#0b1220",
        padding: "0.5rem 1rem",
        fontWeight: 600,
        textAlign: "center",
      }}
    >
      {STATUS_LABEL[status]}
      {detail ? ` — ${detail}` : ""}
    </div>
  );
}
