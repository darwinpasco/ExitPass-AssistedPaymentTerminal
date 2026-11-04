import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { describe, expect, it } from "vitest";

describe("approved PITX APT runtime identity", () => {
  it("keeps packaged and launcher identities aligned with the registered device", () => {
    const config = JSON.parse(readFileSync(resolve(process.cwd(), "public", "apt-config.json"), "utf8"));
    const launcher = readFileSync(resolve(process.cwd(), "..", "..", "scripts", "Start-AptPitxLocal.ps1"), "utf8");

    expect(config.APT_TERMINAL_ID).toBe("PITX-APT-01");
    expect(config.APT_TERMINAL_SERIAL_NUMBER).toBe("ACE4_2E00_653A_D952");
    expect(config.APT_POS_VERSION_LABEL).toBe("Version No. ExitPass V1.0.0");
    expect(launcher).toContain('APT_TERMINAL_ID = "PITX-APT-01"');
    expect(launcher).toContain("APT_TERMINAL_SERIAL_NUMBER = $TerminalSerialNumber.Trim()");
    expect(launcher).toContain("ACE4_2E00_653A_D952");
  });
});
