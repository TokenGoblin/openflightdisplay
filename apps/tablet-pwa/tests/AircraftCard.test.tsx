import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import type { AircraftState } from "@openflightdisplay/shared-models";
import { AircraftCard } from "../src/components/AircraftCard";

const aircraft: AircraftState = {
  provider: "mock",
  icaoHex: "a1b2c3",
  callsign: "UAL123",
  aircraftTypeCode: "B738",
  latitude: 47.62,
  longitude: -122.31,
  geometricAltitudeFt: 8500,
  groundSpeedKt: 240,
  trackHeadingDeg: 90,
  distanceFromObserverKm: 12.34,
  bearingFromObserverDeg: 45,
  emergencyState: "none",
  onGround: false,
  firstSeen: "2026-07-24T12:00:00.000Z",
  lastSeen: "2026-07-24T12:00:05.000Z",
  positionTimestamp: "2026-07-24T12:00:05.000Z",
  dataQualityFlags: [],
};

describe("AircraftCard", () => {
  it("renders callsign, distance, bearing, altitude, and speed", () => {
    render(<AircraftCard aircraft={aircraft} lastUpdatedAt={new Date()} />);
    expect(screen.getByText("UAL123")).toBeInTheDocument();
    expect(screen.getByText("B738")).toBeInTheDocument();
    expect(screen.getByText("12.3 km")).toBeInTheDocument();
    expect(screen.getByText("45°")).toBeInTheDocument();
    expect(screen.getByText("8500 ft")).toBeInTheDocument();
    expect(screen.getByText("240 kt")).toBeInTheDocument();
  });

  it("falls back to the ICAO hex when no callsign is present", () => {
    const { callsign, ...rest } = aircraft;
    render(<AircraftCard aircraft={rest as AircraftState} lastUpdatedAt={null} />);
    expect(screen.getByText("a1b2c3")).toBeInTheDocument();
    expect(screen.getByText(/Updated/)).toHaveTextContent("Updated —");
  });
});
