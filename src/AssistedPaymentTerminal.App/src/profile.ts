export const CASHIER_ASSISTED_TERMINAL = "CASHIER_ASSISTED_TERMINAL";
export const CONTINUITY_TERMINAL = "CONTINUITY_TERMINAL";

export type ProfileValidation =
  | { ok: true; profile: typeof CASHIER_ASSISTED_TERMINAL }
  | { ok: false; code: "MISSING_PROFILE" | "UNSUPPORTED_PROFILE" | "MODE2_NOT_IMPLEMENTED"; message: string };

export function validateOperatingProfile(profile: string | undefined | null): ProfileValidation {
  const normalized = profile?.trim();

  if (!normalized) {
    return {
      ok: false,
      code: "MISSING_PROFILE",
      message: "APT_PROFILE is required. Configure CASHIER_ASSISTED_TERMINAL to start this terminal.",
    };
  }

  if (normalized === CASHIER_ASSISTED_TERMINAL) {
    return { ok: true, profile: CASHIER_ASSISTED_TERMINAL };
  }

  if (normalized === CONTINUITY_TERMINAL) {
    return {
      ok: false,
      code: "MODE2_NOT_IMPLEMENTED",
      message: "CONTINUITY_TERMINAL is not implemented in this slice.",
    };
  }

  return {
    ok: false,
    code: "UNSUPPORTED_PROFILE",
    message: `Unsupported APT_PROFILE '${normalized}'. This slice only supports CASHIER_ASSISTED_TERMINAL.`,
  };
}
