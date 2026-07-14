import type { AptConfig } from "./config";

export type TerminalContext = {
  operatingMode: string;
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

export function buildTerminalContext(config: AptConfig): TerminalContext {
  return {
    operatingMode: "Mode 1 - Cashier-Assisted Terminal",
    terminalId: config.terminalId,
    terminalDisplayName: config.terminalDisplayName,
    siteId: config.siteId,
    siteName: config.siteName,
    siteGroupId: config.siteGroupId,
    posServerId: config.posServerId,
    cashierId: config.cashierId,
    cashierDisplayName: config.cashierDisplayName,
    shiftId: config.shiftId,
    shiftStatus: config.shiftStatus,
    centralPmsConnectionMode: config.centralPmsConnectionMode === "mock" ? "Controlled mock" : "Live Central PMS",
  };
}
