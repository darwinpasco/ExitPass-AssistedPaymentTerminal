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
  await expect(page.getByText("Development Cashier", { exact: true })).toBeVisible();
  await expect(page.getByText("Development Cashier Terminal 1")).toBeVisible();
  await expect(page.getByTestId("cashier-header-shift")).toHaveText("CLOSED");
  await expect(page.getByTestId("cashier-header-custody")).toHaveText("CLOSED");
  await page.getByRole("button", { name: "Open Shift" }).click();
  await page.getByRole("button", { name: "Open Cash Custody" }).click();
  await expect(page.getByTestId("cashier-header-shift")).toHaveText("OPEN");
  await expect(page.getByTestId("cashier-header-custody")).toHaveText("OPEN");
  await expect(page.getByRole("button", { name: "Cashier Session" })).toHaveAttribute("aria-expanded", "false");

  await page.getByLabel("Ticket number").fill("APT-ACTIVE-1001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByRole("heading", { name: "Parking session details" })).toBeVisible();
  await expect(page.getByText("APT-ACTIVE-1001")).toBeVisible();
  await expectActivePayableBasisReady(page);
  await expect(page.getByTestId("local-cash-prerequisites-notice")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Record Cash Received" })).toBeEnabled();
  await expect(page.getByRole("button", { name: "Continue to Cash" })).toHaveCount(0);

  await page.getByLabel("Ticket number").fill("APT-EXPIRED-2001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Parking fee has expired. The current amount will be refreshed before cash is recorded.")).toBeVisible();
  await expect(page.getByRole("button", { name: "Record Cash Received" })).toBeEnabled();
});

test("missing own shift does not create recovered operational state", async ({ page }) => {
  await installLocalJournalBridgeFixture(page, { includeShift: false, includeCustody: false });
  await page.goto("/");

  await expect(page.getByTestId("cashier-header-shift")).toHaveText("CLOSED");
  await expect(page.getByTestId("cashier-header-custody")).toHaveText("CLOSED");
  await expect(page.getByRole("button", { name: "Cashier Session" })).toHaveAttribute("aria-expanded", "true");

  await page.getByLabel("Ticket number").fill("APT-ACTIVE-1001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expectActivePayableBasisReady(page);
  await expect(page.getByTestId("local-cash-prerequisites-notice")).toContainText("Open or resume your cashier shift.");
  await expect(page.getByRole("button", { name: "Record Cash Received" })).toBeDisabled();
});

async function expectActivePayableBasisReady(page) {
  await expect(page.getByTestId("payable-basis-amount")).toHaveText("₱125.00");
  await expect(page.getByText("Total Amount")).toBeVisible();
  await expect(page.getByTestId("session-readiness-value")).toHaveCount(0);
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

  await page.getByLabel("Ticket number").fill("APT-UNAVAILABLE-503");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Central PMS temporarily unavailable")).toBeVisible();
  await expect(page.getByText("Retry is available after Central PMS is reachable.")).toBeVisible();
  await expect(page.getByText(/Support reference:/)).toHaveCount(0);
});
