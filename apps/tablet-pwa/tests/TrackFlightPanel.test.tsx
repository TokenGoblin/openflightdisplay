import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { TrackedFlightStatus } from "@openflightdisplay/protocol";
import { TrackFlightPanel } from "../src/components/TrackFlightPanel";
import * as api from "../src/lib/api";

const BASE = "http://192.168.1.42";
const TOKEN = "test-token";

function statusFor(overrides: Partial<TrackedFlightStatus> = {}): TrackedFlightStatus {
  return {
    flight: "UA1234",
    callsign: "UAL1234",
    destinationIcao: "KSEA",
    phase: "ENROUTE",
    destinationResolved: true,
    minutesRemaining: 42,
    distanceToDestinationNm: 310,
    secondsSinceContact: 4,
    ...overrides,
  };
}

beforeEach(() => {
  vi.restoreAllMocks();
});

describe("TrackFlightPanel entry form", () => {
  it("sends the flight number exactly as typed, leaving IATA expansion to the device", async () => {
    const put = vi.spyOn(api, "putTrackedFlight").mockResolvedValue({} as never);
    const user = userEvent.setup();
    render(<TrackFlightPanel core2BaseUrl={BASE} pairingToken={TOKEN} />);

    await user.type(screen.getByLabelText(/flight number/i), "UA1234");
    await user.type(screen.getByLabelText(/arrival airport/i), "KSEA");
    await user.click(screen.getByRole("button", { name: /start tracking/i }));

    await waitFor(() => expect(put).toHaveBeenCalledOnce());
    expect(put).toHaveBeenCalledWith(BASE, TOKEN, { flight: "UA1234", destinationIcao: "KSEA" });
  });

  it("uppercases the destination so a lowercase entry still works", async () => {
    const put = vi.spyOn(api, "putTrackedFlight").mockResolvedValue({} as never);
    const user = userEvent.setup();
    render(<TrackFlightPanel core2BaseUrl={BASE} pairingToken={TOKEN} />);

    await user.type(screen.getByLabelText(/flight number/i), "ba249");
    await user.type(screen.getByLabelText(/arrival airport/i), "egll");
    await user.click(screen.getByRole("button", { name: /start tracking/i }));

    await waitFor(() => expect(put).toHaveBeenCalledOnce());
    expect(put).toHaveBeenCalledWith(BASE, TOKEN, { flight: "ba249", destinationIcao: "EGLL" });
  });

  // Caught before the request, so the user gets a specific message rather
  // than a round trip ending in a generic 400.
  it("rejects an IATA airport code without contacting the device", async () => {
    const put = vi.spyOn(api, "putTrackedFlight").mockResolvedValue({} as never);
    const user = userEvent.setup();
    render(<TrackFlightPanel core2BaseUrl={BASE} pairingToken={TOKEN} />);

    await user.type(screen.getByLabelText(/flight number/i), "UA1234");
    await user.type(screen.getByLabelText(/arrival airport/i), "SEA");
    await user.click(screen.getByRole("button", { name: /start tracking/i }));

    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(put).not.toHaveBeenCalled();
  });

  it("surfaces a device error instead of failing silently", async () => {
    vi.spyOn(api, "putTrackedFlight").mockRejectedValue(new api.ApiError("Request failed with HTTP 401", 401));
    const user = userEvent.setup();
    render(<TrackFlightPanel core2BaseUrl={BASE} pairingToken={TOKEN} />);

    await user.type(screen.getByLabelText(/flight number/i), "UA1234");
    await user.type(screen.getByLabelText(/arrival airport/i), "KSEA");
    await user.click(screen.getByRole("button", { name: /start tracking/i }));

    expect(await screen.findByRole("alert")).toHaveTextContent(/401/);
  });

  it("tells the user ICAO is required before they submit", () => {
    render(<TrackFlightPanel core2BaseUrl={BASE} pairingToken={TOKEN} />);
    expect(screen.getByText(/KSEA, not SEA/i)).toBeInTheDocument();
  });
});

describe("TrackFlightPanel live status", () => {
  it("shows the ETA reported by the device", () => {
    render(<TrackFlightPanel core2BaseUrl={BASE} pairingToken={TOKEN} status={statusFor()} />);
    expect(screen.getByText("UA1234")).toBeInTheDocument();
    expect(screen.getByText("42 min")).toBeInTheDocument();
    expect(screen.getByText("310 NM")).toBeInTheDocument();
  });

  it("formats an ETA over an hour in hours and minutes", () => {
    render(
      <TrackFlightPanel core2BaseUrl={BASE} pairingToken={TOKEN} status={statusFor({ minutesRemaining: 125 })} />,
    );
    expect(screen.getByText("2h 05m")).toBeInTheDocument();
  });

  // A flight that hasn't departed has no ETA. Showing "0" would read as
  // "landing now" to someone about to leave the house.
  it("shows a dash rather than zero when there is no ETA yet", () => {
    render(
      <TrackFlightPanel
        core2BaseUrl={BASE}
        pairingToken={TOKEN}
        status={statusFor({ phase: "WAITING", minutesRemaining: undefined })}
      />,
    );
    expect(screen.getByText("—")).toBeInTheDocument();
    expect(screen.queryByText("0 min")).not.toBeInTheDocument();
  });

  // The single most consequential distinction in the whole feature.
  it("explains that lost signal is not a landing", () => {
    render(
      <TrackFlightPanel core2BaseUrl={BASE} pairingToken={TOKEN} status={statusFor({ phase: "NO CONTACT" })} />,
    );
    expect(screen.getByText(/not a landing/i)).toBeInTheDocument();
  });

  it("distinguishes an unrecognised airport from a flight yet to depart", () => {
    render(
      <TrackFlightPanel
        core2BaseUrl={BASE}
        pairingToken={TOKEN}
        status={statusFor({ phase: "WAITING", destinationResolved: false })}
      />,
    );
    expect(screen.getByText(/destination airport wasn't recognised/i)).toBeInTheDocument();
  });

  it("never presents the estimate as a scheduled arrival time", () => {
    render(<TrackFlightPanel core2BaseUrl={BASE} pairingToken={TOKEN} status={statusFor()} />);
    expect(screen.getByText(/not a scheduled arrival time/i)).toBeInTheDocument();
  });

  it("can stop tracking", async () => {
    const put = vi.spyOn(api, "putTrackedFlight").mockResolvedValue({} as never);
    const user = userEvent.setup();
    render(<TrackFlightPanel core2BaseUrl={BASE} pairingToken={TOKEN} status={statusFor()} />);

    await user.click(screen.getByRole("button", { name: /stop tracking/i }));

    await waitFor(() => expect(put).toHaveBeenCalledWith(BASE, TOKEN, null));
  });
});
