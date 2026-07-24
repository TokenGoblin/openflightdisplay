import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { SetupWizard } from "../src/components/SetupWizard";

vi.mock("../src/hooks/useQrScanner", () => ({
  useQrScanner: () => ({ decodedText: null, error: null }),
}));

vi.mock("../src/lib/api", async () => {
  const actual = await vi.importActual<typeof import("../src/lib/api")>("../src/lib/api");
  return {
    ...actual,
    pairWithCore2: vi.fn(async () => ({ schemaVersion: 1, pairingToken: "tok-1", deviceId: "core2-abc123" })),
    putCore2Config: vi.fn(async () => ({})),
    claimDeviceWithGateway: vi.fn(async () => ({})),
    putGatewayConfig: vi.fn(async () => ({})),
  };
});

beforeEach(() => {
  window.localStorage.clear();
});

describe("SetupWizard", () => {
  it("walks through pair -> location -> radius -> confirm and completes", async () => {
    const user = userEvent.setup();
    const onComplete = vi.fn();
    render(<SetupWizard onComplete={onComplete} />);

    // Pair step -- switch to manual entry (camera isn't available in jsdom).
    await user.click(screen.getByRole("button", { name: "Enter manually" }));
    await user.type(screen.getByLabelText("Display IP address"), "192.168.1.42");
    await user.type(screen.getByLabelText("Pairing code"), "482913");
    await user.type(screen.getByLabelText("Gateway address"), "http://192.168.1.50:8787");
    await user.click(screen.getByRole("button", { name: "Pair" }));

    // Location step
    await waitFor(() => expect(screen.getByText("Where should we monitor?")).toBeInTheDocument());
    await user.clear(screen.getByLabelText("Latitude"));
    await user.type(screen.getByLabelText("Latitude"), "47.6");
    await user.clear(screen.getByLabelText("Longitude"));
    await user.type(screen.getByLabelText("Longitude"), "-122.3");
    await user.click(screen.getByRole("button", { name: "Next" }));

    // Radius step
    await waitFor(() => expect(screen.getByText("Monitoring radius")).toBeInTheDocument());
    await user.click(screen.getByRole("button", { name: "Next" }));

    // Confirm step
    await waitFor(() => expect(screen.getByText("Confirm setup")).toBeInTheDocument());
    expect(screen.getByText("Display: core2-abc123")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Finish setup" }));

    await waitFor(() => expect(onComplete).toHaveBeenCalledWith(
      expect.objectContaining({ deviceId: "core2-abc123", gatewayBaseUrl: "http://192.168.1.50:8787" }),
    ));
  });
});
