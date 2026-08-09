import type { AptConfig } from "./config";
import type { HumanSessionState } from "./humanSessionBridge";

export type TerminalContext = {
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
  centralPmsConnectionMode: string;
};

export function buildTerminalContext(config: AptConfig, humanSession?: HumanSessionState): TerminalContext {
  const activeShift = humanSession?.activeShift;
  return {
    terminalId: config.terminalId,
    terminalDisplayName: config.terminalDisplayName,
    siteId: config.siteId,
    siteName: config.siteName,
    siteGroupId: config.siteGroupId,
    posServerId: config.posServerId,
    cashierId: humanSession?.userReference ?? "",
    cashierDisplayName: humanSession?.displayName ?? "Not signed in",
    shiftId: activeShift?.id ?? "",
    shiftStatus: activeShift?.status ?? "",
    centralPmsConnectionMode: config.centralPmsConnectionMode === "mock" ? "Controlled mock" : "Live Central PMS",
  };
}
