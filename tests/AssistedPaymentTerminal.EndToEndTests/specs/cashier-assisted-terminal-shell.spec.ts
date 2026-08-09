import { expect, test } from "@playwright/test";
import { installLocalJournalBridgeFixture } from "../local-journal-fixture.mjs";

test("unauthenticated startup mounts only the initialized human-login shell", async ({ page }) => {
  await installLocalJournalBridgeFixture(page, { includeShift: true, includeCustody: true });
  await page.goto("/");

  await expect(page.getByTestId("apt-human-login-shell")).toHaveAttribute("data-app-ready", "true");
  await expect(page.getByRole("heading", { name: "Cashier sign in" })).toBeVisible();
  await expect(page.getByTestId("apt-terminal-shell")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Collect payment" })).toHaveCount(0);
});

test("starts under CASHIER_ASSISTED_TERMINAL and resolves active and expired tickets", async ({ page }) => {
  await installLocalJournalBridgeFixture(page, { includeShift: true, includeCustody: true });
  await page.goto("/");

  await expect(page.getByRole("heading", { name: "Cashier-Assisted Terminal", exact: true })).toBeVisible();
  await expect(page.getByRole("button", { name: "Refresh authority" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Reauthenticate" })).toHaveCount(0);
  await expect(page.getByText("ExitPass Demo Parking")).toBeVisible();
  await expect(page.getByTestId("operational-cashier-summary")).toHaveText("Development Cashier");
  await expect(page.getByTestId("operational-shift-summary")).toHaveText("OPEN");
  await expect(page.getByText("Development Cashier Terminal 1")).toBeVisible();
  await expect(page.getByTestId("operational-pos-readiness-summary")).toHaveText("Configured");
  await page.getByText("Terminal details").click();
  await expect(page.getByTestId("recovered-shift-id")).toHaveText("Open");
  await expect(page.getByTestId("active-custody-id")).toHaveText("Open");

  await page.getByLabel("Ticket reference").fill("APT-ACTIVE-1001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Authoritative payable basis")).toBeVisible();
  await expect(page.getByText("APT-ACTIVE-1001")).toBeVisible();
  await expectActivePayableBasisReady(page);
  await expect(page.getByTestId("local-cash-prerequisites-notice")).toContainText("Local cash prerequisites unavailable");
  await expect(page.getByTestId("local-cash-prerequisites-value")).toHaveText("Blocked");
  await expect(page.getByTestId("continue-to-cash")).toBeDisabled();
  await expect(page.getByRole("button", { name: "Collect payment" })).toBeDisabled();

  await page.getByLabel("Ticket reference").fill("APT-EXPIRED-2001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByRole("heading", { name: "Cash acceptance blocked" })).toBeVisible();
  await expect(page.getByTestId("tariff-readiness-value")).toHaveText("Expired");
  await expect(page.getByTestId("central-cash-ready-value")).toHaveText("false");
  await expect(page.getByTestId("continue-to-cash")).toBeDisabled();
  await expect(page.getByText("Parking fee has expired and must be resolved again.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Collect payment" })).toBeDisabled();
});

test("missing own shift does not create recovered operational state", async ({ page }) => {
  await installLocalJournalBridgeFixture(page, { includeShift: false, includeCustody: false });
  await page.goto("/");

  await expect(page.getByTestId("operational-shift-summary")).toHaveText("No active shift");
  await page.getByText("Terminal details").click();
  await expect(page.getByTestId("recovered-shift-id")).toHaveText("None");
  await expect(page.getByTestId("active-custody-id")).toHaveText("None");

  await page.getByLabel("Ticket reference").fill("APT-ACTIVE-1001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expectActivePayableBasisReady(page);
  await expect(page.getByTestId("local-cash-prerequisites-value")).toHaveText("Blocked");
  await expect(page.getByTestId("continue-to-cash")).toBeDisabled();
});

async function expectActivePayableBasisReady(page) {
  await expect(page.getByTestId("payable-basis-summary")).toContainText("Authoritative payable basis");
  await expect(page.getByTestId("payable-basis-amount")).toHaveText("₱125.00");
  await expect(page.getByRole("heading", { name: "Ready for cash acceptance" })).toBeVisible();
  await expect(page.getByTestId("session-readiness-value")).toHaveText("Resolved Payable");
  await expect(page.getByTestId("tariff-readiness-value")).toHaveText("Current");
  await expect(page.getByTestId("payment-eligibility-value")).toHaveText("Eligible");
  await expect(page.getByTestId("terminal-cash-readiness-value")).toHaveText("Available");
  await expect(page.getByTestId("sales-invoice-readiness-value")).toHaveText("Ready");
  await expect(page.getByTestId("fiscal-readiness-value")).toHaveText("Ready");
  await expect(page.getByTestId("central-cash-ready-value")).toHaveText("true");
}

test("unsupported profile refuses startup", async ({ page }) => {
  await installLocalJournalBridgeFixture(page, { includeShift: true, includeCustody: true });
  await page.goto("/?aptProfile=CONTINUITY_TERMINAL");

  await expect(page.getByText("Unsupported terminal profile")).toBeVisible();
  await expect(page.getByText("CONTINUITY_TERMINAL is not implemented in this slice.")).toBeVisible();
  await expect(page.getByTestId("apt-human-login-shell")).toHaveCount(0);
  await expect(page.getByTestId("apt-terminal-shell")).toHaveCount(0);
});

test("service unavailable scenario does not expose an internal diagnostic correlation", async ({ page }) => {
  await installLocalJournalBridgeFixture(page, { includeShift: true, includeCustody: true });
  await page.goto("/");

  await page.getByLabel("Ticket reference").fill("APT-UNAVAILABLE-503");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Central PMS temporarily unavailable")).toBeVisible();
  await expect(page.getByText("Retry is available after Central PMS is reachable.")).toBeVisible();
  await expect(page.getByText(/Support reference:/)).toHaveCount(0);
});
