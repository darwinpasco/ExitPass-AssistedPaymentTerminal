import { describe, expect, it } from "vitest";
import { parseAptConfig, usesPackagedFallbackConfig } from "./config";
import { rawMode1Config } from "./test/testConfig";

describe("parseAptConfig", () => {
  it("builds terminal context settings for the cashier-assisted terminal", () => {
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

describe("usesPackagedFallbackConfig", () => {
  it("uses local fallback config for packaged WebView2 virtual host", () => {
    expect(usesPackagedFallbackConfig("https:", "apt.local")).toBe(true);
  });

  it("uses local fallback config for legacy file navigation", () => {
    expect(usesPackagedFallbackConfig("file:", "")).toBe(true);
  });

  it("does not use local fallback config for development server URLs", () => {
    expect(usesPackagedFallbackConfig("http:", "localhost")).toBe(false);
  });
});
