import { describe, expect, it } from "vitest";
import { parseAptConfig } from "./config";
import { rawMode1Config } from "./test/testConfig";

describe("parseAptConfig", () => {
  it("builds terminal context settings for supported Mode 1", () => {
    const result = parseAptConfig(rawMode1Config);

    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.config.terminalId).toBe("APT-DEV-001");
      expect(result.config.centralPmsConnectionMode).toBe("mock");
    }
  });

  it("reports missing profile as actionable startup failure", () => {
    const result = parseAptConfig({ ...rawMode1Config, APT_PROFILE: "" });

    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.errors.join(" ")).toContain("APT_PROFILE is required");
    }
  });
});
