import { validateOperatingProfile, type ProfileValidation } from "./profile";

export type RawAptConfig = Record<string, string | undefined>;

export type AptConfig = {
  profile: string;
  terminalId: string;
  terminalDisplayName: string;
  siteId: string;
  siteName: string;
  siteGroupId: string;
  posServerId: string;
  cashierId: string;
  cashierDisplayName: string;
  shiftId: string;
  shiftStatus: string;
  centralPmsBaseUrl: string;
  centralPmsConnectionMode: "mock" | "live";
  webUiUrl?: string;
  vendorSystemId: string;
};

export type ConfigLoadResult =
  | { ok: true; config: AptConfig; profileValidation: ProfileValidation & { ok: true } }
  | { ok: false; profileValidation?: ProfileValidation; errors: string[]; raw?: RawAptConfig };

declare global {
  interface Window {
    __APT_CONFIG__?: RawAptConfig;
  }
}

const requiredSettings = [
  "APT_PROFILE",
  "APT_TERMINAL_ID",
  "APT_TERMINAL_DISPLAY_NAME",
  "APT_SITE_ID",
  "APT_SITE_NAME",
  "APT_SITE_GROUP_ID",
  "APT_POS_SERVER_ID",
  "APT_CASHIER_ID",
  "APT_CASHIER_DISPLAY_NAME",
  "APT_SHIFT_ID",
  "APT_SHIFT_STATUS",
  "CENTRAL_PMS_BASE_URL",
  "USE_MOCK_CENTRAL_PMS",
];

const fileSmokeConfig: RawAptConfig = {
  APT_PROFILE: "CASHIER_ASSISTED_TERMINAL",
  APT_TERMINAL_ID: "APT-DEV-001",
  APT_TERMINAL_DISPLAY_NAME: "Development Cashier Terminal 1",
  APT_SITE_ID: "11111111-1111-1111-1111-111111111111",
  APT_SITE_NAME: "ExitPass Demo Parking",
  APT_SITE_GROUP_ID: "22222222-2222-2222-2222-222222222222",
  APT_POS_SERVER_ID: "POS-DEV-001",
  APT_CASHIER_ID: "CASHIER-DEV-001",
  APT_CASHIER_DISPLAY_NAME: "Development Cashier",
  APT_SHIFT_ID: "SHIFT-DEV-20260714-A",
  APT_SHIFT_STATUS: "OPEN",
  CENTRAL_PMS_BASE_URL: "https://central-pms.example.invalid",
  USE_MOCK_CENTRAL_PMS: "true",
  APT_WEB_UI_URL: "file://production-smoke",
  CENTRAL_PMS_VENDOR_SYSTEM_ID: "VENDOR-PMS-DEV",
};

export async function loadAptConfig(): Promise<ConfigLoadResult> {
  const raw = await loadRawConfig();
  applyQueryOverrides(raw);
  return parseAptConfig(raw);
}

export function parseAptConfig(raw: RawAptConfig): ConfigLoadResult {
  const errors = requiredSettings.filter((key) => !raw[key]?.trim()).map((key) => `${key} is required.`);
  const profileValidation = validateOperatingProfile(raw.APT_PROFILE);

  if (!profileValidation.ok) {
    errors.unshift(profileValidation.message);
  }

  if (errors.length > 0) {
    return { ok: false, profileValidation, errors, raw };
  }

  if (!profileValidation.ok) {
    return { ok: false, profileValidation, errors: [profileValidation.message], raw };
  }

  return {
    ok: true,
    profileValidation,
    config: {
      profile: raw.APT_PROFILE!.trim(),
      terminalId: raw.APT_TERMINAL_ID!.trim(),
      terminalDisplayName: raw.APT_TERMINAL_DISPLAY_NAME!.trim(),
      siteId: raw.APT_SITE_ID!.trim(),
      siteName: raw.APT_SITE_NAME!.trim(),
      siteGroupId: raw.APT_SITE_GROUP_ID!.trim(),
      posServerId: raw.APT_POS_SERVER_ID!.trim(),
      cashierId: raw.APT_CASHIER_ID!.trim(),
      cashierDisplayName: raw.APT_CASHIER_DISPLAY_NAME!.trim(),
      shiftId: raw.APT_SHIFT_ID!.trim(),
      shiftStatus: raw.APT_SHIFT_STATUS!.trim(),
      centralPmsBaseUrl: raw.CENTRAL_PMS_BASE_URL!.trim(),
      centralPmsConnectionMode: raw.USE_MOCK_CENTRAL_PMS!.trim().toLowerCase() === "true" ? "mock" : "live",
      webUiUrl: raw.APT_WEB_UI_URL?.trim(),
      vendorSystemId: raw.CENTRAL_PMS_VENDOR_SYSTEM_ID?.trim() || "VENDOR-PMS-DEV",
    },
  };
}

async function loadRawConfig(): Promise<RawAptConfig> {
  if (window.__APT_CONFIG__) {
    return { ...window.__APT_CONFIG__ };
  }

  if (window.location.protocol === "file:") {
    return { ...fileSmokeConfig };
  }

  const response = await fetch("/apt-config.json", { cache: "no-store" });
  if (!response.ok) {
    return {};
  }

  const payload = (await response.json()) as RawAptConfig;
  return { ...payload };
}

function applyQueryOverrides(raw: RawAptConfig): void {
  const query = new URLSearchParams(window.location.search);
  const profile = query.get("aptProfile");
  if (profile !== null) {
    raw.APT_PROFILE = profile;
  }
}
