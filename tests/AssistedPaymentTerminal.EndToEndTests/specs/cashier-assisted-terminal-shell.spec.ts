import { expect, test } from "@playwright/test";

test("starts under CASHIER_ASSISTED_TERMINAL and resolves active and expired tickets", async ({ page }) => {
  await page.goto("/");

  await expect(page.getByRole("heading", { name: "Cashier-Assisted Terminal", exact: true })).toBeVisible();
  await expect(page.getByText("ExitPass Demo Parking")).toBeVisible();
  await expect(page.getByText("Development Cashier", { exact: true })).toBeVisible();
  await expect(page.getByText("OPEN", { exact: true })).toBeVisible();
  await expect(page.getByText("Development Cashier Terminal 1")).toBeVisible();
  await expect(page.getByText("Configured: POS-DEV-001")).toBeVisible();

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
  await page.goto("/?aptProfile=CONTINUITY_TERMINAL");

  await expect(page.getByText("Unsupported terminal profile")).toBeVisible();
  await expect(page.getByText("CONTINUITY_TERMINAL is not implemented in this slice.")).toBeVisible();
});

test("service unavailable scenario shows support reference", async ({ page }) => {
  await page.goto("/");

  await page.getByLabel("Ticket reference").fill("APT-UNAVAILABLE-503");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Central PMS temporarily unavailable")).toBeVisible();
  await expect(page.getByText("Retry is available after Central PMS is reachable.")).toBeVisible();
  await expect(page.getByText(/Support reference:/)).toBeVisible();
});
