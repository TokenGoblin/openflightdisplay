import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { StatusBanner } from "../src/components/StatusBanner";

describe("StatusBanner", () => {
  it("renders nothing when aircraft are showing (no banner needed)", () => {
    const { container } = render(<StatusBanner status="showing-aircraft" />);
    expect(container).toBeEmptyDOMElement();
  });

  it("renders the configuration-required message", () => {
    render(<StatusBanner status="configuration-required" />);
    expect(screen.getByRole("status")).toHaveTextContent("Configuration required");
  });

  it("renders the data-source-unavailable message with an optional detail", () => {
    render(<StatusBanner status="data-source-unavailable" detail="adsb.lol unreachable" />);
    expect(screen.getByRole("status")).toHaveTextContent("Data source unavailable — adsb.lol unreachable");
  });

  it("renders the stale message", () => {
    render(<StatusBanner status="stale" />);
    expect(screen.getByRole("status")).toHaveTextContent("Data is stale");
  });
});
