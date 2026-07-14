import { describe, expect, it } from "vitest";
import { validateOperatingProfile } from "./profile";

describe("validateOperatingProfile", () => {
  it("accepts supported Mode 1 profile", () => {
    expect(validateOperatingProfile("CASHIER_ASSISTED_TERMINAL")).toEqual({
      ok: true,
      profile: "CASHIER_ASSISTED_TERMINAL",
    });
  });

  it("refuses missing profile", () => {
    const result = validateOperatingProfile("");

    expect(result.ok).toBe(false);
    expect(result).toMatchObject({ code: "MISSING_PROFILE" });
  });

  it("refuses future Mode 2 profile", () => {
    const result = validateOperatingProfile("CONTINUITY_TERMINAL");

    expect(result.ok).toBe(false);
    expect(result).toMatchObject({ code: "MODE2_NOT_IMPLEMENTED" });
  });

  it("refuses unknown profile", () => {
    const result = validateOperatingProfile("ADMIN_WORKSTATION");

    expect(result.ok).toBe(false);
    expect(result).toMatchObject({ code: "UNSUPPORTED_PROFILE" });
  });
});
