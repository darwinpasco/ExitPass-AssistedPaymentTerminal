import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it } from "vitest";
import { App } from "./App";
import { containsInternalGuid } from "./cashierSafeReferences";
import { rawMode1Config } from "./test/testConfig";

afterEach(() => {
  window.history.replaceState({}, "", "/");
});

describe("cashier-safe rendered reference proof", () => {
  it.each([
    ["statutoryDiscountVisualSmoke", ["Application processing", "Applied amount changed", "Restart awaiting review"]],
    ["receiptVisualSmoke", ["Available", "Print history restart recovery", "Inconsistent copy sequence"]],
    ["transactionCompletionVisualSmoke", ["Restart during payment pending", "Restart during fiscal pending", "Receipt available"]],
  ])("does not render canonical GUIDs in the %s cashier surface", async (queryFlag, scenarios) => {
    window.__APT_CONFIG__ = rawMode1Config;
    window.history.replaceState({}, "", `/?${queryFlag}=1`);
    render(<App />);

    await screen.findAllByTestId("apt-terminal-shell");
    assertCashierSurfaceHasNoGuid();

    for (const scenario of scenarios) {
      await userEvent.click(screen.getByRole("button", { name: scenario }));
      await waitFor(assertCashierSurfaceHasNoGuid);
    }
  });
});

function assertCashierSurfaceHasNoGuid() {
  const exposedValues = [document.body.textContent ?? ""];
  for (const element of Array.from(document.querySelectorAll<HTMLElement>("*"))) {
    for (const attribute of ["aria-label", "aria-description", "title", "alt"]) {
      exposedValues.push(element.getAttribute(attribute) ?? "");
    }
    if (element instanceof HTMLInputElement || element instanceof HTMLTextAreaElement || element instanceof HTMLSelectElement) {
      exposedValues.push(element.value);
    }
  }
  expect(containsInternalGuid(exposedValues.join("\n"))).toBe(false);
}
