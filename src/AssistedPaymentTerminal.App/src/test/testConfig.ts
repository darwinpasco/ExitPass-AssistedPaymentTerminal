import type { AptConfig, RawAptConfig } from "../config";
import { parseAptConfig } from "../config";

export const rawMode1Config: RawAptConfig = {
  APT_PROFILE: "CASHIER_ASSISTED_TERMINAL",
  APT_TERMINAL_ID: "APT-DEV-001",
  APT_TERMINAL_DISPLAY_NAME: "Development Cashier Terminal 1",
  APT_SITE_ID: "11111111-1111-1111-1111-111111111111",
  APT_SITE_NAME: "ExitPass Demo Parking",
  APT_SITE_GROUP_ID: "22222222-2222-2222-2222-222222222222",
  APT_POS_SERVER_ID: "POS-DEV-001",
  CENTRAL_PMS_BASE_URL: "https://central-pms.example.invalid",
  USE_MOCK_CENTRAL_PMS: "true",
  APT_WEB_UI_URL: "http://localhost:5173",
  CENTRAL_PMS_VENDOR_SYSTEM_ID: "VENDOR-PMS-DEV",
};

export function mode1Config(): AptConfig {
  const result = parseAptConfig(rawMode1Config);
  if (!result.ok) {
    throw new Error("Invalid test config");
  }

  return result.config;
}
