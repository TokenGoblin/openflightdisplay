/** Small macOS-style rounded status pill (dot + label). */
export function StatusPill({ label, tone = "success" }: { label: string; tone?: "success" | "neutral" }) {
  const { bg, fg } = tone === "success" ? { bg: "#30d158", fg: "#04260f" } : { bg: "#9fb0c8", fg: "#0b1220" };
  return (
    <span
      style={{
        display: "inline-flex",
        alignItems: "center",
        gap: "0.35rem",
        padding: "0.15rem 0.7rem",
        borderRadius: 9999,
        background: bg,
        color: fg,
        fontSize: "0.75rem",
        fontWeight: 600,
        lineHeight: 1.6,
      }}
    >
      <span aria-hidden="true">●</span>
      {label}
    </span>
  );
}
