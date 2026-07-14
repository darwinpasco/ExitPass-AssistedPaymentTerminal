import { expect, test } from "@playwright/test";

test("starts under CASHIER_ASSISTED_TERMINAL and resolves active and expired tickets", async ({ page }) => {
  await page.goto("/");

  await expect(page.getByRole("heading", { name: "Cashier-Assisted Terminal" })).toBeVisible();
  await expect(page.getByText("Mode 1", { exact: true })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Development Cashier Terminal 1" })).toBeVisible();
  await expect(page.getByText("ExitPass Demo Parking")).toBeVisible();
  await expect(page.getByText("POS-DEV-001")).toBeVisible();
  await expect(page.getByText("Development Cashier (CASHIER-DEV-001)")).toBeVisible();
  await expect(page.getByText("SHIFT-DEV-20260714-A")).toBeVisible();

  await page.getByLabel("Ticket reference").fill("APT-ACTIVE-1001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Authoritative payable basis")).toBeVisible();
  await expect(page.getByText("APT-ACTIVE-1001")).toBeVisible();
  await expect(page.getByText("Payable basis is current")).toBeVisible();
  await expect(page.getByRole("button", { name: "Collect payment" })).toBeDisabled();

  await page.getByLabel("Ticket reference").fill("APT-EXPIRED-2001");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Tariff expired")).toBeVisible();
  await expect(page.getByText("Blocked by expired tariff")).toBeVisible();
  await expect(page.getByRole("button", { name: "Collect payment" })).toBeDisabled();

  await page.getByRole("button", { name: "Recalculate Fee" }).click();

  await expect(page.getByText("Recalculated")).toBeVisible();
  await expect(page.getByText("Payable basis is current")).toBeVisible();
});

test("unsupported profile refuses startup", async ({ page }) => {
  await page.goto("/?aptProfile=CONTINUITY_TERMINAL");

  await expect(page.getByText("Unsupported terminal profile")).toBeVisible();
  await expect(page.getByText("CONTINUITY_TERMINAL is not implemented in this slice.")).toBeVisible();
});

test("service unavailable scenario shows support reference", async ({ page }) => {
  await page.goto("/");

  await page.getByLabel("Ticket reference").fill("APT-UNAVAILABLE-503");
  await page.getByRole("button", { name: "Resolve" }).click();

  await expect(page.getByText("Central PMS unavailable")).toBeVisible();
  await expect(page.getByText(/Support reference:/)).toBeVisible();
});
